using System.Globalization;
using System.Numerics;
using System.Text;

namespace TeletextRecoveReese.Core;

/// <summary>
/// Converts MRG/vbit2/QTeletextMaker TTI page files into T42 packets: one
/// header, the FL links (X/27/0), OL,26-28 enhancement packets and display rows
/// per subpage. Pages from several files are collected into one packet list.
/// </summary>
public sealed class TtiImporter
{
    private const byte Escape = 0x1B;

    private readonly List<byte[]> _packets = [];
    private readonly HashSet<(int Page, int Subcode)> _subpages = [];

    public IReadOnlyList<byte[]> Packets => _packets;

    public int SubpageCount => _subpages.Count;

    /// <summary>Reads one TTI file; returns how many subpages it contained.</summary>
    public int Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] data = buffer.ToArray();

        int subpages = 0;
        TtiSubpage? current = null;
        int start = 0;
        while (start < data.Length)
        {
            int end = Array.IndexOf(data, (byte)'\n', start);
            if (end < 0) end = data.Length;
            int lineEnd = end > start && data[end - 1] == '\r' ? end - 1 : end;
            ReadOnlySpan<byte> line = data.AsSpan(start, lineEnd - start);
            start = end + 1;

            if (line.Length < 3 || line[2] != ',') continue;
            string command = Encoding.ASCII.GetString(line[..2]).ToUpperInvariant();
            ReadOnlySpan<byte> value = line[3..];
            switch (command)
            {
                case "PN":
                    if (current is not null && Emit(current)) subpages++;
                    current = TtiSubpage.FromPageNumber(Ascii(value));
                    break;
                case "SC" when current is not null:
                    if (TryParseHex(Ascii(value), out int subcode)) current.Subcode = subcode & 0x3F7F;
                    break;
                case "PS" when current is not null:
                    if (TryParseHex(Ascii(value), out int status)) current.Status = status;
                    break;
                case "FL" when current is not null:
                    current.Links = Ascii(value).Split(',');
                    break;
                case "OL" when current is not null:
                    int comma = value.IndexOf((byte)',');
                    if (comma <= 0 || !int.TryParse(Ascii(value[..comma]), out int row)) break;
                    if (row is >= 0 and <= 24)
                        current.Rows[row] = value[(comma + 1)..].ToArray();
                    else if (row is >= 26 and <= 28)
                        current.Enhancements.Add((row, value[(comma + 1)..].ToArray()));
                    break;
            }
        }
        if (current is not null && Emit(current)) subpages++;
        return subpages;
    }

    private bool Emit(TtiSubpage subpage)
    {
        if (subpage.Page < 0) return false;
        // Every subpage needs its own address, or the pages would read as repeats of
        // one page from a broadcast.
        int subcode = subpage.Subcode ?? subpage.PageNumberSubpage;
        if (_subpages.Contains((subpage.Page, subcode)))
        {
            subcode = subpage.PageNumberSubpage;
            while (_subpages.Contains((subpage.Page, subcode))) subcode = NextSubcode(subcode);
        }
        _subpages.Add((subpage.Page, subcode));

        int magazine = subpage.Page >> 8;
        _packets.Add(Header(subpage, magazine, subcode));
        if (subpage.Links is { } links && Fastext(magazine, links) is { } fastext) _packets.Add(fastext);
        // One OL line per designation code; send them as X/27, X/28, then X/26.
        foreach (int row in (int[])[27, 28, 26])
            foreach ((int enhancementRow, byte[] enhancement) in subpage.Enhancements)
                if (enhancementRow == row && Enhancement(magazine, row, enhancement) is { } packet)
                    _packets.Add(packet);
        for (int row = 1; row <= 24; row++)
        {
            if (subpage.Rows[row] is not { } text) continue;
            byte[] packet = Packet(magazine, row);
            WriteText(packet.AsSpan(2), DecodeText(text));
            _packets.Add(packet);
        }
        return true;
    }

    private static int NextSubcode(int subcode)
    {
        // Subpage numbers count in decimal digits, like 0009 → 0010.
        int next = subcode + 1;
        if ((next & 0x0F) > 9) next = (next & ~0x0F) + 0x10;
        return next & 0x3F7F;
    }

    private static byte[] Header(TtiSubpage subpage, int magazine, int subcode)
    {
        byte[] packet = Packet(magazine, 0);
        int status = subpage.Status;
        // PS bits as QTeletextMaker reads them: 0x4000 is C4, 0x0001-0x0040 are
        // C5-C11 and 0x0200/0x0100/0x0080 are the national option bits C12-C14.
        int c4 = (status >> 14) & 1;
        int c5To11 = status & 0x7F;
        int nationalOption = ((status >> 9) & 1) | ((status >> 7) & 2) | ((status >> 5) & 4);
        packet[2] = Hamming.Encode84(subpage.Page & 0x0F);
        packet[3] = Hamming.Encode84((subpage.Page >> 4) & 0x0F);
        packet[4] = Hamming.Encode84(subcode & 0x0F);
        packet[5] = Hamming.Encode84(((subcode >> 4) & 0x07) | (c4 << 3));
        packet[6] = Hamming.Encode84((subcode >> 8) & 0x0F);
        packet[7] = Hamming.Encode84(((subcode >> 12) & 0x03) | ((c5To11 & 0x03) << 2));
        packet[8] = Hamming.Encode84((c5To11 >> 2) & 0x0F);
        packet[9] = Hamming.Encode84(((c5To11 >> 6) & 0x01) | (nationalOption << 1));

        // Columns 0-7 of OL,0 hold the page number placeholder.
        byte[] text = subpage.Rows[0] is { } row0 ? DecodeText(row0) : DecodeText([]);
        WriteText(packet.AsSpan(10), text.AsSpan(8));
        return packet;
    }

    private static byte[]? Fastext(int magazine, string[] links)
    {
        if (links.Length < 6) return null;
        byte[] packet = Packet(magazine, 27);
        packet[2] = Hamming.Encode84(0);
        for (int link = 0; link < 6; link++)
        {
            int target = TryParseHex(links[link], out int page) && page is >= 0x100 and <= 0x8FF ? page : 0x8FF;
            int relativeMagazine = ((target >> 8) & 0x07) ^ (magazine & 0x07);
            int offset = 3 + link * 6;
            EncodeByte(packet, offset, target & 0xFF);
            EncodeByte(packet, offset + 2, 0x7F | ((relativeMagazine & 0x01) << 7));
            EncodeByte(packet, offset + 4, 0x3F | ((relativeMagazine & 0x06) << 5));
        }
        // Link control as PageAssembler writes it; bytes 40-41 are the page CRC.
        packet[39] = Hamming.Encode84(0);
        return packet;
    }

    /// <summary>
    /// QTeletextMaker stores packets 26-28 as the designation code and 13 triplets,
    /// each triplet as three characters carrying six bits plus 0x40.
    /// </summary>
    private static byte[]? Enhancement(int magazine, int row, byte[] text)
    {
        if (text.Length == 0 || text[0] is < 0x40 or > 0x4F) return null;
        if (text.Length < 40)
        {
            // Like QTeletextMaker: complete a short X/26 with dummy triplets
            // (address 41, mode 0x1E) and other packets with zero data.
            int whole = (text.Length - 1) / 3 * 3 + 1;
            var padded = new byte[40];
            text.AsSpan(0, whole).CopyTo(padded);
            for (int i = whole; i + 2 < 40; i += 3)
            {
                padded[i] = (byte)(row == 26 ? 'i' : '@');
                padded[i + 1] = (byte)(row == 26 ? '^' : '@');
                padded[i + 2] = (byte)'@';
            }
            text = padded;
        }
        int designation = text[0] & 0x0F;
        // X/27/0-3 are Hamming 8/4 editorial links, written with FL instead.
        if (row == 27 && designation < 4) return null;
        byte[] packet = Packet(magazine, row);
        packet[2] = Hamming.Encode84(designation);
        for (int triplet = 0; triplet < 13; triplet++)
        {
            int index = 1 + triplet * 3;
            int value = (text[index] & 0x3F) | ((text[index + 1] & 0x3F) << 6) | ((text[index + 2] & 0x3F) << 12);
            int encoded = Hamming.Encode24_18(value);
            packet[3 + triplet * 3] = (byte)encoded;
            packet[4 + triplet * 3] = (byte)(encoded >> 8);
            packet[5 + triplet * 3] = (byte)(encoded >> 16);
        }
        return packet;
    }

    /// <summary>
    /// Display characters: ESC followed by code+0x40, a byte with bit 7 set, 0x10 for
    /// double height (0x0D) or the byte itself. Rows shorter than 40 characters are
    /// padded with spaces.
    /// </summary>
    private static byte[] DecodeText(byte[] text)
    {
        var decoded = new byte[40];
        Array.Fill(decoded, (byte)0x20);
        int column = 0;
        for (int index = 0; index < text.Length && column < 40; index++)
        {
            byte value = text[index];
            if (value == Escape && index + 1 < text.Length)
                value = (byte)(text[++index] & 0xBF);
            else if (value == 0x10)
                value = 0x0D;
            decoded[column++] = (byte)(value & 0x7F);
        }
        return decoded;
    }

    private static void WriteText(Span<byte> destination, ReadOnlySpan<byte> text)
    {
        for (int i = 0; i < text.Length && i < destination.Length; i++)
        {
            byte value = (byte)(text[i] & 0x7F);
            destination[i] = BitOperations.PopCount(value) % 2 == 0 ? (byte)(value | 0x80) : value;
        }
    }

    private static byte[] Packet(int magazine, int row)
    {
        var packet = new byte[42];
        int address = (magazine & 0x07) | (row << 3);
        packet[0] = Hamming.Encode84(address & 0x0F);
        packet[1] = Hamming.Encode84((address >> 4) & 0x0F);
        return packet;
    }

    private static void EncodeByte(byte[] destination, int offset, int value)
    {
        destination[offset] = Hamming.Encode84(value & 0x0F);
        destination[offset + 1] = Hamming.Encode84((value >> 4) & 0x0F);
    }

    private static string Ascii(ReadOnlySpan<byte> value) => Encoding.Latin1.GetString(value).Trim();

    private static bool TryParseHex(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

    private sealed class TtiSubpage
    {
        public int Page { get; private init; } = -1;
        public int PageNumberSubpage { get; private init; }
        public int? Subcode { get; set; }
        public int Status { get; set; } = 0x8000;
        public string[]? Links { get; set; }
        public byte[]?[] Rows { get; } = new byte[]?[25];
        public List<(int Row, byte[] Text)> Enhancements { get; } = [];

        /// <summary>PN,mppss: a hexadecimal page number and two subpage digits.</summary>
        public static TtiSubpage FromPageNumber(string value)
        {
            if (value.Length < 3 || !TryParseHex(value[..3], out int page) || page is < 0x100 or > 0x8FF)
                return new TtiSubpage();
            int subpage = value.Length >= 5 && TryParseHex(value[3..5], out int parsed) ? parsed & 0x7F : 0;
            return new TtiSubpage { Page = page, PageNumberSubpage = subpage };
        }
    }
}
