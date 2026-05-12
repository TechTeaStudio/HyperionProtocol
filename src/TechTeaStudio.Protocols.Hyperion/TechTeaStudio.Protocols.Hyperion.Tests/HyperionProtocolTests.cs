using System.Net;
using System.Net.Sockets;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class HyperionProtocolTests
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
    public async Task SendReceive_SimpleString_Success()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        const string testMessage = "Hello HyperionProtocol!";
        await protocol.SendAsync(testMessage, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Echo: {testMessage}"));
    }

    [Test]
    public async Task SendReceive_EmptyString_Success()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        await protocol.SendAsync(string.Empty, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo("Echo: "));
    }

    [Test]
    public async Task SendReceive_LargeString_Success()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        var largeString = new string('A', 10_000);
        await protocol.SendAsync(largeString, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Echo: {largeString}"));
    }

    [Test]
    public async Task SendReceive_ByteArray_Success()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        var testData = GenerateTestData(1024);
        await protocol.SendAsync(testData, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Received {testData.Length} bytes"));
    }

    [Test]
    public async Task SendReceive_LargeByteArray_Success()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        var largeData = GenerateTestData(5 * 1024 * 1024);
        await protocol.SendAsync(largeData, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Received {largeData.Length} bytes"));
    }

    [Test]
    public async Task SendReceive_MultipleChunks_Success()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());

        var data = GenerateTestData(2 * 1024 * 1024);
        await protocol.SendAsync(data, stream);
        var response = await protocol.ReceiveAsync<string>(stream);

        Assert.That(response, Is.EqualTo($"Received {data.Length} bytes"));
    }

    [Test]
    public async Task SendReceive_ConcurrentClients_Success()
    {
        const int clientCount = 10;
        var tasks = new List<Task>();

        for (int i = 0; i < clientCount; i++)
        {
            int clientId = i;
            tasks.Add(Task.Run(async () =>
            {
                using var client = await ConnectAsync();
                using var stream = client.GetStream();
                var protocol = new HyperionProtocol(new DefaultSerializer());
                var message = $"Client {clientId} message";

                await protocol.SendAsync(message, stream);
                var response = await protocol.ReceiveAsync<string>(stream);
                Assert.That(response, Is.EqualTo($"Echo: {message}"));
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Test]
    public void SendAsync_NullStream_ThrowsArgumentNullException()
    {
        var protocol = new HyperionProtocol(new DefaultSerializer());
        NetworkStream nullStream = null!;
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await protocol.SendAsync("test", nullStream));
    }

    [Test]
    public void ReceiveAsync_NullStream_ThrowsArgumentNullException()
    {
        var protocol = new HyperionProtocol(new DefaultSerializer());
        NetworkStream nullStream = null!;
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await protocol.ReceiveAsync<string>(nullStream));
    }

    [Test]
    public async Task SendAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        using var client = await ConnectAsync();
        using var stream = client.GetStream();
        var protocol = new HyperionProtocol(new DefaultSerializer());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await protocol.SendAsync("test", stream, cts.Token));
    }

    [Test]
    public void Ctor_NullSerializer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HyperionProtocol(null!));
    }

    [Test]
    public void Ctor_InvalidOptions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = 0 }));
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

            _ = HandleClientAsync(tcp, ct);
        }
    }

    private static async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
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

    private static byte[] GenerateTestData(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }
}
