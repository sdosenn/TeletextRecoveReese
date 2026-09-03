using System.Runtime.InteropServices;
using DirectShowLib;

namespace TeletextRecoveReese;

internal sealed record DirectShowVideoInput(int? PinIndex, int OutputPinIndex, string Name)
{
    public override string ToString() => Name;
}

internal sealed record DirectShowVideoStandard(AnalogVideoStandard Value, string Name)
{
    public override string ToString() => Name;
}

internal sealed record DirectShowDeviceInfo(
    string Name,
    IReadOnlyList<DirectShowVideoInput> Inputs,
    IReadOnlyList<DirectShowVideoStandard> Standards,
    int? CurrentInputPin,
    AnalogVideoStandard CurrentStandard,
    bool HasVbiPin,
    string? VbiPinName);

/// <summary>
/// Small DirectShow discovery layer. The capture graph builder is important here:
/// USB analogue cards commonly expose IAMCrossbar on an upstream filter rather
/// than on the video-capture filter itself.
/// </summary>
internal static class WindowsDirectShowCapture
{
    public static IReadOnlyList<string> DiscoverDeviceNames()
    {
        EnsureWindows();
        return DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
            .Select(device => device.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static DirectShowDeviceInfo QueryDevice(string friendlyName)
    {
        EnsureWindows();
        DsDevice device = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Name, friendlyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"DirectShow device '{friendlyName}' is no longer available.");

        IGraphBuilder? graph = null;
        ICaptureGraphBuilder2? captureGraph = null;
        IBaseFilter? source = null;
        IBaseFilter? crossbarFilter = null;
        object? crossbarObject = null;
        object? decoderObject = null;
        try
        {
            graph = (IGraphBuilder)new FilterGraph();
            captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(captureGraph.SetFiltergraph(graph));
            Guid filterId = typeof(IBaseFilter).GUID;
            device.Mon.BindToObject(null!, null!, ref filterId, out object sourceObject);
            source = (IBaseFilter)sourceObject;
            DsError.ThrowExceptionForHR(graph.AddFilter(source, device.Name));

            IAMCrossbar? crossbar = null;
            int hr = captureGraph.FindInterface(
                PinCategory.Capture, MediaType.Video, source,
                typeof(IAMCrossbar).GUID, out crossbarObject);
            if (hr >= 0) crossbar = crossbarObject as IAMCrossbar;

            // Many WDM drivers (including Philips/NXP 713x) register the
            // crossbar as a separate device filter. Until graph pins have been
            // connected FindInterface cannot walk from the capture filter to it,
            // so bind the best matching AMKSCrossbar device directly.
            if (crossbar is null)
            {
                crossbarFilter = FindCrossbarFilter(device.Name, device.DevicePath);
                crossbar = crossbarFilter as IAMCrossbar;
            }

            var inputs = new List<DirectShowVideoInput>();
            int? currentInput = null;
            if (crossbar is not null)
            {
                DsError.ThrowExceptionForHR(crossbar.get_PinCounts(out int outputCount, out int inputCount));
                for (int output = 0; output < outputCount; output++)
                {
                    crossbar.get_CrossbarPinInfo(false, output, out _, out PhysicalConnectorType outputType);
                    if (!IsVideoConnector(outputType)) continue;
                    if (crossbar.get_IsRoutedTo(output, out int routedInput) >= 0)
                        currentInput = routedInput;
                    for (int input = 0; input < inputCount; input++)
                    {
                        if (crossbar.CanRoute(output, input) < 0) continue;
                        if (crossbar.get_CrossbarPinInfo(true, input, out _, out PhysicalConnectorType type) < 0
                            || !IsVideoConnector(type)) continue;
                        inputs.Add(new DirectShowVideoInput(input, output, ConnectorName(type)));
                    }
                    if (inputs.Count > 0) break;
                }
            }
            if (inputs.Count == 0)
                inputs.Add(new DirectShowVideoInput(null, -1, "Device default (no DirectShow crossbar)"));

            IAMAnalogVideoDecoder? decoder = source as IAMAnalogVideoDecoder;
            if (decoder is null)
            {
                hr = captureGraph.FindInterface(
                    PinCategory.Capture, MediaType.Video, source,
                    typeof(IAMAnalogVideoDecoder).GUID, out decoderObject);
                if (hr >= 0) decoder = decoderObject as IAMAnalogVideoDecoder;
            }

            var standards = new List<DirectShowVideoStandard>();
            AnalogVideoStandard currentStandard = AnalogVideoStandard.None;
            if (decoder is not null)
            {
                decoder.get_TVFormat(out currentStandard);
                if (decoder.get_AvailableTVFormats(out AnalogVideoStandard available) >= 0)
                {
                    foreach (AnalogVideoStandard value in Enum.GetValues<AnalogVideoStandard>())
                    {
                        int raw = (int)value;
                        if (value == AnalogVideoStandard.None || (raw & (raw - 1)) != 0) continue;
                        if ((available & value) != 0)
                            standards.Add(new DirectShowVideoStandard(value, StandardName(value)));
                    }
                }
            }
            if (standards.Count == 0)
                standards.Add(new DirectShowVideoStandard(
                    AnalogVideoStandard.None, "Device default / automatic"));

            (bool hasVbiPin, string? vbiPinName) = FindVbiOutput(source);
            return new DirectShowDeviceInfo(
                device.Name, inputs, standards, currentInput, currentStandard,
                hasVbiPin, vbiPinName);
        }
        finally
        {
            Release(decoderObject);
            if (!ReferenceEquals(crossbarObject, decoderObject)) Release(crossbarObject);
            Release(crossbarFilter);
            Release(source);
            Release(captureGraph);
            Release(graph);
        }
    }

    private static (bool Found, string? Name) FindVbiOutput(IBaseFilter source)
    {
        foreach (Guid category in new[] { PinCategory.VBI, PinCategory.VideoPortVBI })
        {
            IPin? pin = DsFindPin.ByCategory(source, category, 0);
            if (pin is null) continue;
            try { return (true, ReadPinName(pin)); }
            finally { Release(pin); }
        }

        // Some older WDM drivers omit pin-category metadata. Inspect every
        // output pin's advertised media types and finally its driver name.
        DsError.ThrowExceptionForHR(source.EnumPins(out IEnumPins enumPins));
        try
        {
            var pins = new IPin[1];
            while (enumPins.Next(1, pins, IntPtr.Zero) == 0)
            {
                IPin pin = pins[0];
                try
                {
                    if (pin.QueryDirection(out PinDirection direction) < 0 || direction != PinDirection.Output)
                        continue;
                    string? name = ReadPinName(pin);
                    if (PinAdvertisesVbi(pin)
                        || name?.Contains("VBI", StringComparison.OrdinalIgnoreCase) == true)
                        return (true, name);
                }
                finally { Release(pin); }
            }
        }
        finally { Release(enumPins); }
        return (false, null);
    }

    private static bool PinAdvertisesVbi(IPin pin)
    {
        if (pin.EnumMediaTypes(out IEnumMediaTypes mediaTypes) < 0 || mediaTypes is null)
            return false;
        try
        {
            var values = new AMMediaType[1];
            while (mediaTypes.Next(1, values, IntPtr.Zero) == 0)
            {
                AMMediaType value = values[0];
                try
                {
                    if (value.majorType == MediaType.VBI
                        || value.majorType == MediaType.AuxLine21Data
                        || value.subType == MediaSubType.VBI
                        || value.subType == MediaSubType.Line21_VBIRawData)
                        return true;
                }
                finally { DsUtils.FreeAMMediaType(value); }
            }
        }
        finally { Release(mediaTypes); }
        return false;
    }

    private static string? ReadPinName(IPin pin)
    {
        if (pin.QueryPinInfo(out PinInfo info) < 0) return null;
        try { return string.IsNullOrWhiteSpace(info.name) ? null : info.name; }
        finally { Release(info.filter); }
    }

    internal static IBaseFilter BindFilter(DsDevice device)
    {
        Guid filterId = typeof(IBaseFilter).GUID;
        device.Mon.BindToObject(null!, null!, ref filterId, out object filter);
        return (IBaseFilter)filter;
    }

    internal static IBaseFilter? FindCrossbarFilter(string captureName, string captureDevicePath)
    {
        string[] captureTokens = NameTokens(captureName);
        string captureHardwareKey = HardwareInstanceKey(captureDevicePath);
        var devices = new[]
            {
                FilterCategory.AMKSCrossbar,
                FilterCategory.LegacyAmFilterCategory,
            }
            .SelectMany(DsDevice.GetDevicesOfCat)
            .DistinctBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .Select(device => new
            {
                Device = device,
                Score = (HardwareInstanceKey(device.DevicePath).Equals(
                             captureHardwareKey, StringComparison.OrdinalIgnoreCase) ? 10_000 : 0)
                        + NameTokens(device.Name).Count(token =>
                            captureTokens.Contains(token, StringComparer.OrdinalIgnoreCase)),
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        foreach (var candidate in devices)
        {
            IBaseFilter? filter = null;
            try
            {
                filter = BindFilter(candidate.Device);
                if (filter is IAMCrossbar) return filter;
            }
            catch (COMException)
            {
                // Some legacy filters cannot be instantiated without their
                // hardware or owning graph. Continue with the remaining ones.
            }
            Release(filter);
        }
        return null;
    }

    private static string HardwareInstanceKey(string devicePath)
    {
        // KS device monikers for the capture and crossbar filters contain the
        // same PnP instance path; only the category GUID after "#{" differs.
        int category = devicePath.IndexOf("#{", StringComparison.Ordinal);
        return category >= 0 ? devicePath[..category] : devicePath;
    }

    private static string[] NameTokens(string name) => name
        .Split([' ', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length >= 3
                        && !token.Equals("analog", StringComparison.OrdinalIgnoreCase)
                        && !token.Equals("capture", StringComparison.OrdinalIgnoreCase)
                        && !token.Equals("crossbar", StringComparison.OrdinalIgnoreCase)
                        && !token.Equals("xbar", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static bool IsVideoConnector(PhysicalConnectorType type) =>
        // DirectShow reserves 1..4095 for every video connector. Do not stop at
        // Video_SerialDigital: crossbar output pins are commonly reported as
        // Video_VideoDecoder (for example Philips/NXP 713x), and newer/other
        // devices may use USB, 1394, SCART, AUX or parallel-digital types.
        type >= PhysicalConnectorType.Video_Tuner && type < PhysicalConnectorType.Audio_Tuner;

    private static string ConnectorName(PhysicalConnectorType type) => type switch
    {
        PhysicalConnectorType.Video_Tuner => "TV tuner",
        PhysicalConnectorType.Video_Composite => "Composite video",
        PhysicalConnectorType.Video_SVideo => "S-Video",
        PhysicalConnectorType.Video_RGB => "RGB video",
        PhysicalConnectorType.Video_YRYBY => "Component video (Y/R-Y/B-Y)",
        PhysicalConnectorType.Video_SerialDigital => "Serial digital video",
        PhysicalConnectorType.Video_ParallelDigital => "Parallel digital video",
        PhysicalConnectorType.Video_AUX => "Auxiliary video",
        PhysicalConnectorType.Video_1394 => "IEEE 1394 video",
        PhysicalConnectorType.Video_USB => "USB video",
        PhysicalConnectorType.Video_SCART => "SCART",
        _ => type.ToString().Replace("Video_", string.Empty).Replace('_', ' '),
    };

    private static string StandardName(AnalogVideoStandard value) => value.ToString().Replace('_', ' ');

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DirectShow capture is available on Windows.");
    }

    private static void Release(object? value)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
