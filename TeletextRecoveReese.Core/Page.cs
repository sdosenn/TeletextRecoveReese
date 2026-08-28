namespace TeletextRecoveReese.Core;

public enum TeletextColor
{
    Black, Red, Green, Yellow, Blue, Magenta, Cyan, White
}

/// <summary>A single cell in a teletext page's 40x24 grid.</summary>
public struct Cell
{
    public char Character;
    /// <summary>
    /// Optional Unicode text supplied by a Level 1.5 X/26 enhancement. It is a
    /// decoded display value only; RawRows and EnhancementPackets remain the
    /// byte-level source of truth.
    /// </summary>
    public string? EnhancementText;
    /// <summary>
    /// G0 source character and ETS 300 706 diacritical index used to render an
    /// X/26 character without requiring a precomposed Unicode glyph in the font.
    /// </summary>
    public char EnhancementBaseCharacter;
    public int EnhancementDiacritical;
    public string? EnhancementDescription;
    public int EnhancementDesignationCode;
    public int EnhancementTripletNumber;
    public TeletextColor Foreground;
    public TeletextColor Background;
    public bool DoubleHeight;
    public bool DoubleWidth;
    public bool Flash;
    public bool IsMosaic;
    public bool MosaicHeld;
    public bool HoldMosaics;
    public bool MosaicSeparated; // true = small gaps between sixels (0x1A), false = contiguous (0x19, default)
    public byte MosaicPattern; // 6-bit sixel value (0-63), meaningful only when IsMosaic is true
    public bool Boxed;
    public bool Conceal;

    public static Cell Default => new()
    {
        Character = ' ',
        Foreground = TeletextColor.White,
        Background = TeletextColor.Black,
        EnhancementDiacritical = -1,
        EnhancementDesignationCode = -1,
        EnhancementTripletNumber = -1,
    };
}

/// <summary>Fastext link (row 24 / X/27/0) - up to 6 links: 4 fastext buttons + index + next.</summary>
public class FastextLink
{
    public int PageNumber { get; set; } // e.g. 0x123 (BCD, magazine+page)
    public int SubPage { get; set; }
    public string Label { get; set; } = string.Empty;
}

public sealed class EnhancementTriplet
{
    public int DesignationCode { get; init; }
    public int TripletNumber { get; init; }
    public int Address { get; init; }
    public int Mode { get; init; }
    public int Data { get; init; }
    public bool CorrectedError { get; init; }
    public bool UncorrectableError { get; init; }

    public int ExtendedMode => Address >= 40 ? Mode : Mode + 0x20;
}

public sealed class EnhancementPacket
{
    public int DesignationCode { get; init; }
    public int PacketIndex { get; init; }
    public byte[] RawPacket { get; init; } = Array.Empty<byte>();
    public List<EnhancementTriplet> Triplets { get; } = new();
}

public class TeletextPage
{
    public int Magazine { get; set; } // 1-8
    public int PageNumber { get; set; } // 00-FF (BCD)
    public int SubPage { get; set; }
    /// <summary>Legacy Latin G0 national-option value from header bits C12-C14.</summary>
    public int NationalOption { get; set; }
    /// <summary>Optional display override; -1 selects the unmodified Latin G0 set.</summary>
    public int? NationalOptionOverride { get; set; }

    /// <summary>40 columns x 25 rows. Row index maps directly to teletext row number:
    /// index 0 = header (row 0), indices 1-24 = body text (rows 1-24).</summary>
    public Cell[,] Grid { get; } = new Cell[40, 25];

    /// <summary>The exact 42-byte packet received for each row (null if that row was
    /// never captured for this instance). This is the byte-level source of truth -
    /// Grid is a decoded VIEW of these bytes, not the other way around. Keeping the
    /// raw bytes means a transfer or save can write back exactly what was broadcast,
    /// bit-for-bit, rather than only a re-encoded approximation of the decoded text.</summary>
    public byte[]?[] RawRows { get; } = new byte[25][];

    /// <summary>Index of each display row's source packet in the complete capture.
    /// A value of -1 means that no packet for that row existed in the loaded file.</summary>
    public int[] RawRowPacketIndices { get; } = new int[25];

    public List<FastextLink> FastextLinks { get; } = new(); // Level 1.5
    public List<EnhancementPacket> EnhancementPackets { get; } = new(); // X/26/0-X/26/15
    public List<string> SidePanelRows { get; } = new(); // Level 2.5+, reserved for later

    public bool Newsflash { get; set; }
    public bool Subtitle { get; set; }
    public bool Suppress { get; set; }

    public TeletextPage()
    {
        Array.Fill(RawRowPacketIndices, -1);
        for (int y = 0; y < 25; y++)
            for (int x = 0; x < 40; x++)
                Grid[x, y] = Cell.Default;
    }

    public string RowText(int row)
    {
        var chars = new char[40];
        for (int x = 0; x < 40; x++)
            chars[x] = Grid[x, row].Character;
        return new string(chars);
    }
}
