using System.IO.Pipelines;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

/// <summary>
/// Deterministic, seed-driven fuzzing of the receive paths. The oracle is simple: whatever bytes a peer sends,
/// a receiver either returns a payload or throws <see cref="HyperionProtocolException"/>; it never throws anything
/// else, never hangs, and with checksums on it never returns a payload that differs from what was sent.
/// </summary>
[TestFixture]
[Category("Fuzz")]
public class FuzzTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Test]
    public async Task RandomGarbage_NeverEscapesAsForeignException([Range(0, 149)] int seed)
    {
        var random = new Random(seed);
        var garbage = new byte[random.Next(1, 400)];
        random.NextBytes(garbage);

        await ExpectPayloadOrProtocolException(garbage);
    }

    [Test]
    public async Task RandomGarbage_WithPlausiblePrefix_NeverEscapesAsForeignException([Range(0, 99)] int seed)
    {
        // Steer the garbage into the interesting branches: a small header length prefix or a smart-mode magic byte.
        var random = new Random(1000 + seed);
        var garbage = new byte[random.Next(8, 600)];
        random.NextBytes(garbage);

        switch (seed % 3)
        {
            case 0:
                garbage[0] = 0; garbage[1] = 0; garbage[2] = 0; garbage[3] = (byte)random.Next(1, 255);
                break;
            case 1:
                garbage[0] = SmartHyperionProtocol.LightweightMagic;
                break;
            default:
                garbage[0] = SmartHyperionProtocol.DirectMagic;
                break;
        }

        await ExpectPayloadOrProtocolException(garbage);
    }

    [Test]
    public async Task MutatedValidFrames_NeverEscapeAsForeignException([Range(0, 199)] int seed)
    {
        var random = new Random(5000 + seed);
        var payload = TestTransport.RandomBytes(random.Next(0, 60), seed);
        var bytes = Frames.Concat(Frames.Chunks(payload, chunkSize: 16, withChecksum: seed % 2 == 0));

        int flips = random.Next(1, 4);
        for (int i = 0; i < flips; i++)
            bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);

        await ExpectPayloadOrProtocolException(bytes);
    }

    [Test]
    public async Task TruncatedValidFrames_AlwaysReportEndOfStream([Range(0, 99)] int seed)
    {
        var random = new Random(9000 + seed);
        var payload = TestTransport.RandomBytes(random.Next(1, 60), seed);
        var bytes = Frames.Concat(Frames.Chunks(payload, chunkSize: 16));
        var truncated = bytes.AsSpan(0, random.Next(0, bytes.Length)).ToArray();

        foreach (var receive in Receivers())
        {
            var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receive(truncated).WaitAsync(Deadline));
            Assert.That(ex!.ErrorCode, Is.EqualTo(truncated.Length == 0 ? HyperionErrorCode.PeerClosed : HyperionErrorCode.UnexpectedEndOfStream), ex.ToString());
        }
        await Task.CompletedTask;
    }

    [Test]
    public async Task WithChecksums_PayloadCorruptionIsAlwaysDetected([Range(0, 199)] int seed)
    {
        // Flip one byte anywhere in a checksummed message: the receiver must either reject it or
        // (if the flip landed somewhere harmless, e.g. a reserved flag bit) return the original payload.
        var random = new Random(7000 + seed);
        var payload = TestTransport.RandomBytes(random.Next(1, 80), seed);
        var bytes = Frames.Concat(Frames.Chunks(payload, chunkSize: 16, withChecksum: true));
        bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);

        foreach (var receive in Receivers())
        {
            try
            {
                var result = await receive(bytes).WaitAsync(Deadline);
                Assert.That(result, Is.EqualTo(payload), "a corrupted payload must never be returned when checksums are present");
            }
            catch (HyperionProtocolException)
            {
                // Rejected: the expected outcome for almost every mutation.
            }
        }
    }

    [Test]
    public async Task CompressedFrames_Unmutated_RoundTripOrAreRefused([Range(0, 19)] int seed)
    {
        var random = new Random(11000 + seed);
        var payload = TestTransport.RandomBytes(random.Next(1, 120), seed);
        var bytes = Frames.Concat(Frames.CompressedChunks(payload, chunkSize: 16, withChecksum: seed % 2 == 0));

        foreach (var receive in Receivers(CompressedOptions()))
        {
            if (HyperionProtocol.IsCompressionSupported)
            {
                Assert.That(await receive(bytes).WaitAsync(Deadline), Is.EqualTo(payload));
            }
            else
            {
                // No codec on this target: the receiver is Off and must refuse from the first header.
                var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receive(bytes).WaitAsync(Deadline));
                Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.CompressionNotSupported), ex.ToString());
            }
        }
    }

    [Test]
    public async Task MutatedCompressedFrames_NeverEscapeAsForeignException([Range(0, 299)] int seed)
    {
        var random = new Random(13000 + seed);
        var payload = TestTransport.RandomBytes(random.Next(1, 120), seed);
        var bytes = Frames.Concat(Frames.CompressedChunks(payload, chunkSize: 16, withChecksum: seed % 2 == 0));

        int flips = random.Next(1, 4);
        for (int i = 0; i < flips; i++)
            bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);

        await ExpectPayloadOrProtocolException(bytes, Receivers(CompressedOptions()));
        await ExpectPayloadOrProtocolException(bytes, Receivers());
    }

    [Test]
    public async Task MutatedCompressedPayloadBytes_NeverEscapeAsForeignException([Range(0, 199)] int seed)
    {
        // Mutate only the Zstandard bytes (headers stay valid, no checksum), so every mutation reaches the decoder.
        var random = new Random(15000 + seed);
        var content = TestTransport.RandomBytes(random.Next(1, 200), seed);
        var wire = Frames.ZstdRawFrame(content);

        int flips = random.Next(1, 6);
        for (int i = 0; i < flips; i++)
            wire[random.Next(wire.Length)] ^= (byte)random.Next(1, 256);
        if (seed % 4 == 0)
            wire = wire.AsSpan(0, random.Next(1, wire.Length)).ToArray();

        var bytes = Frames.CompressedChunk(Guid.NewGuid(), 0, 1, wire, rawLength: content.Length);
        var options = new HyperionProtocolOptions
        {
            ChunkSize = 256,
            ChunkCompression = HyperionProtocol.IsCompressionSupported ? ChunkCompressionMode.Accept : ChunkCompressionMode.Off,
        };

        foreach (var receive in Receivers(options))
        {
            try
            {
                var result = await receive(bytes).WaitAsync(Deadline);
                Assert.That(result, Has.Length.EqualTo(content.Length), "a decoded chunk always has exactly RawLength bytes");
            }
            catch (HyperionProtocolException)
            {
            }
        }
    }

    [Test]
    public async Task WithChecksums_CompressedPayloadCorruptionIsAlwaysDetected([Range(0, 199)] int seed)
    {
        var random = new Random(17000 + seed);
        var payload = TestTransport.RandomBytes(random.Next(1, 120), seed);
        var chunks = Frames.CompressedChunks(payload, chunkSize: 16, withChecksum: true);
        var bytes = Frames.Concat(chunks);

        // Flip one byte of one chunk's on-wire payload. The checksum covers exactly those bytes (the Zstandard frame
        // for a compressed chunk), not the header: a flip inside the header is outside what CRC-32C promises.
        int target = random.Next(chunks.Length);
        int payloadLength = Frames.Parse(chunks[target]).Single().Payload.Length;
        int chunkEnd = chunks.Take(target + 1).Sum(c => c.Length);
        bytes[chunkEnd - 1 - random.Next(payloadLength)] ^= (byte)random.Next(1, 256);

        foreach (var receive in Receivers(CompressedOptions()))
        {
            try
            {
                var result = await receive(bytes).WaitAsync(Deadline);
                Assert.Fail($"a corrupted on-wire payload was accepted ({result.Length} bytes returned)");
            }
            catch (HyperionProtocolException ex)
            {
                // Checksum first: the decoder is never reached. Without a codec the header is refused before that.
                Assert.That(ex.ErrorCode, Is.EqualTo(HyperionProtocol.IsCompressionSupported
                    ? HyperionErrorCode.ChecksumMismatch
                    : HyperionErrorCode.CompressionNotSupported), ex.ToString());
            }
        }
    }

    /// <summary>Chunk size 64 leaves room for a 16-byte chunk wrapped in a raw-block Zstandard frame (25 bytes).</summary>
    private static HyperionProtocolOptions CompressedOptions() => new()
    {
        ChunkSize = 64,
        ChunkCompression = HyperionProtocol.IsCompressionSupported ? ChunkCompressionMode.Accept : ChunkCompressionMode.Off,
    };

    private static Task ExpectPayloadOrProtocolException(byte[] bytes) => ExpectPayloadOrProtocolException(bytes, Receivers());

    private static async Task ExpectPayloadOrProtocolException(byte[] bytes, IEnumerable<Func<byte[], Task<byte[]>>> receivers)
    {
        foreach (var receive in receivers)
        {
            try
            {
                _ = await receive(bytes).WaitAsync(Deadline);
            }
            catch (HyperionProtocolException)
            {
                // The only exception type a receiver may surface for bad input.
            }
        }
    }

    private static IEnumerable<Func<byte[], Task<byte[]>>> Receivers()
        => Receivers(new HyperionProtocolOptions { ChunkSize = 16, ChunkChecksum = ChunkChecksumMode.Off });

    private static IEnumerable<Func<byte[], Task<byte[]>>> Receivers(HyperionProtocolOptions options)
    {
        var baseProtocol = new HyperionProtocol(new DefaultSerializer(), options);
        var smart = new SmartHyperionProtocol(new DefaultSerializer(), options);

        yield return bytes => baseProtocol.ReceiveAsync<byte[]>(new MemoryStream(bytes));
        yield return bytes => smart.ReceiveAsync<byte[]>(new MemoryStream(bytes));
        yield return bytes => baseProtocol.ReceiveAsync<byte[]>(PipeReader.Create(new MemoryStream(bytes)));
        yield return async bytes =>
        {
            var collected = new List<byte>();
            await foreach (var chunk in baseProtocol.ReceiveStreamingAsync(new MemoryStream(bytes)))
                collected.AddRange(chunk.ToArray());
            return collected.ToArray();
        };
        yield return async bytes =>
        {
            var collected = new List<byte>();
            await foreach (var chunk in smart.ReceiveStreamingAsync(new MemoryStream(bytes)))
                collected.AddRange(chunk.ToArray());
            return collected.ToArray();
        };
    }
}
