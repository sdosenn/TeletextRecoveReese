namespace TeletextRecoveReese.Core;

/// <summary>
/// Feeds raw 42-byte packets in and pushes completed PageInstance snapshots into a
/// PageStore. Unlike a naive decoder, this does NOT overwrite the same page in place
/// when a new header arrives - instead it finalizes whatever was being built for that
/// magazine as one instance, then starts a fresh grid for the new header cycle. That
/// way every occurrence of a page in the stream is preserved separately, which is the
/// whole point for recovery work (identical subpage, different bit errors each time
/// it was broadcast).
/// </summary>
public class PageAssembler
{
    private readonly PageStore _store;
    private readonly bool _decodeEnhancements;

    // magazine -> the instance currently being built (rows are still arriving for it)
    private readonly Dictionary<int, PageInstance> _inProgress = new();

    public TeletextPage? LastUpdatedPage { get; private set; }

    public PageAssembler(PageStore store, bool decodeEnhancements = true)
    {
        _store = store;
        _decodeEnhancements = decodeEnhancements;
    }

    private static (int value, bool uncorrectable) DecodeNibblePair(byte lo, byte hi)
    {
        var a = Hamming.Decode84(lo);
        var b = Hamming.Decode84(hi);
        return (a.Value | (b.Value << 4), a.UncorrectableError || b.UncorrectableError);
    }

    public void Feed(byte[] raw42, int packetIndex = -1)
    {
        LastUpdatedPage = null;
        if (raw42.Length != 42) return;

        var (mrag, mragBad) = DecodeNibblePair(raw42[0], raw42[1]);
        if (mragBad) return;

        int row = (mrag >> 3) & 0x1F;
        int magazineBits = mrag & 0x07;
        int magazine = magazineBits == 0 ? 8 : magazineBits;

        if (row == 0)
        {
            HandleHeader(magazine, raw42, packetIndex);
        }
        else if (row is >= 1 and <= 24)
        {
            HandleTextRow(magazine, row, raw42, packetIndex);
        }
        else if (row == 26 && _decodeEnhancements)
        {
            HandleEnhancementPacket(magazine, raw42, packetIndex);
        }

        if (_inProgress.TryGetValue(magazine, out var updated))
            LastUpdatedPage = updated.Page;
    }

    private void HandleHeader(int magazine, byte[] raw42, int packetIndex)
    {
        var payload = raw42[2..];

        var unitsR = Hamming.Decode84(payload[0]);
        var tensR = Hamming.Decode84(payload[1]);
        if (unitsR.UncorrectableError || tensR.UncorrectableError) return;

        int pageNumber = unitsR.Value | (tensR.Value << 4);

        var (subWord1, sub1Bad) = DecodeNibblePair(payload[2], payload[3]);
        var (subWord2, sub2Bad) = DecodeNibblePair(payload[4], payload[5]);
        int subpage = 0;
        if (!sub1Bad && !sub2Bad)
            subpage = (subWord1 | (subWord2 << 8)) & 0x3F7F;

        // Whatever was being built for this magazine is now complete - a new header
        // means a new transmission cycle started, regardless of whether the page/
        // subpage number is the same as before.
        FinalizeInProgress(magazine);

        var newInstance = new PageInstance
        {
            Magazine = magazine,
            PageNumber = pageNumber,
            Subpage = subpage,
            Page = new TeletextPage { Magazine = magazine, PageNumber = pageNumber, SubPage = subpage },
        };
        _inProgress[magazine] = newInstance;

        ApplyRow(newInstance.Page, 0, raw42, packetIndex);
    }

    private void HandleTextRow(int magazine, int row, byte[] raw42, int packetIndex)
    {
        // A body row with no header seen yet for this magazine is orphaned - we don't
        // know which page it belongs to (see earlier discussion on why teletext
        // doesn't repeat the page number in every packet).
        if (!_inProgress.TryGetValue(magazine, out var instance)) return;

        ApplyRow(instance.Page, row, raw42, packetIndex);
        instance.RowsReceived.Add(row);
    }

    private void HandleEnhancementPacket(int magazine, byte[] raw42, int packetIndex)
    {
        if (!_inProgress.TryGetValue(magazine, out var instance)) return;
        var packet = DecodeEnhancementPacket(raw42, packetIndex);
        if (packet is null) return;
        instance.Page.EnhancementPackets.Add(packet);
        ApplyLevel15Enhancements(instance.Page);
    }

    public static EnhancementPacket? DecodeEnhancementPacket(byte[] raw42, int packetIndex = -1)
    {
        if (raw42.Length != 42) return null;
        var designation = Hamming.Decode84(raw42[2]);
        if (designation.UncorrectableError) return null;

        var packet = new EnhancementPacket
        {
            DesignationCode = designation.Value,
            PacketIndex = packetIndex,
            RawPacket = (byte[])raw42.Clone(),
        };
        for (int tripletNumber = 0; tripletNumber < 13; tripletNumber++)
        {
            int offset = 3 + tripletNumber * 3;
            var decoded = Hamming.Decode24_18(raw42[offset], raw42[offset + 1], raw42[offset + 2]);
            packet.Triplets.Add(new EnhancementTriplet
            {
                DesignationCode = designation.Value,
                TripletNumber = tripletNumber,
                Address = decoded.Value & 0x3F,
                Mode = (decoded.Value >> 6) & 0x1F,
                Data = (decoded.Value >> 11) & 0x7F,
                CorrectedError = decoded.CorrectableErrorFixed,
                UncorrectableError = decoded.UncorrectableError,
            });
        }
        return packet;
    }

    public static void ReplaceEnhancementPackets(
        TeletextPage page,
        IEnumerable<(byte[] RawPacket, int PacketIndex)> packets)
    {
        page.EnhancementPackets.Clear();
        foreach (var (rawPacket, packetIndex) in packets)
        {
            var decoded = DecodeEnhancementPacket(rawPacket, packetIndex);
            if (decoded is not null)
                page.EnhancementPackets.Add(decoded);
        }
        ApplyLevel15Enhancements(page);
    }

    /// <summary>
    /// Stores the exact raw packet for this row on the page (byte-level source of
    /// truth) and decodes it into the Grid for display. This is the single entry
    /// point used both for live capture decoding (above) AND for transferring a row
    /// from the broadcast pane into the squash pane in the UI - both cases end up
    /// with identical, correctly-decoded bytes, never a re-derived approximation.
    /// </summary>
    public static void ApplyRow(TeletextPage page, int row, byte[] raw42, int packetIndex = -1)
    {
        page.RawRows[row] = raw42;
        if (packetIndex >= 0)
            page.RawRowPacketIndices[row] = packetIndex;
        var payload = raw42[2..];

        bool nationalOptionChanged = false;
        if (row == 0)
        {
            var c11To14 = Hamming.Decode84(payload[7]);
            if (!c11To14.UncorrectableError)
            {
                int nationalOption = (c11To14.Value >> 1) & 0x07;
                nationalOptionChanged = page.NationalOption != nationalOption;
                page.NationalOption = nationalOption;
            }
        }

        if (row == 0)
            DecodeRowInto(page, 0, payload, payloadStartX: 8, gridStartX: 8, length: 32);
        else
            DecodeRowInto(page, row, payload, payloadStartX: 0, gridStartX: 0, length: 40);

        // A replaced header can change C12-C14 on an already populated editable
        // page. Re-decode its stored body rows with the new G0 subset immediately.
        if (nationalOptionChanged)
        {
            for (int bodyRow = 1; bodyRow < 25; bodyRow++)
            {
                if (page.RawRows[bodyRow] is not { Length: 42 } storedRow) continue;
                DecodeRowInto(page, bodyRow, storedRow.AsSpan(2).ToArray(), 0, 0, 40);
            }
        }

        // Broadcast decoding deliberately does not collect X/26 packets. Avoid a
        // full 40x25 overlay pass after every ordinary row when there is therefore
        // nothing to apply; this was the major full-broadcast loading regression.
        if (page.EnhancementPackets.Count > 0)
            ApplyLevel15Enhancements(page);
    }

    public static void SetNationalOptionOverride(TeletextPage page, int? nationalOptionOverride)
    {
        page.NationalOptionOverride = nationalOptionOverride;
        for (int row = 0; row < 25; row++)
        {
            if (page.RawRows[row] is not { Length: 42 } raw) continue;
            byte[] payload = raw.AsSpan(2).ToArray();
            if (row == 0)
                DecodeRowInto(page, 0, payload, payloadStartX: 8, gridStartX: 8, length: 32);
            else
                DecodeRowInto(page, row, payload, payloadStartX: 0, gridStartX: 0, length: 40);
        }
        if (page.EnhancementPackets.Count > 0)
            ApplyLevel15Enhancements(page);
    }

    /// <summary>
    /// Applies the Level 1.5 G0-with-diacritical X/26 triplets to their decoded
    /// grid positions. This changes only the view model; raw T42 packets are never
    /// rewritten or synthesized here.
    /// </summary>
    public static void ApplyLevel15Enhancements(TeletextPage page)
    {
        for (int row = 0; row < 25; row++)
        for (int column = 0; column < 40; column++)
        {
            var cell = page.Grid[column, row];
            cell.EnhancementText = null;
            cell.EnhancementBaseCharacter = '\0';
            cell.EnhancementDiacritical = -1;
            cell.EnhancementDescription = null;
            cell.EnhancementDesignationCode = -1;
            cell.EnhancementTripletNumber = -1;
            page.Grid[column, row] = cell;
        }

        int activeRow = -1;
        bool terminated = false;
        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (triplet.UncorrectableError) continue;

                int mode = triplet.ExtendedMode;
                if (mode == 0x1F && (triplet.Data & 1) != 0)
                {
                    terminated = true;
                    break;
                }
                if (mode == 0x1F)
                    continue;

                if (mode == 0x04)
                {
                    activeRow = triplet.Address == 40 ? 24 : triplet.Address - 40;
                    continue;
                }

                if (mode == 0x07 && triplet.Address == 63)
                {
                    activeRow = 0;
                    continue;
                }

                if (activeRow is < 0 or >= 25 || triplet.Address >= 40)
                    continue;

                int column = triplet.Address;
                if (mode == 0x2F && triplet.Data is 0x62 or 0x72)
                {
                    char g2Character = triplet.Data == 0x62 ? 'Đ' : 'đ';
                    var g2Target = page.Grid[column, activeRow];
                    g2Target.EnhancementText = g2Character.ToString();
                    g2Target.EnhancementBaseCharacter = triplet.Data == 0x62 ? 'D' : 'd';
                    g2Target.EnhancementDiacritical = 16;
                    g2Target.EnhancementDescription = $"Latin G2: {g2Character}";
                    g2Target.EnhancementDesignationCode = packet.DesignationCode;
                    g2Target.EnhancementTripletNumber = triplet.TripletNumber;
                    page.Grid[column, activeRow] = g2Target;
                    continue;
                }

                if (mode is < 0x30 or > 0x3F)
                    continue;

                string source = CharacterSets.Decode((byte)triplet.Data).ToString();
                int diacritical = mode - 0x30;
                string text = (source + DiacriticalCombiningMarks[diacritical]).Normalize();
                var target = page.Grid[column, activeRow];
                target.EnhancementText = text;
                target.EnhancementBaseCharacter = source[0];
                target.EnhancementDiacritical = diacritical;
                target.EnhancementDescription =
                    $"{DiacriticalNames[diacritical]}: {source} → {text}";
                target.EnhancementDesignationCode = packet.DesignationCode;
                target.EnhancementTripletNumber = triplet.TripletNumber;
                page.Grid[column, activeRow] = target;
            }

            if (terminated) break;
        }
    }

    private static readonly string[] DiacriticalNames =
    [
        "No diacritical", "Grave", "Acute", "Circumflex", "Tilde", "Macron", "Breve", "Dot above",
        "Diaeresis", "Dot below", "Ring", "Cedilla", "Underscore", "Double acute", "Ogonek", "Caron"
    ];

    private static readonly string[] DiacriticalCombiningMarks =
    [
        "", "\u0300", "\u0301", "\u0302", "\u0303", "\u0304", "\u0306", "\u0307",
        "\u0308", "\u0323", "\u030A", "\u0327", "\u0332", "\u030B", "\u0328", "\u030C"
    ];

    public static bool TryMoveLevel15Diacritic(
        TeletextPage page,
        int sourceDesignationCode,
        int sourceTripletNumber,
        int targetColumn,
        int targetRow,
        out string error)
    {
        error = string.Empty;
        if (targetColumn is < 0 or >= 40 || targetRow is < 0 or >= 25
            || (targetRow == 0 && targetColumn < 8))
        {
            error = "That position cannot contain a display enhancement.";
            return false;
        }

        var entries = new List<(EnhancementTriplet Triplet, int ActiveRow)>();
        (EnhancementTriplet Triplet, int ActiveRow)? source = null;
        int activeRow = -1;
        bool terminated = false;

        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (triplet.UncorrectableError)
                {
                    error = "The X/26 data contains an uncorrectable triplet.";
                    return false;
                }

                int mode = triplet.ExtendedMode;
                if (mode == 0x1F)
                {
                    terminated = true;
                    break;
                }
                if (mode == 0x04)
                    activeRow = triplet.Address == 40 ? 24 : triplet.Address - 40;
                else if (mode == 0x07 && triplet.Address == 63)
                    activeRow = 0;

                var entry = (CloneTriplet(triplet), activeRow);
                entries.Add(entry);
                if (packet.DesignationCode == sourceDesignationCode
                    && triplet.TripletNumber == sourceTripletNumber
                    && IsLevel15Character(triplet))
                    source = entry;
            }
            if (terminated) break;
        }

        if (source is null)
        {
            error = "The source diacritic is no longer available.";
            return false;
        }
        if (source.Value.ActiveRow == targetRow && source.Value.Triplet.Address == targetColumn)
            return true;

        int sourceIndex = entries.FindIndex(entry =>
            entry.Triplet.DesignationCode == sourceDesignationCode
            && entry.Triplet.TripletNumber == sourceTripletNumber);
        entries.RemoveAt(sourceIndex);

        var moved = new EnhancementTriplet
        {
            Address = targetColumn,
            Mode = source.Value.Triplet.Mode,
            Data = source.Value.Triplet.Data,
        };

        var sameRow = entries
            .Select((entry, index) => (entry, index))
            .Where(item => IsLevel15Character(item.entry.Triplet) && item.entry.ActiveRow == targetRow)
            .ToList();

        int insertionIndex;
        bool needsPosition;
        if (sameRow.Count > 0)
        {
            insertionIndex = sameRow
                .Where(item => item.entry.Triplet.Address <= targetColumn)
                .Select(item => item.index + 1)
                .DefaultIfEmpty(sameRow[0].index)
                .Max();
            needsPosition = false;
        }
        else
        {
            var nextRowItem = entries
                .Select((entry, index) => (entry, index))
                .FirstOrDefault(item => IsLevel15Character(item.entry.Triplet) && item.entry.ActiveRow > targetRow);
            insertionIndex = nextRowItem.entry.Triplet is null ? entries.Count : nextRowItem.index;

            if (insertionIndex < entries.Count)
            {
                for (int index = insertionIndex - 1; index >= 0; index--)
                {
                    if (entries[index].Triplet.ExtendedMode is 0x04 or 0x07)
                    {
                        insertionIndex = index;
                        break;
                    }
                }
            }
            needsPosition = true;
        }

        if (needsPosition)
        {
            entries.Insert(insertionIndex++, (new EnhancementTriplet
            {
                Address = targetRow == 0 ? 63 : targetRow == 24 ? 40 : targetRow + 40,
                Mode = targetRow == 0 ? 0x07 : 0x04,
                Data = targetColumn,
            }, targetRow));
        }
        entries.Insert(insertionIndex, (moved, targetRow));

        var packedTriplets = entries.Select(entry => entry.Triplet).ToList();
        packedTriplets.Add(new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 7 });
        if (packedTriplets.Count > 16 * 13)
        {
            error = "There is no room for another Set Active Position triplet.";
            return false;
        }

        RebuildEnhancementPackets(page, packedTriplets);
        ApplyLevel15Enhancements(page);
        return true;
    }

    public static bool TrySetLevel15Diacritic(
        TeletextPage page,
        int targetColumn,
        int targetRow,
        char baseCharacter,
        int diacritical,
        out string error)
    {
        error = string.Empty;
        if (targetColumn is < 0 or >= 40 || targetRow is < 0 or >= 25
            || (targetRow == 0 && targetColumn < 8)
            || baseCharacter is < '\x20' or > '\x7F'
            || diacritical is < 1 or > 17)
        {
            error = "That character cannot be stored as a Level 1.5 enhancement.";
            return false;
        }

        var entries = new List<(EnhancementTriplet Triplet, int ActiveRow)>();
        int activeRow = -1;
        bool terminated = false;
        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (triplet.UncorrectableError)
                {
                    error = "The X/26 data contains an uncorrectable triplet.";
                    return false;
                }

                int mode = triplet.ExtendedMode;
                if (mode == 0x1F)
                {
                    terminated = true;
                    break;
                }
                if (mode == 0x04)
                    activeRow = triplet.Address == 40 ? 24 : triplet.Address - 40;
                else if (mode == 0x07 && triplet.Address == 63)
                    activeRow = 0;

                // Typing over an existing enhancement replaces it.
                if (IsLevel15Character(triplet)
                    && activeRow == targetRow
                    && triplet.Address == targetColumn)
                    continue;

                entries.Add((CloneTriplet(triplet), activeRow));
            }
            if (terminated) break;
        }

        var sameRow = entries
            .Select((entry, index) => (entry, index))
            .Where(item => IsLevel15Character(item.entry.Triplet) && item.entry.ActiveRow == targetRow)
            .ToList();

        int insertionIndex;
        bool needsPosition;
        if (sameRow.Count > 0)
        {
            insertionIndex = sameRow
                .Where(item => item.entry.Triplet.Address <= targetColumn)
                .Select(item => item.index + 1)
                .DefaultIfEmpty(sameRow[0].index)
                .Max();
            needsPosition = false;
        }
        else
        {
            var nextRowItem = entries
                .Select((entry, index) => (entry, index))
                .FirstOrDefault(item => IsLevel15Character(item.entry.Triplet) && item.entry.ActiveRow > targetRow);
            insertionIndex = nextRowItem.entry.Triplet is null ? entries.Count : nextRowItem.index;

            if (insertionIndex < entries.Count)
            {
                for (int index = insertionIndex - 1; index >= 0; index--)
                {
                    if (entries[index].Triplet.ExtendedMode is 0x04 or 0x07)
                    {
                        insertionIndex = index;
                        break;
                    }
                }
            }
            needsPosition = true;
        }

        if (needsPosition)
        {
            entries.Insert(insertionIndex++, (new EnhancementTriplet
            {
                Address = targetRow == 0 ? 63 : targetRow == 24 ? 40 : targetRow + 40,
                Mode = targetRow == 0 ? 0x07 : 0x04,
                Data = targetColumn,
            }, targetRow));
        }

        entries.Insert(insertionIndex, (new EnhancementTriplet
        {
            Address = targetColumn,
            Mode = diacritical is 16 or 17 ? 0x0F : 0x10 + diacritical,
            Data = diacritical == 16 ? 0x62 : diacritical == 17 ? 0x72 : baseCharacter,
        }, targetRow));

        var packedTriplets = entries.Select(entry => entry.Triplet).ToList();
        packedTriplets.Add(new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 7 });
        if (packedTriplets.Count > 16 * 13)
        {
            error = "There is no room for another Set Active Position triplet.";
            return false;
        }

        RebuildEnhancementPackets(page, packedTriplets);
        ApplyLevel15Enhancements(page);
        return true;
    }

    /// <summary>
    /// Adds a diacritic in a new designation packet without rewriting a single byte
    /// of the existing X/26 packets. This is the recovery-safe path for pages whose
    /// old packets contain uncorrectable Hamming words that the user wants to retain.
    /// </summary>
    public static bool TryAppendLevel15DiacriticPacket(
        TeletextPage page,
        int targetColumn,
        int targetRow,
        char baseCharacter,
        int diacritical,
        out string error)
    {
        error = string.Empty;
        if (targetColumn is < 0 or >= 40 || targetRow is < 0 or >= 25
            || (targetRow == 0 && targetColumn < 8)
            || baseCharacter is < '\x20' or > '\x7F'
            || diacritical is < 1 or > 17)
        {
            error = "That character cannot be stored as a Level 1.5 enhancement.";
            return false;
        }

        var termination = new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 7 };
        EnhancementTriplet[] additions =
        [
            new EnhancementTriplet
            {
                Address = targetRow == 0 ? 63 : targetRow == 24 ? 40 : targetRow + 40,
                Mode = targetRow == 0 ? 0x07 : 0x04,
                Data = targetColumn,
            },
            new EnhancementTriplet
            {
                Address = targetColumn,
                Mode = diacritical is 16 or 17 ? 0x0F : 0x10 + diacritical,
                Data = diacritical == 16 ? 0x62 : diacritical == 17 ? 0x72 : baseCharacter,
            },
            termination,
        ];

        // If an existing packet has ordinary termination padding, use that padding
        // in place. Every preceding raw triplet remains byte-for-byte identical.
        var terminal = page.EnhancementPackets
            .OrderBy(packet => packet.DesignationCode)
            .SelectMany(packet => packet.Triplets.Select(triplet => (Packet: packet, Triplet: triplet)))
            .FirstOrDefault(item => !item.Triplet.UncorrectableError
                && item.Triplet.ExtendedMode == 0x1F
                && (item.Triplet.Data & 1) != 0);
        if (terminal.Packet is not null && terminal.Triplet.TripletNumber <= 10)
        {
            byte[] patchedRaw = (byte[])terminal.Packet.RawPacket.Clone();
            for (int index = 0; index < additions.Length; index++)
                EncodeEnhancementTripletAt(
                    patchedRaw,
                    terminal.Triplet.TripletNumber + index,
                    additions[index]);
            EnhancementPacket? patched = DecodeEnhancementPacket(
                patchedRaw, terminal.Packet.PacketIndex);
            if (patched is null)
            {
                error = "The new X/26 packet could not be encoded.";
                return false;
            }
            int packetIndex = page.EnhancementPackets.IndexOf(terminal.Packet);
            page.EnhancementPackets[packetIndex] = patched;
            ApplyLevel15Enhancements(page);
            return true;
        }

        int designation = page.EnhancementPackets
            .Select(packet => packet.DesignationCode)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        if (designation > 15)
        {
            error = "There is no free X/26 designation packet for this diacritic.";
            return false;
        }

        // A terminal marker in one of the last two slots cannot hold all three new
        // triplets. Turn only that marker into a continuation and append a packet;
        // no character or positioning triplet is touched.
        if (terminal.Packet is not null)
        {
            byte[] continuedRaw = (byte[])terminal.Packet.RawPacket.Clone();
            EncodeEnhancementTripletAt(
                continuedRaw,
                terminal.Triplet.TripletNumber,
                new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 0 });
            EnhancementPacket? continued = DecodeEnhancementPacket(
                continuedRaw, terminal.Packet.PacketIndex);
            if (continued is null)
            {
                error = "The X/26 continuation marker could not be encoded.";
                return false;
            }
            int packetIndex = page.EnhancementPackets.IndexOf(terminal.Packet);
            page.EnhancementPackets[packetIndex] = continued;
        }

        byte[] raw = CreateStandaloneEnhancementPacket(
            page.Magazine, designation, additions, termination);
        EnhancementPacket? packet = DecodeEnhancementPacket(raw, packetIndex: -1);
        if (packet is null)
        {
            error = "The new X/26 packet could not be encoded.";
            return false;
        }

        page.EnhancementPackets.Add(packet);
        ApplyLevel15Enhancements(page);
        return true;
    }

    /// <summary>
    /// Inserts a new position/character pair into the logical row order while
    /// copying every existing Hamming 24/18 codeword as the original three bytes.
    /// Corrupt codewords therefore remain corrupt in exactly the same way instead
    /// of being decoded, guessed, dropped, or freshly encoded.
    /// </summary>
    public static bool TryInsertLevel15DiacriticRawPreserving(
        TeletextPage page,
        int targetColumn,
        int targetRow,
        char baseCharacter,
        int diacritical,
        out string error)
    {
        error = string.Empty;
        if (targetColumn is < 0 or >= 40 || targetRow is < 0 or >= 25
            || (targetRow == 0 && targetColumn < 8)
            || baseCharacter is < '\x20' or > '\x7F'
            || diacritical is < 1 or > 17)
        {
            error = "That character cannot be stored as a Level 1.5 enhancement.";
            return false;
        }

        var orderedPackets = page.EnhancementPackets
            .OrderBy(packet => packet.DesignationCode)
            .ToList();
        if (orderedPackets.Count >= 16)
        {
            error = "There is no free X/26 designation packet for this diacritic.";
            return false;
        }

        var rawTriplets = new List<byte[]>(orderedPackets.Count * 13 + 3);
        var decodedSlots = new List<(EnhancementTriplet Triplet, int Slot, int ActiveRow)>();
        int activeRow = -1;
        int? firstTerminalSlot = null;
        foreach (EnhancementPacket packet in orderedPackets)
        {
            for (int tripletNumber = 0; tripletNumber < 13; tripletNumber++)
            {
                int offset = 3 + tripletNumber * 3;
                rawTriplets.Add(packet.RawPacket.AsSpan(offset, 3).ToArray());
                EnhancementTriplet triplet = packet.Triplets[tripletNumber];
                int slot = rawTriplets.Count - 1;
                if (!triplet.UncorrectableError)
                {
                    if (triplet.ExtendedMode == 0x04)
                        activeRow = triplet.Address == 40 ? 24 : triplet.Address - 40;
                    else if (triplet.ExtendedMode == 0x07 && triplet.Address == 63)
                        activeRow = 0;
                    if (triplet.ExtendedMode == 0x1F && (triplet.Data & 1) != 0)
                        firstTerminalSlot ??= slot;
                }
                decodedSlots.Add((triplet, slot, activeRow));
            }
        }

        int insertionSlot = firstTerminalSlot ?? rawTriplets.Count;
        var sameRowCharacters = decodedSlots
            .Where(item => !item.Triplet.UncorrectableError
                && IsLevel15Character(item.Triplet)
                && item.ActiveRow == targetRow
                && (!firstTerminalSlot.HasValue || item.Slot < firstTerminalSlot.Value))
            .ToList();
        if (sameRowCharacters.Count > 0)
        {
            var following = sameRowCharacters.FirstOrDefault(item => item.Triplet.Address > targetColumn);
            insertionSlot = following.Triplet is null
                ? sameRowCharacters[^1].Slot + 1
                : following.Slot;
        }
        else
        {
            var laterRow = decodedSlots.FirstOrDefault(item =>
                !item.Triplet.UncorrectableError
                && item.Triplet.ExtendedMode is 0x04 or 0x07
                && item.ActiveRow > targetRow
                && (!firstTerminalSlot.HasValue || item.Slot < firstTerminalSlot.Value));
            if (laterRow.Triplet is not null)
                insertionSlot = laterRow.Slot;
        }
        if (firstTerminalSlot.HasValue)
            insertionSlot = Math.Min(insertionSlot, firstTerminalSlot.Value);

        var activePosition = new EnhancementTriplet
        {
            Address = targetRow == 0 ? 63 : targetRow == 24 ? 40 : targetRow + 40,
            Mode = targetRow == 0 ? 0x07 : 0x04,
            Data = targetColumn,
        };
        var character = new EnhancementTriplet
        {
            Address = targetColumn,
            Mode = diacritical is 16 or 17 ? 0x0F : 0x10 + diacritical,
            Data = diacritical == 16 ? 0x62 : diacritical == 17 ? 0x72 : baseCharacter,
        };
        rawTriplets.Insert(insertionSlot++, EncodeEnhancementTriplet(activePosition));
        rawTriplets.Insert(insertionSlot, EncodeEnhancementTriplet(character));
        if (!firstTerminalSlot.HasValue)
        {
            rawTriplets.Add(EncodeEnhancementTriplet(
                new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 7 }));
        }

        int packetCount = (rawTriplets.Count + 12) / 13;
        if (packetCount > 16)
        {
            error = "There is no room for the inserted X/26 triplets.";
            return false;
        }

        var existingIndices = orderedPackets
            .GroupBy(packet => packet.DesignationCode)
            .ToDictionary(group => group.Key, group => group.First().PacketIndex);
        byte[] terminalBytes = EncodeEnhancementTriplet(
            new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 7 });
        page.EnhancementPackets.Clear();
        for (int designation = 0; designation < packetCount; designation++)
        {
            byte[] raw = CreateEmptyEnhancementPacket(page.Magazine, designation);
            for (int tripletNumber = 0; tripletNumber < 13; tripletNumber++)
            {
                int sourceIndex = designation * 13 + tripletNumber;
                byte[] bytes = sourceIndex < rawTriplets.Count
                    ? rawTriplets[sourceIndex]
                    : terminalBytes;
                bytes.CopyTo(raw, 3 + tripletNumber * 3);
            }
            EnhancementPacket? decoded = DecodeEnhancementPacket(
                raw, existingIndices.GetValueOrDefault(designation, -1));
            if (decoded is not null)
                page.EnhancementPackets.Add(decoded);
        }

        ApplyLevel15Enhancements(page);
        return true;
    }

    private static byte[] EncodeEnhancementTriplet(EnhancementTriplet triplet)
    {
        int value = triplet.Address | (triplet.Mode << 6) | (triplet.Data << 11);
        int encoded = Hamming.Encode24_18(value);
        return [(byte)encoded, (byte)(encoded >> 8), (byte)(encoded >> 16)];
    }

    private static byte[] CreateEmptyEnhancementPacket(int magazine, int designation)
    {
        var raw = new byte[42];
        int magazineBits = magazine == 8 ? 0 : magazine & 0x07;
        int address = magazineBits | (26 << 3);
        raw[0] = Hamming.Encode84(address & 0x0F);
        raw[1] = Hamming.Encode84((address >> 4) & 0x0F);
        raw[2] = Hamming.Encode84(designation);
        return raw;
    }

    private static void EncodeEnhancementTripletAt(
        byte[] rawPacket,
        int tripletNumber,
        EnhancementTriplet triplet)
    {
        int value = triplet.Address | (triplet.Mode << 6) | (triplet.Data << 11);
        int encoded = Hamming.Encode24_18(value);
        int offset = 3 + tripletNumber * 3;
        rawPacket[offset] = (byte)encoded;
        rawPacket[offset + 1] = (byte)(encoded >> 8);
        rawPacket[offset + 2] = (byte)(encoded >> 16);
    }

    private static bool IsDiacritical(EnhancementTriplet triplet) =>
        triplet.ExtendedMode is >= 0x30 and <= 0x3F;

    private static bool IsLevel15Character(EnhancementTriplet triplet) =>
        IsDiacritical(triplet) || triplet.ExtendedMode == 0x2F;

    private static EnhancementTriplet CloneTriplet(EnhancementTriplet triplet) => new()
    {
        DesignationCode = triplet.DesignationCode,
        TripletNumber = triplet.TripletNumber,
        Address = triplet.Address,
        Mode = triplet.Mode,
        Data = triplet.Data,
    };

    public static bool DeleteEnhancementTriplet(
        TeletextPage page,
        EnhancementPacket sourcePacket,
        int sourceTripletNumber)
    {
        if (!page.EnhancementPackets.Contains(sourcePacket)) return false;

        var remaining = new List<EnhancementTriplet>();
        bool found = false;
        bool terminated = false;
        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (ReferenceEquals(packet, sourcePacket)
                    && triplet.TripletNumber == sourceTripletNumber)
                {
                    found = true;
                    continue;
                }

                if (!triplet.UncorrectableError
                    && triplet.ExtendedMode == 0x1F
                    && (triplet.Data & 1) != 0)
                {
                    terminated = true;
                    break;
                }

                remaining.Add(CloneTriplet(triplet));
            }

            if (terminated) break;
        }

        if (!found) return false;
        RebuildEnhancementPackets(page, remaining);
        ApplyLevel15Enhancements(page);
        return true;
    }

    /// <summary>
    /// Sanitizes triplets that Hamming 24/18 could not correct. Correct and
    /// single-bit-corrected triplets from the same packets are retained. Plausible
    /// active-position values are re-encoded; characters depending on an impossible
    /// active position are omitted until the next trustworthy position.
    /// </summary>
    public static int RemoveUncorrectableEnhancementTriplets(TeletextPage page)
    {
        var remaining = new List<EnhancementTriplet>();
        int removed = 0;
        int repaired = 0;
        bool activePositionKnown = false;
        bool terminated = false;
        foreach (var packet in page.EnhancementPackets.OrderBy(packet => packet.DesignationCode))
        {
            foreach (var triplet in packet.Triplets)
            {
                if (triplet.UncorrectableError)
                {
                    // A damaged Set Active Position sometimes still decodes to a
                    // completely plausible row/column. Retaining and freshly
                    // encoding it preserves the following good characters. If its
                    // decoded position is impossible, discard dependent characters
                    // until the next trustworthy position instead of moving them to
                    // the preceding row by accident.
                    if (IsPlausibleActivePosition(triplet))
                    {
                        remaining.Add(CloneTriplet(triplet));
                        activePositionKnown = true;
                        repaired++;
                        continue;
                    }
                    if (triplet.ExtendedMode is 0x04 or 0x07)
                        activePositionKnown = false;
                    removed++;
                    continue;
                }

                if (triplet.ExtendedMode == 0x1F)
                {
                    terminated = true;
                    break;
                }

                if (triplet.ExtendedMode is 0x04 or 0x07)
                    activePositionKnown = true;
                else if (IsLevel15Character(triplet) && !activePositionKnown)
                {
                    removed++;
                    continue;
                }

                remaining.Add(CloneTriplet(triplet));
            }
            if (terminated) break;
        }

        int changes = removed + repaired;
        if (changes == 0) return 0;
        RebuildEnhancementPackets(page, remaining);
        ApplyLevel15Enhancements(page);
        return changes;
    }

    private static bool IsPlausibleActivePosition(EnhancementTriplet triplet) =>
        (triplet.ExtendedMode == 0x04 && triplet.Address is >= 40 and <= 63 && triplet.Data is >= 0 and < 40)
        || (triplet.ExtendedMode == 0x07 && triplet.Address == 63 && triplet.Data is >= 0 and < 40);

    private static void RebuildEnhancementPackets(
        TeletextPage page,
        IReadOnlyList<EnhancementTriplet> triplets)
    {
        var existingIndices = page.EnhancementPackets
            .GroupBy(packet => packet.DesignationCode)
            .ToDictionary(group => group.Key, group => group.First().PacketIndex);
        int packetCount = Math.Max(page.EnhancementPackets.Count, (triplets.Count + 12) / 13);
        var termination = new EnhancementTriplet { Address = 63, Mode = 0x1F, Data = 7 };

        page.EnhancementPackets.Clear();
        for (int designation = 0; designation < packetCount; designation++)
        {
            var raw = CreateEnhancementPacket(page.Magazine, designation, triplets, termination);
            var packet = new EnhancementPacket
            {
                DesignationCode = designation,
                PacketIndex = existingIndices.GetValueOrDefault(designation, -1),
                RawPacket = raw,
            };
            for (int tripletNumber = 0; tripletNumber < 13; tripletNumber++)
            {
                int index = designation * 13 + tripletNumber;
                var value = index < triplets.Count ? triplets[index] : termination;
                packet.Triplets.Add(new EnhancementTriplet
                {
                    DesignationCode = designation,
                    TripletNumber = tripletNumber,
                    Address = value.Address,
                    Mode = value.Mode,
                    Data = value.Data,
                });
            }
            page.EnhancementPackets.Add(packet);
        }
    }

    private static byte[] CreateEnhancementPacket(
        int magazine,
        int designation,
        IReadOnlyList<EnhancementTriplet> triplets,
        EnhancementTriplet termination)
    {
        var raw = new byte[42];
        int magazineBits = magazine == 8 ? 0 : magazine & 0x07;
        int address = magazineBits | (26 << 3);
        raw[0] = Hamming.Encode84(address & 0x0F);
        raw[1] = Hamming.Encode84((address >> 4) & 0x0F);
        raw[2] = Hamming.Encode84(designation);

        for (int tripletNumber = 0; tripletNumber < 13; tripletNumber++)
        {
            int index = designation * 13 + tripletNumber;
            var triplet = index < triplets.Count ? triplets[index] : termination;
            int value = triplet.Address | (triplet.Mode << 6) | (triplet.Data << 11);
            int encoded = Hamming.Encode24_18(value);
            int offset = 3 + tripletNumber * 3;
            raw[offset] = (byte)encoded;
            raw[offset + 1] = (byte)(encoded >> 8);
            raw[offset + 2] = (byte)(encoded >> 16);
        }
        return raw;
    }

    private static byte[] CreateStandaloneEnhancementPacket(
        int magazine,
        int designation,
        IReadOnlyList<EnhancementTriplet> triplets,
        EnhancementTriplet termination)
    {
        var raw = new byte[42];
        int magazineBits = magazine == 8 ? 0 : magazine & 0x07;
        int address = magazineBits | (26 << 3);
        raw[0] = Hamming.Encode84(address & 0x0F);
        raw[1] = Hamming.Encode84((address >> 4) & 0x0F);
        raw[2] = Hamming.Encode84(designation);

        for (int tripletNumber = 0; tripletNumber < 13; tripletNumber++)
        {
            EnhancementTriplet triplet = tripletNumber < triplets.Count
                ? triplets[tripletNumber]
                : termination;
            int value = triplet.Address | (triplet.Mode << 6) | (triplet.Data << 11);
            int encoded = Hamming.Encode24_18(value);
            int offset = 3 + tripletNumber * 3;
            raw[offset] = (byte)encoded;
            raw[offset + 1] = (byte)(encoded >> 8);
            raw[offset + 2] = (byte)(encoded >> 16);
        }
        return raw;
    }

    /// <summary>
    /// Decodes one row's worth of raw payload bytes into a page's grid, applying the
    /// alpha color control codes (0x00-0x07). This is a partial spacing-attributes
    /// implementation - mosaic graphics, double height/width, flash, box, and conceal
    /// are still TODO, but foreground color now actually renders instead of being
    /// silently dropped.
    ///
    /// Per EN 300 706: codes 0x00-0x07 set the foreground color for all characters
    /// FROM that column onward (until the next color code or end of row); the
    /// column holding the code itself displays as a space. TeletextColor's enum
    /// order (Black,Red,Green,Yellow,Blue,Magenta,Cyan,White) matches the spec's
    /// code order 0-7, so the code can be cast directly.
    ///
    /// Control code table and G1 mosaic sixel bit-mapping verified against
    /// handwiki.org/wiki/Teletext_character_set (which cites ETS 300 706 sec 12.2
    /// and 15.7.1 directly) - cross-checked with concrete examples from that table:
    /// code 0x21 -> sextant "top-left only" (sixel value 1, bit0), code 0x35 -> "▌"
    /// left-half block (sixel value 21 = bits 0+2+4 = top/mid/bottom-left), code
    /// 0x7F -> full block (sixel value 63, all 6 bits set). All three matched the
    /// formula below exactly.
    ///
    /// One assumption not explicitly confirmed in the source above: all spacing
    /// attributes are treated here as "set-after" (the code's own cell is blank/
    /// held-mosaic, the new attribute applies starting the NEXT column). Some
    /// implementations treat a couple of codes (e.g. Alpha Black) as "set-at"
    /// instead. If mosaic/color edges look shifted by one column on real captures,
    /// this is the first thing to revisit.
    ///
    /// Double height/width/size (0x0C-0x0F) are recognized (so they don't render as
    /// garbage) but not yet applied to layout - per your instruction, that's next.
    /// Box (Start/End Box) and Flash are tracked on the Cell but not yet used by the
    /// renderer (no windowing/blink support yet).
    ///
    /// IsMosaic classification: a cell is "mosaic" when it holds an actual sixel byte
    /// (handled in the non-control-code branch below - this already covers plain
    /// spaces (0x20) rendered as an empty-pattern graphics cell while in mosaic mode,
    /// since 0x20 is not a control code), OR when the cell itself carries a mosaic
    /// COLOR code (0x10-0x17, MBK/MSR/MSG/.../MSW). That second case matters because
    /// a mosaic color code is what actually turns graphics mode on/switches color for
    /// the mosaic run that follows - its own cell shows no dots, but it is still
    /// conceptually a (blank) mosaic cell, not alpha text, so the editor can select
    /// and toggle it as graphics.
    ///
    /// All OTHER control codes (New Background, Hold/Release Mosaics, Double Size,
    /// Flash, Box, Conceal, ESC) are NOT mosaic-defining codes even if mosaicMode
    /// happens to be active when they occur - they stay plain alpha spaces
    /// (IsMosaic = false), so they don't light up the mosaic overlay/toggle UI.
    /// </summary>
    private static void DecodeRowInto(TeletextPage page, int row, byte[] payload, int payloadStartX, int gridStartX, int length)
    {
        var currentFg = TeletextColor.White;
        var currentBg = TeletextColor.Black;
        bool mosaicMode = false;
        bool contiguous = true;
        bool holdActive = false;
        bool concealed = false;
        bool flash = false;
        bool boxed = false;
        bool doubleHeight = false;
        bool doubleWidth = false;
        byte heldPattern = 0;
        bool haveHeldPattern = false;

        for (int i = 0; i < length && payloadStartX + i < payload.Length; i++)
        {
            byte raw = (byte)(payload[payloadStartX + i] & 0x7F);
            int gridX = gridStartX + i;
            if (gridX >= 40) break;

            bool isControlCode = raw <= 0x1F;

            if (isControlCode)
            {
                switch (raw)
                {
                    case <= 0x07: // Alpha color: ABK,ANR,ANG,ANY,ANB,ANM,ANC,ANW
                        mosaicMode = false;
                        currentFg = (TeletextColor)raw;
                        break;
                    case >= 0x10 and <= 0x17: // Mosaic color: MBK,MSR,MSG,MSY,MSB,MSM,MSC,MSW
                        mosaicMode = true;
                        currentFg = (TeletextColor)(raw - 0x10);
                        break;
                    case 0x08: flash = true; break;          // FSH
                    case 0x09: flash = false; break;         // STD
                    case 0x0A: boxed = false; break;         // EBX End Box
                    case 0x0B: boxed = true; break;          // SBX Start Box
                    case 0x0C: doubleHeight = false; doubleWidth = false; break; // NSZ Normal size
                    case 0x0D: doubleHeight = true; doubleWidth = false; break;  // DBH Double height
                    case 0x0E: doubleHeight = false; doubleWidth = true; break;  // DBW Double width
                    case 0x0F: doubleHeight = true; doubleWidth = true; break;   // DBS Double size
                    case 0x18: concealed = true; break;      // CDY Conceal display
                    case 0x19: contiguous = true; break;     // Contiguous mosaic graphics
                    case 0x1A: contiguous = false; break;    // Separated mosaic graphics
                    case 0x1B: break;                        // ESC/Switch (charset switching not implemented)
                    case 0x1C: currentBg = TeletextColor.Black; break; // BBD Black background
                    case 0x1D: currentBg = currentFg; break;  // NBD New background = current foreground
                    case 0x1E: holdActive = true; break;      // HMS Hold mosaics
                    case 0x1F: holdActive = false; break;     // RMS Release mosaics
                }
            }

            var cell = page.Grid[gridX, row];
            cell.Background = currentBg;
            cell.Flash = flash;
            cell.Boxed = boxed;
            cell.Conceal = concealed;
            cell.DoubleHeight = doubleHeight;
            cell.DoubleWidth = doubleWidth;
            cell.MosaicSeparated = !contiguous;
            cell.HoldMosaics = holdActive;

            if (isControlCode)
            {
                // Any control code blanks the cell's sixel content UNLESS hold-graphics
                // is active and we're in mosaic mode, in which case it repeats the last
                // mosaic pattern instead - this is what lets a color change happen mid
                // graphic without leaving a visible gap.
                bool isMosaicColorCode = raw is >= 0x10 and <= 0x17;

                if (holdActive && mosaicMode && haveHeldPattern)
                {
                    cell.IsMosaic = true;
                    cell.MosaicPattern = heldPattern;
                    cell.MosaicHeld = true;
                }
                else if (isMosaicColorCode)
                {
                    // This code is what puts the row into (or switches the color
                    // within) graphics mode. Its own cell shows no dots, but it's
                    // still a mosaic cell with an empty pattern - not alpha text -
                    // so it stays selectable/toggleable as graphics in the editor.
                    cell.IsMosaic = true;
                    cell.MosaicPattern = 0;
                    cell.MosaicHeld = false;
                }
                else
                {
                    // Every other control code (background, hold/release, double
                    // size, flash, box, conceal, esc) is a plain alpha space, even
                    // if mosaicMode happens to already be active - it doesn't itself
                    // define graphics content.
                    cell.IsMosaic = false;
                    cell.Character = ' ';
                }

                cell.Foreground = currentFg;
            }
            else if (mosaicMode && raw is not (>= 0x40 and <= 0x5F))
            {
                // In graphics mode only G1 columns 2, 3, 6 and 7 are mosaics.
                // Columns 4 and 5 (0x40-0x5F) are handled by the text branch below
                // as EN 300 706 "blast-through" G0 alphanumerics.
                byte pattern = (byte)(raw <= 0x3F
                    ? raw - 0x20
                    : 32 + (raw - 0x60));
                cell.IsMosaic = true;
                cell.MosaicPattern = pattern;
                cell.MosaicHeld = false;
                cell.Foreground = currentFg;
                heldPattern = pattern;
                haveHeldPattern = true;
            }
            else
            {
                // Normal text, or blast-through alphanumerics while in mosaic mode.
                cell.IsMosaic = false;
                cell.Character = CharacterSets.Decode(
                    raw,
                    page.NationalOptionOverride ?? page.NationalOption);
                cell.Foreground = currentFg;
            }

            page.Grid[gridX, row] = cell;
        }
    }


    private void FinalizeInProgress(int magazine)
    {
        if (_inProgress.TryGetValue(magazine, out var instance))
        {
            _store.AddInstance(instance);
            _inProgress.Remove(magazine);
        }
    }

    /// <summary>Call this after feeding the whole file/stream, so the very last page
    /// being built (which never saw a "next" header to trigger finalization) still
    /// gets pushed into the store.</summary>
    public void FinalizeAll()
    {
        foreach (var magazine in _inProgress.Keys.ToList())
            FinalizeInProgress(magazine);
    }
}
