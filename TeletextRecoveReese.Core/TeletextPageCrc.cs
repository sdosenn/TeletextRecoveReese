namespace TeletextRecoveReese.Core;

/// <summary>ETS 300 706 section 9.6.1 page CRC used by packet X/27/0.</summary>
public static class TeletextPageCrc
{
    private const byte Space = 0x20;

    public static ushort Calculate(TeletextPage page)
    {
        ushort crc = 0;

        if (page.RawRows[0] is { Length: 42 } header)
            crc = Update(crc, header.AsSpan(10, 24));
        else
            crc = UpdateWithSpaces(crc, 24);

        for (int row = 1; row <= 25; row++)
        {
            if (page.RawRows[row] is { Length: 42 } packet)
                crc = Update(crc, packet.AsSpan(2, 40));
            else
                crc = UpdateWithSpaces(crc, 40);
        }

        return crc;
    }

    public static ushort? ReadTransmitted(TeletextPage page)
    {
        if (page.FastextPacket is not { Length: 42 } packet)
            return null;

        var designation = Hamming.Decode84(packet[2]);
        if (designation.UncorrectableError || designation.Value != 0)
            return null;

        // ETS transmission order is bits 9-16 followed by bits 1-8.
        return (ushort)((packet[40] << 8) | packet[41]);
    }

    private static ushort UpdateWithSpaces(ushort crc, int count)
    {
        for (int index = 0; index < count; index++)
            crc = UpdateByte(crc, Space);
        return crc;
    }

    private static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            crc = UpdateByte(crc, value);
        return crc;
    }

    private static ushort UpdateByte(ushort crc, byte value)
    {
        for (int bitIndex = 0; bitIndex < 8; bitIndex++)
        {
            int inputBit = (value >> (7 - bitIndex)) & 1;
            int feedback = ((crc >> 15) ^ (crc >> 11) ^ (crc >> 8) ^ (crc >> 6) ^ inputBit) & 1;
            crc = (ushort)((crc << 1) | feedback);
        }
        return crc;
    }
}
