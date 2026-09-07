namespace TeletextRecoveReese.Core;

/// <summary>Decodes broadcast service data carried by packet 8/30 format 1.</summary>
public static class BroadcastServiceData
{
    private static readonly DateOnly ModifiedJulianEpoch = new(1858, 11, 17);

    public static bool TryDecodeFormat1Date(ReadOnlySpan<byte> packet, out DateOnly date)
    {
        date = default;
        if (packet.Length != 42) return false;

        var addressLow = Hamming.Decode84(packet[0]);
        var addressHigh = Hamming.Decode84(packet[1]);
        var designation = Hamming.Decode84(packet[2]);
        if (addressLow.UncorrectableError
            || addressHigh.UncorrectableError
            || designation.UncorrectableError)
            return false;

        int address = addressLow.Value | (addressHigh.Value << 4);
        int magazine = address & 0x07;
        int row = (address >> 3) & 0x1F;
        if (magazine != 0 || row != 30 || designation.Value is not (0 or 1))
            return false;

        // T42 omits clock run-in and framing bytes, so EN 300 706 bytes 16-18
        // are packet offsets 12-14. Each transmitted decimal digit is +1.
        Span<int> digits = stackalloc int[5]
        {
            (packet[12] & 0x0F) - 1,
            ((packet[13] >> 4) & 0x0F) - 1,
            (packet[13] & 0x0F) - 1,
            ((packet[14] >> 4) & 0x0F) - 1,
            (packet[14] & 0x0F) - 1,
        };
        for (int index = 0; index < digits.Length; index++)
        {
            if (digits[index] is < 0 or > 9) return false;
        }

        int modifiedJulianDate = digits[0] * 10000
            + digits[1] * 1000
            + digits[2] * 100
            + digits[3] * 10
            + digits[4];
        try
        {
            date = ModifiedJulianEpoch.AddDays(modifiedJulianDate);
            return date.Year is >= 1970 and <= 2100;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
