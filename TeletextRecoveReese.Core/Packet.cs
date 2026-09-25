namespace TeletextRecoveReese.Core;

/// <summary>
/// Raw teletext packet - 2 address bytes (Hamming 8/4 each) + 40 payload bytes (T42 format).
/// </summary>
public class TeletextPacket
{
    public const int Length = 42;

    public byte[] Raw { get; }

    public TeletextPacket(byte[] raw42)
    {
        if (raw42.Length != Length)
            throw new ArgumentException("A teletext packet must be exactly 42 bytes (2 address + 40 payload).");
        Raw = raw42;
    }

    /// <summary>
    /// vhs-teletext --keep-empty represents an input VBI line that could not be
    /// deconvolved as one all-zero 42-byte packet. It is a positional placeholder,
    /// not a damaged Teletext packet.
    /// </summary>
    public static bool IsPadding(ReadOnlySpan<byte> raw42)
    {
        if (raw42.Length != Length) return false;
        foreach (byte value in raw42)
        {
            if (value != 0) return false;
        }
        return true;
    }

    /// <summary>Decodes magazine (1-8) and row (0-31) from the address bytes.</summary>
    public (int magazine, int row, bool ok) DecodeAddress()
    {
        var r1 = Hamming.Decode84(Raw[0]);
        var r2 = Hamming.Decode84(Raw[1]);

        if (r1.UncorrectableError || r2.UncorrectableError)
            return (0, 0, false);

        int magazineBits = r1.Value & 0b0111; // low 3 bits = magazine (0 = magazine 8)
        int magazine = magazineBits == 0 ? 8 : magazineBits;

        int rowLow = (r1.Value >> 3) & 0b0001; // MSB of r1
        int row = rowLow | (r2.Value << 1);

        return (magazine, row & 0b11111, true);
    }

    public byte[] Payload => Raw[2..];
}
