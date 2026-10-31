namespace TeletextRecoveReese.Core;

/// <summary>
/// Amplifies weak VBI lines to the level of the strongest recent lines before
/// Teletext detection. Some tapes (seen on Video 2000) replay the first VBI lines
/// after the head switch with a gain that only recovers over several lines, so
/// their run-in falls below the absolute detection thresholds. Each line is
/// scaled around its own black level by the ratio of a decaying peak swing to
/// the line's swing; noise on empty lines stays below the standard-deviation gate.
/// Lines whose black level differs from the reference line's, such as lines
/// carrying sync pulses, are left unchanged.
/// </summary>
public sealed class VbiLineGainNormalizer
{
    public const float MaximumGain = 3f;
    // Per-line decay of the reference swing: it follows the strongest lines of
    // the last few frames and adapts when the signal level changes.
    private const float ReferenceDecay = 0.995f;
    private const int HistogramBins = 256;

    private readonly bool _isUInt16;
    private readonly int[] _histogram = new int[HistogramBins];
    private float _referenceSwing;
    private float _referenceBlack;

    public VbiLineGainNormalizer(bool isUInt16) => _isUInt16 = isUInt16;

    /// <summary>Scales <paramref name="raw"/> in place and returns the gain applied.</summary>
    public float Normalize(byte[] raw)
    {
        int sampleCount = _isUInt16 ? raw.Length / 2 : raw.Length;
        if (sampleCount == 0) return 1f;
        int shift = _isUInt16 ? 8 : 0;
        Array.Clear(_histogram);
        for (int i = 0; i < sampleCount; i++)
            _histogram[Sample(raw, i) >> shift]++;
        float black = Percentile(sampleCount * 0.02) * (1 << shift);
        float peak = Percentile(sampleCount * 0.98) * (1 << shift);
        float swing = Math.Max(peak - black, 1f);

        float decayed = _referenceSwing * ReferenceDecay;
        if (swing >= decayed)
        {
            _referenceSwing = swing;
            _referenceBlack = black;
            return 1f;
        }
        _referenceSwing = decayed;
        if (Math.Abs(black - _referenceBlack) > _referenceSwing * 0.25f) return 1f;
        float gain = Math.Min(_referenceSwing / swing, MaximumGain);
        if (gain <= 1.01f) return 1f;

        int maximum = _isUInt16 ? ushort.MaxValue : byte.MaxValue;
        for (int i = 0; i < sampleCount; i++)
        {
            float value = black + (Sample(raw, i) - black) * gain;
            int scaled = Math.Clamp((int)MathF.Round(value), 0, maximum);
            if (_isUInt16)
            {
                raw[i * 2] = (byte)scaled;
                raw[i * 2 + 1] = (byte)(scaled >> 8);
            }
            else
            {
                raw[i] = (byte)scaled;
            }
        }
        return gain;
    }

    private int Sample(byte[] raw, int index) =>
        _isUInt16 ? raw[index * 2] | (raw[index * 2 + 1] << 8) : raw[index];

    private int Percentile(double rank)
    {
        long seen = 0;
        for (int bin = 0; bin < HistogramBins; bin++)
        {
            seen += _histogram[bin];
            if (seen > rank) return bin;
        }
        return HistogramBins - 1;
    }
}
