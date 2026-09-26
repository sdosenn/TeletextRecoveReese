using System.Text;

namespace TeletextRecoveReese.Core;

public readonly record struct DvbTeletextExtractionProgress(long BytesRead, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : BytesRead * 100.0 / TotalBytes;
}

public sealed record DvbTeletextDescriptor(
    string Language,
    int TeletextType,
    int Magazine,
    int PageNumber);

public sealed record DvbTeletextService(
    int Pid,
    int? ProgramNumber,
    IReadOnlyList<DvbTeletextDescriptor> Descriptors,
    byte[] T42Data)
{
    public int PacketCount => T42Data.Length / 42;

    public string DisplayName
    {
        get
        {
            string program = ProgramNumber is { } number ? $"Program {number} · " : string.Empty;
            string languages = string.Join(", ", Descriptors
                .Select(descriptor => descriptor.Language)
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            string language = languages.Length > 0 ? $" · {languages}" : string.Empty;
            return $"{program}PID 0x{Pid:X}{language} · {PacketCount:N0} packets";
        }
    }
}

public sealed record DvbTeletextExtractionResult(
    int TransportPacketSize,
    IReadOnlyList<DvbTeletextService> Services,
    long RejectedDataUnits);

/// <summary>
/// Extracts EN 300 472 DVB Teletext data units directly from MPEG-TS/M2TS PES
/// packets and converts their bit-transmission order into ordinary 42-byte T42
/// packets.
/// </summary>
public static class DvbTeletextExtractor
{
    private const int TsPayloadSize = 188;
    private const int MaxPesBytes = 1024 * 1024;

    public static DvbTeletextExtractionResult Extract(
        Stream input,
        IProgress<DvbTeletextExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
            throw new ArgumentException("DVB Teletext import requires a readable, seekable stream.", nameof(input));

        long originalPosition = input.Position;
        (int packetSize, int syncOffset) = DetectTransportPacketLayout(input);
        input.Position = originalPosition;

        var pmtPrograms = new Dictionary<int, int>();
        var serviceMetadata = new Dictionary<int, (int Program, List<DvbTeletextDescriptor> Descriptors)>();
        var psiCollectors = new Dictionary<int, PsiSectionCollector>
        {
            [0] = new PsiSectionCollector(),
        };
        var pesBuffers = new Dictionary<int, MemoryStream>();
        var extracted = new Dictionary<int, MemoryStream>();
        long rejectedDataUnits = 0;
        long totalBytes = Math.Max(input.Length - originalPosition, 0);
        long bytesRead = 0;
        long packetNumber = 0;
        byte[] packet = new byte[packetSize];

        while (ReadFullPacket(input, packet))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead += packetSize;
            packetNumber++;

            int sync = syncOffset;
            if (sync + TsPayloadSize > packet.Length || packet[sync] != 0x47)
                continue;

            bool transportError = (packet[sync + 1] & 0x80) != 0;
            bool payloadUnitStart = (packet[sync + 1] & 0x40) != 0;
            int pid = ((packet[sync + 1] & 0x1F) << 8) | packet[sync + 2];
            int adaptationControl = (packet[sync + 3] >> 4) & 0x03;
            if (transportError || adaptationControl is 0 or 2)
                continue;

            int payloadStart = sync + 4;
            int packetEnd = sync + TsPayloadSize;
            if (adaptationControl == 3)
            {
                if (payloadStart >= packetEnd) continue;
                payloadStart += 1 + packet[payloadStart];
            }
            if (payloadStart >= packetEnd) continue;

            ReadOnlySpan<byte> payload = packet.AsSpan(payloadStart, packetEnd - payloadStart);
            if (pid == 0 || pmtPrograms.ContainsKey(pid))
            {
                if (!psiCollectors.TryGetValue(pid, out PsiSectionCollector? collector))
                {
                    collector = new PsiSectionCollector();
                    psiCollectors[pid] = collector;
                }
                collector.Feed(payload, payloadUnitStart, section =>
                {
                    if (pid == 0)
                    {
                        ParsePat(section, pmtPrograms);
                        foreach (int pmtPid in pmtPrograms.Keys)
                            psiCollectors.TryAdd(pmtPid, new PsiSectionCollector());
                    }
                    else if (pmtPrograms.TryGetValue(pid, out int programNumber))
                    {
                        ParsePmt(section, programNumber, serviceMetadata);
                    }
                });
            }

            if (payloadUnitStart)
            {
                if (pesBuffers.Remove(pid, out MemoryStream? completedPes))
                {
                    rejectedDataUnits += ParseTeletextPes(pid, completedPes, extracted);
                    completedPes.Dispose();
                }

                if (payload.Length >= 6
                    && payload[0] == 0x00
                    && payload[1] == 0x00
                    && payload[2] == 0x01
                    && payload[3] == 0xBD)
                {
                    var pes = new MemoryStream(Math.Max(payload.Length, 2048));
                    pes.Write(payload);
                    pesBuffers[pid] = pes;
                }
            }
            else if (pesBuffers.TryGetValue(pid, out MemoryStream? pes))
            {
                if (pes.Length + payload.Length <= MaxPesBytes)
                    pes.Write(payload);
                else
                {
                    pes.Dispose();
                    pesBuffers.Remove(pid);
                }
            }

            if ((packetNumber & 0xFFF) == 0)
                progress?.Report(new DvbTeletextExtractionProgress(bytesRead, totalBytes));
        }

        foreach ((int pid, MemoryStream pes) in pesBuffers)
        {
            rejectedDataUnits += ParseTeletextPes(pid, pes, extracted);
            pes.Dispose();
        }

        var services = new List<DvbTeletextService>();
        foreach ((int pid, MemoryStream packets) in extracted.OrderBy(item => item.Key))
        {
            if (packets.Length == 0)
            {
                packets.Dispose();
                continue;
            }

            serviceMetadata.TryGetValue(pid, out var metadata);
            services.Add(new DvbTeletextService(
                pid,
                metadata.Program == 0 ? null : metadata.Program,
                metadata.Descriptors ?? new List<DvbTeletextDescriptor>(),
                packets.ToArray()));
            packets.Dispose();
        }

        progress?.Report(new DvbTeletextExtractionProgress(bytesRead, totalBytes));
        return new DvbTeletextExtractionResult(packetSize, services, rejectedDataUnits);
    }

    private static (int PacketSize, int SyncOffset) DetectTransportPacketLayout(Stream input)
    {
        long position = input.Position;
        byte[] probe = new byte[Math.Min(64 * 1024, (int)Math.Min(input.Length - position, int.MaxValue))];
        int length = input.Read(probe, 0, probe.Length);
        input.Position = position;
        if (length < 188 * 3)
            throw new InvalidDataException("The file is too short to identify an MPEG transport stream.");

        (int Size, int Offset, int Score) best = default;
        foreach (int size in new[] { 188, 192, 204 })
        {
            for (int offset = 0; offset < size && offset < length; offset++)
            {
                int score = 0;
                for (int index = offset; index < length; index += size)
                {
                    if (probe[index] == 0x47) score++;
                    else break;
                }
                if (score > best.Score)
                    best = (size, offset, score);
            }
        }

        if (best.Score < 3 || best.Offset + TsPayloadSize > best.Size)
            throw new InvalidDataException("No MPEG-TS or M2TS packet layout was found.");
        return (best.Size, best.Offset);
    }

    private static bool ReadFullPacket(Stream input, byte[] packet)
    {
        int read = 0;
        while (read < packet.Length)
        {
            int count = input.Read(packet, read, packet.Length - read);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    private static long ParseTeletextPes(
        int pid,
        MemoryStream pes,
        Dictionary<int, MemoryStream> extracted)
    {
        ReadOnlySpan<byte> bytes = pes.GetBuffer().AsSpan(0, checked((int)pes.Length));
        if (bytes.Length < 10
            || bytes[0] != 0x00 || bytes[1] != 0x00 || bytes[2] != 0x01
            || bytes[3] != 0xBD)
            return 0;

        int declaredLength = (bytes[4] << 8) | bytes[5];
        int pesEnd = declaredLength > 0
            ? Math.Min(bytes.Length, 6 + declaredLength)
            : bytes.Length;
        int dataStart = 9 + bytes[8];
        if (dataStart >= pesEnd || bytes[dataStart] is < 0x10 or > 0x1F)
            return 0;

        int position = dataStart + 1;
        long rejected = 0;
        Span<byte> t42 = stackalloc byte[42];
        while (position + 2 <= pesEnd)
        {
            byte dataUnitId = bytes[position++];
            int dataUnitLength = bytes[position++];
            if (position + dataUnitLength > pesEnd) break;
            ReadOnlySpan<byte> unit = bytes.Slice(position, dataUnitLength);
            position += dataUnitLength;

            if (dataUnitId == 0xFF) continue;
            if (dataUnitId is not (0x02 or 0x03)
                || dataUnitLength < 44
                || unit[1] != 0xE4)
            {
                rejected++;
                continue;
            }

            for (int index = 0; index < t42.Length; index++)
                t42[index] = ReverseBits(unit[index + 2]);

            var low = Hamming.Decode84(t42[0]);
            var high = Hamming.Decode84(t42[1]);
            if (low.UncorrectableError || high.UncorrectableError)
            {
                rejected++;
                continue;
            }

            if (!extracted.TryGetValue(pid, out MemoryStream? output))
            {
                output = new MemoryStream();
                extracted[pid] = output;
            }
            output.Write(t42);
        }
        return rejected;
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0x55) << 1) | ((value >> 1) & 0x55));
        value = (byte)(((value & 0x33) << 2) | ((value >> 2) & 0x33));
        return (byte)((value << 4) | (value >> 4));
    }

    private static void ParsePat(byte[] section, Dictionary<int, int> pmtPrograms)
    {
        if (section.Length < 12 || section[0] != 0x00) return;
        int end = section.Length - 4;
        for (int position = 8; position + 4 <= end; position += 4)
        {
            int program = (section[position] << 8) | section[position + 1];
            int pid = ((section[position + 2] & 0x1F) << 8) | section[position + 3];
            if (program != 0) pmtPrograms[pid] = program;
        }
    }

    private static void ParsePmt(
        byte[] section,
        int programNumber,
        Dictionary<int, (int Program, List<DvbTeletextDescriptor> Descriptors)> metadata)
    {
        if (section.Length < 16 || section[0] != 0x02) return;
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int position = 12 + programInfoLength;
        int end = section.Length - 4;
        while (position + 5 <= end)
        {
            int elementaryPid = ((section[position + 1] & 0x1F) << 8) | section[position + 2];
            int infoLength = ((section[position + 3] & 0x0F) << 8) | section[position + 4];
            int descriptorPosition = position + 5;
            int descriptorEnd = Math.Min(descriptorPosition + infoLength, end);
            while (descriptorPosition + 2 <= descriptorEnd)
            {
                int tag = section[descriptorPosition++];
                int length = section[descriptorPosition++];
                if (descriptorPosition + length > descriptorEnd) break;
                if (tag == 0x56)
                {
                    if (!metadata.TryGetValue(elementaryPid, out var service))
                        service = (programNumber, new List<DvbTeletextDescriptor>());
                    for (int entry = descriptorPosition; entry + 5 <= descriptorPosition + length; entry += 5)
                    {
                        string language = Encoding.ASCII.GetString(section, entry, 3);
                        int typeAndMagazine = section[entry + 3];
                        int magazine = typeAndMagazine & 0x07;
                        if (magazine == 0) magazine = 8;
                        int pageBcd = section[entry + 4];
                        var descriptor = new DvbTeletextDescriptor(
                            language,
                            (typeAndMagazine >> 3) & 0x1F,
                            magazine,
                            ((pageBcd >> 4) * 10) + (pageBcd & 0x0F));
                        if (!service.Descriptors.Contains(descriptor))
                            service.Descriptors.Add(descriptor);
                    }
                    metadata[elementaryPid] = service;
                }
                descriptorPosition += length;
            }
            position += 5 + infoLength;
        }
    }

    private sealed class PsiSectionCollector
    {
        private readonly List<byte> _buffer = new(512);

        public void Feed(ReadOnlySpan<byte> payload, bool payloadUnitStart, Action<byte[]> onSection)
        {
            if (payload.IsEmpty) return;
            if (payloadUnitStart)
            {
                int pointer = payload[0];
                if (pointer + 1 > payload.Length)
                {
                    _buffer.Clear();
                    return;
                }

                if (_buffer.Count > 0 && pointer > 0)
                {
                    Append(payload.Slice(1, pointer));
                    Drain(onSection);
                }
                _buffer.Clear();
                payload = payload[(pointer + 1)..];
            }

            Append(payload);
            Drain(onSection);
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
                _buffer.Add(value);
        }

        private void Drain(Action<byte[]> onSection)
        {
            while (_buffer.Count >= 3)
            {
                if (_buffer[0] == 0xFF)
                {
                    _buffer.Clear();
                    return;
                }
                int sectionLength = ((_buffer[1] & 0x0F) << 8) | _buffer[2];
                int totalLength = 3 + sectionLength;
                if (totalLength is < 3 or > 4096)
                {
                    _buffer.Clear();
                    return;
                }
                if (_buffer.Count < totalLength) return;
                byte[] section = _buffer.GetRange(0, totalLength).ToArray();
                _buffer.RemoveRange(0, totalLength);
                onSection(section);
            }
        }
    }
}
