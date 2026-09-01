using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TeletextRecoveReese.Core;

public sealed record LinuxV4l2Input(int Index, string Name, ulong SupportedStandards)
{
    public override string ToString() => Name;
}

public sealed record LinuxV4l2Standard(
    ulong Id,
    string Name,
    uint FramePeriodNumerator,
    uint FramePeriodDenominator,
    uint FrameLines)
{
    public override string ToString() => Name;
}

public sealed record LinuxV4l2DeviceInfo(
    string Path,
    string Driver,
    string Card,
    string BusInfo,
    IReadOnlyList<LinuxV4l2Input> Inputs,
    int CurrentInputIndex,
    IReadOnlyList<LinuxV4l2Standard> Standards,
    ulong CurrentStandardId);

public sealed record LinuxV4l2DeviceIdentity(
    string Path,
    string Driver,
    string Card,
    string BusInfo,
    uint DeviceCapabilities);

public sealed record LinuxV4l2VideoFrame(
    int Width,
    int Height,
    uint PixelFormat,
    uint Field,
    int BytesPerLine,
    byte[] Data);

public sealed class LinuxVbiCaptureStream : Stream
{
    private const uint VbiCaptureType = 4;
    private const ulong VideoQueryCapabilities = 0x80685600;
    private const ulong VideoEnumerateStandard = 0xC0485619;
    private const ulong VideoGetStandard = 0x80085617;
    private const ulong VideoSetStandard = 0x40085618;
    private const ulong VideoEnumerateInput = 0xC050561A;
    private const ulong VideoGetInput = 0x80045626;
    private const ulong VideoSetInput = 0xC0045627;
    private const ulong VideoGetFormat = 0xC0D05604;
    private const ulong VideoSetFormat = 0xC0D05605;
    private const uint YuyvFourCc = 0x56595559; // YUYV
    private const uint GreyFourCc = 0x59455247; // GREY
    private readonly FileStream _device;
    private readonly byte[] _frame;
    private int _frameOffset;
    private int _frameLength;
    private long _capturedFrames;

    public uint SamplingRate { get; }
    public int SamplesPerLine { get; }
    public int FirstFieldLines { get; }
    public int SecondFieldLines { get; }
    public int LinesPerFrame => FirstFieldLines + SecondFieldLines;
    public long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public uint SampleFormat { get; }
    public Action<byte[]>? RawFrameCaptured { get; set; }

    public LinuxVbiCaptureStream(string path, int? inputIndex = null, ulong? standardId = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Raw V4L2 VBI capture is available on Linux.");
        _device = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous);
        if (inputIndex is int selectedInput)
            SetIntegerIoctl(_device.SafeFileHandle, VideoSetInput, selectedInput, "VIDIOC_S_INPUT", path);
        if (standardId is ulong selectedStandard)
            SetUInt64Ioctl(_device.SafeFileHandle, VideoSetStandard, selectedStandard, "VIDIOC_S_STD", path);

        byte[] format = new byte[208];
        BitConverter.TryWriteBytes(format.AsSpan(0, 4), VbiCaptureType);
        GCHandle pinned = GCHandle.Alloc(format, GCHandleType.Pinned);
        try
        {
            int result = ioctl(
                _device.SafeFileHandle.DangerousGetHandle().ToInt32(),
                VideoGetFormat,
                pinned.AddrOfPinnedObject());
            if (result < 0)
                throw new IOException($"VIDIOC_G_FMT failed for {path} (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally { pinned.Free(); }

        // The v4l2_format union starts at offset 8 on Linux. The raw VBI member
        // contains sampling rate, samples/line, format, starts and field counts.
        SamplingRate = BitConverter.ToUInt32(format, 8);
        SamplesPerLine = checked((int)BitConverter.ToUInt32(format, 16));
        SampleFormat = BitConverter.ToUInt32(format, 20);
        FirstFieldLines = checked((int)BitConverter.ToUInt32(format, 32));
        SecondFieldLines = checked((int)BitConverter.ToUInt32(format, 36));
        if (SamplesPerLine <= 0 || LinesPerFrame <= 0)
            throw new InvalidDataException("The V4L2 device returned an invalid raw VBI format.");
        if (SampleFormat != GreyFourCc)
            throw new NotSupportedException(
                $"The VBI device uses unsupported sample format 0x{SampleFormat:X8}; GREY 8-bit samples are required.");
        _frame = new byte[checked(SamplesPerLine * LinesPerFrame)];
    }

    public static LinuxV4l2DeviceInfo QueryDevice(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("V4L2 device discovery is available on Linux.");

        using var device = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.None);
        SafeFileHandle handle = device.SafeFileHandle;

        byte[] capabilities = InvokeBufferIoctl(handle, VideoQueryCapabilities, new byte[104], "VIDIOC_QUERYCAP", path);
        string driver = ReadCString(capabilities, 0, 16);
        string card = ReadCString(capabilities, 16, 32);
        string busInfo = ReadCString(capabilities, 48, 32);

        int currentInput = GetIntegerIoctl(handle, VideoGetInput, "VIDIOC_G_INPUT", path);
        var inputs = new List<LinuxV4l2Input>();
        for (int index = 0; ; index++)
        {
            var buffer = new byte[80];
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), index);
            if (!TryBufferIoctl(handle, VideoEnumerateInput, buffer, out int error))
            {
                if (error == 22) break;
                throw new IOException($"VIDIOC_ENUMINPUT failed for {path} (errno {error}).");
            }
            inputs.Add(new LinuxV4l2Input(
                index,
                ReadCString(buffer, 4, 32),
                BitConverter.ToUInt64(buffer, 48)));
        }

        ulong currentStandard = GetUInt64Ioctl(handle, VideoGetStandard, "VIDIOC_G_STD", path);
        var standards = new List<LinuxV4l2Standard>();
        for (int index = 0; ; index++)
        {
            var buffer = new byte[72];
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), index);
            if (!TryBufferIoctl(handle, VideoEnumerateStandard, buffer, out int error))
            {
                if (error == 22) break;
                throw new IOException($"VIDIOC_ENUMSTD failed for {path} (errno {error}).");
            }
            standards.Add(new LinuxV4l2Standard(
                BitConverter.ToUInt64(buffer, 8),
                ReadCString(buffer, 16, 24),
                BitConverter.ToUInt32(buffer, 40),
                BitConverter.ToUInt32(buffer, 44),
                BitConverter.ToUInt32(buffer, 48)));
        }

        return new LinuxV4l2DeviceInfo(
            path, driver, card, busInfo, inputs, currentInput, standards, currentStandard);
    }

    public static LinuxV4l2DeviceIdentity QueryIdentity(string path)
    {
        using var device = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.None);
        byte[] capabilities = InvokeBufferIoctl(
            device.SafeFileHandle, VideoQueryCapabilities, new byte[104], "VIDIOC_QUERYCAP", path);
        uint allCapabilities = BitConverter.ToUInt32(capabilities, 84);
        uint deviceCapabilities = (allCapabilities & 0x80000000) != 0
            ? BitConverter.ToUInt32(capabilities, 88)
            : allCapabilities;
        return new LinuxV4l2DeviceIdentity(
            path,
            ReadCString(capabilities, 0, 16),
            ReadCString(capabilities, 16, 32),
            ReadCString(capabilities, 48, 32),
            deviceCapabilities);
    }

    public static void ConfigureDevice(string path, int inputIndex, ulong standardId)
    {
        using var device = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.None);
        SetIntegerIoctl(device.SafeFileHandle, VideoSetInput, inputIndex, "VIDIOC_S_INPUT", path);
        SetUInt64Ioctl(device.SafeFileHandle, VideoSetStandard, standardId, "VIDIOC_S_STD", path);
    }

    public static async Task<LinuxV4l2VideoFrame> CaptureVideoFrameAsync(
        string path,
        CancellationToken cancellationToken)
    {
        LinuxV4l2VideoFrame? captured = null;
        await CaptureVideoFramesAsync(
            path,
            TimeSpan.Zero,
            frame =>
            {
                captured = frame;
                return false;
            },
            cancellationToken).ConfigureAwait(false);
        return captured ?? throw new EndOfStreamException($"{path} did not return a video frame.");
    }

    public static async Task CaptureVideoFramesAsync(
        string path,
        TimeSpan snapshotInterval,
        Func<LinuxV4l2VideoFrame, bool> receiveFrame,
        CancellationToken cancellationToken)
    {
        var device = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous);
        using (device)
        using (cancellationToken.Register(() =>
               {
                   try { device.Dispose(); } catch { }
               }))
        {
            var format = new byte[208];
            BitConverter.TryWriteBytes(format.AsSpan(0, 4), 1u); // V4L2_BUF_TYPE_VIDEO_CAPTURE
            InvokeBufferIoctl(device.SafeFileHandle, VideoGetFormat, format, "VIDIOC_G_FMT", path);

            // Prefer packed 4:2:2 for snapshots. Several older analogue capture
            // drivers expose interlaced YU12 with a driver-specific chroma layout;
            // luma then looks correct while ordinary planar conversion produces
            // bright green horizontal bands. Packed YUYV has no separate chroma
            // planes and is both cheap and unambiguous to convert locally.
            if (BitConverter.ToUInt32(format, 16) != YuyvFourCc)
            {
                var packedFormat = (byte[])format.Clone();
                BitConverter.TryWriteBytes(packedFormat.AsSpan(16, 4), YuyvFourCc);
                BitConverter.TryWriteBytes(packedFormat.AsSpan(24, 4), 0u); // bytesperline: driver chooses
                BitConverter.TryWriteBytes(packedFormat.AsSpan(28, 4), 0u); // sizeimage: driver chooses
                if (TryBufferIoctl(device.SafeFileHandle, VideoSetFormat, packedFormat, out _) &&
                    BitConverter.ToUInt32(packedFormat, 16) == YuyvFourCc)
                {
                    format = packedFormat;
                }
            }

            int width = checked((int)BitConverter.ToUInt32(format, 8));
            int height = checked((int)BitConverter.ToUInt32(format, 12));
            uint pixelFormat = BitConverter.ToUInt32(format, 16);
            uint field = BitConverter.ToUInt32(format, 20);
            int bytesPerLine = checked((int)BitConverter.ToUInt32(format, 24));
            int imageSize = checked((int)BitConverter.ToUInt32(format, 28));
            if (width <= 0 || height <= 0 || imageSize <= 0)
                throw new InvalidDataException($"{path} returned an invalid video capture format.");

            var frame = new byte[imageSize];
            long nextSnapshot = 0;
            long intervalTicks = Math.Max(
                0, (long)(snapshotInterval.TotalSeconds * Stopwatch.Frequency));
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    // A V4L2 read represents one frame. Never concatenate short
                    // reads: that can splice two frames and shifts the U/V planes,
                    // which appears as intermittent green horizontal bands.
                    read = await device.ReadAsync(frame, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                if (read == 0)
                    throw new EndOfStreamException($"{path} stopped returning video frames.");

                int requiredBytes = pixelFormat switch
                {
                    YuyvFourCc => checked(bytesPerLine * height),
                    _ => imageSize,
                };
                if (read < requiredBytes)
                    continue;

                long now = Stopwatch.GetTimestamp();
                if (now < nextSnapshot)
                    continue;

                byte[] snapshot = frame.AsSpan(0, read).ToArray();
                if (!receiveFrame(new LinuxV4l2VideoFrame(
                        width, height, pixelFormat, field, bytesPerLine, snapshot)))
                    return;
                nextSnapshot = now + intervalTicks;
            }
        }
    }

    private static byte[] InvokeBufferIoctl(
        SafeFileHandle handle, ulong request, byte[] buffer, string operation, string path)
    {
        if (!TryBufferIoctl(handle, request, buffer, out int error))
            throw new IOException($"{operation} failed for {path} (errno {error}).");
        return buffer;
    }

    private static bool TryBufferIoctl(
        SafeFileHandle handle, ulong request, byte[] buffer, out int error)
    {
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            int result = ioctl(handle.DangerousGetHandle().ToInt32(), request, pinned.AddrOfPinnedObject());
            error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            return result >= 0;
        }
        finally { pinned.Free(); }
    }

    private static int GetIntegerIoctl(
        SafeFileHandle handle, ulong request, string operation, string path)
    {
        byte[] buffer = InvokeBufferIoctl(handle, request, new byte[4], operation, path);
        return BitConverter.ToInt32(buffer, 0);
    }

    private static ulong GetUInt64Ioctl(
        SafeFileHandle handle, ulong request, string operation, string path)
    {
        byte[] buffer = InvokeBufferIoctl(handle, request, new byte[8], operation, path);
        return BitConverter.ToUInt64(buffer, 0);
    }

    private static void SetIntegerIoctl(
        SafeFileHandle handle, ulong request, int value, string operation, string path)
    {
        var buffer = new byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        InvokeBufferIoctl(handle, request, buffer, operation, path);
    }

    private static void SetUInt64Ioctl(
        SafeFileHandle handle, ulong request, ulong value, string operation, string path)
    {
        var buffer = new byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        InvokeBufferIoctl(handle, request, buffer, operation, path);
    }

    private static string ReadCString(byte[] buffer, int offset, int length)
    {
        int end = Array.IndexOf(buffer, (byte)0, offset, length);
        if (end < 0) end = offset + length;
        return System.Text.Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_frameOffset >= _frameLength)
        {
            _frameOffset = 0;
            _frameLength = 0;
            while (_frameLength < _frame.Length)
            {
                int read = await _device.ReadAsync(
                    _frame.AsMemory(_frameLength), cancellationToken).ConfigureAwait(false);
                if (read == 0) return 0;
                _frameLength += read;
            }
            Interlocked.Increment(ref _capturedFrames);
            // The callback runs synchronously before this frame buffer can be
            // refilled, so consumers can render it without an extra 25 fps clone.
            try { RawFrameCaptured?.Invoke(_frame); }
            catch { }
        }
        int count = Math.Min(buffer.Length, _frameLength - _frameOffset);
        _frame.AsMemory(_frameOffset, count).CopyTo(buffer);
        _frameOffset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _device.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr argument);
}
