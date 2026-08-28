namespace TeletextRecoveReese.Core;

/// <summary>ETSI EN 300 706 Latin G0 national-option decoding.</summary>
public static class CharacterSets
{
    private static readonly byte[] NationalPositions =
        [0x23, 0x24, 0x40, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x7B, 0x7C, 0x7D, 0x7E];

    // C12-C14 values 0..6 in the legacy Western/Central Europe designation.
    private static readonly char[][] WesternNationalSubsets =
    [
        ['£', '$', '@', '←', '½', '→', '↑', '#', '—', '¼', '‖', '¾', '÷'],
        ['#', '$', '§', 'Ä', 'Ö', 'Ü', '^', '_', '°', 'ä', 'ö', 'ü', 'ß'],
        ['#', '¤', 'É', 'Ä', 'Ö', 'Å', 'Ü', '_', 'é', 'ä', 'ö', 'å', 'ü'],
        ['£', '$', 'é', '°', 'ç', '→', '↑', '#', 'ù', 'à', 'ò', 'è', 'ì'],
        ['é', 'ï', 'à', 'ë', 'ê', 'ù', 'î', '#', 'è', 'â', 'ô', 'û', 'ç'],
        ['ç', '$', '¡', 'á', 'é', 'í', 'ó', 'ú', '¿', 'ü', 'ñ', 'è', 'à'],
        ['#', 'ů', 'č', 'ť', 'ž', 'ý', 'í', 'ř', 'é', 'á', 'ě', 'ú', 'š'],
    ];

    public static char Decode(byte g0Code, int nationalOption = -1)
    {
        if (g0Code is < 0x20 or > 0x7F) return ' ';
        if (nationalOption is >= 0 and < 7)
        {
            int position = Array.IndexOf(NationalPositions, g0Code);
            if (position >= 0) return WesternNationalSubsets[nationalOption][position];
        }

        return g0Code switch
        {
            0x24 => '¤',
            0x7C => '¦',
            0x7F => '■',
            _ => (char)g0Code,
        };
    }
}
