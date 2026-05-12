using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TechTeaStudio.Protocols.Hyperion.Benchmarks;

/// <summary>
/// One-way throughput over a loopback TCP pair: send a payload of <see cref="PayloadBytes"/> bytes
/// and receive it on the other end. Measures total send+receive time.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class OneWayThroughputBenchmarks
{
    [Params(64 * 1024, 1 * 1024 * 1024, 16 * 1024 * 1024, 64 * 1024 * 1024)]
    public int PayloadBytes { get; set; }

    private byte[] _data = null!;
    private HyperionProtocol _protocol = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[PayloadBytes];
        new Random(42).NextBytes(_data);
        _protocol = new HyperionProtocol(new DefaultSerializer());
    }

    [Benchmark(Baseline = true)]
    public async Task<int> NetworkStream()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await accept;
            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            var sendTask = _protocol.SendAsync(_data, clientStream);
            var received = await _protocol.ReceiveAsync<byte[]>(serverStream);
            await sendTask;
            return received.Length;
        }
        finally { listener.Stop(); }
    }

    [Benchmark]
    public async Task<int> Pipelines()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await accept;
            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            var writer = PipeWriter.Create(clientStream);
            var reader = PipeReader.Create(serverStream);

            var sendTask = _protocol.SendAsync(_data, writer);
            var received = await _protocol.ReceiveAsync<byte[]>(reader);
            await sendTask;
            return received.Length;
        }
        finally { listener.Stop(); }
    }

    [Benchmark]
    public async Task<long> Streaming()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port);
            using var serverTcp = await accept;
            using var clientStream = clientTcp.GetStream();
            using var serverStream = serverTcp.GetStream();

            var sendTask = _protocol.SendAsync(_data, clientStream);
            long total = 0;
            await foreach (var chunk in _protocol.ReceiveStreamingAsync(serverStream))
                total += chunk.Length;
            await sendTask;
            return total;
        }
        finally { listener.Stop(); }
    }
}
