using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

/// <summary>
/// Opt-in per-chunk Zstandard compression (protocol version 2, library 0.5.0). The first half runs on every
/// target framework and covers what a peer without compression does; the second half needs the .NET 11 codec.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CompressionTests
{
    private const int Chunk = 4096;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A hand-written Zstandard frame (RFC 8878) holding "abc" in one raw block: magic, frame header descriptor
    /// 0x20 (single segment, 1-byte content size, no checksum), content size 3, block header 0x000019
    /// (last block, raw, 3 bytes), then the bytes.
    /// </summary>
    private static readonly byte[] GoldenAbcFrame =
        { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x03, 0x19, 0x00, 0x00, 0x61, 0x62, 0x63 };

    private static HyperionProtocolOptions Off(int chunkSize = Chunk) => new() { ChunkSize = chunkSize };

    private static byte[] Compressible(int size, int salt = 0)
    {
        var sb = new StringBuilder(size + 64);
        for (int i = 0; sb.Length < size; i++)
            sb.Append("line ").Append((i + salt) % 97).Append(": the quick brown fox jumps over the lazy dog\n");
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    /// <summary>Every receive path, each returning the reassembled payload.</summary>
    private static IEnumerable<(string Name, Func<byte[], Task<byte[]>> Receive)> Receivers(HyperionProtocolOptions options)
    {
        var baseProtocol = new HyperionProtocol(new DefaultSerializer(), options);
        var smart = new SmartHyperionProtocol(new DefaultSerializer(), options);

        yield return ("base stream", bytes => baseProtocol.ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        yield return ("base pipe", bytes => baseProtocol.ReceiveAsync<byte[]>(PipeReader.Create(new MemoryStream(bytes))));
        yield return ("base streaming", bytes => Collect(baseProtocol.ReceiveStreamingAsync(new MemoryStream(bytes))));
        yield return ("base pipe streaming", bytes => Collect(baseProtocol.ReceiveStreamingAsync(PipeReader.Create(new MemoryStream(bytes)))));
        yield return ("smart stream", bytes => smart.ReceiveAsync<byte[]>(new MemoryStream(bytes)));
        yield return ("smart streaming", bytes => Collect(smart.ReceiveStreamingAsync(new MemoryStream(bytes))));
    }

    private static async Task<byte[]> Collect(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        var collected = new MemoryStream();
        await foreach (var chunk in chunks)
            collected.Write(chunk.Span);
        return collected.ToArray();
    }

    private static void ExpectOnEveryPath(HyperionProtocolOptions options, byte[] wire, HyperionErrorCode code)
    {
        foreach (var (name, receive) in Receivers(options))
        {
            var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receive(wire).WaitAsync(Deadline), name);
            Assert.That(ex!.ErrorCode, Is.EqualTo(code), $"{name}: {ex}");
        }
    }

    private static async Task ExpectPayloadOnEveryPath(HyperionProtocolOptions options, byte[] wire, byte[] payload)
    {
        foreach (var (name, receive) in Receivers(options))
            Assert.That(await receive(wire).WaitAsync(Deadline), Is.EqualTo(payload), name);
    }

    /// <summary>Rebuilds the wire image the way 0.4.1 would have written it, from the captured packet id and payloads.</summary>
    private static byte[] RebuildAs041(byte[] wire, bool withChecksum = false)
    {
        var frames = Frames.Parse(wire);
        return Frames.Concat(frames
            .Select((f, i) => Frames.Chunk(f.Header.PacketId, i, frames.Count, f.Payload, withChecksum ? Crc32C.Compute(f.Payload) : null))
            .ToArray());
    }

    // --- Every target framework ---------------------------------------------------------------

    [Test]
    public void Constants_AreStable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HyperionProtocol.ProtocolVersion, Is.EqualTo(1));
            Assert.That(HyperionProtocol.MaxSupportedProtocolVersion, Is.EqualTo(2));
            Assert.That(PacketHeader.ZstandardCompression, Is.EqualTo("zstd"));
            Assert.That((int)HyperionErrorCode.CompressionNotSupported, Is.EqualTo(14));
            Assert.That((int)HyperionErrorCode.DecompressionFailed, Is.EqualTo(15));
            Assert.That((int)ChunkCompressionMode.Off, Is.EqualTo(0));
            Assert.That((int)ChunkCompressionMode.Accept, Is.EqualTo(1));
            Assert.That((int)ChunkCompressionMode.Compress, Is.EqualTo(2));
        });
    }

    [Test]
    public void Options_DefaultToOff_WithThreshold1024()
    {
        var options = new HyperionProtocolOptions();
        Assert.That(options.ChunkCompression, Is.EqualTo(ChunkCompressionMode.Off));
        Assert.That(options.CompressionThreshold, Is.EqualTo(1024));
    }

    [Test]
    public void Options_InvalidValues_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkCompression = (ChunkCompressionMode)42 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { CompressionThreshold = -1 }));
        Assert.DoesNotThrow(() =>
            new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { CompressionThreshold = 0 }));
    }

    [Test]
    public async Task DefaultOptions_EmitFramesByteIdenticalTo041()
    {
        var payload = Compressible(3 * Chunk + 100);

        var wire = await Frames.CaptureAsync(new HyperionProtocol(new DefaultSerializer(), Off()), payload);
        var frames = Frames.Parse(wire);

        Assert.That(frames, Has.Count.EqualTo(4));
        Assert.That(wire, Is.EqualTo(RebuildAs041(wire)));
        foreach (var frame in frames)
        {
            Assert.That(frame.HeaderJson, Does.StartWith("{\"Version\":1,"));
            Assert.That(frame.HeaderJson, Does.Not.Contain("Compression").And.Not.Contain("RawLength"));
        }

        var checksummed = await Frames.CaptureAsync(
            new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { ChunkSize = Chunk, ChunkChecksum = ChunkChecksumMode.Emit }), payload);
        Assert.That(checksummed, Is.EqualTo(RebuildAs041(checksummed, withChecksum: true)));

        var smartWire = await Frames.CaptureAsync(new SmartHyperionProtocol(new DefaultSerializer(), Off()), Compressible(70_000));
        Assert.That(smartWire, Is.EqualTo(RebuildAs041(smartWire)));
    }

    [Test]
    public void HeaderJson_PutsTheNewMembersLast_AndOmitsThemWhenNull()
    {
        var header = new PacketHeader
        {
            Version = 2,
            Magic = "TTS",
            PacketId = Guid.Parse("6f1d2c3e-4b5a-4c6d-8e7f-9a0b1c2d3e4f"),
            ChunkNumber = 0,
            TotalChunks = 1,
            DataLength = 12,
            Flags = 1,
            Compression = PacketHeader.ZstandardCompression,
            RawLength = 3,
        };
        Assert.That(
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(header, HyperionJsonContext.Default.PacketHeader)),
            Is.EqualTo("{\"Version\":2,\"Magic\":\"TTS\",\"PacketId\":\"6f1d2c3e-4b5a-4c6d-8e7f-9a0b1c2d3e4f\",\"ChunkNumber\":0," +
                       "\"TotalChunks\":1,\"DataLength\":12,\"Flags\":1,\"Compression\":\"zstd\",\"RawLength\":3}"));

        header.Checksum = 7;
        header.Compression = null;
        header.RawLength = null;
        Assert.That(
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(header, HyperionJsonContext.Default.PacketHeader)),
            Does.EndWith("\"Flags\":1,\"Crc32C\":7}"));
    }

    [Test]
    public void ReceiverOff_RejectsCompressedChunk_FromTheHeaderAlone_OnEveryPath()
    {
        // The header announces 12 compressed bytes; none of them is sent and the peer closes. A receiver that tried
        // to read the payload would report UnexpectedEndOfStream.
        var headerOnly = Frames.CompressedChunk(Guid.NewGuid(), 0, 1, Array.Empty<byte>(), rawLength: 3, dataLength: GoldenAbcFrame.Length);

        ExpectOnEveryPath(Off(), headerOnly, HyperionErrorCode.CompressionNotSupported);
    }

    [Test]
    public void ReceiverOff_RejectsCompressedChunk_InsideAMessage()
    {
        var id = Guid.NewGuid();
        var wire = Frames.Concat(
            Frames.Chunk(id, 0, 2, new byte[] { 1, 2, 3 }, version: 2),
            Frames.CompressedChunk(id, 1, 2, GoldenAbcFrame, rawLength: 3));

        ExpectOnEveryPath(Off(), wire, HyperionErrorCode.CompressionNotSupported);
    }

    [Test]
    public void UnknownAlgorithm_IsCompressionNotSupported()
    {
        var wire = Frames.CompressedChunk(Guid.NewGuid(), 0, 1, new byte[] { 1, 2, 3 }, rawLength: 3, compression: "lz4");

        ExpectOnEveryPath(Off(), wire, HyperionErrorCode.CompressionNotSupported);
    }

    [Test]
    public async Task Version2Header_WithoutCompression_IsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes("raw chunk of a version 2 message");
        var wire = Frames.Chunk(Guid.NewGuid(), 0, 1, payload, version: 2);

        await ExpectPayloadOnEveryPath(Off(), wire, payload);
    }

    [Test]
    public void Version3Header_IsUnsupportedVersion()
    {
        var wire = Frames.Chunk(Guid.NewGuid(), 0, 1, new byte[] { 1 }, version: 3);

        ExpectOnEveryPath(Off(), wire, HyperionErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task RawLength_WithoutCompression_IsIgnored()
    {
        var payload = new byte[] { 9, 8, 7 };
        var id = Guid.NewGuid();
        var wire = Frames.Chunk(Frames.HeaderJson(id, 0, 1, payload.Length, last: true, rawLength: 999_999_999), payload);
        var nullMember = Frames.Chunk(Frames.HeaderJson(id, 0, 1, payload.Length, last: true, extraMembers: "\"Compression\":null,\"RawLength\":null"), payload);

        await ExpectPayloadOnEveryPath(Off(), wire, payload);
        await ExpectPayloadOnEveryPath(Off(), nullMember, payload);
    }

    [Test]
    public void NewMembers_OfTheWrongJsonType_AreMalformedHeader()
    {
        var id = Guid.NewGuid();
        var wire = Frames.Chunk(Frames.HeaderJson(id, 0, 1, 1, last: true, extraMembers: "\"Compression\":5"), new byte[] { 1 });
        var wire2 = Frames.Chunk(Frames.HeaderJson(id, 0, 1, 1, last: true, extraMembers: "\"RawLength\":\"3\""), new byte[] { 1 });

        ExpectOnEveryPath(Off(), wire, HyperionErrorCode.MalformedHeader);
        ExpectOnEveryPath(Off(), wire2, HyperionErrorCode.MalformedHeader);
    }

    [Test]
    public void ZstdRawFrameHelper_MatchesTheGoldenFrame()
    {
        Assert.That(Frames.ZstdRawFrame(Encoding.ASCII.GetBytes("abc")), Is.EqualTo(GoldenAbcFrame));
    }

#if !NET11_0_OR_GREATER

    // --- Below .NET 11: no codec ---------------------------------------------------------------

    [Test]
    public void BelowNet11_CompressionIsNotSupported()
    {
        Assert.That(HyperionProtocol.IsCompressionSupported, Is.False);
    }

    [Test]
    public void BelowNet11_NonOffModes_FailAtConstruction()
    {
        foreach (var mode in new[] { ChunkCompressionMode.Accept, ChunkCompressionMode.Compress })
        {
            var options = new HyperionProtocolOptions { ChunkCompression = mode };
            var ex = Assert.Throws<PlatformNotSupportedException>(() => new HyperionProtocol(new DefaultSerializer(), options));
            Assert.That(ex!.Message, Does.Contain(".NET 11"));
            Assert.Throws<PlatformNotSupportedException>(() => new SmartHyperionProtocol(new DefaultSerializer(), options));
        }
    }

#else

    // --- .NET 11: the codec --------------------------------------------------------------------

    private static HyperionProtocolOptions Compress(int chunkSize = Chunk, ChunkChecksumMode checksum = ChunkChecksumMode.Off, int? threshold = null)
        => threshold is int t
            ? new() { ChunkSize = chunkSize, ChunkCompression = ChunkCompressionMode.Compress, ChunkChecksum = checksum, CompressionThreshold = t }
            : new() { ChunkSize = chunkSize, ChunkCompression = ChunkCompressionMode.Compress, ChunkChecksum = checksum };

    private static HyperionProtocolOptions Accept(int chunkSize = Chunk, long maxMessageSize = HyperionProtocolOptions.DefaultMaxMessageSize)
        => new() { ChunkSize = chunkSize, ChunkCompression = ChunkCompressionMode.Accept, MaxMessageSize = maxMessageSize };

    private static byte[] Zstd(byte[] raw)
    {
        var buffer = new byte[System.IO.Compression.ZstandardEncoder.GetMaxCompressedLength(raw.Length)];
        Assert.That(System.IO.Compression.ZstandardEncoder.TryCompress(raw, buffer, out int written), Is.True);
        return buffer.AsSpan(0, written).ToArray();
    }

    private static async Task<byte[]> CapturePipeAsync<T>(HyperionProtocol protocol, T message)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await protocol.SendAsync(message, writer);
        await writer.CompleteAsync();
        return ms.ToArray();
    }

    private static async Task<byte[]> CaptureStreamingAsync(HyperionProtocol protocol, byte[] payload)
    {
        using var ms = new MemoryStream();
        await protocol.SendStreamingAsync(new MemoryStream(payload), payload.Length, ms);
        return ms.ToArray();
    }

    [Test]
    public void Net11_CompressionIsSupported_AndModesConstruct()
    {
        Assert.That(HyperionProtocol.IsCompressionSupported, Is.True);
        Assert.DoesNotThrow(() => new HyperionProtocol(new DefaultSerializer(), Compress()));
        Assert.DoesNotThrow(() => new SmartHyperionProtocol(new DefaultSerializer(), Accept()));
    }

    [Test]
    public void Codec_TrailingBytesAfterTheFrame_AreRejected()
    {
        Assert.That(ZstdCodec.Decompress(GoldenAbcFrame, 3, 0), Is.EqualTo(Encoding.ASCII.GetBytes("abc")));

        var trailing = Frames.Concat(GoldenAbcFrame, new byte[] { 0x00 });
        var ex = Assert.Throws<HyperionProtocolException>(() => ZstdCodec.Decompress(trailing, 3, 0));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.DecompressionFailed));

        // The one-shot decoder treats a payload as a sequence of frames, so two complete frames whose output adds up
        // to RawLength decode. The output is still bounded by RawLength; the sender never emits this.
        var twoFrames = Frames.Concat(GoldenAbcFrame, GoldenAbcFrame);
        Assert.That(ZstdCodec.Decompress(twoFrames, 6, 0), Is.EqualTo(Encoding.ASCII.GetBytes("abcabc")));
    }

    [Test]
    public void Codec_TryCompress_RefusesOutputThatIsNotSmaller()
    {
        var text = Compressible(Chunk);
        var scratch = new byte[Chunk];

        Assert.That(ZstdCodec.TryCompress(text, scratch.AsSpan(0, text.Length - 1), out int written), Is.True);
        Assert.That(written, Is.GreaterThan(0).And.LessThan(text.Length));
        Assert.That(ZstdCodec.Decompress(scratch.AsSpan(0, written), text.Length, 0), Is.EqualTo(text));

        var random = TestTransport.RandomBytes(Chunk);
        Assert.That(ZstdCodec.TryCompress(random, scratch.AsSpan(0, random.Length - 1), out written), Is.False);
        Assert.That(written, Is.Zero);

        Assert.That(ZstdCodec.TryCompress(text, scratch.AsSpan(0, 8), out _), Is.False, "destination too small");
        Assert.That(ZstdCodec.TryCompress(text, Span<byte>.Empty, out _), Is.False);
        Assert.That(ZstdCodec.TryCompress(ReadOnlySpan<byte>.Empty, scratch, out _), Is.False);
    }

    [Test]
    public async Task RoundTrip_BaseProtocol_EverySendPath_EveryReceivePath()
    {
        var payload = Compressible(3 * Chunk + 100);
        var sender = new HyperionProtocol(new DefaultSerializer(), Compress());

        var wires = new (string Name, byte[] Wire)[]
        {
            ("stream", await Frames.CaptureAsync(sender, payload)),
            ("pipe", await CapturePipeAsync(sender, payload)),
            ("streaming", await CaptureStreamingAsync(sender, payload)),
        };

        foreach (var (name, wire) in wires)
        {
            Assert.That(wire.Length, Is.LessThan(payload.Length), name);

            var frames = Frames.Parse(wire);
            Assert.That(frames, Has.Count.EqualTo(4), name);
            for (int i = 0; i < frames.Count; i++)
            {
                int raw = i < 3 ? Chunk : 100;
                var (header, json, wirePayload) = frames[i];
                Assert.Multiple(() =>
                {
                    Assert.That(header.Version, Is.EqualTo(2), name);
                    Assert.That(header.Compression, Is.EqualTo("zstd"), name);
                    Assert.That(header.RawLength, Is.EqualTo(raw), name);
                    Assert.That(header.DataLength, Is.LessThan(raw), name);
                    Assert.That(json, Does.EndWith($",\"Compression\":\"zstd\",\"RawLength\":{raw}}}"), name);
                    Assert.That(ZstdCodec.Decompress(wirePayload, raw, i), Is.EqualTo(payload.AsSpan(i * Chunk, raw).ToArray()), name);
                });
            }

            // Parse walked the image by DataLength and ended exactly at its end, so DataLength is the on-wire length.
            Assert.That(frames.Sum(f => 4 + Encoding.UTF8.GetByteCount(f.HeaderJson) + f.Header.DataLength), Is.EqualTo(wire.Length), name);

            await ExpectPayloadOnEveryPath(Accept(), wire, payload);
            await ExpectPayloadOnEveryPath(Compress(), wire, payload);
        }
    }

    [Test]
    public async Task RoundTrip_SmartProtocol_ChunkedPath()
    {
        const int chunkSize = 32 * 1024;
        var payload = Compressible(200_000);
        var sender = new SmartHyperionProtocol(new DefaultSerializer(), Compress(chunkSize));

        foreach (var wire in new[] { await Frames.CaptureAsync(sender, payload), await CapturePipeAsync(sender, payload) })
        {
            Assert.That(wire.Length, Is.LessThan(payload.Length / 2));
            Assert.That(Frames.Parse(wire).Select(f => f.Header.Compression), Is.All.EqualTo("zstd"));
            await ExpectPayloadOnEveryPath(Accept(chunkSize), wire, payload);
        }
        Assert.That(sender.GetStatsSnapshot().ChunkedMessagesSent, Is.EqualTo(1), "only the Stream overload counts in Stats");
    }

    [Test]
    public async Task StreamingReceive_YieldsUncompressedChunkSizes()
    {
        var payload = Compressible(3 * Chunk + 100);
        var wire = await Frames.CaptureAsync(new HyperionProtocol(new DefaultSerializer(), Compress()), payload);
        var receiver = new HyperionProtocol(new DefaultSerializer(), Accept());
        var smart = new SmartHyperionProtocol(new DefaultSerializer(), Accept());

        var streams = new[]
        {
            receiver.ReceiveStreamingAsync(new MemoryStream(wire)),
            receiver.ReceiveStreamingAsync(PipeReader.Create(new MemoryStream(wire))),
            smart.ReceiveStreamingAsync(new MemoryStream(wire)),
        };
        foreach (var stream in streams)
        {
            var sizes = new List<int>();
            await foreach (var chunk in stream)
                sizes.Add(chunk.Length);
            Assert.That(sizes, Is.EqualTo(new[] { Chunk, Chunk, Chunk, 100 }));
        }
    }

    [Test]
    public async Task IncompressibleChunk_IsSentRaw_AndTheMixedMessageRoundTrips()
    {
        var payload = Frames.Concat(Compressible(Chunk), TestTransport.RandomBytes(Chunk), Compressible(2000, salt: 5));
        var sender = new HyperionProtocol(new DefaultSerializer(), Compress(checksum: ChunkChecksumMode.Emit));

        foreach (var wire in new[] { await Frames.CaptureAsync(sender, payload), await CapturePipeAsync(sender, payload), await CaptureStreamingAsync(sender, payload) })
        {
            var frames = Frames.Parse(wire);
            Assert.That(frames, Has.Count.EqualTo(3));
            Assert.That(frames.Select(f => f.Header.Version), Is.All.EqualTo(2), "every chunk of an attempted message is version 2");
            Assert.That(frames.Select(f => f.Header.Compression), Is.EqualTo(new[] { "zstd", null, "zstd" }));
            Assert.That(frames.Select(f => f.Header.RawLength), Is.EqualTo(new int?[] { Chunk, null, 2000 }));
            Assert.That(frames[1].Header.DataLength, Is.EqualTo(Chunk));
            Assert.That(frames[1].Payload, Is.EqualTo(payload.AsSpan(Chunk, Chunk).ToArray()));
            Assert.That(frames[1].HeaderJson, Does.Not.Contain("Compression").And.Not.Contain("RawLength"));

            await ExpectPayloadOnEveryPath(Accept(), wire, payload);
        }
    }

    [Test]
    public async Task FullyIncompressibleMessage_IsVersion2_AllRaw()
    {
        var payload = TestTransport.RandomBytes(2 * Chunk + 17);
        var wire = await Frames.CaptureAsync(new HyperionProtocol(new DefaultSerializer(), Compress()), payload);
        var frames = Frames.Parse(wire);

        Assert.That(frames.Select(f => f.Header.Version), Is.All.EqualTo(2));
        Assert.That(frames.Select(f => f.Header.Compression), Is.All.Null);
        await ExpectPayloadOnEveryPath(Off(), wire, payload); // nothing compressed: even an Off receiver reads it
    }

    [Test]
    public async Task BelowThreshold_IsNotCompressed_AndStaysVersion1()
    {
        var sender = new HyperionProtocol(new DefaultSerializer(), Compress());

        var below = await Frames.CaptureAsync(sender, Compressible(1023));
        Assert.That(below, Is.EqualTo(RebuildAs041(below)));
        Assert.That(Frames.Parse(below).Single().Header.Version, Is.EqualTo(1));

        var at = Frames.Parse(await Frames.CaptureAsync(sender, Compressible(1024))).Single();
        Assert.That(at.Header.Version, Is.EqualTo(2));
        Assert.That(at.Header.Compression, Is.EqualTo("zstd"));
        Assert.That(at.Header.RawLength, Is.EqualTo(1024));

        var pipe = await CapturePipeAsync(sender, Compressible(1023));
        Assert.That(pipe, Is.EqualTo(RebuildAs041(pipe)));
        var streaming = await CaptureStreamingAsync(sender, Compressible(1023));
        Assert.That(streaming, Is.EqualTo(RebuildAs041(streaming)));
    }

    [Test]
    public async Task CustomThreshold_IsHonoured_AndEmptyMessagesAreNeverCompressed()
    {
        var eager = new HyperionProtocol(new DefaultSerializer(), Compress(threshold: 0));
        Assert.That(Frames.Parse(await Frames.CaptureAsync(eager, Compressible(300))).Single().Header.Compression, Is.EqualTo("zstd"));

        var empty = await Frames.CaptureAsync(eager, Array.Empty<byte>());
        Assert.That(empty, Is.EqualTo(RebuildAs041(empty)));
        var emptyStreamed = await CaptureStreamingAsync(eager, Array.Empty<byte>());
        Assert.That(emptyStreamed, Is.EqualTo(RebuildAs041(emptyStreamed)));

        var lazy = new HyperionProtocol(new DefaultSerializer(), Compress(threshold: 100_000));
        var wire = await Frames.CaptureAsync(lazy, Compressible(3 * Chunk));
        Assert.That(wire, Is.EqualTo(RebuildAs041(wire)));
    }

    [Test]
    public async Task Smart_SmallMessages_AreIdenticalToCompressionOff()
    {
        var on = new SmartHyperionProtocol(new DefaultSerializer(), Compress(threshold: 0));
        var off = new SmartHyperionProtocol(new DefaultSerializer(), Off());

        foreach (int size in new[] { 0, 100, 1023, 1024, 5000, 65535 })
        {
            var payload = Compressible(size);
            var wire = await Frames.CaptureAsync(on, payload);
            Assert.That(wire, Is.EqualTo(await Frames.CaptureAsync(off, payload)), $"size {size}");
            Assert.That(wire.Length, Is.EqualTo(size + (size < 1024 ? 3 : 5)));
        }
    }

    [Test]
    public async Task Accept_ReceivesCompressed_ButEmitsNone()
    {
        var payload = Compressible(3 * Chunk + 100);
        var accept = new HyperionProtocol(new DefaultSerializer(), Accept());

        var emitted = await Frames.CaptureAsync(accept, payload);
        Assert.That(emitted, Is.EqualTo(RebuildAs041(emitted)));
        var piped = await CapturePipeAsync(accept, payload);
        Assert.That(piped, Is.EqualTo(RebuildAs041(piped)));
        var streamed = await CaptureStreamingAsync(accept, payload);
        Assert.That(streamed, Is.EqualTo(RebuildAs041(streamed)));

        var compressed = await Frames.CaptureAsync(new HyperionProtocol(new DefaultSerializer(), Compress()), payload);
        Assert.That(await accept.ReceiveAsync<byte[]>(new MemoryStream(compressed)), Is.EqualTo(payload));
    }

    [Test]
    public async Task CompressSender_ToOffReceiver_Fails_AndTheReverseWorks()
    {
        var payload = Compressible(3 * Chunk + 100);
        var compressing = new HyperionProtocol(new DefaultSerializer(), Compress());
        var plain = new HyperionProtocol(new DefaultSerializer(), Off());

        ExpectOnEveryPath(Off(), await Frames.CaptureAsync(compressing, payload), HyperionErrorCode.CompressionNotSupported);
        await ExpectPayloadOnEveryPath(Compress(), await Frames.CaptureAsync(plain, payload), payload);
    }

    [Test]
    public async Task Checksum_CoversTheWireBytes_AndIsVerifiedBeforeDecompression()
    {
        var payload = Compressible(2 * Chunk + 1500);
        var sender = new HyperionProtocol(new DefaultSerializer(), Compress(checksum: ChunkChecksumMode.Emit));
        var wire = await Frames.CaptureAsync(sender, payload);

        foreach (var frame in Frames.Parse(wire))
        {
            Assert.That(frame.Header.Compression, Is.EqualTo("zstd"));
            Assert.That(frame.Header.Checksum, Is.EqualTo(Crc32C.Compute(frame.Payload)));
        }
        await ExpectPayloadOnEveryPath(Accept(), wire, payload);

        // Corrupt each byte position of the last compressed payload in turn: always a checksum error, never a decoder error.
        int lastPayloadLength = Frames.Parse(wire)[^1].Header.DataLength;
        for (int back = 1; back <= lastPayloadLength; back++)
        {
            var corrupted = (byte[])wire.Clone();
            corrupted[^back] ^= 0x10;
            var ex = Assert.ThrowsAsync<HyperionProtocolException>(() =>
                new HyperionProtocol(new DefaultSerializer(), Accept()).ReceiveAsync<byte[]>(new MemoryStream(corrupted)));
            Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.ChecksumMismatch), $"byte -{back}");
        }

        var flipped = (byte[])wire.Clone();
        flipped[^1] ^= 0x01;
        ExpectOnEveryPath(Accept(), flipped, HyperionErrorCode.ChecksumMismatch);
    }

    [Test]
    public void CorruptCompressedPayload_WithoutChecksum_IsDecompressionFailed()
    {
        var id = Guid.NewGuid();
        var frame = Zstd(Compressible(Chunk));

        var badMagic = (byte[])frame.Clone();
        badMagic[0] ^= 0xFF;
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, badMagic, rawLength: Chunk), HyperionErrorCode.DecompressionFailed);

        var truncated = frame.AsSpan(0, frame.Length - 1).ToArray();
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, truncated, rawLength: Chunk), HyperionErrorCode.DecompressionFailed);

        var garbage = TestTransport.RandomBytes(200);
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, garbage, rawLength: Chunk), HyperionErrorCode.DecompressionFailed);

        var trailing = Frames.Concat(GoldenAbcFrame, new byte[] { 0x00 });
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, trailing, rawLength: 3), HyperionErrorCode.DecompressionFailed);
    }

    [Test]
    public void DecompressionBomb_IsDecompressionFailed()
    {
        var bomb = Zstd(new byte[1024 * 1024]);
        Assert.That(bomb.Length, Is.LessThan(200), "1 MiB of zeros compresses to a few dozen bytes");

        var wire = Frames.CompressedChunk(Guid.NewGuid(), 0, 1, bomb, rawLength: 16);
        ExpectOnEveryPath(Accept(), wire, HyperionErrorCode.DecompressionFailed);
        ExpectOnEveryPath(Accept(HyperionProtocolOptions.DefaultChunkSize), wire, HyperionErrorCode.DecompressionFailed);
    }

    [Test]
    public void RawLength_AboveChunkSize_IsInvalidDataLength_FromTheHeaderAlone()
    {
        var headerOnly = Frames.CompressedChunk(Guid.NewGuid(), 0, 1, Array.Empty<byte>(), rawLength: Chunk + 1, dataLength: 50);

        ExpectOnEveryPath(Accept(), headerOnly, HyperionErrorCode.InvalidDataLength);
    }

    [Test]
    public void RawLength_MissingZeroOrNegative_OrEmptyPayload_IsInvalidDataLength_FromTheHeaderAlone()
    {
        var id = Guid.NewGuid();

        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, Array.Empty<byte>(), rawLength: null, dataLength: 12), HyperionErrorCode.InvalidDataLength);
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, Array.Empty<byte>(), rawLength: 0, dataLength: 12), HyperionErrorCode.InvalidDataLength);
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, Array.Empty<byte>(), rawLength: -3, dataLength: 12), HyperionErrorCode.InvalidDataLength);
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, Array.Empty<byte>(), rawLength: 3, dataLength: 0), HyperionErrorCode.InvalidDataLength);
    }

    [Test]
    public void TotalRawLength_AboveMaxMessageSize_IsMessageTooLarge_FromTheFirstHeader()
    {
        var id = Guid.NewGuid();

        // Three chunks of 4096 raw bytes describe at least 8193 bytes; 20 bytes on the wire per chunk do not matter.
        var multi = Frames.CompressedChunk(id, 0, 3, Array.Empty<byte>(), rawLength: Chunk, dataLength: 20);
        ExpectOnEveryPath(Accept(maxMessageSize: 5000), multi, HyperionErrorCode.MessageTooLarge);

        var single = Frames.CompressedChunk(id, 0, 1, Array.Empty<byte>(), rawLength: Chunk, dataLength: 20);
        ExpectOnEveryPath(Accept(maxMessageSize: 1000), single, HyperionErrorCode.MessageTooLarge);
    }

    [Test]
    public void RawLength_ThatDoesNotMatchTheContent_IsDecompressionFailed()
    {
        var id = Guid.NewGuid();

        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, GoldenAbcFrame, rawLength: 5), HyperionErrorCode.DecompressionFailed);
        ExpectOnEveryPath(Accept(), Frames.CompressedChunk(id, 0, 1, GoldenAbcFrame, rawLength: 2), HyperionErrorCode.DecompressionFailed);
    }

    [Test]
    public async Task UniformChunkRule_AppliesToRawLength_NotToDataLength()
    {
        var id = Guid.NewGuid();
        var a = Compressible(Chunk);
        var b = Frames.Concat(Compressible(Chunk / 2, salt: 3), new byte[Chunk / 2]);
        var c = Compressible(Chunk, salt: 40);
        var za = Zstd(a);
        var zb = Zstd(b);
        var zc = Zstd(c);
        Assert.That(za.Length, Is.Not.EqualTo(zb.Length), "the test needs differing on-wire lengths");

        // Same RawLength, different DataLength, one chunk raw: legal.
        var legal = Frames.Concat(
            Frames.CompressedChunk(id, 0, 3, za, rawLength: Chunk),
            Frames.Chunk(id, 1, 3, c, version: 2),
            Frames.CompressedChunk(id, 2, 3, zb, rawLength: Chunk));
        await ExpectPayloadOnEveryPath(Accept(), legal, Frames.Concat(a, c, b));

        // A raw first chunk fixes S as well.
        var rawFirst = Frames.Concat(
            Frames.Chunk(id, 0, 2, a, version: 2),
            Frames.CompressedChunk(id, 1, 2, zc, rawLength: Chunk));
        await ExpectPayloadOnEveryPath(Accept(), rawFirst, Frames.Concat(a, c));

        // A middle chunk whose RawLength differs from chunk 0 breaks the rule even though the bytes are a valid frame.
        var shortMiddle = Compressible(Chunk - 96);
        var illegalMiddle = Frames.Concat(
            Frames.CompressedChunk(id, 0, 3, za, rawLength: Chunk),
            Frames.CompressedChunk(id, 1, 3, Zstd(shortMiddle), rawLength: shortMiddle.Length),
            Frames.CompressedChunk(id, 2, 3, zb, rawLength: Chunk));
        ExpectOnEveryPath(Accept(), illegalMiddle, HyperionErrorCode.InvalidDataLength);

        // A raw middle chunk is measured by DataLength.
        var illegalRawMiddle = Frames.Concat(
            Frames.CompressedChunk(id, 0, 3, za, rawLength: Chunk),
            Frames.Chunk(id, 1, 3, shortMiddle, version: 2),
            Frames.CompressedChunk(id, 2, 3, zb, rawLength: Chunk));
        ExpectOnEveryPath(Accept(), illegalRawMiddle, HyperionErrorCode.InvalidDataLength);

        // The final chunk may not be longer than chunk 0.
        var longFinal = Frames.Concat(
            Frames.CompressedChunk(id, 0, 2, Zstd(shortMiddle), rawLength: shortMiddle.Length),
            Frames.CompressedChunk(id, 1, 2, za, rawLength: Chunk));
        ExpectOnEveryPath(Accept(), longFinal, HyperionErrorCode.InvalidDataLength);
    }

    [Test]
    public async Task GoldenFrame_DecodesToAbc()
    {
        var wire = Frames.CompressedChunk(Guid.Parse("6f1d2c3e-4b5a-4c6d-8e7f-9a0b1c2d3e4f"), 0, 1, GoldenAbcFrame, rawLength: 3);

        // The complete frame of the specification's worked example, byte for byte.
        var expected = Frames.Concat(
            new byte[] { 0x00, 0x00, 0x00, 0xAA },
            Encoding.ASCII.GetBytes(
                "{\"Version\":2,\"Magic\":\"TTS\",\"PacketId\":\"6f1d2c3e-4b5a-4c6d-8e7f-9a0b1c2d3e4f\",\"ChunkNumber\":0," +
                "\"TotalChunks\":1,\"DataLength\":12,\"Flags\":1,\"Compression\":\"zstd\",\"RawLength\":3}"),
            GoldenAbcFrame);
        Assert.That(wire, Is.EqualTo(expected));

        await ExpectPayloadOnEveryPath(Accept(), wire, Encoding.ASCII.GetBytes("abc"));
        Assert.That(await new HyperionProtocol(new DefaultSerializer(), Accept()).ReceiveAsync<string>(new MemoryStream(wire)), Is.EqualTo("abc"));
    }

    [Test]
    public async Task StalledPeer_MidCompressedChunk_StillTimesOut()
    {
        var options = new HyperionProtocolOptions
        {
            ChunkSize = Chunk,
            ChunkCompression = ChunkCompressionMode.Accept,
            ReceiveTimeout = TimeSpan.FromMilliseconds(200),
        };
        var receiver = new HyperionProtocol(new DefaultSerializer(), options);
        using var link = new TestTransport.Link();

        var frame = Frames.CompressedChunk(Guid.NewGuid(), 0, 1, Zstd(Compressible(Chunk)), rawLength: Chunk);
        await link.WriteRawAsync(frame.AsSpan(0, frame.Length - 5).ToArray());

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<byte[]>(link.Reader).WaitAsync(Deadline));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.Timeout), ex.ToString());
        Assert.That(ex.InnerException, Is.InstanceOf<TimeoutException>());
    }

    [Test]
    public async Task Diagnostics_CountUncompressedBytes()
    {
        var counts = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HyperionDiagnostics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string mode = "";
            foreach (var tag in tags)
                if (tag.Key == "hyperion.mode") mode = tag.Value?.ToString() ?? "";
            counts.AddOrUpdate($"{instrument.Name}|{mode}", value, (_, old) => old + value);
        });
        listener.Start();

        var payload = Compressible(3 * Chunk + 100);
        var sender = new HyperionProtocol(new DefaultSerializer(), Compress());
        var receiver = new HyperionProtocol(new DefaultSerializer(), Accept());

        var wire = await Frames.CaptureAsync(sender, payload);
        Assert.That(wire.Length, Is.LessThan(payload.Length / 2));
        _ = await receiver.ReceiveAsync<byte[]>(new MemoryStream(wire));
        await foreach (var _ in receiver.ReceiveStreamingAsync(new MemoryStream(wire))) { }

        Assert.Multiple(() =>
        {
            Assert.That(counts["hyperion.bytes.sent|chunked"], Is.EqualTo(payload.Length));
            Assert.That(counts["hyperion.chunks.sent|chunked"], Is.EqualTo(4));
            Assert.That(counts["hyperion.bytes.received|chunked"], Is.EqualTo(2L * payload.Length));
            Assert.That(counts["hyperion.chunks.received|chunked"], Is.EqualTo(8));
        });
    }

    [Test]
    public async Task EndToEnd_OverTcp_BothDirections()
    {
        await using var pair = await TestTransport.TcpPair.CreateAsync();
        var options = new HyperionProtocolOptions
        {
            ChunkSize = 64 * 1024,
            ChunkCompression = ChunkCompressionMode.Compress,
            ChunkChecksum = ChunkChecksumMode.Required,
        };
        var client = new SmartHyperionProtocol(new DefaultSerializer(), options);
        var server = new SmartHyperionProtocol(new DefaultSerializer(), options);
        var payload = Compressible(1_000_000);

        var send = client.SendAsync(payload, pair.ClientStream);
        var received = await server.ReceiveAsync<byte[]>(pair.ServerStream).WaitAsync(Deadline);
        await send.WaitAsync(Deadline);
        Assert.That(received, Is.EqualTo(payload));

        send = server.SendAsync("small reply", pair.ServerStream);
        Assert.That(await client.ReceiveAsync<string>(pair.ClientStream).WaitAsync(Deadline), Is.EqualTo("small reply"));
        await send.WaitAsync(Deadline);
    }

#endif
}
