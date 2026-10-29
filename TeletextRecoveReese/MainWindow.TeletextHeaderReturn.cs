using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Threading;
using TeletextRecoveReese.Core;

namespace TeletextRecoveReese;

public partial class MainWindow
{
    private TeletextGridControl? _headerRenderGrid;

    /// <summary>Render a header packet (row 0) in the native screenshot style,
    /// with the G0 subset currently chosen for display.</summary>
    private (int Width, int Height, byte[] Rgba) RenderHeaderRow(byte[] packet)
    {
        var page = new TeletextPage();
        if (TryDecodePacketAddress(packet, out int magazine, out _)) page.Magazine = magazine;
        PageAssembler.ApplyRow(page, 0, packet);
        ApplyFileG0SubsetToPage(page);
        _headerRenderGrid ??= new TeletextGridControl
        {
            IsHitTestVisible = false,
            SuppressFlash = true,
            IsActive = false,
        };
        return _headerRenderGrid.RenderRowRgba(page, 0);
    }

    /// <summary>Returns each changed teletext header to a TCP VBI sender as an image,
    /// tagged with the VBI frame it was decoded from, so the sender can show or burn
    /// in the broadcast clock at the right moment.</summary>
    private sealed class TeletextHeaderReturn : IVbiPacketObserver, IAsyncDisposable
    {
        // At most ten images per second; the clock changes once per second.
        private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(100);
        private readonly MainWindow _owner;
        private readonly TcpVbiCaptureStream _stream;
        private readonly int _linesPerFrame;
        private readonly Channel<(long Frame, byte[] Packet)> _headers =
            Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _sender;

        public TeletextHeaderReturn(MainWindow owner, TcpVbiCaptureStream stream, int linesPerFrame)
        {
            _owner = owner;
            _stream = stream;
            _linesPerFrame = linesPerFrame;
            _sender = Task.Run(SendAsync);
        }

        public void OnPacket(long lineIndex, byte[] packet)
        {
            if (!TryDecodePacketAddress(packet, out _, out int row) || row != 0) return;
            _headers.Writer.TryWrite((lineIndex / _linesPerFrame, (byte[])packet.Clone()));
        }

        private async Task SendAsync()
        {
            byte[]? lastText = null;
            long lastSent = 0;
            try
            {
                await foreach (var (frame, packet) in _headers.Reader.ReadAllAsync(_stop.Token))
                {
                    // Bytes 10-41 are the 32 displayed header columns.
                    byte[] text = packet[10..42];
                    if (lastText is not null && text.AsSpan().SequenceEqual(lastText)) continue;
                    if (lastSent != 0 && Stopwatch.GetElapsedTime(lastSent) < MinimumInterval) continue;
                    var (width, height, rgba) = await Dispatcher.UIThread.InvokeAsync(
                        () => _owner.RenderHeaderRow(packet));
                    await _stream.SendHeaderImageAsync(frame, width, height, rgba, _stop.Token);
                    lastText = text;
                    lastSent = Stopwatch.GetTimestamp();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _headers.Writer.TryComplete();
            _stop.Cancel();
            try { await _sender.ConfigureAwait(false); } catch { }
            _stop.Dispose();
        }
    }
}
