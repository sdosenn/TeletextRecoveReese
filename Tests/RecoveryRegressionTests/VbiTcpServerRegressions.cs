#nullable enable
using System.Net;
using System.Net.Sockets;
using TeletextRecoveReese.Core;

internal static class VbiTcpServerRegressions
{
    public static void Run()
    {
        ServerStreamDescribesItsFormat().GetAwaiter().GetResult();
        HeaderlessStreamKeepsItsFirstSamples().GetAwaiter().GetResult();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly VbiCaptureOptions VhsDecodeOptions = new(
        "VHS-decode VBI-only TBC", 17_734_375, 1135, 0, 0, true, 16, 0, 16);

    private static async Task ServerStreamDescribesItsFormat()
    {
        var description = new VbiStreamDescription(
            "cx231xx", 28_636_363, 1440, 18, 18, false, 40, 140, 14, 64, 28, 0.2);
        using var server = VbiTcpServer.Start(FreePort(), description);
        // Frames written before a receiver connects are not sent to it.
        server.Write(new byte[description.FrameBytes]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await TcpVbiCaptureStream.ConnectAsync(
            $"tcp://127.0.0.1:{server.Port}", VhsDecodeOptions, timeout.Token);
        if (!client.IsServerStream || client.Description != description
            || client.SamplesPerLine != 1440 || client.LinesPerFrame != 36)
            throw new Exception("The VBI server header was not applied.");

        var frames = new byte[description.FrameBytes * 2 + 100];
        for (int i = 0; i < frames.Length; i++) frames[i] = (byte)(i * 7);
        // Odd chunk sizes check that frames are reassembled from contiguous writes.
        for (int offset = 0; offset < frames.Length; offset += 1000)
            server.Write(frames.AsSpan(offset, Math.Min(1000, frames.Length - offset)));
        var received = new byte[description.FrameBytes * 2];
        await client.ReadExactlyAsync(received, timeout.Token);
        if (!received.AsSpan().SequenceEqual(frames.AsSpan(0, received.Length)))
            throw new Exception("The VBI server changed the frame data.");
        if (client.CapturedFrames != 2)
            throw new Exception($"The receiver counted {client.CapturedFrames} frames.");
        server.Dispose();
        if (await client.ReadAsync(new byte[16], timeout.Token) != 0)
            throw new Exception("A partial frame was sent after the server stopped.");
        Console.WriteLine("PASS: VBI TCP server describes its format and sends whole frames");
    }

    private static async Task HeaderlessStreamKeepsItsFirstSamples()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int frameBytes = 1135 * 32 * 2;
        var frame = new byte[frameBytes];
        for (int i = 0; i < frame.Length; i++) frame[i] = (byte)(i * 13 + 1);
        Task sender = Task.Run(async () =>
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            await connection.GetStream().WriteAsync(frame);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await TcpVbiCaptureStream.ConnectAsync(
            $"tcp://127.0.0.1:{port}", VhsDecodeOptions, timeout.Token);
        var received = new byte[frameBytes];
        await client.ReadExactlyAsync(received, timeout.Token);
        await sender;
        listener.Stop();
        if (client.IsServerStream || !received.AsSpan().SequenceEqual(frame))
            throw new Exception("A headerless VHS-decode stream lost its first samples.");
        Console.WriteLine("PASS: headerless VHS-decode TCP stream is read unchanged");
    }
}
