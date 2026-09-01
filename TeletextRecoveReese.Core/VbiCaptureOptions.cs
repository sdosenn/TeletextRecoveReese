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
    int FieldRangeEnd);

public readonly record struct VbiDeconvolutionProgress(
    long ProcessedLines,
    long TotalLines,
    long TeletextLines,
    long PacketsWritten)
{
    public double Percent => TotalLines <= 0 ? 0 : ProcessedLines * 100.0 / TotalLines;
}

public sealed record VbiDeconvolutionResult(
    long ProcessedLines,
    long TeletextLines,
    long PacketsWritten,
    string OpenClDevice);

public interface IVbiDecodedPacketProgress : IProgress<IReadOnlyList<byte[]>>
{
    bool IsEnabled { get; }
}
