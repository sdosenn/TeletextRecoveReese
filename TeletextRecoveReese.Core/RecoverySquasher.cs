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
    public bool KeepInfrequentlyReceivedRows { get; init; }
    public bool SeparateContentVariants { get; init; }
}

/// <summary>
/// Builds recovery pages from every occurrence in a broadcast, optionally
/// separating contents at the same address into virtual subpages.
/// A complete, internally coherent row is selected first; individual payload bytes
/// are replaced only when at least two receptions independently agree on a better
/// odd-parity value. Singleton pages are retained; body rows must be present in
/// most receptions unless KeepInfrequentlyReceivedRows is enabled.
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
        // Recovery votes on raw packets only; decoding a display grid for every
        // reception of a long capture would need ~64 KB each (16 GB for 270k).
        var assembler = new PageAssembler(store, decodeEnhancements: true,
            combineRepeatedHeaderPreambles: true, decodeGrid: false);
        for (int index = 0; index < broadcastPackets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] packet = broadcastPackets[index];
            if (!TeletextPacket.IsPadding(packet))
                assembler.Feed(packet, index);
            if ((index & 0x3FF) == 0 || index == broadcastPackets.Count - 1)
                reportProgress?.Invoke("Reading captured versions", index + 1, broadcastPackets.Count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke("Finalizing captured pages", 0, 1);
        assembler.FinalizeAll();
        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke("Finalizing captured pages", 1, 1);

        var knownAddresses = store.GetKnownAddresses().ToList();
        var headerProfile = BuildServiceHeaderProfile(
            store,
            knownAddresses,
            reportProgress,
            cancellationToken);
        var addresses = new List<(int magazine, int page, int subpage)>(knownAddresses.Count);
        for (int index = 0; index < knownAddresses.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = knownAddresses[index];
            if (AddressPassesFilters(store, address, options, headerProfile, cancellationToken))
                addresses.Add(address);
            if ((index & 0x3F) == 0 || index == knownAddresses.Count - 1)
                reportProgress?.Invoke("Filtering pages", index + 1, knownAddresses.Count);
        }
        var output = new List<byte[]>(addresses.Count * 20);
        // Reserve even filtered source addresses: virtual subcodes must never
        // collide with a real subpage present in the capture.
        var reservedSubpages = knownAddresses.GroupBy(a => (a.magazine, a.page))
            .ToDictionary(group => group.Key, group => group.Select(a => a.subpage).ToHashSet());
        for (int addressIndex = 0; addressIndex < addresses.Count; addressIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = addresses[addressIndex];
            var sourceVersions = store.GetInstances(address.magazine, address.page, address.subpage);
            var variantGroups = options.SeparateContentVariants
                ? RecoveryContentGroups.Split(sourceVersions, cancellationToken)
                : new List<List<PageInstance>> { sourceVersions.ToList() };

            for (int variantIndex = 0; variantIndex < variantGroups.Count; variantIndex++)
            {
                var versions = variantGroups[variantIndex];
                int outputSubpage = address.subpage;
                if (variantIndex > 0)
                {
                    var reserved = reservedSubpages[(address.magazine, address.page)];
                    outputSubpage = Enumerable.Range(1, 0x3F7F)
                        .FirstOrDefault(value => (value & ~0x3F7F) == 0 && !reserved.Contains(value));
                    if (outputSubpage == 0)
                        throw new InvalidOperationException($"No free virtual subcodes remain for page {address.magazine}{address.page:X2}.");
                    reserved.Add(outputSubpage);
                }

                for (int row = 0; row < 26; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidates = versions
                        .Select(instance => instance.Page.RawRows[row])
                        .Where(raw => raw is { Length: 42 })
                        .Select(raw => raw!)
                        .ToList();
                    // Like vhs-teletext, vote on row presence before its contents.
                    // Missing rows win ties, so an isolated stray packet cannot fill
                    // a normally empty line. Explicitly transmitted blank rows still
                    // participate in the byte consensus normally.
                    if (row > 0 && !options.KeepInfrequentlyReceivedRows
                        && candidates.Count <= versions.Count / 2)
                        continue;
                    if (candidates.Count > 0)
                    {
                        byte[] recovered = BuildConsensusRow(candidates, row, cancellationToken);
                        if (row == 0 && outputSubpage != address.subpage)
                            RewriteSubpage(recovered, outputSubpage);
                        output.Add(recovered);
                    }
                }

                foreach (var designationGroup in versions
                             .SelectMany(instance => instance.Page.EnhancementPackets)
                             .GroupBy(packet => packet.DesignationCode)
                             .OrderBy(group => group.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var selected = designationGroup
                        .GroupBy(packet => Convert.ToHexString(packet.RawPacket))
                        .OrderByDescending(group => group.Count())
                        .ThenBy(group => EnhancementErrorCount(group.First()))
                        .First()
                        .First();
                    output.Add((byte[])selected.RawPacket.Clone());
                }

                var fastextCandidates = versions
                    .Select(instance => instance.Page.FastextPacket)
                    .Where(packet => packet is { Length: 42 })
                    .Select(packet => packet!)
                    .ToList();
                if (fastextCandidates.Count > 0)
                    output.Add(BuildFastextConsensus(fastextCandidates, address.magazine, cancellationToken));

                foreach (var group in versions.SelectMany(instance => instance.Page.EffectiveCharacterSetPackets)
                             .GroupBy(packet => packet.DesignationCode).OrderBy(group => group.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var selected = group.GroupBy(packet => Convert.ToHexString(packet.RawPacket))
                        .OrderByDescending(copies => copies.Count()).First().First();
                    output.Add((byte[])selected.RawPacket.Clone());
                }
            }

            if ((addressIndex & 0x0F) == 0 || addressIndex == addresses.Count - 1)
                reportProgress?.Invoke("Recovering pages", addressIndex + 1, addresses.Count);
        }

        if (output.Count > 0)
        {
            byte[]? broadcastServicePacket = SelectBroadcastServicePacket(
                broadcastPackets,
                cancellationToken);
            if (broadcastServicePacket is not null)
                output.Add(broadcastServicePacket);
        }

        return output;
    }

    private static void RewriteSubpage(byte[] header, int subpage)
    {
        // S1-S4 share their bytes with C4-C6. Keep those control bits intact.
        int low = Hamming.Decode84(header[4]).Value | (Hamming.Decode84(header[5]).Value << 4);
        int high = Hamming.Decode84(header[6]).Value | (Hamming.Decode84(header[7]).Value << 4);
        low = (low & 0x80) | (subpage & 0x7F);
        high = (high & 0xC0) | ((subpage >> 8) & 0x3F);
        header[4] = Hamming.Encode84(low & 15);
        header[5] = Hamming.Encode84(low >> 4);
        header[6] = Hamming.Encode84(high & 15);
        header[7] = Hamming.Encode84(high >> 4);
    }

    private static byte[] BuildFastextConsensus(
        IReadOnlyList<byte[]> candidates,
        int magazine,
        CancellationToken cancellationToken)
    {
        byte[] result = (byte[])candidates
            .OrderBy(FastextErrorCount)
            .First()
            .Clone();
        int address = (27 << 3) | (magazine & 0x07);
        result[0] = Hamming.Encode84(address & 0x0F);
        result[1] = Hamming.Encode84((address >> 4) & 0x0F);

        // Designation, six links and control are all Hamming 8/4 coded.
        for (int offset = 2; offset <= 39; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var winner = candidates
                .Select(packet => Hamming.Decode84(packet[offset]))
                .Where(decoded => !decoded.UncorrectableError)
                .GroupBy(decoded => decoded.Value)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .FirstOrDefault();
            if (winner is not null)
                result[offset] = Hamming.Encode84(winner.Key);
        }
        return result;
    }

    private static int FastextErrorCount(byte[] packet)
    {
        int errors = 0;
        for (int offset = 0; offset <= 39; offset++)
        {
            var decoded = Hamming.Decode84(packet[offset]);
            if (decoded.UncorrectableError) errors += 10;
            else if (decoded.CorrectableErrorFixed) errors++;
        }
        return errors;
    }

    private static byte[]? SelectBroadcastServicePacket(
        IReadOnlyList<byte[]> packets,
        CancellationToken cancellationToken)
    {
        (int Day, int Month)? headerDate = FindMostCommonHeaderDate(packets, cancellationToken);
        var candidates = new List<(byte[] Packet, DateOnly Date)>();
        foreach (byte[] packet in packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BroadcastServiceData.TryDecodeFormat1Date(packet, out DateOnly date))
                candidates.Add((packet, date));
        }
        if (candidates.Count == 0) return null;

        IEnumerable<(byte[] Packet, DateOnly Date)> trusted = candidates;
        if (headerDate is { } expected)
        {
            var exact = candidates.Where(candidate =>
                candidate.Date.Day == expected.Day && candidate.Date.Month == expected.Month).ToList();
            if (exact.Count > 0)
            {
                trusted = exact;
            }
            else
            {
                var adjacent = candidates.Where(candidate =>
                {
                    DateOnly previous = candidate.Date.AddDays(-1);
                    DateOnly next = candidate.Date.AddDays(1);
                    return (previous.Day == expected.Day && previous.Month == expected.Month)
                        || (next.Day == expected.Day && next.Month == expected.Month);
                }).ToList();
                if (adjacent.Count == 0) return null;
                trusted = adjacent;
            }
        }

        DateOnly selectedDate = trusted
            .GroupBy(candidate => candidate.Date)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;
        byte[] selected = (byte[])trusted.First(candidate => candidate.Date == selectedDate).Packet.Clone();

        // Repair the three Hamming-coded routing bytes before storing the packet.
        selected[0] = Hamming.Encode84(0);
        selected[1] = Hamming.Encode84(15); // magazine 8 (0) + row 30
        selected[2] = Hamming.Encode84(Hamming.Decode84(selected[2]).Value);
        return selected;
    }

    private static (int Day, int Month)? FindMostCommonHeaderDate(
        IReadOnlyList<byte[]> packets,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<(int Day, int Month), int>();
        foreach (byte[] packet in packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packet.Length != 42) continue;
            var low = Hamming.Decode84(packet[0]);
            var high = Hamming.Decode84(packet[1]);
            if (low.UncorrectableError || high.UncorrectableError) continue;
            int address = low.Value | (high.Value << 4);
            if (((address >> 3) & 0x1F) != 0) continue;

            int day = ((packet[27] & 0x7F) - '0') * 10 + (packet[28] & 0x7F) - '0';
            int month = ((packet[30] & 0x7F) - '0') * 10 + (packet[31] & 0x7F) - '0';
            if ((packet[29] & 0x7F) != '.'
                || day is < 1 or > 31
                || month is < 1 or > 12)
                continue;
            counts[(day, month)] = counts.GetValueOrDefault((day, month)) + 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.Month)
            .ThenBy(pair => pair.Key.Day)
            .Select(pair => ((int Day, int Month)?)pair.Key)
            .FirstOrDefault();
    }

    private static bool AddressPassesFilters(
        PageStore store,
        (int magazine, int page, int subpage) address,
        RecoverySquashOptions options,
        IReadOnlyList<(int Offset, int Value)> headerProfile,
        CancellationToken cancellationToken)
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
        int bestSimilarity = 0;
        foreach (var instance in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bestSimilarity = Math.Max(
                bestSimilarity,
                HeaderSimilarity(instance.Page.RawRows[0], headerProfile));
        }
        return bestSimilarity >= Math.Clamp(options.MinimumHeaderSimilarityPercent, 0, 100);
    }

    private static IReadOnlyList<(int Offset, int Value)> BuildServiceHeaderProfile(
        PageStore store,
        IReadOnlyList<(int magazine, int page, int subpage)> addresses,
        Action<string, int, int>? reportProgress,
        CancellationToken cancellationToken)
    {
        // Give every distinct address one vote. Otherwise a page broadcast very
        // frequently could dominate the learned service name by itself.
        var headers = new List<byte[]>(addresses.Count);
        for (int index = 0; index < addresses.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = addresses[index];
            var candidates = store.GetInstances(address.magazine, address.page, address.subpage)
                .Select(instance => instance.Page.RawRows[0])
                .Where(raw => raw is { Length: 42 })
                .Select(raw => raw!)
                .ToList();
            if (candidates.Count > 0)
                headers.Add(BuildConsensusRow(candidates, 0, cancellationToken));
            if ((index & 0x3F) == 0 || index == addresses.Count - 1)
                reportProgress?.Invoke("Profiling service headers", index + 1, addresses.Count);
        }
        if (headers.Count < 5) return Array.Empty<(int, int)>();

        var profile = new List<(int Offset, int Value)>();
        for (int offset = 10; offset < 42; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static byte[] BuildConsensusRow(
        IReadOnlyList<byte[]> candidates,
        int row,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 1)
            return (byte[])candidates[0].Clone();

        int payloadStart = row == 0 ? 10 : 2;
        var valueFrequencies = BuildValueFrequencies(candidates, payloadStart, cancellationToken);
        byte[] baseline = candidates
            .Select((packet, index) => new
            {
                Packet = packet,
                Index = index,
                Score = RowQuality(packet, row) * 8
                    + AgreementScore(packet, valueFrequencies, payloadStart),
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .First()
            .Packet;
        var result = (byte[])baseline.Clone();

        for (int offset = payloadStart; offset < 42; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static int[][] BuildValueFrequencies(
        IReadOnlyList<byte[]> candidates,
        int payloadStart,
        CancellationToken cancellationToken)
    {
        var frequencies = new int[42][];
        for (int offset = payloadStart; offset < 42; offset++)
            frequencies[offset] = new int[128];

        foreach (byte[] packet in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int offset = payloadStart; offset < 42; offset++)
                frequencies[offset][packet[offset] & 0x7F]++;
        }
        return frequencies;
    }

    private static int AgreementScore(byte[] packet, int[][] valueFrequencies, int payloadStart)
    {
        int score = 0;
        for (int offset = payloadStart; offset < 42; offset++)
            score += valueFrequencies[offset][packet[offset] & 0x7F] - 1;
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
