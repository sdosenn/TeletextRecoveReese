namespace TeletextRecoveReese.Core;

public sealed record BroadcastBackPropagationResult(
    IReadOnlyList<byte[]> Packets,
    int ReplacedPackets,
    int MatchedPageTransmissions,
    int PaddingSlots);

/// <summary>
/// Applies repaired carousel pages to their occurrences in an as-broadcast T42
/// stream without adding, removing or reordering packet slots. This is the
/// line-aware equivalent of Teletext Recovery Editor's back-merge workflow.
/// </summary>
public static class BroadcastBackPropagator
{
    public static BroadcastBackPropagationResult Apply(
        IReadOnlyList<byte[]> broadcastPackets,
        IEnumerable<TeletextPage> restoredPages)
    {
        var repairs = restoredPages
            .GroupBy(page => (page.Magazine, page.PageNumber, page.SubPage))
            .ToDictionary(group => group.Key, group => group.Last());
        var activeRepairs = new TeletextPage?[9];
        var output = broadcastPackets.ToList();
        int replaced = 0;
        int matchedTransmissions = 0;
        int paddingSlots = 0;

        for (int index = 0; index < broadcastPackets.Count; index++)
        {
            byte[] source = broadcastPackets[index];
            if (TeletextPacket.IsPadding(source))
            {
                paddingSlots++;
                continue;
            }
            if (!TryDecodeAddress(source, out int magazine, out int row))
                continue;

            if (row == 0)
            {
                activeRepairs[magazine] = null;
                if (!TryDecodeHeaderAddress(source, magazine, out var address)
                    || !repairs.TryGetValue(address, out TeletextPage? repair))
                    continue;

                activeRepairs[magazine] = repair;
                matchedTransmissions++;

                // Preserve routing, subcode/control flags, date and clock from
                // the original transmission. The editable, non-dynamic part of
                // the visible header occupies packet bytes 10-26.
                if (repair.RawRows[0] is { Length: TeletextPacket.Length } header)
                {
                    byte[] merged = (byte[])source.Clone();
                    Array.Copy(header, 10, merged, 10, 17);
                    if (!source.AsSpan().SequenceEqual(merged))
                    {
                        output[index] = merged;
                        replaced++;
                    }
                }
                continue;
            }

            TeletextPage? page = activeRepairs[magazine];
            if (page is null) continue;

            byte[]? replacement = row switch
            {
                >= 1 and <= 25 => page.RawRows[row],
                26 => FindEnhancement(page, source),
                27 => FindFastext(page, source),
                _ => null,
            };
            if (replacement is not { Length: TeletextPacket.Length }) continue;
            if (source.AsSpan().SequenceEqual(replacement)) continue;

            output[index] = (byte[])replacement.Clone();
            replaced++;
        }

        return new BroadcastBackPropagationResult(
            output, replaced, matchedTransmissions, paddingSlots);
    }

    private static byte[]? FindEnhancement(TeletextPage page, byte[] source)
    {
        Hamming.HammingResult designation = Hamming.Decode84(source[2]);
        if (designation.UncorrectableError) return null;
        return page.EnhancementPackets
            .LastOrDefault(packet => packet.DesignationCode == designation.Value)?
            .RawPacket;
    }

    private static byte[]? FindFastext(TeletextPage page, byte[] source)
    {
        Hamming.HammingResult designation = Hamming.Decode84(source[2]);
        if (designation.UncorrectableError || designation.Value != 0) return null;
        return page.FastextPacket;
    }

    private static bool TryDecodeAddress(byte[] packet, out int magazine, out int row)
    {
        magazine = row = 0;
        if (packet.Length != TeletextPacket.Length) return false;
        Hamming.HammingResult low = Hamming.Decode84(packet[0]);
        Hamming.HammingResult high = Hamming.Decode84(packet[1]);
        if (low.UncorrectableError || high.UncorrectableError) return false;
        int address = low.Value | (high.Value << 4);
        row = (address >> 3) & 0x1F;
        int magazineBits = address & 0x07;
        magazine = magazineBits == 0 ? 8 : magazineBits;
        return true;
    }

    private static bool TryDecodeHeaderAddress(
        byte[] packet,
        int magazine,
        out (int Magazine, int Page, int Subpage) address)
    {
        address = default;
        Hamming.HammingResult units = Hamming.Decode84(packet[2]);
        Hamming.HammingResult tens = Hamming.Decode84(packet[3]);
        if (units.UncorrectableError || tens.UncorrectableError) return false;

        Hamming.HammingResult sub1Low = Hamming.Decode84(packet[4]);
        Hamming.HammingResult sub1High = Hamming.Decode84(packet[5]);
        Hamming.HammingResult sub2Low = Hamming.Decode84(packet[6]);
        Hamming.HammingResult sub2High = Hamming.Decode84(packet[7]);
        int subpage = 0;
        if (!sub1Low.UncorrectableError && !sub1High.UncorrectableError
            && !sub2Low.UncorrectableError && !sub2High.UncorrectableError)
        {
            int subWord1 = sub1Low.Value | (sub1High.Value << 4);
            int subWord2 = sub2Low.Value | (sub2High.Value << 4);
            subpage = (subWord1 | (subWord2 << 8)) & 0x3F7F;
        }

        address = (magazine, units.Value | (tens.Value << 4), subpage);
        return true;
    }
}
