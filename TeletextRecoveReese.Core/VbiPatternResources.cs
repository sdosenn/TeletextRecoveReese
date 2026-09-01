using System.Reflection;

namespace TeletextRecoveReese.Core;

/// <summary>
/// Opens the trained VHS byte-pattern tables bundled with the application.
/// Embedded resources keep the tables available in every packaged build.
/// </summary>
public static class VbiPatternResources
{
    private const string ResourcePrefix =
        "TeletextRecoveReese.Core.Assets.VbiPatterns.vhs.";

    public static Stream OpenVhsFull() => Open("full.dat");

    public static Stream OpenVhsParity() => Open("parity.dat");

    public static Stream OpenVhsHamming() => Open("hamming.dat");

    public const int ObservedCriFcBits = 24;

    public const int ObservedCriFcSamplesPerBit = 8;

    /// <summary>Ideal 16-bit clock run-in followed by the 8-bit framing code.</summary>
    public static ReadOnlySpan<sbyte> IdealCriFc =>
    [
         1, -1,  1, -1,  1, -1,  1, -1,
         1, -1,  1, -1,  1, -1,  1, -1,
         1,  1,  1, -1, -1,  1, -1, -1,
    ];

    /// <summary>
    /// Averaged, observed clock run-in and framing-code waveform from
    /// vhs-teletext. Values are stored bit-major as 24 rows of 8 samples.
    /// </summary>
    public static ReadOnlySpan<byte> ObservedCriFc =>
    [
        133, 132, 129, 127, 124, 121, 119, 117,
        116, 115, 115, 115, 116, 117, 118, 119,
        120, 121, 121, 121, 121, 120, 119, 118,
        118, 117, 116, 116, 116, 117, 117, 118,
        119, 120, 120, 121, 121, 121, 120, 119,
        119, 118, 117, 116, 116, 116, 116, 117,
        118, 119, 120, 121, 122, 122, 122, 122,
        121, 120, 119, 118, 117, 117, 117, 117,
        118, 118, 119, 120, 121, 121, 121, 121,
        121, 120, 119, 119, 118, 118, 117, 117,
        118, 118, 119, 120, 121, 122, 122, 122,
        122, 121, 120, 119, 118, 118, 117, 117,
        117, 117, 118, 119, 120, 120, 121, 121,
        122, 122, 122, 122, 121, 121, 121, 121,
        120, 120, 119, 118, 116, 115, 113, 110,
        108, 105, 104, 103, 104, 107, 112, 119,
        128, 137, 147, 157, 166, 174, 179, 183,
        184, 183, 181, 178, 175, 171, 168, 166,
        164, 163, 162, 160, 159, 156, 153, 147,
        141, 133, 124, 114, 104,  96,  88,  82,
         78,  77,  79,  83,  90,  99, 108, 118,
        127, 134, 140, 144, 146, 145, 141, 136,
        128, 119, 110, 100,  91,  83,  76,  69,
         65,  61,  59,  57,  57,  57,  57,  58,
    ];

    private static Stream Open(string fileName)
    {
        string resourceName = ResourcePrefix + fileName;
        return typeof(VbiPatternResources).Assembly.GetManifestResourceStream(resourceName)
               ?? throw new InvalidOperationException(
                   $"Embedded VBI pattern resource '{resourceName}' is missing.");
    }
}
