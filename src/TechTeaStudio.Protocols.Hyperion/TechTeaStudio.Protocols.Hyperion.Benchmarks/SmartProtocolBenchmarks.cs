using System.Net;
using System.Net.Sockets;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Benchmarks;

/// <summary>
/// SmartHyperionProtocol round-trip latency across the three framing modes.
/// Each iteration: client sends a payload, server echoes back, client reads the echo.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SmartProtocolBenchmarks
{
    [Params(64, 8 * 1024, 256 * 1024)]
    public int PayloadBytes { get; set; }

    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[PayloadBytes];
        new Random(42).NextBytes(_data);
    }

    [Benchmark]
    public async Task<int> RoundTrip()
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

            var clientProto = new SmartHyperionProtocol(new DefaultSerializer());
            var serverProto = new SmartHyperionProtocol(new DefaultSerializer());

            var sendTask = clientProto.SendAsync(_data, clientStream);
            var received = await serverProto.ReceiveAsync<byte[]>(serverStream);
            await sendTask;

            var echoTask = serverProto.SendAsync(received, serverStream);
            var roundTripped = await clientProto.ReceiveAsync<byte[]>(clientStream);
            await echoTask;

            return roundTripped.Length;
        }
        finally { listener.Stop(); }
    }
}
