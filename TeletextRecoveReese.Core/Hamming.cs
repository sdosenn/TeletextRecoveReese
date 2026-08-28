namespace TeletextRecoveReese.Core;

/// <summary>
/// Hamming error-correction codes used in teletext packets (EN 300 706).
/// </summary>
public static class Hamming
{
    /// <summary>
    /// Decode result, including whether a correctable 1-bit error was fixed
    /// or an uncorrectable (2+ bit) error was detected.
    /// </summary>
    public readonly struct HammingResult
    {
        public int Value { get; }
        public bool CorrectableErrorFixed { get; }
        public bool UncorrectableError { get; }

        public HammingResult(int value, bool fixed_, bool uncorrectable)
        {
            Value = value;
            CorrectableErrorFixed = fixed_;
            UncorrectableError = uncorrectable;
        }
    }

    /// <summary>
    /// Hamming 8/4 decoding (used for magazine/row addresses, page number digits, etc).
    /// Standard extended Hamming (8,4) SECDED layout:
    /// bit positions (1-indexed, bit1 = transmitted first / LSB):
    /// 1=P1 2=P2 3=D1 4=P3 5=D2 6=D3 7=D4 8=P8 (overall parity)
    /// </summary>
    public static HammingResult Decode84(byte b)
    {
        int p1 = (b >> 0) & 1;
        int d1 = (b >> 1) & 1;
        int p2 = (b >> 2) & 1;
        int d2 = (b >> 3) & 1;
        int p3 = (b >> 4) & 1;
        int d3 = (b >> 5) & 1;
        int p4 = (b >> 6) & 1;
        int d4 = (b >> 7) & 1;

        // c_i = 0 means that parity bit matches what the data bits imply.
        int c1 = p1 ^ 1 ^ d1 ^ d3 ^ d4;
        int c2 = p2 ^ 1 ^ d1 ^ d2 ^ d4;
        int c3 = p3 ^ 1 ^ d1 ^ d2 ^ d3;

        // Valid codewords always have odd parity across all 8 bits (this falls
        // directly out of how P4 is defined) - verified against both 0x02 and 0x15.
        int rawParity = p1 ^ d1 ^ p2 ^ d2 ^ p3 ^ d3 ^ p4 ^ d4;

        bool fixed_ = false;
        bool uncorrectable = false;

        if (c1 == 0 && c2 == 0 && c3 == 0)
        {
            if (rawParity == 0)
            {
                // error in P4 only - data (d1..d4) is unaffected
                fixed_ = true;
            }
            // else: clean, no error
        }
        else if (rawParity == 1)
        {
            // single correctable error, located by which checks disagree
            fixed_ = true;
            if (c1 == 1 && c2 == 1 && c3 == 1) d1 ^= 1;
            else if (c1 == 0 && c2 == 1 && c3 == 1) d2 ^= 1;
            else if (c1 == 1 && c2 == 0 && c3 == 1) d3 ^= 1;
            else if (c1 == 1 && c2 == 1 && c3 == 0) d4 ^= 1;
            // else: error was in P1, P2, or P3 - data is unaffected
        }
        else
        {
            // rawParity == 0 with a nonzero syndrome => even number of errors (2-bit) => uncorrectable
            uncorrectable = true;
        }

        int value = d1 | (d2 << 1) | (d3 << 2) | (d4 << 3);
        return new HammingResult(value, fixed_, uncorrectable);
    }

    /// <summary>Encodes one 4-bit value as a teletext Hamming 8/4 codeword.</summary>
    public static byte Encode84(int nibble)
    {
        int d1 = nibble & 1;
        int d2 = (nibble >> 1) & 1;
        int d3 = (nibble >> 2) & 1;
        int d4 = (nibble >> 3) & 1;

        int p1 = 1 ^ d1 ^ d3 ^ d4;
        int p2 = 1 ^ d1 ^ d2 ^ d4;
        int p3 = 1 ^ d1 ^ d2 ^ d3;
        int p4 = 1 ^ p1 ^ d1 ^ p2 ^ d2 ^ p3 ^ d3 ^ d4;

        return (byte)(p1 | (d1 << 1) | (p2 << 2) | (d2 << 3) |
                      (p3 << 4) | (d3 << 5) | (p4 << 6) | (d4 << 7));
    }

    /// <summary>
    /// Hamming 24/18 decoding - used for the page header, and X/26, X/27, X/28 packets
    /// (18 data bits protected by 6 parity bits, EN 300 706 sec 8.3).
    ///
    /// The transmitted bit positions are 1-based. P1-P5 occupy positions
    /// 1,2,4,8,16; P6 at position 24 is the overall odd-parity bit. Data occupies
    /// positions 3,5-7,9-15 and 17-23.
    /// </summary>
    public static HammingResult Decode24_18(int triplet)
    {
        int codeword = triplet & 0x00FFFFFF;
        int syndrome = 0;

        foreach (int parityPosition in new[] { 1, 2, 4, 8, 16 })
        {
            int parity = 0;
            for (int position = 1; position <= 23; position++)
            {
                if ((position & parityPosition) != 0)
                    parity ^= (codeword >> (position - 1)) & 1;
            }
            if (parity != 1) syndrome |= parityPosition; // teletext uses odd parity
        }

        int overallParity = 0;
        for (int position = 1; position <= 24; position++)
            overallParity ^= (codeword >> (position - 1)) & 1;

        bool corrected = false;
        bool uncorrectable = false;
        if (syndrome == 0)
        {
            // Only P6 is wrong; the 18 data bits are unaffected.
            corrected = overallParity == 0;
        }
        else if (overallParity == 0 && syndrome <= 23)
        {
            codeword ^= 1 << (syndrome - 1);
            corrected = true;
        }
        else
        {
            uncorrectable = true;
        }

        int value = 0;
        int dataBit = 0;
        for (int position = 1; position <= 23; position++)
        {
            if (position is 1 or 2 or 4 or 8 or 16) continue;
            value |= ((codeword >> (position - 1)) & 1) << dataBit++;
        }

        return new HammingResult(value, corrected, uncorrectable);
    }

    public static HammingResult Decode24_18(byte first, byte second, byte third) =>
        Decode24_18(first | (second << 8) | (third << 16));

    /// <summary>Encodes an 18-bit X/26 triplet payload as teletext Hamming 24/18.</summary>
    public static int Encode24_18(int value)
    {
        value &= 0x3FFFF;
        int codeword = 0;
        int dataBit = 0;
        for (int position = 1; position <= 23; position++)
        {
            if (position is 1 or 2 or 4 or 8 or 16) continue;
            codeword |= ((value >> dataBit++) & 1) << (position - 1);
        }

        foreach (int parityPosition in new[] { 1, 2, 4, 8, 16 })
        {
            int parity = 0;
            for (int position = 1; position <= 23; position++)
                if (position != parityPosition && (position & parityPosition) != 0)
                    parity ^= (codeword >> (position - 1)) & 1;
            if (parity == 0) codeword |= 1 << (parityPosition - 1); // odd parity
        }

        int overallParity = 0;
        for (int position = 1; position <= 23; position++)
            overallParity ^= (codeword >> (position - 1)) & 1;
        if (overallParity == 0) codeword |= 1 << 23; // overall odd parity

        return codeword;
    }
}
