#nullable enable
using TeletextRecoveReese.Core;

internal static class EnhancementEditRegressions
{
    private const int Caron = 15;

    public static void Run()
    {
        ContinuationMarkerKeepsLaterDiacritics();
        DeleteKeepsUncorrectableTripletsHidden();
        HiddenCharactersAreRemoved();
    }

    private static void Set(TeletextPage page, int column, int row)
    {
        if (!PageAssembler.TrySetLevel15Diacritic(page, column, row, 'c', Caron, out string error))
            throw new Exception(error);
    }

    private static bool Shown(TeletextPage page, int column, int row) =>
        !string.IsNullOrEmpty(page.Grid[column, row].EnhancementText);

    private static TeletextPage PageWithContinuationMarker()
    {
        var page = new TeletextPage { Magazine = 1 };
        // Ten characters in one row move the termination marker to slot 11, so the
        // appended diacritic needs a continuation marker and a second packet.
        for (int column = 0; column < 10; column++) Set(page, column, 10);
        if (!PageAssembler.TryAppendLevel15DiacriticPacket(page, 4, 20, 'c', Caron, out string error))
            throw new Exception(error);
        if (page.EnhancementPackets.Count != 2 || !Shown(page, 4, 20))
            throw new Exception("The appended diacritic was not stored in a second packet.");
        return page;
    }

    private static void ContinuationMarkerKeepsLaterDiacritics()
    {
        TeletextPage page = PageWithContinuationMarker();

        Set(page, 3, 5);
        if (!Shown(page, 4, 20) || !Shown(page, 3, 5) || !Shown(page, 9, 10))
            throw new Exception("Typing a diacritic dropped the diacritics after a continuation marker.");
        // The raw-preserving path (used when a triplet is uncorrectable) must not
        // shift the continuation marker's padding into the middle of the data.
        page = PageWithContinuationMarker();
        if (!PageAssembler.TryInsertLevel15DiacriticRawPreserving(page, 6, 15, 'c', Caron, out string error))
            throw new Exception(error);
        if (!Shown(page, 4, 20) || !Shown(page, 6, 15) || !Shown(page, 9, 10))
            throw new Exception("A raw-preserving insert hid diacritics after a continuation marker.");
        page = PageWithContinuationMarker();
        if (!PageAssembler.DeleteEnhancementTriplet(page, page.EnhancementPackets[0], 1))
            throw new Exception("The triplet was not deleted.");
        if (!Shown(page, 4, 20) || Shown(page, 0, 10) || !Shown(page, 9, 10))
            throw new Exception("Deleting a triplet hid diacritics after a continuation marker.");
        Console.WriteLine("PASS: typing a diacritic keeps X/26 data after a continuation marker");
    }

    private static void DeleteKeepsUncorrectableTripletsHidden()
    {
        var page = new TeletextPage { Magazine = 1 };
        Set(page, 1, 3);
        Set(page, 2, 3);
        Set(page, 3, 3);
        EnhancementPacket packet = page.EnhancementPackets[0];
        // Two flipped bits make the third character uncorrectable, so it is not shown.
        byte[] raw = (byte[])packet.RawPacket.Clone();
        raw[3 + 3 * 3 + 1] ^= 0x03;
        PageAssembler.ReplaceEnhancementPackets(page, [(raw, packet.PacketIndex)]);
        if (!page.EnhancementPackets[0].Triplets[3].UncorrectableError || Shown(page, 3, 3))
            throw new Exception("The test triplet did not become uncorrectable.");

        if (!PageAssembler.DeleteEnhancementTriplet(page, page.EnhancementPackets[0], 1))
            throw new Exception("The triplet was not deleted.");
        if (Shown(page, 1, 3) || Shown(page, 3, 3) || !Shown(page, 2, 3)
            || !page.EnhancementPackets[0].Triplets[2].UncorrectableError)
            throw new Exception("Deleting a triplet re-encoded an uncorrectable one into a visible character.");
        Console.WriteLine("PASS: deleting an X/26 triplet keeps uncorrectable triplets hidden");
    }

    private static void HiddenCharactersAreRemoved()
    {
        var page = new TeletextPage { Magazine = 1 };
        var doubleHeightRow = new byte[42];
        Array.Fill(doubleHeightRow, (byte)0x20, 2, 40);
        doubleHeightRow[2] = 0x0D;
        PageAssembler.ApplyRow(page, 10, doubleHeightRow);
        Set(page, 5, 11);
        Set(page, 5, 12);
        if (!Shown(page, 5, 11))
            throw new Exception("The hidden test diacritic was not decoded.");

        int removed = PageAssembler.RemoveHiddenEnhancementCharacters(page);
        if (removed != 1 || Shown(page, 5, 11) || !Shown(page, 5, 12))
            throw new Exception($"Hidden X/26 clean-up removed {removed} triplet(s).");
        if (PageAssembler.RemoveHiddenEnhancementCharacters(page) != 0)
            throw new Exception("A second clean-up still found hidden X/26 characters.");
        Console.WriteLine("PASS: X/26 characters in a double-height bottom-half row are removed");
    }
}
