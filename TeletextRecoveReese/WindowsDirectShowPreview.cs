using System.Runtime.InteropServices;
using DirectShowLib;

namespace TeletextRecoveReese;

internal sealed record DirectShowPreviewFrame(byte[] Bgra, int Width, int Height, int Stride);

internal sealed class WindowsDirectShowPreview : ISampleGrabberCB, IDisposable
{
    private readonly Action<DirectShowPreviewFrame> _frameReady;
    private IGraphBuilder? _graph;
    private ICaptureGraphBuilder2? _captureGraph;
    private IMediaControl? _mediaControl;
    private IBaseFilter? _source;
    private IBaseFilter? _crossbarFilter;
    private IBaseFilter? _sampleGrabberFilter;
    private IBaseFilter? _nullRenderer;
    private ISampleGrabber? _sampleGrabber;
    private int _width;
    private int _height;
    private int _stride;
    private bool _bottomUp;
    private bool _disposed;
    private long _lastFrameTick;

    public WindowsDirectShowPreview(
        string deviceName,
        DirectShowVideoInput input,
        DirectShowVideoStandard standard,
        Action<DirectShowPreviewFrame> frameReady)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DirectShow preview is available on Windows.");
        _frameReady = frameReady;
        try { Build(deviceName, input, standard); }
        catch { Dispose(); throw; }
    }

    private void Build(string deviceName, DirectShowVideoInput input, DirectShowVideoStandard standard)
    {
        DsDevice device = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"DirectShow device '{deviceName}' is no longer available.");

        _graph = (IGraphBuilder)new FilterGraph();
        _captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        DsError.ThrowExceptionForHR(_captureGraph.SetFiltergraph(_graph));
        _source = BindFilter(device);
        DsError.ThrowExceptionForHR(_graph.AddFilter(_source, device.Name));

        if (input.PinIndex is int inputPin)
        {
            _crossbarFilter = FindMatchingCrossbar(device.DevicePath);
            if (_crossbarFilter is not IAMCrossbar crossbar)
                throw new InvalidOperationException("The selected DirectShow crossbar is no longer available.");
            DsError.ThrowExceptionForHR(_graph.AddFilter(_crossbarFilter, "Video Crossbar"));
            DsError.ThrowExceptionForHR(crossbar.Route(input.OutputPinIndex, inputPin));

            IPin? crossbarOutput = DsFindPin.ByDirection(_crossbarFilter, PinDirection.Output, input.OutputPinIndex);
            IPin? captureInput = DsFindPin.ByDirection(_source, PinDirection.Input, 0);
            if (crossbarOutput is null || captureInput is null)
                throw new InvalidOperationException("Could not find the crossbar-to-capture video pins.");
            try { DsError.ThrowExceptionForHR(_graph.Connect(crossbarOutput, captureInput)); }
            finally { Release(crossbarOutput); Release(captureInput); }
        }

        if (standard.Value != AnalogVideoStandard.None && _source is IAMAnalogVideoDecoder decoder)
            DsError.ThrowExceptionForHR(decoder.put_TVFormat(standard.Value));

        _sampleGrabber = (ISampleGrabber)new SampleGrabber();
        _sampleGrabberFilter = (IBaseFilter)_sampleGrabber;
        _nullRenderer = (IBaseFilter)new NullRenderer();
        DsError.ThrowExceptionForHR(_graph.AddFilter(_sampleGrabberFilter, "Preview Sample Grabber"));
        DsError.ThrowExceptionForHR(_graph.AddFilter(_nullRenderer, "Preview Null Renderer"));

        var requestedType = new AMMediaType
        {
            majorType = MediaType.Video,
            subType = MediaSubType.RGB32,
            formatType = FormatType.VideoInfo,
        };
        DsError.ThrowExceptionForHR(_sampleGrabber.SetMediaType(requestedType));
        DsUtils.FreeAMMediaType(requestedType);
        DsError.ThrowExceptionForHR(_sampleGrabber.SetOneShot(false));
        DsError.ThrowExceptionForHR(_sampleGrabber.SetBufferSamples(false));
        DsError.ThrowExceptionForHR(_sampleGrabber.SetCallback(this, 1));

        DsError.ThrowExceptionForHR(_captureGraph.RenderStream(
            PinCategory.Capture, MediaType.Video, _source,
            _sampleGrabberFilter, _nullRenderer));

        var connectedType = new AMMediaType();
        DsError.ThrowExceptionForHR(_sampleGrabber.GetConnectedMediaType(connectedType));
        try
        {
            if (connectedType.formatType != FormatType.VideoInfo || connectedType.formatPtr == IntPtr.Zero)
                throw new InvalidOperationException("The DirectShow preview did not negotiate VIDEOINFOHEADER/RGB32.");
            VideoInfoHeader header = Marshal.PtrToStructure<VideoInfoHeader>(connectedType.formatPtr)
                ?? throw new InvalidOperationException("DirectShow returned an empty VIDEOINFOHEADER.");
            _width = Math.Abs(header.BmiHeader.Width);
            _height = Math.Abs(header.BmiHeader.Height);
            _bottomUp = header.BmiHeader.Height > 0;
            _stride = checked(_width * 4);
        }
        finally { DsUtils.FreeAMMediaType(connectedType); }

        _mediaControl = (IMediaControl)_graph;
        DsError.ThrowExceptionForHR(_mediaControl.Run());
    }

    public int SampleCB(double sampleTime, IMediaSample sample) => 0;

    public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen)
    {
        if (_disposed || _width <= 0 || _height <= 0 || bufferLen < _stride * _height) return 0;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFrameTick) < 40) return 0;
        Interlocked.Exchange(ref _lastFrameTick, now);

        var pixels = new byte[_stride * _height];
        if (!_bottomUp)
        {
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        else
        {
            for (int row = 0; row < _height; row++)
                Marshal.Copy(IntPtr.Add(buffer, (_height - row - 1) * _stride), pixels, row * _stride, _stride);
        }
        _frameReady(new DirectShowPreviewFrame(pixels, _width, _height, _stride));
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sampleGrabber?.SetCallback(null!, 0); } catch { }
        try { _mediaControl?.Stop(); } catch { }
        Release(_nullRenderer);
        Release(_sampleGrabberFilter);
        Release(_crossbarFilter);
        Release(_source);
        Release(_captureGraph);
        Release(_graph);
        _nullRenderer = null;
        _sampleGrabberFilter = null;
        _crossbarFilter = null;
        _source = null;
        _captureGraph = null;
        _graph = null;
        _sampleGrabber = null;
        _mediaControl = null;
    }

    private static IBaseFilter FindMatchingCrossbar(string capturePath)
    {
        string hardwareKey = HardwareInstanceKey(capturePath);
        DsDevice device = DsDevice.GetDevicesOfCat(FilterCategory.AMKSCrossbar)
            .OrderByDescending(candidate => HardwareInstanceKey(candidate.DevicePath).Equals(
                hardwareKey, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(candidate => BindSupportsCrossbar(candidate))
            ?? throw new InvalidOperationException("No IAMCrossbar filter was found for this capture device.");
        return BindFilter(device);
    }

    private static bool BindSupportsCrossbar(DsDevice device)
    {
        IBaseFilter? filter = null;
        try { filter = BindFilter(device); return filter is IAMCrossbar; }
        catch { return false; }
        finally { Release(filter); }
    }

    private static IBaseFilter BindFilter(DsDevice device)
    {
        Guid iid = typeof(IBaseFilter).GUID;
        device.Mon.BindToObject(null!, null!, ref iid, out object filter);
        return (IBaseFilter)filter;
    }

    private static string HardwareInstanceKey(string path)
    {
        int category = path.IndexOf("#{", StringComparison.Ordinal);
        return category >= 0 ? path[..category] : path;
    }

    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
