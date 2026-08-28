namespace TeletextRecoveReese.Core;

/// <summary>
/// Standalone packet parser for inspecting raw .t42 captures. Prints magazine, row,
/// and (for header packets) full page/subpage/control-bit info per packet.
///
/// Byte layout and formulas verified against:
/// - EN 300 706 Fig. 4/5/6 (MRAG and page-number bit layout)
/// - teletext.wiki.zxnet.co.uk/wiki/Hamming_codes (Hamming 8/4 parity formulas)
/// - dvbsnoop src/ebu/teletext.c (byte offsets for subcode and control bits C4-C14)
/// </summary>
public class PacketInspector
{
    private readonly Dictionary<int, (int page, int subpage)> _lastHeaderByMagazine = new();

    /// <summary>Combines two Hamming-8/4 nibbles into a byte, low nibble first (matches
    /// dvbsnoop's unhamW84: used for MRAG and for subcode words).</summary>
    private static (int value, bool uncorrectable) DecodeNibblePair(byte lo, byte hi)
    {
        var a = Hamming.Decode84(lo);
        var b = Hamming.Decode84(hi);
        return (a.Value | (b.Value << 4), a.UncorrectableError || b.UncorrectableError);
    }

    public void ParseAndPrint(byte[] raw42, int packetIndex = -1)
    {
        if (raw42.Length != 42)
        {
            Console.WriteLine($"[{packetIndex}] SKIPPED - expected 42 bytes, got {raw42.Length}");
            return;
        }

        var (mrag, mragBad) = DecodeNibblePair(raw42[0], raw42[1]);
        if (mragBad)
        {
            Console.WriteLine($"[{packetIndex}] ADDRESS UNCORRECTABLE - raw bytes: {raw42[0]:X2} {raw42[1]:X2}");
            return;
        }

        int row = (mrag >> 3) & 0x1F;
        int magazineBits = mrag & 0x07;
        int magazine = magazineBits == 0 ? 8 : magazineBits;

        var payload = raw42[2..];

        if (row == 0)
        {
            PrintHeader(packetIndex, magazine, payload);
        }
        else if (row is >= 1 and <= 25)
        {
            string pageStr = _lastHeaderByMagazine.TryGetValue(magazine, out var last)
                ? $"{magazine}.{last.page:X2}"
                : $"{magazine}.??";
            Console.WriteLine($"[{packetIndex}] {pageStr,-14} row={row,2} (TEXT/DISPLAY)");
        }
        else
        {
            string pageStr = _lastHeaderByMagazine.TryGetValue(magazine, out var last)
                ? $"{magazine}.{last.page:X2}"
                : $"{magazine}.??";
            Console.WriteLine($"[{packetIndex}] {pageStr,-14} row={row,2} (X/26-31: enhancement/reserved)");
        }
    }

    private void PrintHeader(int packetIndex, int magazine, byte[] payload)
    {
        // page number: units nibble from payload[0], tens nibble from payload[1]
        var unitsR = Hamming.Decode84(payload[0]);
        var tensR = Hamming.Decode84(payload[1]);

        if (unitsR.UncorrectableError || tensR.UncorrectableError)
        {
            Console.WriteLine($"[{packetIndex}] mag={magazine} row=0 (HEADER) - PAGE NUMBER UNCORRECTABLE");
            return;
        }

        int pageNumber = unitsR.Value | (tensR.Value << 4); // e.g. 0x00-0xFF, matches display digits directly

        var (subWord1, sub1Bad) = DecodeNibblePair(payload[2], payload[3]); // S1,S2 (+C4 in S2's D4 bit)
        var (subWord2, sub2Bad) = DecodeNibblePair(payload[4], payload[5]); // S3,S4 (+C5,C6 in S4's D3,D4 bits)

        int subpage = 0;
        string subpageStr = "UNCORRECTABLE";
        if (!sub1Bad && !sub2Bad)
        {
            int subpageRaw = subWord1 | (subWord2 << 8);
            subpage = subpageRaw & 0x3F7F; // strips the C4/C5/C6 control bits packed into the same bytes
            subpageStr = $"0x{subpage:X4}";
        }

        _lastHeaderByMagazine[magazine] = (pageNumber, subpage);

        var c6byte = Hamming.Decode84(payload[5]); // S4 + C5,C6
        var c7to10 = Hamming.Decode84(payload[6]);
        var c11to14 = Hamming.Decode84(payload[7]);

        bool erasePage = !sub1Bad && (Hamming.Decode84(payload[3]).Value & 0x8) != 0; // C4 = D4 of S2 byte
        bool newsflash = !c6byte.UncorrectableError && (c6byte.Value & 0x4) != 0;      // C5 = D3 of S4 byte
        bool subtitle = !c6byte.UncorrectableError && (c6byte.Value & 0x8) != 0;       // C6 = D4 of S4 byte

        bool suppressHeader = !c7to10.UncorrectableError && (c7to10.Value & 0x1) != 0; // C7 = D1
        bool update = !c7to10.UncorrectableError && (c7to10.Value & 0x2) != 0;         // C8 = D2
        bool interrupted = !c7to10.UncorrectableError && (c7to10.Value & 0x4) != 0;    // C9 = D3
        bool inhibitDisplay = !c7to10.UncorrectableError && (c7to10.Value & 0x8) != 0; // C10 = D4

        bool magazineSerial = !c11to14.UncorrectableError && (c11to14.Value & 0x1) != 0; // C11 = D1
        int nationalOption = c11to14.UncorrectableError ? -1 : (c11to14.Value >> 1) & 0x7; // C12-14 = D2,D3,D4

        string pageStr = $"{magazine}.{pageNumber:X2}";
        Console.Write($"[{packetIndex}] {pageStr,-14} row= 0 (HEADER)  page={magazine}{pageNumber:X2} subpage={subpageStr}");

        var flags = new List<string>();
        if (erasePage) flags.Add("ErasePage");
        if (newsflash) flags.Add("Newsflash");
        if (subtitle) flags.Add("Subtitle");
        if (suppressHeader) flags.Add("SuppressHeader");
        if (update) flags.Add("Update");
        if (interrupted) flags.Add("Interrupted");
        if (inhibitDisplay) flags.Add("InhibitDisplay");
        if (magazineSerial) flags.Add("MagazineSerial");

        if (flags.Count > 0)
            Console.Write($"  [{string.Join(",", flags)}]");

        if (nationalOption >= 0)
            Console.Write($"  natOpt={nationalOption}");

        Console.WriteLine();
    }

    /// <summary>Reads a raw .t42 file (concatenated 42-byte packets) and prints each one.
    /// Creates a fresh PacketInspector internally, so per-magazine "last header" state
    /// does not leak across separate files.</summary>
    public static void ParseFile(string path)
    {
        var inspector = new PacketInspector();
        var bytes = File.ReadAllBytes(path);
        int count = bytes.Length / 42;

        if (bytes.Length % 42 != 0)
            Console.WriteLine($"WARNING: file length {bytes.Length} is not a multiple of 42 - trailing {bytes.Length % 42} bytes ignored.");

        for (int i = 0; i < count; i++)
        {
            var packet = bytes[(i * 42)..((i + 1) * 42)];
            inspector.ParseAndPrint(packet, i);
        }
    }
}
