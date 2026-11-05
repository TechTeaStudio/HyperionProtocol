using System.IO.Pipelines;
using System.Text;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class ChecksumTests
{
    private static HyperionProtocolOptions With(ChunkChecksumMode mode) => new() { ChunkChecksum = mode };

    [Test]
    public async Task Emit_RoundTrips_OnEveryReceivePath()
    {
        var payload = TestTransport.RandomBytes(2 * 1024 * 1024 + 123);
        var sender = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Emit));
        var receiver = new HyperionProtocol(new DefaultSerializer());
        var bytes = await Frames.CaptureAsync(sender, payload);

        Assert.That(await receiver.ReceiveAsync<byte[]>(new MemoryStream(bytes)), Is.EqualTo(payload));
        Assert.That(await new SmartHyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(bytes)), Is.EqualTo(payload));
        Assert.That(await receiver.ReceiveAsync<byte[]>(PipeReader.Create(new MemoryStream(bytes))), Is.EqualTo(payload));

        var streamed = new List<byte>();
        await foreach (var chunk in receiver.ReceiveStreamingAsync(new MemoryStream(bytes)))
            streamed.AddRange(chunk.ToArray());
        Assert.That(streamed.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public async Task Emit_PutsCrc32CIntoEveryHeader()
    {
        var sender = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Emit));
        var bytes = await Frames.CaptureAsync(sender, TestTransport.RandomBytes(3 * 1024 * 1024));
        var text = Encoding.Latin1.GetString(bytes);

        Assert.That(CountOccurrences(text, "\"Crc32C\":"), Is.EqualTo(3));
    }

    [Test]
    public async Task CorruptedPayload_IsDetected_EvenWhenReceiverIsOff()
    {
        var sender = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Emit));
        var bytes = await Frames.CaptureAsync(sender, TestTransport.RandomBytes(100_000));
        bytes[^1] ^= 0x40; // last byte of the (single) chunk payload

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.ChecksumMismatch));
    }

    [Test]
    public async Task CorruptedPayload_IsDetected_OnStreamingPath_BeforeYield()
    {
        var sender = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Emit));
        var bytes = await Frames.CaptureAsync(sender, TestTransport.RandomBytes(1024 * 1024 + 10));
        bytes[^1] ^= 0x01; // inside chunk 1's payload

        int yielded = 0;
        var ex = Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in new HyperionProtocol(new DefaultSerializer()).ReceiveStreamingAsync(new MemoryStream(bytes)))
                yielded++;
        });
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.ChecksumMismatch));
        Assert.That(yielded, Is.EqualTo(1), "chunk 0 was intact and must have been yielded; chunk 1 must not");
    }

    [Test]
    public async Task Required_RejectsFramesWithoutChecksum()
    {
        var legacySender = new HyperionProtocol(new DefaultSerializer()); // Off: emits no Crc32C, like 0.3.x
        var receiver = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Required));
        var bytes = await Frames.CaptureAsync(legacySender, Encoding.UTF8.GetBytes("legacy"));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.ChecksumMissing));
    }

    [Test]
    public async Task Required_AcceptsFramesWithChecksum()
    {
        var sender = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Required));
        var receiver = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Required));
        var payload = TestTransport.RandomBytes(5000);

        var bytes = await Frames.CaptureAsync(sender, payload);
        Assert.That(await receiver.ReceiveAsync<byte[]>(new MemoryStream(bytes)), Is.EqualTo(payload));
    }

    [Test]
    public async Task SendStreaming_WithChecksum_RoundTrips()
    {
        var payload = TestTransport.RandomBytes(2 * 1024 * 1024 + 7);
        var sender = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Emit));
        var receiver = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Required));

        using var wire = new MemoryStream();
        await sender.SendStreamingAsync(new MemoryStream(payload), payload.Length, wire);
        wire.Position = 0;

        Assert.That(await receiver.ReceiveAsync<byte[]>(wire), Is.EqualTo(payload));
    }

    [Test]
    public async Task HandBuiltFrame_WithCorrectCrc_IsAccepted_AndWrongCrcRejected()
    {
        var payload = Encoding.UTF8.GetBytes("checksum me");
        var id = Guid.NewGuid();
        var receiver = new HyperionProtocol(new DefaultSerializer(), With(ChunkChecksumMode.Required));

        var good = Frames.Chunk(id, 0, 1, payload, crc: Crc32C.Compute(payload));
        Assert.That(await receiver.ReceiveAsync<string>(new MemoryStream(good)), Is.EqualTo("checksum me"));

        var bad = Frames.Chunk(id, 0, 1, payload, crc: Crc32C.Compute(payload) ^ 1);
        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<string>(new MemoryStream(bad)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.ChecksumMismatch));
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
