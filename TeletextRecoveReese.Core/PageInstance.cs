namespace TeletextRecoveReese.Core;

/// <summary>
/// One captured occurrence ("instance") of a page as it passed through the stream.
/// Two instances can have identical (magazine, page, subpage) but different bit
/// errors - that's the whole point of keeping them separate for recovery purposes.
/// </summary>
public class PageInstance
{
    public int Magazine { get; init; }
    public int PageNumber { get; init; } // hex page number, e.g. 0x00-0xFF
    public int Subpage { get; init; }

    /// <summary>0-based order in which this instance was captured, among all
    /// instances of this exact (magazine, page, subpage). Not a global packet index.</summary>
    public int VersionIndex { get; set; }

    /// <summary>The decoded grid for this specific occurrence.</summary>
    public TeletextPage Page { get; set; } = null!;

    /// <summary>Source packet positions for display rows 0–24 in a broadcast.</summary>
    public int[] BroadcastRowPacketIndices { get; } = Enumerable.Repeat(-1, 25).ToArray();

    /// <summary>Which body rows (1-24) actually received at least one packet for this
    /// instance - a quick completeness indicator for the UI (e.g. "18/24 rows
    /// captured") without having to inspect the grid manually.</summary>
    public HashSet<int> RowsReceived { get; } = new();
}
