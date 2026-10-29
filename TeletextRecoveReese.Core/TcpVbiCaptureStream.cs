using System.Buffers.Binary;
using System.Net.Sockets;

namespace TeletextRecoveReese.Core;

// Raw, headerless VBI-only TBC transport. A connection starts with a first field.
// Teletext header row images can be returned to the sender on the same connection:
// a 20-byte little-endian header ("OXTH", version 1, kind 1, two reserved bytes,
// the VBI frame index counted from the first field received as Int64, width and
// height as UInt16) followed by width x height straight-alpha RGBA pixels.
public sealed class TcpVbiCaptureStream : LiveVbiCaptureStream
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TcpClient _client;
    private readonly NetworkStream _source;
    private readonly VbiCaptureOptions _options;
    private readonly byte[] _frame;
    private int _offset;
    private int _length;
    private long _capturedFrames;

    private TcpVbiCaptureStream(TcpClient client, VbiCaptureOptions options)
    {
        _client = client;
        _source = client.GetStream();
        _options = options;
        _frame = new byte[checked(options.LineLength * options.FieldLines * 2 * 2)];
    }

    public static async Task<TcpVbiCaptureStream> ConnectAsync(
        string address, VbiCaptureOptions options, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "tcp"
            || string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535
            || uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("Enter tcp://HOST:5556.", nameof(address));
        if (!options.IsUInt16 || options.LineLength != 1135 || options.FieldLines != 16
            || options.FieldRangeStart != 0 || options.FieldRangeEnd != 16)
            throw new ArgumentException("Select the VHS-decode VBI-only TBC preset (UInt16, 1135 samples, 16 lines).", nameof(options));
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
            return new TcpVbiCaptureStream(client, options);
        }
        catch { client.Dispose(); throw; }
    }

    public override uint SamplingRate => checked((uint)_options.SampleRate);
    public override int SamplesPerLine => _options.LineLength;
    public override int FirstFieldLines => 16;
    public override int SecondFieldLines => 16;
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
        if (_offset == _length)
        {
            _offset = _length = 0;
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
                var preview = new byte[_frame.Length / 2];
                for (int i = 0; i < preview.Length; i++) preview[i] = _frame[i * 2 + 1];
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
