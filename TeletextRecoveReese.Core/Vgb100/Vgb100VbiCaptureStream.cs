// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace TeletextRecoveReese.Core.Vgb100;

public sealed record Vgb100DeviceInfo(byte Bus, byte Address, ushort Revision)
{
    public string Path => $"vgb100:{Bus:D3}:{Address:D3}";

    public string Name => Revision == Vgb100Device.SupportedRevision
        ? $"August VGB100 (USB {Bus}-{Address})"
        : $"August VGB100 rev {Revision >> 8:x}.{Revision & 0xff:x2} (USB {Bus}-{Address}, untested)";

    public override string ToString() => Name;
}

/// <summary>
/// Raw VBI capture from an August VGB100 through libusb, without the cx231xx
/// kernel driver. Frames hold two fields of 18 (625/50) or 12 (525/60) lines ×
/// 1440 GREY samples at 27 MHz, the layout the Linux driver exposes.
/// </summary>
public sealed class Vgb100VbiCaptureStream : LiveVbiCaptureStream
{
    private const int TransferCount = 8;
    private const int TransferSize = 32 * 512;
    private const uint ControlTimeoutMs = 1000;
    private const int MaxConsecutiveTransferErrors = 50;
    private const int VideoTransferCount = 8;
    private const int VideoPacketsPerTransfer = 64; // 8 ms of microframes per transfer
    private const int DefaultVideoPacketSize = 3 * 964; // alt 3: 3 × 964-byte transactions
    private static readonly int[] ClaimedInterfaces = [1, Vgb100Device.VideoInterface,
        Vgb100Device.VbiInterface, Vgb100Device.SlicedCaptionInterface];

    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Vgb100VbiParser _parser;
    private readonly List<int> _claimed = new();
    private readonly IntPtr[] _transfers = new IntPtr[TransferCount];
    private readonly IntPtr[] _videoTransfers = new IntPtr[VideoTransferCount];
    private readonly Vgb100VideoParser? _videoParser;
    private int _videoPacketSize = DefaultVideoPacketSize;
    private volatile bool _videoStopped;
    private int _consecutiveVideoErrors;
    private IntPtr _context;
    private IntPtr _handle;
    private GCHandle _self;
    private Vgb100Device? _device;
    private Thread? _eventThread;
    private volatile bool _stopping;
    private int _inFlight;
    private readonly ConcurrentQueue<IntPtr> _stalledTransfers = new();
    private int _recoveringStall;
    private int _consecutiveTransferErrors;
    private long _stallRecoveries;
    private Exception? _fault;
    private byte[]? _readFrame;
    private int _readOffset;
    private long _capturedFrames;
    private bool _disposed;

    public override uint SamplingRate => Vgb100Device.VbiSamplingRate;
    public override int SamplesPerLine => Vgb100Device.VbiSamplesPerLine;
    public override int FirstFieldLines => _parser.LinesPerField;
    public override int SecondFieldLines => _parser.LinesPerField;
    public override long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public override Action<byte[]>? RawFrameCaptured { get; set; }

    public Vgb100Input Input { get; }
    public Vgb100Standard Standard { get; }
    public long StallRecoveries => Interlocked.Read(ref _stallRecoveries);

    /// <summary>True when the isochronous video stream was requested.</summary>
    public bool VideoEnabled => _videoParser is not null;
    /// <summary>Why video stopped while VBI capture continues, if it did.</summary>
    public string? VideoError { get; private set; }
    /// <summary>Asked before a frame is copied; return false to skip it.</summary>
    public Func<bool>? TryAcquireVideoFrame { get; set; }
    /// <summary>Receives 720×576 or 720×480 interlaced YUYV frames on the USB event thread.</summary>
    public Action<LinuxV4l2VideoFrame>? VideoFrameCaptured { get; set; }
    public ushort DecoderId { get; private set; }

    private Vgb100VbiCaptureStream(Vgb100Input input, Vgb100Standard standard, bool enableVideo)
    {
        Input = input;
        Standard = standard;
        bool lines625 = Vgb100Device.Is625Lines(standard);
        _parser = new Vgb100VbiParser(frame => _frames.Writer.TryWrite(frame),
            lines625 ? Vgb100VbiParser.PalLinesPerField : Vgb100VbiParser.NtscLinesPerField);
        if (enableVideo)
            _videoParser = new Vgb100VideoParser(OnVideoFrame,
                lines625 ? Vgb100VideoParser.PalHeight : Vgb100VideoParser.NtscHeight);
    }

    public static bool IsLibUsbAvailable => LibUsb.IsAvailable;

    public static unsafe IReadOnlyList<Vgb100DeviceInfo> Enumerate()
    {
        var result = new List<Vgb100DeviceInfo>();
        if (!LibUsb.IsAvailable) return result;
        IntPtr context;
        if (LibUsb.libusb_init(&context) != LibUsb.Success) return result;
        try
        {
            IntPtr* list;
            nint count = LibUsb.libusb_get_device_list(context, &list);
            if (count < 0) return result;
            try
            {
                for (nint i = 0; i < count; i++)
                {
                    LibUsb.DeviceDescriptor descriptor;
                    if (LibUsb.libusb_get_device_descriptor(list[i], &descriptor) != LibUsb.Success) continue;
                    if (descriptor.VendorId != Vgb100Device.VendorId
                        || descriptor.ProductId != Vgb100Device.ProductId) continue;
                    result.Add(new Vgb100DeviceInfo(
                        LibUsb.libusb_get_bus_number(list[i]),
                        LibUsb.libusb_get_device_address(list[i]),
                        descriptor.DeviceVersion));
                }
            }
            finally
            {
                LibUsb.libusb_free_device_list(list, 1);
            }
        }
        finally
        {
            LibUsb.libusb_exit(context);
        }
        return result;
    }

    public static Vgb100VbiCaptureStream Open(Vgb100DeviceInfo info, Vgb100Input input,
        bool enableVideo = false, Vgb100Standard standard = Vgb100Standard.Pal)
    {
        var stream = new Vgb100VbiCaptureStream(input, standard, enableVideo);
        try
        {
            stream.OpenDevice(info);
            stream.StartCapture();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private unsafe void OpenDevice(Vgb100DeviceInfo info)
    {
        IntPtr context;
        LibUsb.Check(LibUsb.libusb_init(&context), "libusb_init");
        _context = context;

        IntPtr* list;
        nint count = LibUsb.libusb_get_device_list(_context, &list);
        LibUsb.Check((int)Math.Min(count, 0), "libusb_get_device_list");
        IntPtr device = IntPtr.Zero;
        try
        {
            for (nint i = 0; i < count; i++)
            {
                if (LibUsb.libusb_get_bus_number(list[i]) != info.Bus
                    || LibUsb.libusb_get_device_address(list[i]) != info.Address) continue;
                device = list[i];
                break;
            }
            if (device == IntPtr.Zero)
                throw new IOException($"{info.Name} is no longer connected.");
            IntPtr handle;
            int result = LibUsb.libusb_open(device, &handle);
            if (result == -3)
                throw new UnauthorizedAccessException(OperatingSystem.IsLinux()
                    ? "No permission to open the VGB100 USB device. Install the udev rule for 1f4d:0102 and replug the card."
                    : "No permission to open the VGB100 USB device.");
            if (result == -5 || result == -12)
                throw new IOException(OperatingSystem.IsWindows()
                    ? "Windows has no WinUSB driver bound to the VGB100. Bind WinUSB to the device (for example with Zadig) and try again."
                    : $"libusb cannot open the VGB100 ({LibUsb.ErrorName(result)}).");
            LibUsb.Check(result, "libusb_open");
            _handle = handle;
            bool highSpeed = LibUsb.libusb_get_device_speed(device) == 3;
            if (_videoParser is not null)
            {
                try
                {
                    int size = LibUsb.libusb_get_max_alt_packet_size(device, Vgb100Device.VideoInterface,
                        Vgb100Device.VideoAlternateSetting, Vgb100Device.VideoEndpoint);
                    if (size > 0) _videoPacketSize = size;
                }
                catch (EntryPointNotFoundException)
                {
                    // libusb before 1.0.23; the VGB100 descriptor value is used.
                }
            }
            _device = new Vgb100Device(new Transport(this), highSpeed);
        }
        finally
        {
            LibUsb.libusb_free_device_list(list, 1);
        }

        foreach (int interfaceNumber in ClaimedInterfaces)
        {
            // cx231xx binds only the control interface 1 and keeps the audio
            // function. Detaching it tears down its ALSA card, which can deadlock
            // against PipeWire closing that card, so a bound interface is left to
            // the kernel; vendor control requests need no claimed interface.
            if (LibUsb.libusb_kernel_driver_active(_handle, interfaceNumber) == 1)
            {
                if (interfaceNumber == 1) continue;
                throw new IOException(
                    $"VGB100 interface {interfaceNumber} is owned by a kernel driver; usermode capture cannot use it.");
            }
            int result = LibUsb.libusb_claim_interface(_handle, interfaceNumber);
            if (result == LibUsb.Success)
            {
                _claimed.Add(interfaceNumber);
                continue;
            }
            if (interfaceNumber == Vgb100Device.VbiInterface || result == -6)
                throw new IOException(result == -6
                    ? "The VGB100 is in use by another application or driver."
                    : $"Cannot claim the VGB100 VBI interface ({LibUsb.ErrorName(result)}).");
        }
    }

    private unsafe void StartCapture()
    {
        Vgb100Device device = _device!;
        device.Probe();
        DecoderId = device.DecoderId;
        device.SelectInput(Input);
        device.SetStandard(Standard);
        device.OpenVbi();
        device.PrepareVbiStreaming();

        _self = GCHandle.Alloc(this);
        delegate* unmanaged[Cdecl]<IntPtr, void> callback = &TransferCompleted;
        for (int i = 0; i < _transfers.Length; i++)
        {
            IntPtr transfer = LibUsb.libusb_alloc_transfer(0);
            if (transfer == IntPtr.Zero) throw new OutOfMemoryException("libusb_alloc_transfer failed.");
            _transfers[i] = transfer;
            var native = (LibUsb.Transfer*)transfer;
            native->DeviceHandle = _handle;
            native->Endpoint = Vgb100Device.VbiEndpoint;
            native->Type = LibUsb.TransferTypeBulk;
            native->Timeout = 0;
            native->Length = TransferSize;
            native->Buffer = (IntPtr)NativeMemory.Alloc(TransferSize);
            native->Callback = (IntPtr)callback;
            native->UserData = GCHandle.ToIntPtr(_self);
            native->IsoPacketCount = 0;
        }
        foreach (IntPtr transfer in _transfers)
        {
            LibUsb.Check(LibUsb.libusb_submit_transfer(transfer), "submit VBI transfer");
            Interlocked.Increment(ref _inFlight);
        }

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "VGB100 libusb events",
        };
        _eventThread.Start();

        device.StartVbiStreaming();
        if (_videoParser is not null) StartVideo(device, callback);
    }

    private unsafe void StartVideo(Vgb100Device device, delegate* unmanaged[Cdecl]<IntPtr, void> callback)
    {
        try
        {
            int length = VideoPacketsPerTransfer * _videoPacketSize;
            for (int i = 0; i < _videoTransfers.Length; i++)
            {
                IntPtr transfer = LibUsb.libusb_alloc_transfer(VideoPacketsPerTransfer);
                if (transfer == IntPtr.Zero) throw new OutOfMemoryException("libusb_alloc_transfer failed.");
                _videoTransfers[i] = transfer;
                var native = (LibUsb.Transfer*)transfer;
                native->DeviceHandle = _handle;
                native->Endpoint = Vgb100Device.VideoEndpoint;
                native->Type = LibUsb.TransferTypeIsochronous;
                native->Timeout = 0;
                native->Length = length;
                native->Buffer = (IntPtr)NativeMemory.Alloc((nuint)length);
                native->Callback = (IntPtr)callback;
                native->UserData = GCHandle.ToIntPtr(_self);
                native->IsoPacketCount = VideoPacketsPerTransfer;
                LibUsb.IsoPacketDescriptor* packets = LibUsb.IsoPackets(transfer);
                for (int packet = 0; packet < VideoPacketsPerTransfer; packet++)
                    packets[packet].Length = (uint)_videoPacketSize;
            }
            // cx231xx_init_isoc() submits the URBs before enabling the endpoint.
            foreach (IntPtr transfer in _videoTransfers)
            {
                LibUsb.Check(LibUsb.libusb_submit_transfer(transfer), "submit video transfer");
                Interlocked.Increment(ref _inFlight);
            }
            device.StartVideoStreaming();
        }
        catch (Exception ex)
        {
            StopVideo($"Video preview unavailable: {ex.Message}");
        }
    }

    private void StopVideo(string reason)
    {
        if (_videoStopped) return;
        VideoError = reason;
        _videoStopped = true;
        foreach (IntPtr transfer in _videoTransfers)
            if (transfer != IntPtr.Zero) LibUsb.libusb_cancel_transfer(transfer);
    }

    private void OnVideoFrame(byte[] frame)
    {
        Action<LinuxV4l2VideoFrame>? callback = VideoFrameCaptured;
        if (callback is null) return;
        try
        {
            if (TryAcquireVideoFrame?.Invoke() == false) return;
            callback(new LinuxV4l2VideoFrame(
                Vgb100VideoParser.Width, _videoParser!.Height, YuyvFourCc, InterlacedField,
                Vgb100VideoParser.BytesPerLine, frame.ToArray()));
        }
        catch
        {
            // A preview consumer must not stop VBI capture.
        }
    }

    private const uint YuyvFourCc = 0x56595559;
    private const uint InterlacedField = 4; // V4L2_FIELD_INTERLACED

    private unsafe void EventLoop()
    {
        var timeout = new LibUsb.TimeVal { Seconds = new CLong(0), Microseconds = new CLong(100_000) };
        while (!_stopping || Volatile.Read(ref _inFlight) > 0)
        {
            int result = LibUsb.libusb_handle_events_timeout_completed(_context, &timeout, null);
            if (result < 0 && result != -10 /* interrupted */)
            {
                Fail(new IOException($"VGB100 USB event handling failed: {LibUsb.ErrorName(result)}."));
                if (_stopping) break;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TransferCompleted(IntPtr transfer)
    {
        var native = (LibUsb.Transfer*)transfer;
        if (GCHandle.FromIntPtr(native->UserData).Target is not Vgb100VbiCaptureStream stream) return;
        stream.OnTransferCompleted(native, transfer);
    }

    private unsafe void OnVideoTransferCompleted(LibUsb.Transfer* native, IntPtr transfer)
    {
        switch (native->Status)
        {
            case LibUsb.TransferStatus.Completed:
                if (_stopping || _videoStopped) break;
                Volatile.Write(ref _consecutiveVideoErrors, 0);
                LibUsb.IsoPacketDescriptor* packets = LibUsb.IsoPackets(transfer);
                for (int packet = 0; packet < native->IsoPacketCount; packet++)
                {
                    // Like cx231xx_isoc_copy(), skip failed and empty packets.
                    if (packets[packet].Status != LibUsb.TransferStatus.Completed
                        || packets[packet].ActualLength == 0
                        || packets[packet].ActualLength > (uint)_videoPacketSize) continue;
                    var data = new ReadOnlySpan<byte>(
                        (byte*)native->Buffer + packet * _videoPacketSize, (int)packets[packet].ActualLength);
                    try { _videoParser!.Feed(data); }
                    catch (Exception ex) { StopVideo($"Video preview stopped: {ex.Message}"); }
                }
                break;
            case LibUsb.TransferStatus.Cancelled:
                break;
            case LibUsb.TransferStatus.NoDevice:
                Fail(new IOException("The VGB100 was disconnected."));
                break;
            default:
                _videoParser!.Reset();
                if (!_stopping && Interlocked.Increment(ref _consecutiveVideoErrors) > MaxConsecutiveTransferErrors)
                    StopVideo($"Video preview stopped: isochronous transfers keep failing ({native->Status}).");
                break;
        }

        if (!_stopping && !_videoStopped && _fault is null && LibUsb.libusb_submit_transfer(transfer) == LibUsb.Success)
            return;
        Interlocked.Decrement(ref _inFlight);
    }

    private unsafe void OnTransferCompleted(LibUsb.Transfer* native, IntPtr transfer)
    {
        if (native->Endpoint == Vgb100Device.VideoEndpoint)
        {
            OnVideoTransferCompleted(native, transfer);
            return;
        }
        switch (native->Status)
        {
            case LibUsb.TransferStatus.Completed:
            case LibUsb.TransferStatus.TimedOut:
                if (native->ActualLength > 0 && !_stopping)
                {
                    Volatile.Write(ref _consecutiveTransferErrors, 0);
                    try { _parser.Feed(new ReadOnlySpan<byte>((void*)native->Buffer, native->ActualLength)); }
                    catch (Exception ex) { Fail(ex); }
                }
                break;
            case LibUsb.TransferStatus.Cancelled:
                break;
            case LibUsb.TransferStatus.NoDevice:
                Fail(new IOException("The VGB100 was disconnected."));
                break;
            case LibUsb.TransferStatus.Stall when !_stopping && _fault is null:
                // A halted bulk endpoint needs CLEAR_FEATURE(ENDPOINT_HALT), a
                // synchronous request that must not run on the event thread.
                _parser.Reset();
                _stalledTransfers.Enqueue(transfer);
                if (Interlocked.Exchange(ref _recoveringStall, 1) == 0)
                    ThreadPool.UnsafeQueueUserWorkItem(_ => RecoverStalledEndpoint(), null);
                return;
            default:
                // cx231xx logs other URB errors and resubmits; a lost transfer
                // only costs the frame it belonged to.
                if (!_stopping && Interlocked.Increment(ref _consecutiveTransferErrors) > MaxConsecutiveTransferErrors)
                    Fail(new IOException($"VGB100 VBI transfers keep failing ({native->Status})."));
                _parser.Reset();
                break;
        }

        if (!_stopping && _fault is null && LibUsb.libusb_submit_transfer(transfer) == LibUsb.Success)
            return;
        Interlocked.Decrement(ref _inFlight);
    }

    private void RecoverStalledEndpoint()
    {
        do
        {
            if (!_stopping && _fault is null)
            {
                int result = LibUsb.libusb_clear_halt(_handle, Vgb100Device.VbiEndpoint);
                if (result < 0)
                    Fail(new IOException($"VGB100 VBI endpoint stalled and could not be cleared: {LibUsb.ErrorName(result)}."));
                else if (Interlocked.Increment(ref _consecutiveTransferErrors) > MaxConsecutiveTransferErrors)
                    Fail(new IOException("VGB100 VBI endpoint keeps stalling."));
                else
                    Interlocked.Increment(ref _stallRecoveries);
            }
            while (_stalledTransfers.TryDequeue(out IntPtr transfer))
            {
                if (_stopping || _fault is not null || LibUsb.libusb_submit_transfer(transfer) != LibUsb.Success)
                    Interlocked.Decrement(ref _inFlight);
            }
            Volatile.Write(ref _recoveringStall, 0);
        } while (!_stalledTransfers.IsEmpty && Interlocked.Exchange(ref _recoveringStall, 1) == 0);
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _fault, exception, null) is null)
            _frames.Writer.TryComplete(exception);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return 0;
        if (_readFrame is null || _readOffset >= _readFrame.Length)
        {
            try
            {
                _readFrame = await _frames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                if (ex.InnerException is not null) throw ex.InnerException;
                return 0;
            }
            _readOffset = 0;
            Interlocked.Increment(ref _capturedFrames);
            try { RawFrameCaptured?.Invoke(_readFrame); }
            catch { }
        }
        int count = Math.Min(buffer.Length, _readFrame.Length - _readOffset);
        _readFrame.AsMemory(_readOffset, count).CopyTo(buffer);
        _readOffset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override unsafe void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }
        _disposed = true;
        _stopping = true;

        if (_handle != IntPtr.Zero && _device is not null)
        {
            try { _device.StopVbiStreaming(); } catch { }
            if (_videoParser is not null)
            {
                try { _device.StopVideoStreaming(); } catch { }
            }
        }
        foreach (IntPtr transfer in _transfers.Concat(_videoTransfers))
        {
            if (transfer != IntPtr.Zero) LibUsb.libusb_cancel_transfer(transfer);
        }
        if (_eventThread is not null)
            _eventThread.Join(TimeSpan.FromSeconds(3));
        else
            _inFlight = 0;

        if (Volatile.Read(ref _inFlight) == 0)
        {
            foreach (IntPtr transfer in _transfers.Concat(_videoTransfers))
            {
                if (transfer == IntPtr.Zero) continue;
                NativeMemory.Free((void*)((LibUsb.Transfer*)transfer)->Buffer);
                LibUsb.libusb_free_transfer(transfer);
            }
        }
        if (_self.IsAllocated && Volatile.Read(ref _inFlight) == 0) _self.Free();

        if (_handle != IntPtr.Zero)
        {
            try { _device?.CloseVbi(); } catch { }
            foreach (int interfaceNumber in _claimed)
                LibUsb.libusb_release_interface(_handle, interfaceNumber);
            LibUsb.libusb_close(_handle);
            _handle = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero && Volatile.Read(ref _inFlight) == 0)
        {
            LibUsb.libusb_exit(_context);
            _context = IntPtr.Zero;
        }
        _frames.Writer.TryComplete();
        base.Dispose(disposing);
    }

    private sealed unsafe class Transport(Vgb100VbiCaptureStream owner) : ICx231xxTransport
    {
        private const byte RequestTypeIn = LibUsb.EndpointIn | LibUsb.RequestTypeVendor | LibUsb.RecipientDevice;
        private const byte RequestTypeOut = LibUsb.EndpointOut | LibUsb.RequestTypeVendor | LibUsb.RecipientDevice;

        public int ControlIn(byte request, ushort value, ushort index, Span<byte> data)
        {
            fixed (byte* pointer = data)
                return LibUsb.libusb_control_transfer(owner._handle, RequestTypeIn, request, value, index,
                    pointer, (ushort)data.Length, ControlTimeoutMs);
        }

        public int ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data)
        {
            fixed (byte* pointer = data)
                return LibUsb.libusb_control_transfer(owner._handle, RequestTypeOut, request, value, index,
                    pointer, (ushort)data.Length, ControlTimeoutMs);
        }

        public void SetInterfaceAltSetting(int interfaceNumber, int alternateSetting)
        {
            if (!owner._claimed.Contains(interfaceNumber)) return;
            LibUsb.Check(LibUsb.libusb_set_interface_alt_setting(owner._handle, interfaceNumber, alternateSetting),
                $"set interface {interfaceNumber} alternate setting {alternateSetting}");
        }

        public void ClearHalt(byte endpoint) => LibUsb.libusb_clear_halt(owner._handle, endpoint);

        public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
    }
}
