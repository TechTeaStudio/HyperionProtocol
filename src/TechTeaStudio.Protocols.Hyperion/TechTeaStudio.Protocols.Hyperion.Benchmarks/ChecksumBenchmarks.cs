using BenchmarkDotNet.Attributes;

namespace TechTeaStudio.Protocols.Hyperion.Benchmarks;

/// <summary>
/// Cost of the optional per-chunk CRC-32C: the hardware path (SSE4.2 / ARMv8) versus the table fallback,
/// and a chunked send with checksums on versus off.
/// </summary>
[MemoryDiagnoser]
public class ChecksumBenchmarks
{
    private byte[] _chunk = null!;
    private byte[] _message = null!;
    private HyperionProtocol _plain = null!;
    private HyperionProtocol _checked = null!;
    private MemoryStream _sink = null!;

    [Params(64 * 1024, 1024 * 1024)]
    public int ChunkBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _chunk = new byte[ChunkBytes];
        new Random(42).NextBytes(_chunk);

        _message = new byte[8 * 1024 * 1024];
        new Random(7).NextBytes(_message);

        _plain = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = ChunkBytes });
        _checked = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = ChunkBytes, ChunkChecksum = ChunkChecksumMode.Emit });
        _sink = new MemoryStream(_message.Length + 64 * 1024);
    }

    [Benchmark(Baseline = true)]
    public uint Crc32C_Hardware() => Crc32C.Compute(_chunk);

    [Benchmark]
    public uint Crc32C_Software() => Crc32C.ComputeSoftware(_chunk);

    [Benchmark]
    public async Task Send8MiB_NoChecksum()
    {
        _sink.Position = 0;
        await _plain.SendAsync(_message, _sink);
    }

    [Benchmark]
    public async Task Send8MiB_WithChecksum()
    {
        _sink.Position = 0;
        await _checked.SendAsync(_message, _sink);
    }
}
