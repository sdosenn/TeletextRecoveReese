#nullable enable
using System.Text;
using TeletextRecoveReese.Core;

internal static class TtiImportRegressions
{
    public static void Run()
    {
        // Two subpages of P801 without SC lines, Croatian/Slovenian national option
        // (PS 0x8000 | C12-C14 = 101), an escaped control code, a high-bit control
        // code, Fastext links and one X/26 character (G0 + caron on row 3, column 5).
        int rowTriplet = (40 + 3) | (0x04 << 6);                // set active row 3
        int caronTriplet = 5 | (0x1F << 6) | ('c' << 11);      // column 5: G0 'c' + diacritic 15
        int terminator = 63 | (0x1F << 6) | (1 << 11);
        string Triplet(int value) => new([(char)((value & 0x3F) | 0x40),
            (char)(((value >> 6) & 0x3F) | 0x40), (char)(((value >> 12) & 0x3F) | 0x40)]);
        string x26 = "@" + Triplet(rowTriplet) + Triplet(caronTriplet)
            + string.Concat(Enumerable.Repeat(Triplet(terminator), 11));
        string tti =
            "DE,test page\r\n" +
            "PN,80100\r\nPS,8280\r\n" +
            "OL,0,XXXXXXXXHEADER TEXT\r\n" +
            "OL,1,\u001bAred\u0083yellow\r\n" +
            "OL,26," + x26 + "\r\n" +
            "FL,802,803,804,805,8ff,100\r\n" +
            "PN,80101\r\nOL,2,second\r\n";

        var importer = new TtiImporter();
        if (importer.Read(new MemoryStream(Encoding.Latin1.GetBytes(tti))) != 2 || importer.SubpageCount != 2)
            throw new Exception("TTI subpages were not imported.");

        var store = new PageStore();
        var assembler = new PageAssembler(store, decodeEnhancements: true);
        for (int i = 0; i < importer.Packets.Count; i++) assembler.Feed(importer.Packets[i].ToArray(), i);
        assembler.FinalizeAll();

        IReadOnlyList<PageInstance> first = store.GetInstances(8, 0x01, 0);
        IReadOnlyList<PageInstance> second = store.GetInstances(8, 0x01, 1);
        if (first.Count != 1 || second.Count != 1)
            throw new Exception("TTI subpages did not get their own addresses.");
        TeletextPage page = first[0].Page;
        if (page.NationalOption != 5)
            throw new Exception($"TTI page status gave national option {page.NationalOption}.");
        if (page.RawRows[1] is not { } row1 || (row1[2] & 0x7F) != 0x01 || (row1[6] & 0x7F) != 0x03
            || page.Grid[1, 1].Character != 'r')
            throw new Exception("TTI control codes were not decoded.");
        if ((page.RawRows[0]![10] & 0x7F) != 'H')
            throw new Exception("The TTI header text is misplaced.");
        if (page.FastextLinks.Count < 4 || page.FastextLinks[0].PageNumber != 0x802)
            throw new Exception("TTI Fastext links were not imported.");
        if (page.Grid[5, 3].EnhancementText is not { Length: > 0 })
            throw new Exception("The TTI X/26 character is not shown.");
        if (second[0].Page.Grid[0, 2].Character != 's')
            throw new Exception("The second TTI subpage lost its text.");
        Console.WriteLine("PASS: TTI pages import with subpages, control codes, Fastext and X/26");
    }
}
