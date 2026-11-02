namespace TeletextRecoveReese.Core;

/// <summary>Level 1 layout rules shared by the renderer and X/26 clean-up.</summary>
public static class TeletextLayout
{
    public const int Rows = 25;
    public const int Columns = 40;

    /// <summary>
    /// EN 300 706 / QTeletextMaker Level-1 behaviour: if any Double Height (0x0D)
    /// or Double Size (0x0F) attribute occurs in a row, the entire next row is
    /// reserved for bottom halves and its own content is not shown.
    /// </summary>
    public static bool[] FindBottomHalfRows(TeletextPage page)
    {
        var bottomHalfRows = new bool[Rows];

        // Include the final display row as a bottom half when the penultimate
        // row contains a height attribute, so both halves render together.
        for (int row = 1; row < Rows - 1; row++)
        {
            bool hasDoubleHeightAttribute = false;
            if (page.RawRows[row] is { Length: 42 } raw)
            {
                for (int column = 0; column < Columns; column++)
                {
                    byte code = (byte)(raw[2 + column] & 0x7F);
                    if (code is 0x0D or 0x0F)
                    {
                        hasDoubleHeightAttribute = true;
                        break;
                    }
                }
            }

            if (hasDoubleHeightAttribute)
            {
                bottomHalfRows[row + 1] = true;
                row++; // a bottom-half row cannot itself begin another pair
            }
        }

        return bottomHalfRows;
    }

    public static bool IsDoubleWidthLeadCell(TeletextPage page, int column, int row)
    {
        // The size control occupies the first cell of a double-width run. After
        // it, alternating cells contain a character and its reserved right half.
        int runStart = column;
        while (runStart > 0 && page.Grid[runStart - 1, row].DoubleWidth)
            runStart--;

        return column > runStart && (column - runStart) % 2 == 1;
    }
}
