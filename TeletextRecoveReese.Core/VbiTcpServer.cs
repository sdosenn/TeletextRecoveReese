using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;

namespace TeletextRecoveReese.Core;

/// <summary>
/// Hosts a live capture's raw VBI for TCP receivers (trusted LAN). Bytes written to
/// this stream are the capture's contiguous raw sample stream; they are cut into
/// whole frames, and each connected receiver gets the <see cref="VbiStreamDescription"/>
/// header followed by every frame captured after it connected, so its stream always
/// starts with a first field. A receiver that falls behind loses whole frames.
/// Data sent back by a receiver (header row images) is read and discarded.
/// </summary>
public sealed class VbiTcpServer : Stream
{
    public const int DefaultPort = 5556;
    private const int ClientQueueFrames = 50;

    private readonly TcpListener _listener;
    private readonly byte[] _header;
    private readonly int _frameBytes;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _clientsLock = new();
    private readonly List<Channel<byte[]>> _clients = [];
    private byte[] _frame;
    private int _frameLength;

    private VbiTcpServer(TcpListener listener, VbiStreamDescription description)
    {
        _listener = listener;
        _header = description.Serialize();
        _frameBytes = description.FrameBytes;
        _frame = new byte[_frameBytes];
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    /// <summary>Listens on every local IPv4 and IPv6 address.</summary>
    public static VbiTcpServer Start(int port, VbiStreamDescription description)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        try
        {
            listener.Start();
            return new VbiTcpServer(listener, description);
        }
        catch { listener.Stop(); throw; }
    }

    /// <summary>tcp:// addresses a receiver on the local network can use.</summary>
    public IReadOnlyList<string> GetAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add($"tcp://{unicast.Address}:{Port}");
            }
        }
        catch (NetworkInformationException) { }
        if (addresses.Count == 0) addresses.Add($"tcp://127.0.0.1:{Port}");
        return addresses;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false); }
            catch { return; }
            client.NoDelay = true;
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueueFrames)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            lock (_clientsLock) _clients.Add(queue);
            _ = ServeClientAsync(client, queue);
        }
    }

    private async Task ServeClientAsync(TcpClient client, Channel<byte[]> queue)
    {
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                _ = DiscardIncomingAsync(stream, disconnected);
                await stream.WriteAsync(_header, disconnected.Token).ConfigureAwait(false);
                await foreach (byte[] frame in queue.Reader.ReadAllAsync(disconnected.Token).ConfigureAwait(false))
                    await stream.WriteAsync(frame, disconnected.Token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            lock (_clientsLock) _clients.Remove(queue);
            disconnected.Cancel();
        }
    }

    private static async Task DiscardIncomingAsync(NetworkStream stream, CancellationTokenSource disconnected)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (await stream.ReadAsync(buffer, disconnected.Token).ConfigureAwait(false) > 0) { }
            disconnected.Cancel();
        }
        catch { }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(buffer.Length, _frameBytes - _frameLength);
            buffer[..count].CopyTo(_frame.AsSpan(_frameLength));
            _frameLength += count;
            buffer = buffer[count..];
            if (_frameLength < _frameBytes) continue;
            lock (_clientsLock)
                foreach (Channel<byte[]> client in _clients)
                    client.Writer.TryWrite(_frame);
            // Queued frames are shared by every receiver and never modified again.
            _frame = new byte[_frameBytes];
            _frameLength = 0;
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
            _listener.Stop();
            lock (_clientsLock)
                foreach (Channel<byte[]> client in _clients)
                    client.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }
}
