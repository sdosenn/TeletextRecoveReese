using System.Runtime.InteropServices;
using System.Threading.Channels;
using DirectShowLib;
using TeletextRecoveReese.Core;

namespace TeletextRecoveReese;

internal sealed class WindowsDirectShowVbiCaptureStream : LiveVbiCaptureStream, ISampleGrabberCB
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VbiInfoHeader
    {
        public uint StartLine, EndLine, SamplingFrequency;
        public uint MinLineStartTime, MaxLineStartTime, ActualLineStartTime, ActualLineEndTime;
        public uint VideoStandard, SamplesPerLine, StrideInBytes, BufferSize;
        public uint Reserved;
    }

    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly object _fieldLock = new();
    private IGraphBuilder? _graph;
    private ICaptureGraphBuilder2? _captureGraph;
    private IMediaControl? _mediaControl;
    private IBaseFilter? _source, _crossbarFilter, _grabberFilter, _nullRenderer;
    private ISampleGrabber? _grabber;
    private IBaseFilter? _videoGrabberFilter, _videoNullRenderer;
    private ISampleGrabber? _videoGrabber;
    private VideoCallback? _videoCallback;
    private int _videoWidth, _videoHeight, _videoStride;
    private bool _videoBottomUp;
    private byte[]? _firstField;
    private byte[]? _readFrame;
    private int _readOffset;
    private int _fieldLines;
    private int _driverStride;
    private int _driverSamplesPerLine;
    private uint _samplingRate;
    private int _samplesPerLine;
    private long _capturedFrames;
    private bool _disposed;

    public override uint SamplingRate => _samplingRate;
    public override int SamplesPerLine => _samplesPerLine;
    public override int FirstFieldLines => _fieldLines;
    public override int SecondFieldLines => _fieldLines;
    public override long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public override Action<byte[]>? RawFrameCaptured { get; set; }
    public Action<DirectShowPreviewFrame>? VideoFrameCaptured { get; set; }

    public WindowsDirectShowVbiCaptureStream(
        string deviceName, DirectShowVideoInput input, DirectShowVideoStandard standard,
        int configuredLineLength, int configuredFieldLines,
        bool enableVideoPreview = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DirectShow VBI capture is available on Windows.");
        try { Build(deviceName, input, standard, configuredLineLength, configuredFieldLines, enableVideoPreview); }
        catch { Dispose(); throw; }
    }

    private void Build(
        string deviceName, DirectShowVideoInput input, DirectShowVideoStandard standard,
        int configuredLineLength, int configuredFieldLines, bool enableVideoPreview)
    {
        DsDevice device = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"DirectShow device '{deviceName}' is no longer available.");
        _graph = (IGraphBuilder)new FilterGraph();
        _captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        DsError.ThrowExceptionForHR(_captureGraph.SetFiltergraph(_graph));
        _source = WindowsDirectShowCapture.BindFilter(device);
        DsError.ThrowExceptionForHR(_graph.AddFilter(_source, device.Name));

        if (input.PinIndex is int inputPin)
        {
            _crossbarFilter = WindowsDirectShowCapture.FindCrossbarFilter(device.Name, device.DevicePath)
                ?? throw new InvalidOperationException("The selected DirectShow crossbar is unavailable.");
            DsError.ThrowExceptionForHR(_graph.AddFilter(_crossbarFilter, "Video Crossbar"));
            var crossbar = (IAMCrossbar)_crossbarFilter;
            DsError.ThrowExceptionForHR(crossbar.Route(input.OutputPinIndex, inputPin));
            IPin? xbarOut = DsFindPin.ByDirection(_crossbarFilter, PinDirection.Output, input.OutputPinIndex);
            IPin? analogIn = DsFindPin.ByDirection(_source, PinDirection.Input, 0);
            if (xbarOut is null || analogIn is null)
                throw new InvalidOperationException("Could not locate the analog crossbar connection pins.");
            try { DsError.ThrowExceptionForHR(_graph.Connect(xbarOut, analogIn)); }
            finally { Release(xbarOut); Release(analogIn); }
        }

        if (standard.Value != AnalogVideoStandard.None && _source is IAMAnalogVideoDecoder decoder)
            DsError.ThrowExceptionForHR(decoder.put_TVFormat(standard.Value));

        IPin vbiPin = DsFindPin.ByCategory(_source, PinCategory.VBI, 0)
            ?? throw new InvalidOperationException("The DirectShow VBI output pin disappeared while opening capture.");
        AMMediaType rawType = SelectRawVbiType(vbiPin, configuredFieldLines);
        ReadFormat(rawType, configuredLineLength);

        _grabber = (ISampleGrabber)new SampleGrabber();
        _grabberFilter = (IBaseFilter)_grabber;
        _nullRenderer = (IBaseFilter)new NullRenderer();
        DsError.ThrowExceptionForHR(_graph.AddFilter(_grabberFilter, "Raw VBI Sample Grabber"));
        DsError.ThrowExceptionForHR(_graph.AddFilter(_nullRenderer, "Raw VBI Null Renderer"));
        try { DsError.ThrowExceptionForHR(_grabber.SetMediaType(rawType)); }
        finally { DsUtils.FreeAMMediaType(rawType); }
        DsError.ThrowExceptionForHR(_grabber.SetOneShot(false));
        DsError.ThrowExceptionForHR(_grabber.SetBufferSamples(false));
        DsError.ThrowExceptionForHR(_grabber.SetCallback(this, 1));

        IPin? grabberIn = DsFindPin.ByDirection(_grabberFilter, PinDirection.Input, 0);
        IPin? grabberOut = DsFindPin.ByDirection(_grabberFilter, PinDirection.Output, 0);
        IPin? rendererIn = DsFindPin.ByDirection(_nullRenderer, PinDirection.Input, 0);
        try
        {
            if (grabberIn is null || grabberOut is null || rendererIn is null)
                throw new InvalidOperationException("Could not create the DirectShow VBI sample path.");
            DsError.ThrowExceptionForHR(_graph.Connect(vbiPin, grabberIn));
        DsError.ThrowExceptionForHR(_graph.Connect(grabberOut, rendererIn));
        }
        finally
        {
            Release(vbiPin); Release(grabberIn); Release(grabberOut); Release(rendererIn);
        }

        if (enableVideoPreview)
            BuildVideoBranch();

        _mediaControl = (IMediaControl)_graph;
        DsError.ThrowExceptionForHR(_mediaControl.Run());
    }

    private void BuildVideoBranch()
    {
        _videoGrabber = (ISampleGrabber)new SampleGrabber();
        _videoGrabberFilter = (IBaseFilter)_videoGrabber;
        _videoNullRenderer = (IBaseFilter)new NullRenderer();
        DsError.ThrowExceptionForHR(_graph!.AddFilter(_videoGrabberFilter, "Live Video Sample Grabber"));
        DsError.ThrowExceptionForHR(_graph.AddFilter(_videoNullRenderer, "Live Video Null Renderer"));
        var media = new AMMediaType
        {
            majorType = MediaType.Video,
            subType = MediaSubType.RGB32,
            formatType = FormatType.VideoInfo,
        };
        try { DsError.ThrowExceptionForHR(_videoGrabber.SetMediaType(media)); }
        finally { DsUtils.FreeAMMediaType(media); }
        _videoCallback = new VideoCallback(this);
        DsError.ThrowExceptionForHR(_videoGrabber.SetOneShot(false));
        DsError.ThrowExceptionForHR(_videoGrabber.SetBufferSamples(false));
        DsError.ThrowExceptionForHR(_videoGrabber.SetCallback(_videoCallback, 1));
        DsError.ThrowExceptionForHR(_captureGraph!.RenderStream(
            PinCategory.Capture, MediaType.Video, _source!,
            _videoGrabberFilter, _videoNullRenderer));

        var connected = new AMMediaType();
        DsError.ThrowExceptionForHR(_videoGrabber.GetConnectedMediaType(connected));
        try
        {
            if (connected.formatType != FormatType.VideoInfo || connected.formatPtr == IntPtr.Zero)
                throw new InvalidOperationException("The live video branch did not negotiate RGB32 VIDEOINFOHEADER.");
            VideoInfoHeader header = Marshal.PtrToStructure<VideoInfoHeader>(connected.formatPtr)
                ?? throw new InvalidOperationException("The live video branch returned an empty VIDEOINFOHEADER.");
            _videoWidth = Math.Abs(header.BmiHeader.Width);
            _videoHeight = Math.Abs(header.BmiHeader.Height);
            _videoStride = checked(_videoWidth * 4);
            _videoBottomUp = header.BmiHeader.Height > 0;
        }
        finally { DsUtils.FreeAMMediaType(connected); }
    }

    private int OnVideoBuffer(IntPtr buffer, int bufferLen)
    {
        if (_disposed || VideoFrameCaptured is null || bufferLen < _videoStride * _videoHeight) return 0;
        var pixels = new byte[_videoStride * _videoHeight];
        for (int row = 0; row < _videoHeight; row++)
        {
            int sourceRow = _videoBottomUp ? _videoHeight - row - 1 : row;
            Marshal.Copy(IntPtr.Add(buffer, sourceRow * _videoStride), pixels, row * _videoStride, _videoStride);
        }
        try { VideoFrameCaptured(new DirectShowPreviewFrame(pixels, _videoWidth, _videoHeight, _videoStride)); }
        catch { }
        return 0;
    }

    private sealed class VideoCallback(WindowsDirectShowVbiCaptureStream owner) : ISampleGrabberCB
    {
        public int SampleCB(double sampleTime, IMediaSample sample) => 0;
        public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen) => owner.OnVideoBuffer(buffer, bufferLen);
    }

    private static AMMediaType SelectRawVbiType(IPin pin, int configuredFieldLines)
    {
        DsError.ThrowExceptionForHR(pin.EnumMediaTypes(out IEnumMediaTypes types));
        AMMediaType? best = null;
        int bestDifference = int.MaxValue;
        try
        {
            var values = new AMMediaType[1];
            while (types.Next(1, values, IntPtr.Zero) == 0)
            {
                AMMediaType value = values[0];
                if (value.majorType != MediaType.VBI || value.formatPtr == IntPtr.Zero
                    || value.formatSize < Marshal.SizeOf<VbiInfoHeader>())
                {
                    DsUtils.FreeAMMediaType(value);
                    continue;
                }
                VbiInfoHeader format = Marshal.PtrToStructure<VbiInfoHeader>(value.formatPtr);
                int lines = checked((int)(format.EndLine - format.StartLine + 1));
                int difference = Math.Abs(lines - configuredFieldLines);
                if (difference < bestDifference)
                {
                    if (best is not null) DsUtils.FreeAMMediaType(best);
                    best = value;
                    bestDifference = difference;
                }
                else
                {
                    DsUtils.FreeAMMediaType(value);
                }
            }
        }
        finally { Release(types); }
        return best
            ?? throw new NotSupportedException("The VBI pin does not advertise a raw VBI waveform format.");
    }

    private void ReadFormat(AMMediaType type, int configuredLineLength)
    {
        VbiInfoHeader format = Marshal.PtrToStructure<VbiInfoHeader>(type.formatPtr);
        _samplingRate = format.SamplingFrequency;
        _driverSamplesPerLine = checked((int)format.SamplesPerLine);
        _driverStride = checked((int)format.StrideInBytes);
        // Stride locates consecutive driver rows; it is not the requested
        // analysis width. Respect the selected capture-card profile exactly.
        _samplesPerLine = configuredLineLength > 0
            ? configuredLineLength
            : _driverSamplesPerLine;
        _fieldLines = checked((int)(format.EndLine - format.StartLine + 1));
        if (SamplingRate == 0 || _driverSamplesPerLine <= 0
            || _driverStride < _driverSamplesPerLine || SamplesPerLine <= 0 || _fieldLines <= 0)
            throw new InvalidDataException("The DirectShow driver returned an invalid raw VBI format.");
    }

    public int SampleCB(double sampleTime, IMediaSample sample) => 0;

    public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen)
    {
        if (_disposed || bufferLen < _driverStride * _fieldLines) return 0;
        var field = new byte[SamplesPerLine * _fieldLines];
        for (int line = 0; line < _fieldLines; line++)
        {
            int destination = line * SamplesPerLine;
            int copied = Math.Min(_driverStride, SamplesPerLine);
            Marshal.Copy(IntPtr.Add(buffer, line * _driverStride), field, destination, copied);
            byte flatLevel = copied > 0 ? field[destination + copied - 1] : (byte)0;
            field.AsSpan(destination + copied, SamplesPerLine - copied).Fill(flatLevel);
        }
        lock (_fieldLock)
        {
            if (_firstField is null) { _firstField = field; return 0; }
            var frame = new byte[_firstField.Length + field.Length];
            Buffer.BlockCopy(_firstField, 0, frame, 0, _firstField.Length);
            Buffer.BlockCopy(field, 0, frame, _firstField.Length, field.Length);
            _firstField = null;
            Interlocked.Increment(ref _capturedFrames);
            try { RawFrameCaptured?.Invoke(frame); } catch { }
            _frames.Writer.TryWrite(frame);
        }
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0) return 0;
        if (_readFrame is null || _readOffset >= _readFrame.Length)
        {
            try { _readFrame = await _frames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (ChannelClosedException) { return 0; }
            _readOffset = 0;
        }
        int count = Math.Min(destination.Length, _readFrame.Length - _readOffset);
        _readFrame.AsMemory(_readOffset, count).CopyTo(destination);
        _readOffset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        _frames.Writer.TryComplete();
        try { _grabber?.SetCallback(null!, 0); } catch { }
        try { _videoGrabber?.SetCallback(null!, 0); } catch { }
        try { _mediaControl?.Stop(); } catch { }
        Release(_videoNullRenderer); Release(_videoGrabberFilter);
        Release(_nullRenderer); Release(_grabberFilter); Release(_crossbarFilter);
        Release(_source); Release(_captureGraph); Release(_graph);
        _videoNullRenderer = _videoGrabberFilter = null;
        _nullRenderer = _grabberFilter = _crossbarFilter = _source = null;
        _captureGraph = null; _graph = null; _grabber = null; _mediaControl = null;
        _videoGrabber = null; _videoCallback = null; VideoFrameCaptured = null;
        base.Dispose(disposing);
    }

    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
