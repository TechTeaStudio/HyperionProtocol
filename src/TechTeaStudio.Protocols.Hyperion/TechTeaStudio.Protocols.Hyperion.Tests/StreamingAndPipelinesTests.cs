using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class StreamingAndPipelinesTests
{
    private TcpListener _listener = null!;
    private CancellationTokenSource _serverCts = null!;
    private int _port;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _serverCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = EchoLoopAsync(_listener, _serverCts.Token);
        await Task.Delay(50);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _serverCts.Cancel();
        _listener.Stop();
        _serverCts.Dispose();
        (_listener as IDisposable)?.Dispose();
    }

    [Test]
    public async Task ReceiveStreamingAsync_YieldsChunksAsTheyArrive()
    {
        // Use a direct loopback pair so we can control both sides and assert chunk counts.
        var serverListener = new TcpListener(IPAddress.Loopback, 0);
        serverListener.Start();
        try
        {
            int port = ((IPEndPoint)serverListener.LocalEndpoint).Port;
            var acceptTask = serverListener.AcceptTcpClientAsync();

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await acceptTask;

            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            var protocol = new HyperionProtocol(new DefaultSerializer());

            // 2.5 MiB → 3 chunks at the 1 MiB default chunk size.
            var data = new byte[(int)(2.5 * 1024 * 1024)];
            new Random(7).NextBytes(data);

            var sendTask = protocol.SendAsync(data, serverStream);

            var collected = new List<byte>();
            int chunkCount = 0;
            await foreach (var chunk in protocol.ReceiveStreamingAsync(clientStream))
            {
                chunkCount++;
                collected.AddRange(chunk.ToArray());
            }

            await sendTask;

            Assert.That(chunkCount, Is.EqualTo(3));
            Assert.That(collected, Has.Count.EqualTo(data.Length));
            Assert.That(collected.ToArray(), Is.EqualTo(data));
        }
        finally
        {
            serverListener.Stop();
            (serverListener as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task PipelinesRoundTrip_OverNetworkStream()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        var writer = PipeWriter.Create(stream);
        var reader = PipeReader.Create(stream);

        const string msg = "Hello via Pipelines";
        await protocol.SendAsync(msg, writer);
        var response = await protocol.ReceiveAsync<string>(reader);

        Assert.That(response, Is.EqualTo($"Echo: {msg}"));
    }

    [Test]
    public async Task PipelinesRoundTrip_LargePayload()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        var writer = PipeWriter.Create(stream);
        var reader = PipeReader.Create(stream);

        var data = new byte[2 * 1024 * 1024 + 7];
        new Random(13).NextBytes(data);

        await protocol.SendAsync(data, writer);
        var response = await protocol.ReceiveAsync<string>(reader);
        Assert.That(response, Is.EqualTo($"Received {data.Length} bytes"));
    }

    [Test]
    public async Task Handshake_NegotiatesMinimumVersion()
    {
        // Two loopback peers exchange handshakes simultaneously.
        var serverListener = new TcpListener(IPAddress.Loopback, 0);
        serverListener.Start();
        try
        {
            var port = ((IPEndPoint)serverListener.LocalEndpoint).Port;
            var acceptTask = serverListener.AcceptTcpClientAsync();

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await acceptTask;

            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            var clientHs = HyperionProtocol.HandshakeAsync(clientStream, localVersion: 1);
            var serverHs = HyperionProtocol.HandshakeAsync(serverStream, localVersion: 1);

            var results = await Task.WhenAll(clientHs, serverHs);
            Assert.That(results[0], Is.EqualTo(1));
            Assert.That(results[1], Is.EqualTo(1));
        }
        finally
        {
            serverListener.Stop();
            (serverListener as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task Handshake_PicksTheLowerVersion()
    {
        var serverListener = new TcpListener(IPAddress.Loopback, 0);
        serverListener.Start();
        try
        {
            var port = ((IPEndPoint)serverListener.LocalEndpoint).Port;
            var acceptTask = serverListener.AcceptTcpClientAsync();

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await acceptTask;

            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            // Client thinks it speaks v9, server stuck at v1 → negotiate v1.
            var clientHs = HyperionProtocol.HandshakeAsync(clientStream, localVersion: 9);
            var serverHs = HyperionProtocol.HandshakeAsync(serverStream, localVersion: 1);

            var results = await Task.WhenAll(clientHs, serverHs);
            Assert.That(results[0], Is.EqualTo(1));
            Assert.That(results[1], Is.EqualTo(1));
        }
        finally
        {
            serverListener.Stop();
            (serverListener as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void ValidateHeader_RejectsUnsupportedVersion()
    {
        var header = new PacketHeader
        {
            Version = HyperionProtocol.ProtocolVersion + 5,
            Magic = HyperionProtocol.ProtocolMagic,
            PacketId = Guid.NewGuid(),
            ChunkNumber = 0,
            TotalChunks = 1,
            DataLength = 0,
            Flags = 1,
        };
        Assert.Throws<HyperionProtocolException>(() =>
            HyperionProtocol.ValidateHeader(header, null, int.MaxValue, 0, HyperionProtocolOptions.DefaultChunkSize));
    }

    [Test]
    public void ValidateHeader_AcceptsLegacyZeroVersion()
    {
        var header = new PacketHeader
        {
            Version = 0, // pre-0.3.0 sender
            Magic = HyperionProtocol.ProtocolMagic,
            PacketId = Guid.NewGuid(),
            ChunkNumber = 0,
            TotalChunks = 1,
            DataLength = 0,
            Flags = 1,
        };
        Assert.DoesNotThrow(() =>
            HyperionProtocol.ValidateHeader(header, null, int.MaxValue, 0, HyperionProtocolOptions.DefaultChunkSize));
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        return client;
    }

    private static async Task EchoLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch when (ct.IsCancellationRequested) { return; }
            _ = HandleAsync(tcp, ct);
        }
    }

    private static async Task HandleAsync(TcpClient tcpClient, CancellationToken ct)
    {
        try
        {
            using (tcpClient)
            using (var networkStream = tcpClient.GetStream())
            {
                var protocol = new HyperionProtocol(new DefaultSerializer());
                var rawData = await protocol.ReceiveAsync<byte[]>(networkStream, ct);

                string response = IsLikelyText(rawData)
                    ? $"Echo: {System.Text.Encoding.UTF8.GetString(rawData)}"
                    : $"Received {rawData.Length} bytes";

                await protocol.SendAsync(response, networkStream, ct);
                await networkStream.FlushAsync(ct);
                await Task.Delay(30, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* swallow */ }
    }

    private static bool IsLikelyText(byte[] data)
    {
        if (data.Length == 0) return true;
        var str = System.Text.Encoding.UTF8.GetString(data);
        if (str.Contains('\0')) return false;
        int printable = str.Count(c =>
            char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c));
        return printable > data.Length * 0.8;
    }
}
