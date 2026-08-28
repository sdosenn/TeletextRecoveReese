namespace TeletextRecoveReese.Core;

/// <summary>
/// In-memory store of every page instance seen in a capture, organized for the kind
/// of drill-down navigation Teletext Meddler's Address bar does: pick a page, pick a
/// subpage, pick which captured version of it to look at.
/// </summary>
public class PageStore
{
    // (magazine, page, subpage) -> all instances captured of that exact address, in
    // the order they were captured (VersionIndex == index into this list).
    private readonly Dictionary<(int magazine, int page, int subpage), List<PageInstance>> _instances = new();
    private readonly List<PageInstance> _allInstances = new();

    /// <summary>Fired whenever a new instance is added (new header cycle completed).</summary>
    public event Action<PageInstance>? InstanceAdded;

    public void AddInstance(PageInstance instance)
    {
        var key = (instance.Magazine, instance.PageNumber, instance.Subpage);
        if (!_instances.TryGetValue(key, out var list))
        {
            list = new List<PageInstance>();
            _instances[key] = list;
        }

        instance.VersionIndex = list.Count;
        list.Add(instance);
        _allInstances.Add(instance);

        InstanceAdded?.Invoke(instance);
    }

    /// <summary>Distinct magazines seen so far, sorted for a magazine picker.</summary>
    public IEnumerable<int> GetKnownMagazines()
    {
        return _instances.Keys
            .Select(k => k.magazine)
            .Distinct()
            .OrderBy(m => m);
    }

    /// <summary>Distinct page numbers seen for a given magazine, sorted for a page picker.</summary>
    public IEnumerable<int> GetKnownPageNumbers(int magazine)
    {
        return _instances.Keys
            .Where(k => k.magazine == magazine)
            .Select(k => k.page)
            .Distinct()
            .OrderBy(p => p);
    }

    /// <summary>Distinct (magazine, page) pairs seen so far, sorted for a page picker.</summary>
    public IEnumerable<(int magazine, int page)> GetKnownPages()
    {
        return _instances.Keys
            .Select(k => (k.magazine, k.page))
            .Distinct()
            .OrderBy(p => p.magazine)
            .ThenBy(p => p.page);
    }

    /// <summary>Every distinct address in normal browsing order: magazine and page
    /// are the major components, while subpage changes fastest.</summary>
    public IEnumerable<(int magazine, int page, int subpage)> GetKnownAddresses()
    {
        return _instances.Keys
            .OrderBy(k => k.magazine)
            .ThenBy(k => k.page)
            .ThenBy(k => k.subpage);
    }

    /// <summary>Distinct subpages seen for a given (magazine, page), sorted for a
    /// subpage picker.</summary>
    public IEnumerable<int> GetKnownSubpages(int magazine, int page)
    {
        return _instances.Keys
            .Where(k => k.magazine == magazine && k.page == page)
            .Select(k => k.subpage)
            .Distinct()
            .OrderBy(s => s);
    }

    /// <summary>All captured versions/instances of one exact (magazine, page, subpage),
    /// in capture order - this is what the "version" combo box lists.</summary>
    public IReadOnlyList<PageInstance> GetInstances(int magazine, int page, int subpage)
    {
        return _instances.TryGetValue((magazine, page, subpage), out var list)
            ? list
            : Array.Empty<PageInstance>();
    }

    /// <summary>Removes every captured instance of one page/subpage address.</summary>
    public IReadOnlyList<PageInstance> RemoveAddress(int magazine, int page, int subpage)
    {
        var key = (magazine, page, subpage);
        if (!_instances.Remove(key, out var removed))
            return Array.Empty<PageInstance>();

        foreach (var instance in removed)
            _allInstances.Remove(instance);

        return removed;
    }

    public int TotalInstanceCount => _instances.Values.Sum(l => l.Count);

    public IReadOnlyList<PageInstance> AllInstances => _allInstances;

    public void Clear()
    {
        _instances.Clear();
        _allInstances.Clear();
    }
}
