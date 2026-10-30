using System.Numerics;
using TeletextRecoveReese.Core;

byte[] Packet(int row, byte character = 0x20)
{
    byte value = BitOperations.PopCount(character) % 2 == 0 ? (byte)(character | 0x80) : character;
    var packet = Enumerable.Repeat(value, 42).ToArray();
    int address = (row << 3) | 1;
    packet[0] = Hamming.Encode84(address & 15);
    packet[1] = Hamming.Encode84(address >> 4);
    if (row == 0)
        for (int offset = 2; offset < 10; offset++) packet[offset] = Hamming.Encode84(0);
    return packet;
}

List<byte[]> Capture(int receptions, int presentRows, bool blanks = false)
{
    var packets = new List<byte[]>();
    for (int index = 0; index < receptions; index++)
    {
        packets.Add(Packet(0));
        // Ordinary rows make this a valid page under the default recovery filters.
        for (int row = 1; row <= 3; row++) packets.Add(Packet(row, (byte)'A'));
        if (blanks || index < presentRows)
            packets.Add(Packet(8, index < presentRows ? (byte)'X' : (byte)0x20));
    }
    return packets;
}

void Check(string name, List<byte[]> capture, bool expectedPresent, bool keep = false, bool expectedBlank = false, bool expectedAlmostBlank = false)
{
    var output = RecoverySquasher.Build(capture, new RecoverySquashOptions { KeepInfrequentlyReceivedRows = keep });
    var row = output.SingleOrDefault(packet => ((Hamming.Decode84(packet[0]).Value | Hamming.Decode84(packet[1]).Value << 4) >> 3) == 8);
    if ((row != null) != expectedPresent || (expectedBlank && row!.Skip(2).Any(value => (value & 0x7f) != 0x20)))
        throw new Exception($"FAILED: {name}");
    if (expectedAlmostBlank && ((row![2] & 0x7f) != 'A' || row.Skip(3).Any(value => (value & 0x7f) != 0x20)))
        throw new Exception($"Incorrect almost empty row: {name}");
    if (!output.Any(packet => ((Hamming.Decode84(packet[0]).Value | Hamming.Decode84(packet[1]).Value << 4) >> 3) == 0))
        throw new Exception($"Missing header: {name}");
    Console.WriteLine($"PASS: {name}");
}

Check("99 absent rows defeat one stray row", Capture(100, 1), false);
Check("option preserves stray row", Capture(100, 1), true, keep: true);
Check("99 explicit blank rows defeat one garbage row", Capture(100, 1, blanks: true), true, expectedBlank: true);
Check("almost empty rows retain only their majority content", Capture(100, 1, blanks: true).Select(packet =>
{
    if (packet[0] == Packet(8)[0] && packet[1] == Packet(8)[1]) packet[2] = (byte)'A';
    return packet;
}).ToList(), true, expectedAlmostBlank: true);
Check("missing row wins a tie", Capture(2, 1), false);
Check("majority row survives", Capture(3, 2), true);
Check("singleton page survives", Capture(1, 1), true);

List<byte[]> RepeatHeaders(List<byte[]> capture, int repetitions)
{
    var repeated = new List<byte[]>();
    foreach (var packet in capture)
    {
        int row = (Hamming.Decode84(packet[0]).Value | Hamming.Decode84(packet[1]).Value << 4) >> 3;
        if (row == 0)
            for (int repeat = 1; repeat < repetitions; repeat++) repeated.Add((byte[])packet.Clone());
        repeated.Add(packet);
    }
    return repeated;
}

Check("repeated header preambles do not erase regularly transmitted body rows", RepeatHeaders(Capture(5, 5), 8), true);
Check("repeated header preambles still reject an infrequent stray row", RepeatHeaders(Capture(5, 1), 8), false);
Check("keep infrequent rows still works with repeated header preambles", RepeatHeaders(Capture(5, 1), 8), true, keep: true);
Check("repeated headers do not remove explicit blank row votes", RepeatHeaders(Capture(5, 1, blanks: true), 8), true, expectedBlank: true);

byte[] Header(int subpage, int controls = 0)
{
    var header = Packet(0);
    int low = (subpage & 0x7F) | (controls & 0x80);
    int high = ((subpage >> 8) & 0x3F) | ((controls >> 8) & 0xC0);
    header[4] = Hamming.Encode84(low & 15);
    header[5] = Hamming.Encode84(low >> 4);
    header[6] = Hamming.Encode84(high & 15);
    header[7] = Hamming.Encode84(high >> 4);
    return header;
}

void AddCopy(List<byte[]> packets, byte content, int subpage = 0, bool noisy = false, int rows = 3, int controls = 0)
{
    var header = Header(subpage, controls);
    // Changing header text must not create content variants.
    header[41] = (byte)(packets.Count % 2 == 0 ? 0x31 : 0x32);
    packets.Add(header);
    for (int row = 1; row <= rows; row++)
    {
        var packet = Packet(row, content);
        if (noisy) packet[10] ^= 1; // parity error: exclude from comparison
        packets.Add(packet);
    }
}

List<byte[]> Rotating(int subpage = 0, int controls = 0)
{
    var packets = new List<byte[]>();
    for (int index = 0; index < 6; index++)
        AddCopy(packets, (byte)(index % 2 == 0 ? 'A' : 'B'), subpage, noisy: index >= 4, controls: controls);
    return packets;
}

List<PageInstance> Recover(List<byte[]> packets, bool enabled = true)
{
    var output = RecoverySquasher.Build(packets, new RecoverySquashOptions
    {
        SeparateContentVariants = enabled,
        MinimumBodyRows = 0,
        RequireServiceHeader = false,
    });
    var store = new PageStore();
    var assembler = new PageAssembler(store);
    foreach (var packet in output) assembler.Feed(packet);
    assembler.FinalizeAll();
    return store.AllInstances.ToList();
}

void Verify(string name, bool condition)
{
    if (!condition) throw new Exception($"FAILED: {name}");
    Console.WriteLine($"PASS: {name}");
}

List<PageInstance> AssemblePreambles(List<byte[]> packets, bool combine)
{
    var store = new PageStore();
    var assembler = new PageAssembler(store, combineRepeatedHeaderPreambles: combine);
    for (int index = 0; index < packets.Count; index++) assembler.Feed(packets[index], index);
    assembler.FinalizeAll();
    return store.AllInstances.ToList();
}

var preambles = RepeatHeaders(Capture(2, 2), 8);
Verify("broadcast browsing retains all original repeated headers", AssemblePreambles(preambles, false).Count == 16);
Verify("squashing counts one reception per repeated-header preamble", AssemblePreambles(preambles, true).Count == 2);
var originalSquash = RecoverySquasher.Build(Capture(5, 5));
var preambleSquash = RecoverySquasher.Build(RepeatHeaders(Capture(5, 5), 8));
Verify("header repetition leaves every recovered row byte-for-byte identical", originalSquash.Count == preambleSquash.Count
    && originalSquash.Zip(preambleSquash).All(pair => pair.First.SequenceEqual(pair.Second)));
Verify("repeated announcements cannot satisfy minimum reception count", RecoverySquasher.Build(
    RepeatHeaders(Capture(1, 1), 8), new RecoverySquashOptions { MinimumReceptions = 2 }).Count == 0);
var parallelPreambles = new List<byte[]>();
foreach (var packet in RepeatHeaders(Capture(2, 2), 4))
{
    parallelPreambles.Add(packet);
    var otherMagazine = Packet(0);
    otherMagazine[0] = Hamming.Encode84(2);
    parallelPreambles.Add(otherMagazine);
}
Verify("other magazines do not break repeated-header preambles", AssemblePreambles(parallelPreambles, true)
    .Count(instance => instance.Magazine == 1) == 2);
var otherPage = Packet(0); otherPage[2] = Hamming.Encode84(1);
Verify("another page separates genuine header-only cycles", AssemblePreambles(
    new List<byte[]> { Header(0), otherPage, Header(0), Packet(1) }, true).Count == 3);
Verify("another subpage separates genuine header-only cycles", AssemblePreambles(
    new List<byte[]> { Header(0), Header(1), Header(0), Packet(1) }, true).Count == 3);
Verify("an explicitly transmitted blank row ends the preamble", AssemblePreambles(
    new List<byte[]> { Header(0), Packet(1), Header(0), Packet(1, (byte)'A') }, true).Count == 2);
Verify("CRC row 25 ends the preamble", AssemblePreambles(
    new List<byte[]> { Header(0), Packet(25), Header(0) }, true).Count == 2);
var fastext = Enumerable.Repeat(Hamming.Encode84(0), 42).ToArray();
fastext[0] = Hamming.Encode84(9); fastext[1] = Hamming.Encode84(13); // Magazine 1, packet 27.
Verify("page-specific Fastext data ends the preamble", AssemblePreambles(
    new List<byte[]> { Header(0), fastext, Header(0) }, true).Count == 2);
var changedHeader = RepeatHeaders(Capture(1, 1), 4);
changedHeader[0] = Header(0, controls: 0x80);
Verify("changing header flags before any body do not create empty votes", AssemblePreambles(changedHeader, true).Count == 1);
Verify("rotating contents still separate with repeated header announcements", Recover(RepeatHeaders(Rotating(), 8)).Count == 2);

var variants = Recover(Rotating());
Verify("rotating contents recover separately in first-seen order", variants.Count == 2
    && variants[0].Subpage == 0 && variants[1].Subpage == 1
    && variants[0].Page.RawRows[1]!.Skip(2).All(value => (value & 0x7F) == 'A')
    && variants[1].Page.RawRows[1]!.Skip(2).All(value => (value & 0x7F) == 'B'));
Verify("default squash remains one page", Recover(Rotating(), enabled: false).Count == 1);
variants = Recover(Rotating(2));
Verify("same switch handles any subcode without changing source address", variants.Count == 2
    && variants[0].Subpage == 2 && variants[1].Subpage == 1);

var collision = Rotating();
AddCopy(collision, (byte)'C', 1);
variants = Recover(collision);
Verify("virtual subcodes avoid real subpages", variants.Select(v => v.Subpage).Distinct().Count() == 3
    && variants[1].Subpage == 2 && variants[2].Subpage == 1);

var stable = new List<byte[]>();
for (int index = 0; index < 8; index++) AddCopy(stable, (byte)'A', noisy: index % 2 == 1);
Verify("parity noise and header changes do not create variants", Recover(stable).Count == 1);
AddCopy(stable, (byte)'X', rows: 1);
Verify("insufficient overlap does not create a virtual page", Recover(stable).Count == 1);

var partial = new List<byte[]>();
for (int index = 0; index < 3; index++)
{
    AddCopy(partial, (byte)'A');
    AddCopy(partial, (byte)'B');
}
for (int index = 0; index < 3; index++) AddCopy(partial, (byte)'A', rows: 1);
variants = Recover(partial);
Verify("partial copies match known content before using temporal proximity", variants.Count == 2
    && variants[0].Page.RawRows[1]!.Skip(2).All(value => (value & 0x7F) == 'A')
    && variants[1].Page.RawRows[1]!.Skip(2).All(value => (value & 0x7F) == 'B'));

var manySubcodes = Rotating();
for (int subpage = 1; subpage <= 127; subpage++) AddCopy(manySubcodes, (byte)'C', subpage);
variants = Recover(manySubcodes);
Verify("allocation skips invalid subcode bits and filtered real addresses", variants[1].Subpage == 0x100
    && variants.Select(v => v.Subpage).Distinct().Count() == variants.Count);

var original = Rotating();
var snapshot = original.Select(packet => (byte[])packet.Clone()).ToList();
Recover(original);
Verify("source packets remain unchanged", original.Zip(snapshot).All(pair => pair.First.SequenceEqual(pair.Second)));

variants = Recover(Rotating(controls: 0xC080));
Verify("virtual header preserves erase/newsflash/subtitle bits", variants.Count == 2 && variants.All(v =>
{
    var header = v.Page.RawRows[0]!;
    int low = Hamming.Decode84(header[4]).Value | Hamming.Decode84(header[5]).Value << 4;
    int high = Hamming.Decode84(header[6]).Value | Hamming.Decode84(header[7]).Value << 4;
    return (low & 0x80) == 0x80 && (high & 0xC0) == 0xC0;
}));
var graphics = new List<byte[]>();
for (int index = 0; index < 6; index++) AddCopy(graphics, (byte)(index % 2 == 0 ? 0x11 : 0x12));
Verify("control and mosaic differences count as content", Recover(graphics).Count == 2);

var outliers = new List<byte[]>();
for (int index = 0; index < 40; index++) AddCopy(outliers, (byte)'A');
for (byte content = (byte)'B'; content <= (byte)'K'; content++)
    for (int index = 0; index < 2; index++) AddCopy(outliers, content);
Verify("many coherent damaged outliers do not become virtual pages", Recover(outliers).Count == 1);
Verify("unconfirmed splits preserve exactly the normal squash output",
    RecoverySquasher.Build(outliers).Select(Convert.ToHexString).SequenceEqual(
        RecoverySquasher.Build(outliers, new RecoverySquashOptions { SeparateContentVariants = true }).Select(Convert.ToHexString)));

var three = new List<byte[]>();
var random = new Random(7341);
for (int index = 0; index < 120; index++)
{
    AddCopy(three, (byte)('A' + index % 3));
    for (int row = 1; row <= 3; row++)
    {
        var packet = three[^(4 - row)];
        for (int column = 2; column < 42; column++)
            if (random.Next(6) == 0) packet[column] ^= 1; // common VHS parity damage
    }
}
variants = Recover(three);
Verify("three recurring noisy contents produce exactly three subpages", variants.Count == 3
    && variants.Select(v => (char)(v.Page.RawRows[1]![2] & 0x7f)).SequenceEqual(new[] { 'A', 'B', 'C' }));

var unsupported = new List<byte[]>();
AddCopy(unsupported, (byte)'A');
AddCopy(unsupported, (byte)'B');
Verify("one-off content differences need repeated confirmation", Recover(unsupported).Count == 1);

var shared = new List<byte[]>();
for (int index = 0; index < 90; index++)
{
    shared.Add(Header(7));
    for (int row = 1; row <= 10; row++)
        shared.Add(Packet(row, row is 4 or 5 ? (byte)('A' + index % 3) : (byte)'S'));
}
Verify("shared static content does not drown out rotating contents", Recover(shared).Count == 3);

// Optional real-capture regression. Source capture stays outside the repository.
if (args.Length > 0)
{
    var raw = File.ReadAllBytes(args[0]);
    var capture = new List<byte[]>();
    for (int offset = 0; offset + 42 <= raw.Length; offset += 42)
        capture.Add(raw[offset..(offset + 42)]);
    List<PageInstance> ReadOutput(IReadOnlyList<byte[]> packets)
    {
        var store = new PageStore();
        var assembler = new PageAssembler(store);
        foreach (var packet in packets) assembler.Feed(packet);
        assembler.FinalizeAll();
        return store.AllInstances.ToList();
    }
    var normalPackets = RecoverySquasher.Build(capture);
    var rotatingPackets = RecoverySquasher.Build(capture, new RecoverySquashOptions { SeparateContentVariants = true });
    var normal = ReadOutput(normalPackets);
    var rotating = ReadOutput(rotatingPackets);
    var page100 = rotating.Where(v => v.Magazine == 1 && v.PageNumber == 0).ToList();
    Verify("Eurotext page 100 has exactly three recovered contents", page100.Count == 3);
    foreach (var page in page100)
        Console.WriteLine($"100/{page.Subpage:X4}: " + string.Join(" | ", new[] { 16, 18, 22 }
            .Select(row => new string(page.Page.RawRows[row]!.Skip(2)
                .Select(value => (char)((value & 127) < 32 ? ' ' : value & 127)).ToArray()).Trim())));
    var normal132 = normal.Single(v => v.Magazine == 1 && v.PageNumber == 0x32);
    var rotating132 = rotating.Single(v => v.Magazine == 1 && v.PageNumber == 0x32);
    Verify("Eurotext stable page 132 stays byte-for-byte identical to normal squash",
        normal132.Page.RawRows.Zip(rotating132.Page.RawRows).All(pair =>
            pair.First is null ? pair.Second is null : pair.Second is not null && pair.First.SequenceEqual(pair.Second)));
    Console.WriteLine($"Real capture: {capture.Count} packets; normal={normal.Count} pages, rotating={rotating.Count} pages.");
}

CharacterSetRegressions.Run();
await CaptureDetectionRegressions.RunAsync();
await FileVbiCaptureRegressions.RunAsync();
await LinuxVbiReadRegressions.RunAsync();
LinuxVideoFormatRegressions.Run();
SquashPageOperationsRegressions.Run();
BroadcastServiceRegressions.Run();
Vgb100Regressions.Run();

var cancelled = new CancellationToken(true);
try
{
    RecoverySquasher.Build(Rotating(), new RecoverySquashOptions { SeparateContentVariants = true }, cancellationToken: cancelled);
    throw new Exception("FAILED: cancellation");
}
catch (OperationCanceledException) { Console.WriteLine("PASS: cancellation"); }

// Swapping recovered rolls must move complete instances and encode their new
// address without changing shared header controls or mutating source packets.
{
    var store = new PageStore();
    PageInstance AddSwapInstance(int subpage, byte text)
    {
        var page = new TeletextPage { Magazine = 1, PageNumber = 0, SubPage = subpage };
        var header = Packet(0);
        header[5] = Hamming.Encode84(8); // C4
        header[7] = Hamming.Encode84(12); // C5/C6
        PageStore.RewriteSubpageHeader(header, subpage);
        PageAssembler.ApplyRow(page, 0, header);
        PageAssembler.ApplyRow(page, 1, Packet(1, text));
        var instance = new PageInstance { Magazine = 1, PageNumber = 0, Subpage = subpage, Page = page };
        instance.RowsReceived.Add(1);
        instance.BroadcastRowPacketIndices[0] = text;
        store.AddInstance(instance);
        return instance;
    }
    var first = AddSwapInstance(1, (byte)'A');
    var extra = AddSwapInstance(1, (byte)'B');
    var second = AddSwapInstance(0x1234, (byte)'C');
    var originalHeader = first.Page.RawRows[0]!;
    byte[] originalBytes = (byte[])originalHeader.Clone();
    if (!store.SwapSubpages(1, 0, 1, 0x1234)
        || !ReferenceEquals(store.GetInstances(1, 0, 1).Single(), second)
        || !store.GetInstances(1, 0, 0x1234).SequenceEqual(new[] { first, extra })
        || first.Page.SubPage != 0x1234 || first.Subpage != 0x1234
        || extra.VersionIndex != 1 || first.BroadcastRowPacketIndices[0] != 'A'
        || !originalHeader.SequenceEqual(originalBytes))
        throw new Exception("Swap lost instance data or mutated a source header");
    var rewritten = first.Page.RawRows[0]!;
    int low = Hamming.Decode84(rewritten[4]).Value | Hamming.Decode84(rewritten[5]).Value << 4;
    int high = Hamming.Decode84(rewritten[6]).Value | Hamming.Decode84(rewritten[7]).Value << 4;
    if (((low & 0x7F) | ((high & 0x3F) << 8)) != 0x1234 || (low & 0x80) != 0x80 || (high & 0xC0) != 0xC0)
        throw new Exception("Swap changed shared header controls or encoded the wrong subpage");
    if (store.SwapSubpages(1, 0, 1, 1) || store.SwapSubpages(1, 0, 1, 9)
        || !store.SwapSubpages(1, 0, 1, 0x1234) || first.Page.SubPage != 1
        || !ReferenceEquals(store.GetInstances(1, 0, 1)[0], first))
        throw new Exception("Swap validation or reverse swap failed");
    Console.WriteLine("PASS: subpage swap preserves content, versions, source mapping and header controls");
}
