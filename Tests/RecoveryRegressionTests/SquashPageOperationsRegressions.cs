using System.Numerics;
using TeletextRecoveReese.Core;

internal static class SquashPageOperationsRegressions
{
    private static byte Odd(int value) => (byte)(value | (BitOperations.PopCount((uint)value) % 2 == 0 ? 128 : 0));
    private static byte[] Packet(int row)
    {
        var packet = Enumerable.Repeat(Odd(32), 42).ToArray();
        int address = row << 3 | 1;
        packet[0] = Hamming.Encode84(address & 15); packet[1] = Hamming.Encode84(address >> 4);
        if (row == 0) for (int index = 2; index < 10; index++) packet[index] = Hamming.Encode84(0);
        return packet;
    }
    public static void Run()
    {
        var source = new TeletextPage { Magazine = 1, PageNumber = 0, SubPage = 0x1234 };
        byte[] header = Packet(0);
        PageStore.RewriteSubpageHeader(header, 0x1234);
        header[5] = Hamming.Encode84(Hamming.Decode84(header[5]).Value | 8);
        PageAssembler.ApplyRow(source, 0, header, 10);
        var body = Packet(1); body[2] = Odd('A');
        PageAssembler.ApplyRow(source, 1, body, 11);
        PageAssembler.ApplyRow(source, 25, Packet(25), 12);
        PageAssembler.SetFastextLink(source, 0, 0x234, 0x0001);
        PageAssembler.SetFastextLink(source, 1, 0x856, 0x0002);
        byte[] originalHeader = (byte[])source.RawRows[0]!.Clone();
        byte[] originalFastext = (byte[])source.FastextPacket!.Clone();
        var moved = SquashPageOperations.Renumber(source, 8, 0xAB);
        if (moved.Magazine != 8 || moved.PageNumber != 0xAB || moved.SubPage != 0x1234
            || moved.Grid[0, 1].Character != 'A' || moved.RawRows[25] is null
            || moved.RawRowPacketIndices.Any(index => index != -1)
            || Hamming.Decode84(moved.RawRows[0]![2]).Value != 11
            || Hamming.Decode84(moved.RawRows[0]![3]).Value != 10
            || Hamming.Decode84(moved.RawRows[0]![5]).Value != Hamming.Decode84(header[5]).Value)
            throw new Exception("Renumber lost content, subpage, CRC row or header flags.");
        if (moved.FastextLinks[0].PageNumber != 0x234 || moved.FastextLinks[1].PageNumber != 0x856
            || moved.FastextLinks[0].SubPage != 1 || moved.FastextLinks[1].SubPage != 2)
            throw new Exception("Moving to another magazine changed Fastext targets.");
        foreach (var raw in moved.RawRows.Where(row => row is not null).Append(moved.FastextPacket))
            if ((Hamming.Decode84(raw![0]).Value & 7) != 0)
                throw new Exception("A moved packet still has the source magazine.");
        if (!source.RawRows[0]!.SequenceEqual(originalHeader) || !source.FastextPacket!.SequenceEqual(originalFastext)
            || source.Magazine != 1 || source.RawRowPacketIndices[0] != 10)
            throw new Exception("Renumber changed source data.");
        moved.RawRows[1]![2] = Odd('B');
        if (source.Grid[0, 1].Character != 'A' || source.RawRows[1]![2] != Odd('A'))
            throw new Exception("Renumbered page shares mutable source packets.");
        Console.WriteLine("PASS: renumber preserves body, subpage, header flags, CRC row and absolute Fastext targets without changing source packets");
        CopiedRowsKeepTargetAddress();
        CopiedPageCrcKeepsFastextLinks();
    }

    private static void CopiedPageCrcKeepsFastextLinks()
    {
        var linked = new TeletextPage { Magazine = 1, PageNumber = 0x00 };
        PageAssembler.ApplyRow(linked, 0, Packet(0));
        PageAssembler.SetFastextLink(linked, 0, 0x234, 0x0001);
        byte[] before = (byte[])linked.FastextPacket!.Clone();
        PageAssembler.SetPageCrc(linked, 0xBEEF);
        if (TeletextPageCrc.ReadTransmitted(linked) != 0xBEEF
            || linked.FastextLinks[0].PageNumber != 0x234 || linked.FastextLinks[0].SubPage != 1
            || !linked.FastextPacket!.AsSpan(0, 40).SequenceEqual(before.AsSpan(0, 40)))
            throw new Exception("Setting the page CRC changed the Fastext links.");

        var unlinked = new TeletextPage { Magazine = 8, PageNumber = 0x42 };
        PageAssembler.ApplyRow(unlinked, 0, Retarget(Packet(0), 8, 0x42));
        PageAssembler.SetPageCrc(unlinked, 0x1234);
        byte[] created = unlinked.FastextPacket!;
        int address = Hamming.Decode84(created[0]).Value | Hamming.Decode84(created[1]).Value << 4;
        if (TeletextPageCrc.ReadTransmitted(unlinked) != 0x1234 || address != (27 << 3)
            || unlinked.FastextLinks.Any(link => link.PageNumber != 0x8FF))
            throw new Exception("Setting a CRC on a page without X/27/0 did not create a valid packet.");
        Console.WriteLine("PASS: copying a page CRC keeps Fastext links and creates X/27/0 when missing");
    }

    private static byte[] Retarget(byte[] packet, int magazine, int pageNumber = -1)
    {
        int row = Hamming.Decode84(packet[0]).Value >> 3 | Hamming.Decode84(packet[1]).Value << 1;
        int address = (magazine & 7) | row << 3;
        packet[0] = Hamming.Encode84(address & 15); packet[1] = Hamming.Encode84(address >> 4);
        if (pageNumber >= 0)
        {
            packet[2] = Hamming.Encode84(pageNumber & 15); packet[3] = Hamming.Encode84(pageNumber >> 4);
        }
        return packet;
    }

    private static void CopiedRowsKeepTargetAddress()
    {
        // Source: broadcast page 2C5/0003 with its own clock text and control bits.
        var sourceHeader = Retarget(Packet(0), 2, 0xC5);
        PageStore.RewriteSubpageHeader(sourceHeader, 0x0003);
        sourceHeader[8] = Hamming.Encode84(0x0F);
        sourceHeader[10] = Odd('2'); sourceHeader[41] = Odd('9');
        var sourceBody = Retarget(Packet(3), 2);
        sourceBody[2] = Odd('Z');

        // Target: squash page 100/0000 whose header and body are already captured.
        var target = new TeletextPage { Magazine = 1, PageNumber = 0x00, SubPage = 0 };
        byte[] targetHeader = Packet(0);
        PageAssembler.ApplyRow(target, 0, targetHeader, 0);
        PageAssembler.ApplyRow(target, 3, Packet(3), 3);

        byte[] header = SquashPageOperations.CopyRowContent(sourceHeader, target, 0);
        byte[] body = SquashPageOperations.CopyRowContent(sourceBody, target, 3);
        if (!header.AsSpan(0, 10).SequenceEqual(targetHeader.AsSpan(0, 10)))
            throw new Exception("A copied header replaced the target's page address, subcode or control bits.");
        if (!header.AsSpan(10).SequenceEqual(sourceHeader.AsSpan(10)))
            throw new Exception("A copied header did not take the source's display bytes.");
        if ((Hamming.Decode84(body[0]).Value & 7) != 1 || Hamming.Decode84(body[0]).Value >> 3 != 1
            || !body.AsSpan(2).SequenceEqual(sourceBody.AsSpan(2)))
            throw new Exception("A copied body row kept the source magazine or lost its display bytes.");

        // A target without a header gets one addressed to itself, not to the source.
        var empty = new TeletextPage { Magazine = 8, PageNumber = 0x42, SubPage = 0x0007 };
        byte[] created = SquashPageOperations.CopyRowContent(sourceHeader, empty, 0);
        var store = new PageStore();
        var assembler = new PageAssembler(store);
        assembler.Feed(created, 0);
        assembler.FinalizeAll();
        if (store.GetKnownAddresses().SingleOrDefault() != (8, 0x42, 0x0007))
            throw new Exception("A header copied onto an empty page was not addressed to that page.");
        if (!created.AsSpan(10).SequenceEqual(sourceHeader.AsSpan(10)))
            throw new Exception("A header copied onto an empty page lost the source display bytes.");

        // A row missing from the capture clears the target row, still at its address.
        byte[] cleared = SquashPageOperations.CopyRowContent(null, target, 3);
        if ((Hamming.Decode84(cleared[0]).Value & 7) != 1 || cleared.Skip(2).Any(value => value != 0x20))
            throw new Exception("Transferring an uncaptured row did not clear the target row.");
        if (!target.RawRows[0]!.SequenceEqual(targetHeader))
            throw new Exception("CopyRowContent changed the target page.");
        Console.WriteLine("PASS: transferring a row keeps the target page's magazine, number, subcode and control bits");
    }
}
