using System.Net;
using System.Net.Sockets;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TechTeaStudio.Protocols.Hyperion.Benchmarks;

/// <summary>
/// Aggregate throughput of N concurrent clients each performing a single 64 KiB round-trip
/// against the same echo server.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class ConcurrentClientsBenchmarks
{
    [Params(10, 50, 100)]
    public int ClientCount { get; set; }

    private byte[] _data = null!;
    private TcpListener _listener = null!;
    private CancellationTokenSource _cts = null!;
    private int _port;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[64 * 1024];
        new Random(42).NextBytes(_data);

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    [Benchmark]
    public async Task<int> AllRoundTrip()
    {
        var tasks = new Task<int>[ClientCount];
        for (int i = 0; i < ClientCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, _port);
                using var stream = tcp.GetStream();
                var protocol = new HyperionProtocol(new DefaultSerializer());

                await protocol.SendAsync(_data, stream);
                var response = await protocol.ReceiveAsync<byte[]>(stream);
                return response.Length;
            });
        }
        var results = await Task.WhenAll(tasks);
        int sum = 0;
        foreach (var r in results) sum += r;
        return sum;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = HandleAsync(tcp, ct);
        }
    }

    private static async Task HandleAsync(TcpClient tcpClient, CancellationToken ct)
    {
        try
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            {
                var protocol = new HyperionProtocol(new DefaultSerializer());
                var data = await protocol.ReceiveAsync<byte[]>(stream, ct);
                await protocol.SendAsync(data, stream, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch { /* swallow */ }
    }
}
