namespace TeletextRecoveReese.Core;

public abstract class LiveVbiCaptureStream : Stream
{
    public abstract uint SamplingRate { get; }
    public abstract int SamplesPerLine { get; }
    public abstract int FirstFieldLines { get; }
    public abstract int SecondFieldLines { get; }
    public int LinesPerFrame => FirstFieldLines + SecondFieldLines;
    public abstract long CapturedFrames { get; }
    public abstract Action<byte[]>? RawFrameCaptured { get; set; }
}
