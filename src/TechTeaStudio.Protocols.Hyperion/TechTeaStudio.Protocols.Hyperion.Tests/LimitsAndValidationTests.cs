using System.Text;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class LimitsAndValidationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    // --- MaxMessageSize ---------------------------------------------------------------------

    [Test]
    public async Task Receive_OversizedMessage_RejectedFromFirstHeader_BeforeAnyPayload()
    {
        // Receiver allows 2 MiB; the first header announces 3 chunks of 1 MiB. Only the header is written,
        // never the payload: the receiver must reject it without waiting for payload bytes.
        var receiver = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = 2L * 1024 * 1024 });
        using var link = new TestTransport.Link();

        var header = Frames.HeaderJson(Guid.NewGuid(), 0, 3, 1024 * 1024, last: false);
        await link.WriteRawAsync(Frames.Chunk(header, Array.Empty<byte>()));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<byte[]>(link.Reader).WaitAsync(Deadline));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));
    }

    [Test]
    public async Task Receive_OversizedMessage_RejectedWhileAccumulating()
    {
        // 2 chunks of 1 MiB: the first header alone (minimum 1 MiB + 1) passes a 1.5 MiB limit;
        // the final chunk pushes the total over it.
        var sender = new HyperionProtocol(new DefaultSerializer());
        var receiver = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = 1536L * 1024 });

        var bytes = await Frames.CaptureAsync(sender, TestTransport.RandomBytes(2 * 1024 * 1024));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));
    }

    [Test]
    public async Task Receive_MessageExactlyAtLimit_Succeeds()
    {
        var payload = TestTransport.RandomBytes(2 * 1024 * 1024);
        var sender = new HyperionProtocol(new DefaultSerializer());
        var receiver = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = payload.Length });

        var bytes = await Frames.CaptureAsync(sender, payload);
        var received = await receiver.ReceiveAsync<byte[]>(new MemoryStream(bytes));

        Assert.That(received, Is.EqualTo(payload));
    }

    [Test]
    public async Task ReceiveStreaming_OversizedMessage_RejectedBeforeFirstYield()
    {
        var receiver = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = 100 });
        using var link = new TestTransport.Link();
        await link.WriteRawAsync(Frames.Chunk(Frames.HeaderJson(Guid.NewGuid(), 0, 2, 100, last: false), Array.Empty<byte>()));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in receiver.ReceiveStreamingAsync(link.Reader).WithCancellation(new CancellationTokenSource(Deadline).Token)) { }
        });
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));
    }

    [Test]
    public void Send_OversizedMessage_ThrowsBeforeWritingAnything()
    {
        var options = new HyperionProtocolOptions { MaxMessageSize = 100 };
        var payload = new byte[101];

        foreach (HyperionProtocol protocol in new HyperionProtocol[]
                 {
                     new HyperionProtocol(new DefaultSerializer(), options),
                     new SmartHyperionProtocol(new DefaultSerializer(), options),
                 })
        {
            using var ms = new MemoryStream();
            var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.SendAsync(payload, ms));
            Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));
            Assert.That(ms.Length, Is.EqualTo(0), "nothing may be written when the message is rejected");

            using var link = new TestTransport.Link();
            ex = Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.SendAsync(payload, link.PipeWriter));
            Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));

            using var destination = new MemoryStream();
            ex = Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.SendStreamingAsync(new MemoryStream(payload), payload.Length, destination));
            Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));
            Assert.That(destination.Length, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Smart_SmallFrame_AboveMaxMessageSize_Rejected()
    {
        var receiver = new SmartHyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = 10 });
        var frame = Frames.Lightweight(new byte[20]);

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<byte[]>(new MemoryStream(frame)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MessageTooLarge));
        await Task.CompletedTask;
    }

    // --- Uniform-chunk rule -----------------------------------------------------------------

    [Test]
    public void Receive_NonFinalChunkShorterThanFirst_Rejected()
    {
        var id = Guid.NewGuid();
        var bytes = Frames.Concat(
            Frames.Chunk(id, 0, 3, new byte[10]),
            Frames.Chunk(id, 1, 3, new byte[5]),
            Frames.Chunk(id, 2, 3, new byte[5]));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidDataLength));
    }

    [Test]
    public void Receive_FinalChunkLargerThanFirst_Rejected()
    {
        var id = Guid.NewGuid();
        var bytes = Frames.Concat(
            Frames.Chunk(id, 0, 2, new byte[10]),
            Frames.Chunk(id, 1, 2, new byte[11]));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidDataLength));
    }

    [Test]
    public void Receive_EmptyFirstChunkOfMultiChunkMessage_Rejected()
    {
        var id = Guid.NewGuid();
        var bytes = Frames.Chunk(id, 0, 2, Array.Empty<byte>());

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidDataLength));
    }

    [Test]
    public async Task Receive_SenderWithSmallerChunkSize_IsAccepted()
    {
        // A sender chunking at 64 KiB talking to a receiver that allows up to 1 MiB per chunk is fine.
        var payload = TestTransport.RandomBytes(300_000);
        var sender = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = 64 * 1024 });
        var receiver = new HyperionProtocol(new DefaultSerializer());

        var bytes = await Frames.CaptureAsync(sender, payload);
        Assert.That(await receiver.ReceiveAsync<byte[]>(new MemoryStream(bytes)), Is.EqualTo(payload));
    }

    // --- Error codes on the static validators ------------------------------------------------

    private static PacketHeader GoodHeader(Guid? id = null) => new()
    {
        Version = HyperionProtocol.ProtocolVersion,
        Magic = HyperionProtocol.ProtocolMagic,
        PacketId = id ?? Guid.NewGuid(),
        ChunkNumber = 0,
        TotalChunks = 1,
        DataLength = 0,
        Flags = PacketHeader.EndFlag,
    };

    [Test]
    public void ValidateHeader_ReportsSpecificErrorCodes()
    {
        const int chunkSize = 1024;
        var id = Guid.NewGuid();

        void Expect(PacketHeader header, HyperionErrorCode code, Guid? expectedId = null, int expectedTotal = int.MaxValue, int received = 0)
        {
            var ex = Assert.Throws<HyperionProtocolException>(() => HyperionProtocol.ValidateHeader(header, expectedId, expectedTotal, received, chunkSize));
            Assert.That(ex!.ErrorCode, Is.EqualTo(code), ex.Message);
        }

        Assert.DoesNotThrow(() => HyperionProtocol.ValidateHeader(GoodHeader(), null, int.MaxValue, 0, chunkSize));

        var h = GoodHeader(); h.Magic = "XXX";
        Expect(h, HyperionErrorCode.InvalidMagic);

        h = GoodHeader(); h.Version = HyperionProtocol.ProtocolVersion + 1;
        Expect(h, HyperionErrorCode.UnsupportedVersion);

        h = GoodHeader(); h.TotalChunks = 0;
        Expect(h, HyperionErrorCode.InvalidChunkCount);

        h = GoodHeader(); h.TotalChunks = 2; h.ChunkNumber = 2; h.Flags = 0;
        Expect(h, HyperionErrorCode.InvalidChunkNumber);

        h = GoodHeader(); h.DataLength = chunkSize + 1;
        Expect(h, HyperionErrorCode.InvalidDataLength);

        h = GoodHeader(); h.Flags = 0;
        Expect(h, HyperionErrorCode.EndFlagMismatch);

        h = GoodHeader(id);
        Expect(h, HyperionErrorCode.PacketIdMismatch, expectedId: Guid.NewGuid(), expectedTotal: 1);

        h = GoodHeader(id); h.TotalChunks = 2; h.ChunkNumber = 1;
        Expect(h, HyperionErrorCode.InvalidChunkCount, expectedId: id, expectedTotal: 3, received: 1);

        h = GoodHeader(id); h.TotalChunks = 3; h.ChunkNumber = 2;
        Expect(h, HyperionErrorCode.ChunkOutOfOrder, expectedId: id, expectedTotal: 3, received: 1);
    }

    [Test]
    public void ValidateHeaderLength_ReportsInvalidHeaderLength()
    {
        foreach (int bad in new[] { 0, -1, int.MinValue, 64 * 1024 + 1 })
        {
            var ex = Assert.Throws<HyperionProtocolException>(() => HyperionProtocol.ValidateHeaderLength(bad, 64 * 1024));
            Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidHeaderLength));
        }
        Assert.DoesNotThrow(() => HyperionProtocol.ValidateHeaderLength(64 * 1024, 64 * 1024));
    }

    [Test]
    public void DeserializeHeader_Malformed_ReportsMalformedHeader()
    {
        var ex = Assert.Throws<HyperionProtocolException>(() => HyperionProtocol.DeserializeHeader(Encoding.UTF8.GetBytes("{\"Magic\":")));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MalformedHeader));

        ex = Assert.Throws<HyperionProtocolException>(() => HyperionProtocol.DeserializeHeader(Encoding.UTF8.GetBytes("null")));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MalformedHeader));
    }

    // --- Wire compatibility ----------------------------------------------------------------

    [Test]
    public async Task LegacyHeader_WithoutVersionOrChecksum_IsAccepted()
    {
        // Exactly what a 0.2.x sender emitted: no Version, no Crc32C.
        var payload = Encoding.UTF8.GetBytes("hello");
        var frame = Frames.Chunk(Frames.HeaderJson(Guid.NewGuid(), 0, 1, payload.Length, last: true, version: null), payload);

        var result = await new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<string>(new MemoryStream(frame));
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void Header_WithoutMagic_IsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("x");
        var json = Frames.HeaderJson(Guid.NewGuid(), 0, 1, payload.Length, last: true).Replace("\"Magic\":\"TTS\",", "");
        Assert.That(json, Does.Not.Contain("Magic"));

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<string>(new MemoryStream(Frames.Chunk(json, payload))));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidMagic));
    }

    [Test]
    public void Header_WithTrailingGarbage_IsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("x");
        var json = Frames.HeaderJson(Guid.NewGuid(), 0, 1, payload.Length, last: true) + "garbage";

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<string>(new MemoryStream(Frames.Chunk(json, payload))));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MalformedHeader));
    }

    [Test]
    public void Header_WithOutOfRangeFlags_IsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("x");
        var json = Frames.HeaderJson(Guid.NewGuid(), 0, 1, payload.Length, last: true).Replace("\"Flags\":1", "\"Flags\":300");

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<string>(new MemoryStream(Frames.Chunk(json, payload))));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.MalformedHeader));
    }

    [Test]
    public async Task Header_WithUnknownMembers_IsAccepted()
    {
        // Forward compatibility: a newer sender may add members this receiver has never heard of.
        var payload = Encoding.UTF8.GetBytes("future");
        var frame = Frames.Chunk(
            Frames.HeaderJson(Guid.NewGuid(), 0, 1, payload.Length, last: true, extraMembers: "\"Compression\":\"zstd\",\"Extensions\":{\"trace\":\"abc\"}"),
            payload);

        var result = await new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<string>(new MemoryStream(frame));
        Assert.That(result, Is.EqualTo("future"));
    }

    [Test]
    public async Task Header_WithReservedFlagBits_IsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes("flags");
        var json = Frames.HeaderJson(Guid.NewGuid(), 0, 1, payload.Length, last: true).Replace("\"Flags\":1", "\"Flags\":129");

        var result = await new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<string>(new MemoryStream(Frames.Chunk(json, payload)));
        Assert.That(result, Is.EqualTo("flags"));
    }

    [Test]
    public async Task Sender_WithChecksumOff_EmitsByteIdentical030Header()
    {
        // With checksums off the header must not grow: the member order and content of 0.3.0 is preserved.
        var bytes = await Frames.CaptureAsync(new HyperionProtocol(new DefaultSerializer()), Encoding.UTF8.GetBytes("abc"));
        var headerLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes);
        var json = Encoding.UTF8.GetString(bytes, 4, headerLength);

        Assert.That(json, Does.StartWith("{\"Version\":1,\"Magic\":\"TTS\",\"PacketId\":\""));
        Assert.That(json, Does.EndWith("\",\"ChunkNumber\":0,\"TotalChunks\":1,\"DataLength\":3,\"Flags\":1}"));
        Assert.That(json, Does.Not.Contain("Crc32C"));
    }

    [Test]
    public async Task Smart_And_Base_ChunkedFrames_AreInterchangeable()
    {
        var payload = TestTransport.RandomBytes(200_000);
        var fromSmart = await Frames.CaptureAsync(new SmartHyperionProtocol(new DefaultSerializer()), payload);
        var fromBase = await Frames.CaptureAsync(new HyperionProtocol(new DefaultSerializer()), payload);

        Assert.That(await new HyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(fromSmart)), Is.EqualTo(payload));
        Assert.That(await new SmartHyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(fromBase)), Is.EqualTo(payload));
    }

    // --- Smart small-frame validation ---------------------------------------------------------

    [Test]
    public void Smart_LightweightFrame_DeclaringTooMuch_Rejected()
    {
        var frame = Frames.Lightweight(new byte[10], declaredLength: SmartHyperionProtocol.LightweightThreshold);
        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new SmartHyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(frame)));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidDataLength));
    }

    [Test]
    public void Smart_DirectFrame_DeclaringTooMuch_Rejected()
    {
        foreach (int declared in new[] { SmartHyperionProtocol.DirectThreshold, -1 })
        {
            var frame = Frames.Direct(new byte[10], declaredLength: declared);
            var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new SmartHyperionProtocol(new DefaultSerializer()).ReceiveAsync<byte[]>(new MemoryStream(frame)));
            Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.InvalidDataLength));
        }
    }

    // --- Options and exception surface -------------------------------------------------------

    [Test]
    public void Options_RejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ReceiveTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { SendTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkChecksum = (ChunkChecksumMode)42 }));
        Assert.DoesNotThrow(() => new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions
        {
            MaxMessageSize = long.MaxValue,
            ReceiveTimeout = TimeSpan.FromSeconds(30),
            SendTimeout = TimeSpan.FromSeconds(30),
            ChunkChecksum = ChunkChecksumMode.Required,
        }));
    }

    [Test]
    public void Exception_LegacyConstructors_ReportUnspecified()
    {
        Assert.That(new HyperionProtocolException("x").ErrorCode, Is.EqualTo(HyperionErrorCode.Unspecified));
        Assert.That(new HyperionProtocolException("x", new Exception()).ErrorCode, Is.EqualTo(HyperionErrorCode.Unspecified));
        Assert.That(new HyperionProtocolException(HyperionErrorCode.Timeout, "x").ToString(), Does.StartWith("[Timeout]"));
    }
}
