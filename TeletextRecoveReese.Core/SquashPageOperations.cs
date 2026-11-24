namespace TeletextRecoveReese.Core;

public static class SquashPageOperations
{
    /// <summary>Build an independent, unsourced page at a new address.</summary>
    public static TeletextPage Renumber(TeletextPage source, int magazine, int pageNumber)
    {
        if (magazine is < 1 or > 8 || pageNumber is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var result = new TeletextPage
        {
            Magazine = magazine, PageNumber = pageNumber, SubPage = source.SubPage,
            NationalOptionOverride = source.NationalOptionOverride,
            SecondaryNationalOptionOverride = source.SecondaryNationalOptionOverride,
        };
        byte[] Move(byte[] raw)
        {
            var copy = (byte[])raw.Clone();
            int address = Hamming.Decode84(raw[0]).Value | Hamming.Decode84(raw[1]).Value << 4;
            address = (address & ~7) | (magazine & 7);
            copy[0] = Hamming.Encode84(address & 15);
            copy[1] = Hamming.Encode84(address >> 4);
            return copy;
        }
        for (int row = 0; row < source.RawRows.Length; row++)
        {
            if (source.RawRows[row] is not { } raw) continue;
            byte[] copy = Move(raw);
            if (row == 0)
            {
                copy[2] = Hamming.Encode84(pageNumber & 15);
                copy[3] = Hamming.Encode84(pageNumber >> 4);
            }
            PageAssembler.ApplyRow(result, row, copy);
        }
        foreach (var charset in source.EffectiveCharacterSetPackets)
            PageAssembler.ApplyCharacterSetPacket(result, Move(charset.RawPacket));
        PageAssembler.ReplaceEnhancementPackets(result,
            source.EnhancementPackets.Select(packet => (Move(packet.RawPacket), -1)));
        if (source.FastextPacket is { } fastext)
        {
            byte[] copy = Move(fastext);
            // Fastext magazines are relative to the containing page's magazine.
            int delta = (source.Magazine & 7) ^ (magazine & 7);
            for (int link = 0; link < 6; link++)
            {
                int offset = 3 + link * 6;
                if (offset + 5 >= copy.Length) break;
                int low = Hamming.Decode84(copy[offset + 2]).Value | Hamming.Decode84(copy[offset + 3]).Value << 4;
                int high = Hamming.Decode84(copy[offset + 4]).Value | Hamming.Decode84(copy[offset + 5]).Value << 4;
                low ^= (delta & 1) << 7;
                high ^= (delta & 6) << 5;
                copy[offset + 2] = Hamming.Encode84(low & 15);
                copy[offset + 3] = Hamming.Encode84(low >> 4);
                copy[offset + 4] = Hamming.Encode84(high & 15);
                copy[offset + 5] = Hamming.Encode84(high >> 4);
            }
            PageAssembler.ApplyFastextPacket(result, copy);
        }
        PageAssembler.SetNationalOptionOverride(result, source.NationalOptionOverride);
        return result;
    }

    /// <summary>
    /// Builds a packet that shows <paramref name="sourceRow"/>'s display bytes in
    /// <paramref name="row"/> of <paramref name="target"/>. Addressing always stays
    /// the target's: the magazine/row address and, on row 0, the page number,
    /// subcode and control bits. Copying a header from another page must change
    /// only what the row shows, never which page the target is saved as.
    /// </summary>
    public static byte[] CopyRowContent(byte[]? sourceRow, TeletextPage target, int row)
    {
        if (row is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(row));
        byte[] packet = target.RawRows[row] is { Length: TeletextPacket.Length } existing
            ? (byte[])existing.Clone()
            : BlankPacket(target, row);
        int address = (target.Magazine & 7) | (row << 3);
        packet[0] = Hamming.Encode84(address & 15);
        packet[1] = Hamming.Encode84(address >> 4);

        int payloadStart = row == 0 ? 10 : 2;
        if (sourceRow is { Length: TeletextPacket.Length })
            Array.Copy(sourceRow, payloadStart, packet, payloadStart, TeletextPacket.Length - payloadStart);
        else
            // A row the capture never received still clears the target row.
            Array.Fill(packet, (byte)0x20, payloadStart, TeletextPacket.Length - payloadStart);
        return packet;
    }

    private static byte[] BlankPacket(TeletextPage page, int row)
    {
        var packet = new byte[TeletextPacket.Length];
        // A parity-protected teletext space is 0x20 (already has odd parity).
        Array.Fill(packet, (byte)0x20, 2, TeletextPacket.Length - 2);
        if (row == 0)
        {
            packet[2] = Hamming.Encode84(page.PageNumber & 15);
            packet[3] = Hamming.Encode84((page.PageNumber >> 4) & 15);
            for (int index = 4; index < 10; index++) packet[index] = Hamming.Encode84(0);
            PageStore.RewriteSubpageHeader(packet, page.SubPage);
        }
        return packet;
    }
}
