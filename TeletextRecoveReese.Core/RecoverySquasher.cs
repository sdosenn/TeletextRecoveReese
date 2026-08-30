using System.Numerics;

namespace TeletextRecoveReese.Core;

public sealed class RecoverySquashOptions
{
    public int MinimumBodyRows { get; init; } = 3;
    public int? MaximumSubpage { get; init; } = 99;
    public bool StandardDecimalPagesOnly { get; init; } = true;
    public int MinimumReceptions { get; init; } = 1;
    public bool RequireServiceHeader { get; init; } = true;
    public int MinimumHeaderSimilarityPercent { get; init; } = 60;
}

/// <summary>
/// Builds one recovery page per address from every occurrence in a broadcast.
/// A complete, internally coherent row is selected first; individual payload bytes
/// are replaced only when at least two receptions independently agree on a better
/// odd-parity value. Singleton pages and singleton rows are always retained.
/// </summary>
public static class RecoverySquasher
{
    public static IReadOnlyList<byte[]> Build(
        IReadOnlyList<byte[]> broadcastPackets,
        RecoverySquashOptions? options = null,
        Action<string, int, int>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RecoverySquashOptions();
        var store = new PageStore();
        var assembler = new PageAssembler(store, decodeEnhancements: true);
        for (int index = 0; index < broadcastPackets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            assembler.Feed(broadcastPackets[index], index);
            if ((index & 0x3FF) == 0 || index == broadcastPackets.Count - 1)
                reportProgress?.Invoke("Reading captured versions", index + 1, broadcastPackets.Count);
        }
        assembler.FinalizeAll();

        var headerProfile = BuildServiceHeaderProfile(store);
        var addresses = store.GetKnownAddresses()
            .Where(address => AddressPassesFilters(store, address, options, headerProfile))
            .ToList();
        var output = new List<byte[]>(addresses.Count * 20);
        for (int addressIndex = 0; addressIndex < addresses.Count; addressIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = addresses[addressIndex];
            var versions = store.GetInstances(address.magazine, address.page, address.subpage);

            for (int row = 0; row < 25; row++)
            {
                var candidates = versions
                    .Select(instance => instance.Page.RawRows[row])
                    .Where(raw => raw is { Length: 42 })
                    .Select(raw => raw!)
                    .ToList();
                if (candidates.Count > 0)
                    output.Add(BuildConsensusRow(candidates, row));
            }

            foreach (var designationGroup in versions
                         .SelectMany(instance => instance.Page.EnhancementPackets)
                         .GroupBy(packet => packet.DesignationCode)
                         .OrderBy(group => group.Key))
            {
                var selected = designationGroup
                    .GroupBy(packet => Convert.ToHexString(packet.RawPacket))
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => EnhancementErrorCount(group.First()))
                    .First()
                    .First();
                output.Add((byte[])selected.RawPacket.Clone());
            }

            reportProgress?.Invoke("Recovering pages", addressIndex + 1, addresses.Count);
        }

        return output;
    }

    private static bool AddressPassesFilters(
        PageStore store,
        (int magazine, int page, int subpage) address,
        RecoverySquashOptions options,
        IReadOnlyList<(int Offset, int Value)> headerProfile)
    {
        var versions = store.GetInstances(address.magazine, address.page, address.subpage);
        if (versions.Count < Math.Max(options.MinimumReceptions, 1)) return false;
        if (options.MaximumSubpage is int maximumSubpage && address.subpage > maximumSubpage) return false;
        if (options.StandardDecimalPagesOnly
            && ((address.page & 0x0F) > 9 || ((address.page >> 4) & 0x0F) > 9)) return false;

        int bodyRows = Enumerable.Range(1, 24)
            .Count(row => versions.Any(instance => instance.Page.RawRows[row] is { Length: 42 }));
        if (bodyRows < Math.Clamp(options.MinimumBodyRows, 0, 24)) return false;

        if (!options.RequireServiceHeader || headerProfile.Count < 4) return true;
        int bestSimilarity = versions
            .Select(instance => HeaderSimilarity(instance.Page.RawRows[0], headerProfile))
            .DefaultIfEmpty(0)
            .Max();
        return bestSimilarity >= Math.Clamp(options.MinimumHeaderSimilarityPercent, 0, 100);
    }

    private static IReadOnlyList<(int Offset, int Value)> BuildServiceHeaderProfile(PageStore store)
    {
        // Give every distinct address one vote. Otherwise a page broadcast very
        // frequently could dominate the learned service name by itself.
        var headers = store.GetKnownAddresses()
            .Select(address => store.GetInstances(address.magazine, address.page, address.subpage)
                .Select(instance => instance.Page.RawRows[0])
                .Where(raw => raw is { Length: 42 })
                .Select(raw => raw!)
                .ToList())
            .Where(candidates => candidates.Count > 0)
            .Select(candidates => BuildConsensusRow(candidates, 0))
            .ToList();
        if (headers.Count < 5) return Array.Empty<(int, int)>();

        var profile = new List<(int Offset, int Value)>();
        for (int offset = 10; offset < 42; offset++)
        {
            var validValues = headers
                .Where(header => HasOddParity(header[offset]))
                .Select(header => header[offset] & 0x7F)
                .Where(value => value is >= 0x21 and <= 0x7E)
                .ToList();
            if (validValues.Count == 0) continue;

            var dominant = validValues
                .GroupBy(value => value)
                .OrderByDescending(group => group.Count())
                .First();
            // Service-name characters remain stable over many pages. Page titles,
            // page numbers and clocks normally fail this threshold and are ignored.
            if (dominant.Count() >= 3 && dominant.Count() * 100 >= validValues.Count * 55)
                profile.Add((offset, dominant.Key));
        }
        return profile;
    }

    private static int HeaderSimilarity(
        byte[]? header,
        IReadOnlyList<(int Offset, int Value)> profile)
    {
        if (header is not { Length: 42 } || profile.Count == 0) return 0;
        int matches = profile.Count(item =>
            HasOddParity(header[item.Offset]) && (header[item.Offset] & 0x7F) == item.Value);
        return matches * 100 / profile.Count;
    }

    private static byte[] BuildConsensusRow(IReadOnlyList<byte[]> candidates, int row)
    {
        if (candidates.Count == 1)
            return (byte[])candidates[0].Clone();

        int payloadStart = row == 0 ? 10 : 2;
        byte[] baseline = candidates
            .Select((packet, index) => new
            {
                Packet = packet,
                Index = index,
                Score = RowQuality(packet, row) * 8 + AgreementScore(packet, candidates, payloadStart),
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .First()
            .Packet;
        var result = (byte[])baseline.Clone();

        for (int offset = payloadStart; offset < 42; offset++)
        {
            int baselineValue = baseline[offset] & 0x7F;
            var groups = candidates
                .GroupBy(packet => packet[offset] & 0x7F)
                .Select(group => new
                {
                    Value = group.Key,
                    Count = group.Count(),
                    ValidCount = group.Count(packet => HasOddParity(packet[offset])),
                })
                .OrderByDescending(group => group.ValidCount * 3 + group.Count)
                .ThenByDescending(group => group.Value == baselineValue)
                .ToList();
            var winner = groups[0];
            var baselineGroup = groups.First(group => group.Value == baselineValue);
            int winnerScore = winner.ValidCount * 3 + winner.Count;
            int baselineScore = baselineGroup.ValidCount * 3 + baselineGroup.Count;
            if (winner.Count >= 2 && winnerScore > baselineScore)
                result[offset] = WithOddParity((byte)winner.Value);
        }

        return result;
    }

    private static int RowQuality(byte[] packet, int row)
    {
        int score = 0;
        int payloadStart = row == 0 ? 10 : 2;
        for (int offset = payloadStart; offset < 42; offset++)
            if (HasOddParity(packet[offset])) score++;

        int hammingEnd = row == 0 ? 10 : 2;
        for (int offset = 0; offset < hammingEnd; offset++)
            if (!Hamming.Decode84(packet[offset]).UncorrectableError) score += 2;
        return score;
    }

    private static int AgreementScore(byte[] packet, IReadOnlyList<byte[]> candidates, int payloadStart)
    {
        int score = 0;
        foreach (byte[] other in candidates)
        {
            if (ReferenceEquals(packet, other)) continue;
            for (int offset = payloadStart; offset < 42; offset++)
                if ((packet[offset] & 0x7F) == (other[offset] & 0x7F)) score++;
        }
        return score;
    }

    private static int EnhancementErrorCount(EnhancementPacket packet) =>
        packet.Triplets.Count(triplet => triplet.UncorrectableError) * 10
        + packet.Triplets.Count(triplet => triplet.CorrectedError);

    private static bool HasOddParity(byte value) => BitOperations.PopCount(value) % 2 == 1;

    private static byte WithOddParity(byte sevenBitValue)
    {
        byte value = (byte)(sevenBitValue & 0x7F);
        return BitOperations.PopCount(value) % 2 == 0 ? (byte)(value | 0x80) : value;
    }
}
