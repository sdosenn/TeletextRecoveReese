namespace TeletextRecoveReese.Core;

/// <summary>Recording format whose trained vhs-teletext pattern tables are used.</summary>
public enum VbiTapeFormat
{
    Vhs,
    Betamax,
    Video2000,
}

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
    double CriFcConfidenceThreshold = 0.20,
    bool KeepEmptyPackets = false,
    // Samples after resampling to eight samples per bit, matching vhs-teletext.
    int ExtraRoll = 0,
    VbiTapeFormat TapeFormat = VbiTapeFormat.Vhs,
    // Amplify weak lines to the level of the strongest recent lines before detection.
    bool EqualizeLineLevels = false);

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

/// <summary>Receives every decoded packet synchronously, in capture order, with the
/// index of the selected VBI line it was decoded from (counted from zero).</summary>
public interface IVbiPacketObserver
{
    void OnPacket(long lineIndex, byte[] packet);
}

public interface IVbiDeconvolutionControl
{
    bool Enabled { get; }
    bool GetLineDecodingEnabled(int fieldLine);
    int GetClockSearchOffset(int fieldLine);
    double GetManualPacketSpanSamples(int fieldLine);
}
