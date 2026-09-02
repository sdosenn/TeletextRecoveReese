namespace TeletextRecoveReese.Core;

public sealed record VbiCaptureOptions(
    string Name,
    double SampleRate,
    int LineLength,
    int LineStart,
    int LineStartEnd,
    bool IsUInt16,
    int FieldLines,
    int FieldRangeStart,
    int FieldRangeEnd,
    float StandardDeviationThreshold = 14,
    float SignalLevelThreshold = 64,
    float CriFcRangeThreshold = 28,
    double CriFcConfidenceThreshold = 0.35);

public readonly record struct VbiDeconvolutionProgress(
    long ProcessedLines,
    long TotalLines,
    long TeletextLines,
    long PacketsWritten,
    double CaptureFramesPerSecond = 0)
{
    public double Percent => TotalLines <= 0 ? 0 : ProcessedLines * 100.0 / TotalLines;
}

public sealed record VbiDeconvolutionResult(
    long ProcessedLines,
    long TeletextLines,
    long PacketsWritten,
    string OpenClDevice);

public readonly record struct VbiLineTiming(
    int StartSample,
    int EndSample,
    bool PllAdjusted);

public interface IVbiDecodedPacketProgress : IProgress<IReadOnlyList<byte[]>>
{
    bool IsEnabled { get; }
}

public interface IVbiDeconvolutionControl
{
    bool Enabled { get; }
    bool GetLineDecodingEnabled(int fieldLine);
    int GetClockSearchOffset(int fieldLine);
    double GetManualPacketSpanSamples(int fieldLine);
}
