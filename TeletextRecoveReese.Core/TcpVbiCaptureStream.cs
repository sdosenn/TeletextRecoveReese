using System.Buffers.Binary;
using System.Net.Sockets;

namespace TeletextRecoveReese.Core;

// Raw, headerless VBI-only TBC transport. A connection starts with a first field.
// Teletext header row images can be returned to the sender on the same connection:
// a 20-byte little-endian header ("OXTH", version 1, kind 1, two reserved bytes,
// the VBI frame index counted from the first field received as Int64, width and
// height as UInt16) followed by width x height straight-alpha RGBA pixels.
// A TeletextRecoveReese VbiTcpServer instead starts with a VbiStreamDescription
// header that gives its own sample format; it ignores returned header rows.
public sealed class TcpVbiCaptureStream : LiveVbiCaptureStream
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TcpClient _client;
    private readonly NetworkStream _source;
    private readonly byte[] _frame;
    private readonly int _sampleBytes;
    private int _offset;
    private int _length;
    private long _capturedFrames;

    private TcpVbiCaptureStream(
        TcpClient client, VbiStreamDescription description, ReadOnlySpan<byte> firstSamples)
    {
        _client = client;
        _source = client.GetStream();
        Description = description;
        _sampleBytes = description.BytesPerSample;
        _frame = new byte[description.FrameBytes];
        // Samples already read while looking for a server header start the first frame.
        firstSamples.CopyTo(_frame);
        _length = firstSamples.Length;
    }

    /// <summary>Stream format: sent by a VbiTcpServer, otherwise the VHS-decode VBI-only TBC layout.</summary>
    public VbiStreamDescription Description { get; }

    /// <summary>True when the sender is a VbiTcpServer rather than a headerless (Oxide RF) sender.</summary>
    public bool IsServerStream { get; private init; }

    public static async Task<TcpVbiCaptureStream> ConnectAsync(
        string address, VbiCaptureOptions options, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "tcp"
            || string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535
            || uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("Enter tcp://HOST:5556.", nameof(address));
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
            NetworkStream source = client.GetStream();
            var start = new byte[VbiStreamDescription.Magic.Length];
            int startLength = 0;
            while (startLength < start.Length)
            {
                int count = await source.ReadAsync(start.AsMemory(startLength), timeout.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The VBI TCP sender closed the connection.");
                startLength += count;
            }
            if (start.AsSpan().SequenceEqual(VbiStreamDescription.Magic))
            {
                VbiStreamDescription description =
                    await VbiStreamDescription.ReadAsync(source, timeout.Token).ConfigureAwait(false);
                return new TcpVbiCaptureStream(client, description, []) { IsServerStream = true };
            }
            if (!options.IsUInt16 || options.LineLength != 1135 || options.FieldLines != 16
                || options.FieldRangeStart != 0 || options.FieldRangeEnd != 16)
                throw new ArgumentException("Select the VHS-decode VBI-only TBC preset (UInt16, 1135 samples, 16 lines).", nameof(options));
            var headerless = new VbiStreamDescription(
                options.Name, options.SampleRate, 1135, 16, 16, true,
                options.LineStart, options.LineStartEnd,
                options.StandardDeviationThreshold, options.SignalLevelThreshold,
                options.CriFcRangeThreshold, options.CriFcConfidenceThreshold);
            return new TcpVbiCaptureStream(client, headerless, start);
        }
        catch { client.Dispose(); throw; }
    }

    public override uint SamplingRate => checked((uint)Math.Round(Description.SampleRate));
    public override int SamplesPerLine => Description.SamplesPerLine;
    public override int FirstFieldLines => Description.FirstFieldLines;
    public override int SecondFieldLines => Description.SecondFieldLines;
    public override long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public override Action<byte[]>? RawFrameCaptured { get; set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_offset == _length && _length == _frame.Length) _offset = _length = 0;
        // The first frame may already hold samples read during connection.
        if (_length < _frame.Length)
        {
            while (_length < _frame.Length)
            {
                int count = await _source.ReadAsync(_frame.AsMemory(_length), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (_length == 0) return 0;
                    // A final original first field has no matching second field.
                    if (_length == _frame.Length / 2) { _length = 0; return 0; }
                    throw new InvalidDataException("VBI TCP stream ended in the middle of a field.");
                }
                _length += count;
            }
            Interlocked.Increment(ref _capturedFrames);
            if (RawFrameCaptured is { } callback)
            {
                var preview = new byte[_frame.Length / _sampleBytes];
                if (_sampleBytes == 1) _frame.CopyTo(preview, 0);
                else for (int i = 0; i < preview.Length; i++) preview[i] = _frame[i * 2 + 1];
                callback(preview);
            }
        }
        int available = Math.Min(buffer.Length, _length - _offset);
        _frame.AsMemory(_offset, available).CopyTo(buffer);
        _offset += available;
        return available;
    }
    /// <summary>Send a rendered teletext header row back to the VBI sender.</summary>
    public async Task SendHeaderImageAsync(
        long frame, int width, int height, byte[] rgba, CancellationToken cancellationToken = default)
    {
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue
            || rgba.Length != width * height * 4)
            throw new ArgumentException("Invalid header image size.", nameof(rgba));
        var header = new byte[20];
        "OXTH"u8.CopyTo(header);
        header[4] = 1;
        header[5] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), frame);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), (ushort)height);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _source.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _source.WriteAsync(rgba, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _source.Dispose(); _client.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
