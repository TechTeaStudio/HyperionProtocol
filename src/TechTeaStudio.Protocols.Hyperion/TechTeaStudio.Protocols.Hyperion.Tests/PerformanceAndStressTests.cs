using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

/// <summary>
/// Performance and stress tests. Tagged so they can be filtered out on slow runners.
/// Run only these locally with:
///   dotnet test --filter "TestCategory=Performance|TestCategory=Stress"
/// </summary>
[TestFixture]
[Category("Performance")]
public class PerformanceAndStressTests
{
    private const int LoopbackMinMbPerSec = 30; // generous lower bound for CI/loopback

    // --- Throughput: one-way, single client ----------------------------------

    [Test, Category("Performance")]
    [TestCase(50, TestName = "Throughput_OneWay_50MB_HyperionProtocol")]
    [TestCase(100, TestName = "Throughput_OneWay_100MB_HyperionProtocol")]
    public async Task Throughput_OneWay_HyperionProtocol(int sizeMb)
    {
        await Loopback(async (clientStream, serverStream) =>
        {
            var protocol = new HyperionProtocol(new DefaultSerializer());
            var data = MakeData(sizeMb * 1024 * 1024);

            // Warmup: serializer JIT, GC.
            await protocol.SendAsync(MakeData(64 * 1024), clientStream);
            _ = await protocol.ReceiveAsync<byte[]>(serverStream);
            GcCollect();

            var sw = Stopwatch.StartNew();
            var sendTask = protocol.SendAsync(data, clientStream);
            var received = await protocol.ReceiveAsync<byte[]>(serverStream);
            await sendTask;
            sw.Stop();

            Assert.That(received, Has.Length.EqualTo(data.Length));
            ReportThroughput($"HyperionProtocol one-way {sizeMb} MB", data.Length, sw.Elapsed);
            AssertMinThroughput(data.Length, sw.Elapsed, LoopbackMinMbPerSec);
        });
    }

    [Test, Category("Performance")]
    [TestCase(50)]
    [TestCase(100)]
    public async Task Throughput_OneWay_Pipelines(int sizeMb)
    {
        await Loopback(async (clientStream, serverStream) =>
        {
            var protocol = new HyperionProtocol(new DefaultSerializer());
            var data = MakeData(sizeMb * 1024 * 1024);

            var writer = PipeWriter.Create(clientStream);
            var reader = PipeReader.Create(serverStream);

            // Warmup.
            await protocol.SendAsync(MakeData(64 * 1024), writer);
            _ = await protocol.ReceiveAsync<byte[]>(reader);
            GcCollect();

            var sw = Stopwatch.StartNew();
            var sendTask = protocol.SendAsync(data, writer);
            var received = await protocol.ReceiveAsync<byte[]>(reader);
            await sendTask;
            sw.Stop();

            Assert.That(received, Has.Length.EqualTo(data.Length));
            ReportThroughput($"Pipelines one-way {sizeMb} MB", data.Length, sw.Elapsed);
            AssertMinThroughput(data.Length, sw.Elapsed, LoopbackMinMbPerSec);
        });
    }

    [Test, Category("Performance")]
    [TestCase(50)]
    [TestCase(100)]
    public async Task Throughput_OneWay_Streaming(int sizeMb)
    {
        await Loopback(async (clientStream, serverStream) =>
        {
            var protocol = new HyperionProtocol(new DefaultSerializer());
            var data = MakeData(sizeMb * 1024 * 1024);

            // Warmup.
            await protocol.SendAsync(MakeData(64 * 1024), clientStream);
            await foreach (var _ in protocol.ReceiveStreamingAsync(serverStream)) { }
            GcCollect();

            var sw = Stopwatch.StartNew();
            var sendTask = protocol.SendAsync(data, clientStream);

            long totalBytes = 0;
            int chunkCount = 0;
            await foreach (var chunk in protocol.ReceiveStreamingAsync(serverStream))
            {
                totalBytes += chunk.Length;
                chunkCount++;
            }
            await sendTask;
            sw.Stop();

            Assert.That(totalBytes, Is.EqualTo(data.Length));
            ReportThroughput($"Streaming one-way {sizeMb} MB ({chunkCount} chunks)", data.Length, sw.Elapsed);
            AssertMinThroughput(data.Length, sw.Elapsed, LoopbackMinMbPerSec);
        });
    }

    [Test, Category("Performance")]
    public async Task Throughput_SmallMessages_OpsPerSecond_Smart()
    {
        // 5_000 small (<1 KiB) round-trips through SmartHyperionProtocol's lightweight path.
        const int rounds = 5_000;

        await Loopback(async (clientStream, serverStream) =>
        {
            var clientProto = new SmartHyperionProtocol(new DefaultSerializer());
            var serverProto = new SmartHyperionProtocol(new DefaultSerializer());

            // Warmup.
            for (int i = 0; i < 50; i++)
            {
                var s = clientProto.SendAsync("warm", clientStream);
                var r = serverProto.ReceiveAsync<string>(serverStream);
                await Task.WhenAll(s, r);
                await serverProto.SendAsync("ok", serverStream);
                _ = await clientProto.ReceiveAsync<string>(clientStream);
            }
            GcCollect();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < rounds; i++)
            {
                var s = clientProto.SendAsync($"m{i}", clientStream);
                var msg = await serverProto.ReceiveAsync<string>(serverStream);
                await s;
                await serverProto.SendAsync($"r{i}", serverStream);
                _ = await clientProto.ReceiveAsync<string>(clientStream);
            }
            sw.Stop();

            var stats = clientProto.GetStatsSnapshot();
            Assert.That(stats.LightweightMessagesSent, Is.EqualTo(rounds + 50));

            double opsPerSec = rounds / sw.Elapsed.TotalSeconds;
            TestContext.Progress.WriteLine(
                $"[perf] Smart lightweight RTT: {rounds} round-trips in {sw.ElapsedMilliseconds} ms = " +
                $"{opsPerSec:N0} ops/sec ({sw.Elapsed.TotalMilliseconds / rounds:F3} ms/op)");

            Assert.That(opsPerSec, Is.GreaterThan(500), "Should sustain at least 500 round-trips/sec on loopback.");
        });
    }

    // --- Stress: many concurrent clients -------------------------------------

    [Test, Category("Stress")]
    [TestCase(50)]
    [TestCase(100)]
    public async Task Stress_ConcurrentClients_AllComplete(int clientCount)
    {
        await WithEchoServer(async port =>
        {
            var sw = Stopwatch.StartNew();
            var tasks = new Task[clientCount];

            for (int i = 0; i < clientCount; i++)
            {
                int id = i;
                tasks[i] = Task.Run(async () =>
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, port);
                    using var stream = tcp.GetStream();

                    var protocol = new HyperionProtocol(new DefaultSerializer());
                    string msg = $"client-{id}";
                    await protocol.SendAsync(msg, stream);
                    var response = await protocol.ReceiveAsync<string>(stream);
                    Assert.That(response, Is.EqualTo($"Echo: {msg}"));
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();
            TestContext.Progress.WriteLine(
                $"[stress] {clientCount} concurrent clients round-trip in {sw.ElapsedMilliseconds} ms " +
                $"({sw.Elapsed.TotalMilliseconds / clientCount:F1} ms/client avg)");
        });
    }

    [Test, Category("Stress")]
    public async Task Stress_MixedSizes_50Clients_SmartProtocol()
    {
        // Mix of payload sizes — exercises all three SmartHyperionProtocol framing modes
        // concurrently and confirms no corruption across the listener.
        const int clientCount = 50;
        int[] sizes = { 100, 1_000, 10_000, 100_000, 500_000, 1_500_000 };

        await WithEchoServer(async port =>
        {
            var sw = Stopwatch.StartNew();
            var tasks = new Task[clientCount];
            long totalSent = 0;

            for (int i = 0; i < clientCount; i++)
            {
                int id = i;
                int size = sizes[i % sizes.Length];
                tasks[i] = Task.Run(async () =>
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, port);
                    using var stream = tcp.GetStream();

                    var protocol = new SmartHyperionProtocol(new DefaultSerializer());
                    var data = MakeData(size, seed: id);
                    await protocol.SendAsync(data, stream);
                    Interlocked.Add(ref totalSent, size);

                    var response = await protocol.ReceiveAsync<string>(stream);
                    Assert.That(response, Is.EqualTo($"Received {size} bytes"));
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            double mbps = totalSent / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            TestContext.Progress.WriteLine(
                $"[stress] mixed-size: {clientCount} clients pushed " +
                $"{totalSent / (1024.0 * 1024.0):F1} MiB in {sw.ElapsedMilliseconds} ms = {mbps:F1} MiB/sec");
        }, useSmart: true);
    }

    [Test, Category("Stress")]
    public async Task Stress_SustainedLoad_100Clients_SmallMessages()
    {
        // Each client does 20 round-trips of small messages → 2000 messages total.
        const int clientCount = 100;
        const int rtripsPerClient = 20;

        await WithEchoServer(async port =>
        {
            var sw = Stopwatch.StartNew();
            var tasks = new Task[clientCount];

            for (int i = 0; i < clientCount; i++)
            {
                int id = i;
                tasks[i] = Task.Run(async () =>
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, port);
                    using var stream = tcp.GetStream();

                    var protocol = new SmartHyperionProtocol(new DefaultSerializer());
                    for (int r = 0; r < rtripsPerClient; r++)
                    {
                        string msg = $"c{id}-r{r}";
                        await protocol.SendAsync(msg, stream);
                        var response = await protocol.ReceiveAsync<string>(stream);
                        Assert.That(response, Is.EqualTo($"Echo: {msg}"));
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            int total = clientCount * rtripsPerClient;
            double opsPerSec = total / sw.Elapsed.TotalSeconds;
            TestContext.Progress.WriteLine(
                $"[stress] sustained: {clientCount} clients × {rtripsPerClient} RTs = {total} ops in " +
                $"{sw.ElapsedMilliseconds} ms = {opsPerSec:N0} ops/sec");
        }, useSmart: true);
    }

    // --- Helpers --------------------------------------------------------------

    private static byte[] MakeData(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void GcCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void ReportThroughput(string label, long bytes, TimeSpan elapsed)
    {
        double mibps = bytes / (1024.0 * 1024.0) / elapsed.TotalSeconds;
        TestContext.Progress.WriteLine(
            $"[perf] {label}: {bytes / (1024.0 * 1024.0):F1} MiB in {elapsed.TotalMilliseconds:F0} ms = {mibps:F1} MiB/sec");
    }

    private static void AssertMinThroughput(long bytes, TimeSpan elapsed, double minMbPerSec)
    {
        double mibps = bytes / (1024.0 * 1024.0) / elapsed.TotalSeconds;
        Assert.That(mibps, Is.GreaterThan(minMbPerSec),
            $"Throughput {mibps:F1} MiB/s below regression floor of {minMbPerSec} MiB/s.");
    }

    /// <summary>Direct 1:1 loopback. No echo server — both ends are controlled here.</summary>
    private static async Task Loopback(Func<NetworkStream, NetworkStream, Task> body)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await acceptTask;

            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            await body(clientStream, serverStream);
        }
        finally
        {
            listener.Stop();
            (listener as IDisposable)?.Dispose();
        }
    }

    /// <summary>Stand up an echo server on an ephemeral port, run <paramref name="body"/>, then tear down.</summary>
    private static async Task WithEchoServer(Func<int, Task> body, bool useSmart = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cts = new CancellationTokenSource();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(listener, useSmart, cts.Token);
            await body(port);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            (listener as IDisposable)?.Dispose();
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, bool useSmart, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch when (ct.IsCancellationRequested) { return; }

            _ = HandleAsync(tcp, useSmart, ct);
        }
    }

    private static async Task HandleAsync(TcpClient tcpClient, bool useSmart, CancellationToken ct)
    {
        try
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            {
                HyperionProtocol protocol = useSmart
                    ? new SmartHyperionProtocol(new DefaultSerializer())
                    : new HyperionProtocol(new DefaultSerializer());

                // Loop until the client disconnects (protocol surfaces this as a HyperionProtocolException
                // wrapping EndOfStreamException, which falls through to the catch below).
                while (!ct.IsCancellationRequested)
                {
                    var rawData = await protocol.ReceiveAsync<byte[]>(stream, ct);
                    string response = IsLikelyText(rawData)
                        ? $"Echo: {System.Text.Encoding.UTF8.GetString(rawData)}"
                        : $"Received {rawData.Length} bytes";

                    await protocol.SendAsync(response, stream, ct);
                    await stream.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* peer closed or framing error — drop the connection */ }
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
