using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace TeletextRecoveReese.Core;

/// <summary>
/// Sample format and detection preset of a VBI stream hosted by <see cref="VbiTcpServer"/>.
/// The server sends it before the first frame so a receiver needs no matching preset.
/// Wire format: the 8-byte magic "TRVBISRV", a little-endian UInt32 length and that
/// many bytes of UTF-8 JSON. A headerless (Oxide RF) stream starts with samples instead.
/// </summary>
public sealed record VbiStreamDescription(
    string PresetName,
    double SampleRate,
    int SamplesPerLine,
    int FirstFieldLines,
    int SecondFieldLines,
    bool IsUInt16,
    int LineStart,
    int LineStartEnd,
    float StandardDeviationThreshold,
    float SignalLevelThreshold,
    float CriFcRangeThreshold,
    double CriFcConfidenceThreshold)
{
    public static ReadOnlySpan<byte> Magic => "TRVBISRV"u8;
    private const int MaximumJsonLength = 64 * 1024;

    public int BytesPerSample => IsUInt16 ? 2 : 1;
    public int FrameBytes => checked(SamplesPerLine * (FirstFieldLines + SecondFieldLines) * BytesPerSample);

    public byte[] Serialize()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(this);
        var header = new byte[Magic.Length + 4 + json.Length];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(Magic.Length), (uint)json.Length);
        json.CopyTo(header, Magic.Length + 4);
        return header;
    }

    /// <summary>Reads the JSON that follows the magic.</summary>
    public static async Task<VbiStreamDescription> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await source.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        uint jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(length);
        if (jsonLength is 0 or > MaximumJsonLength)
            throw new InvalidDataException("Invalid VBI stream header.");
        var json = new byte[jsonLength];
        await source.ReadExactlyAsync(json, cancellationToken).ConfigureAwait(false);
        VbiStreamDescription description = JsonSerializer.Deserialize<VbiStreamDescription>(json)
            ?? throw new InvalidDataException("Invalid VBI stream header.");
        if (description.SampleRate <= 0 || description.SamplesPerLine is < 1 or > 16384
            || description.FirstFieldLines is < 1 or > 64 || description.SecondFieldLines is < 0 or > 64)
            throw new InvalidDataException(
                $"Unsupported VBI stream format: {Encoding.UTF8.GetString(json)}");
        return description;
    }
}
