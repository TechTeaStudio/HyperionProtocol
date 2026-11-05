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

    private static async Task ExpectPayloadOrProtocolException(byte[] bytes)
    {
        foreach (var receive in Receivers())
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
    {
        var options = new HyperionProtocolOptions { ChunkSize = 16, ChunkChecksum = ChunkChecksumMode.Off };
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
