#nullable enable
using System.Buffers.Binary;
using TeletextRecoveReese.Core;

internal static class VbiTapeFormatRegressions
{
    public static void Run()
    {
        foreach (VbiTapeFormat format in Enum.GetValues<VbiTapeFormat>())
        {
            CheckTable(format, "full", VbiPatternResources.OpenFull(format));
            CheckTable(format, "parity", VbiPatternResources.OpenParity(format));
            CheckTable(format, "hamming", VbiPatternResources.OpenHamming(format));
        }
        Console.WriteLine("PASS: VHS, Betamax and Grundig 2x4 pattern tables are bundled");
        LineGainEqualizesWeakLines();
    }

    private static byte[] Line(byte black, byte high)
    {
        var line = new byte[2048];
        for (int i = 0; i < line.Length; i++) line[i] = (i / 8) % 2 == 0 ? black : high;
        return line;
    }

    private static void LineGainEqualizesWeakLines()
    {
        var normalizer = new VbiLineGainNormalizer(isUInt16: false);
        byte[] strong = Line(45, 165);
        if (normalizer.Normalize(strong) != 1f || strong[8] != 165)
            throw new Exception("A full-level line was rescaled.");
        byte[] weak = Line(45, 91);
        float gain = normalizer.Normalize(weak);
        if (gain < 2.4f || weak[0] != 45 || Math.Abs(weak[8] - 165) > 3)
            throw new Exception($"A weak line was scaled by {gain} to {weak[8]}.");
        var noise = new byte[2048];
        var random = new Random(1);
        for (int i = 0; i < noise.Length; i++) noise[i] = (byte)(45 + random.Next(-4, 5));
        normalizer.Normalize(noise);
        double mean = noise.Average(value => (double)value);
        double deviation = Math.Sqrt(noise.Average(value => (value - mean) * (value - mean)));
        if (deviation >= 14) throw new Exception($"Noise was amplified past the std-dev gate ({deviation:0.0}).");
        Console.WriteLine("PASS: line level equalization lifts weak lines and keeps empty lines below the noise gate");
    }

    private static void CheckTable(VbiTapeFormat format, string name, Stream stream)
    {
        using (stream)
        {
            Span<byte> header = stackalloc byte[14];
            stream.ReadExactly(header);
            long width = BinaryPrimitives.ReadUInt32BigEndian(header);
            long outputWidth = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            long count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            if (outputWidth != 1 || stream.Length != 14 + count * (width + 1))
                throw new Exception($"{format} {name} pattern table is malformed.");
        }
    }
}
