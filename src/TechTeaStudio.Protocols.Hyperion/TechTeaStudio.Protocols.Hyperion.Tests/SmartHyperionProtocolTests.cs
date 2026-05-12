using System.Net;
using System.Net.Sockets;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class SmartHyperionProtocolTests
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

        _ = AcceptLoopAsync(_listener, _serverCts.Token);
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
    public async Task Lightweight_RoundTrips_SmallString()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new SmartHyperionProtocol(new DefaultSerializer());

        const string msg = "tiny";
        await protocol.SendAsync(msg, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Echo: {msg}"));
        var stats = protocol.GetStatsSnapshot();
        Assert.That(stats.LightweightMessagesSent, Is.EqualTo(1));
    }

    [Test]
    public async Task Direct_RoundTrips_MediumPayload()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new SmartHyperionProtocol(new DefaultSerializer());

        // ~5 KiB string lands in the direct (< 64 KiB) bucket.
        var msg = new string('B', 5000);
        await protocol.SendAsync(msg, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Echo: {msg}"));
        var stats = protocol.GetStatsSnapshot();
        Assert.That(stats.DirectMessagesSent, Is.EqualTo(1));
    }

    [Test]
    public async Task Chunked_RoundTrips_LargePayload()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new SmartHyperionProtocol(new DefaultSerializer());

        var data = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(data);

        await protocol.SendAsync(data, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Received {data.Length} bytes"));
        var stats = protocol.GetStatsSnapshot();
        Assert.That(stats.ChunkedMessagesSent, Is.EqualTo(1));
    }

    [Test]
    public async Task ResetStats_ZeroesCounters()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new SmartHyperionProtocol(new DefaultSerializer());

        await protocol.SendAsync("x", stream);
        _ = await protocol.ReceiveAsync<string>(stream);
        Assert.That(protocol.GetStatsSnapshot().TotalMessagesSent, Is.GreaterThan(0));

        protocol.ResetStats();
        Assert.That(protocol.GetStatsSnapshot().TotalMessagesSent, Is.EqualTo(0));
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        return client;
    }

    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
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
                var protocol = new SmartHyperionProtocol(new DefaultSerializer());
                var rawData = await protocol.ReceiveAsync<byte[]>(networkStream, ct);

                string response = IsLikelyText(rawData)
                    ? $"Echo: {System.Text.Encoding.UTF8.GetString(rawData)}"
                    : $"Received {rawData.Length} bytes";

                await protocol.SendAsync(response, networkStream, ct);
                await networkStream.FlushAsync(ct);
                await Task.Delay(20, ct);
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
