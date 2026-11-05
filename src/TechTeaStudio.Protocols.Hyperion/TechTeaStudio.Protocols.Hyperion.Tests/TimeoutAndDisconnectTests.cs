using System.Diagnostics;
using System.IO.Pipelines;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
public class TimeoutAndDisconnectTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static HyperionProtocolOptions ReceiveTimeout(int ms, int chunkSize = HyperionProtocolOptions.DefaultChunkSize)
        => new() { ReceiveTimeout = TimeSpan.FromMilliseconds(ms), ChunkSize = chunkSize };

    // --- Receive timeout ----------------------------------------------------------------------

    [Test]
    public async Task ReceiveTimeout_PeerSilent_ThrowsTimeoutCode_OnEveryPath()
    {
        var options = ReceiveTimeout(200);
        var baseProtocol = new HyperionProtocol(new DefaultSerializer(), options);
        var smart = new SmartHyperionProtocol(new DefaultSerializer(), options);

        using var link1 = new TestTransport.Link();
        using var link2 = new TestTransport.Link();
        using var link3 = new TestTransport.Link();
        using var link4 = new TestTransport.Link();

        var sw = Stopwatch.StartNew();
        AssertTimeout(Assert.ThrowsAsync<HyperionProtocolException>(() => baseProtocol.ReceiveAsync<byte[]>(link1.Reader).WaitAsync(Deadline)));
        AssertTimeout(Assert.ThrowsAsync<HyperionProtocolException>(() => smart.ReceiveAsync<byte[]>(link2.Reader).WaitAsync(Deadline)));
        AssertTimeout(Assert.ThrowsAsync<HyperionProtocolException>(() => baseProtocol.ReceiveAsync<byte[]>(link3.PipeReader).WaitAsync(Deadline)));
        AssertTimeout(Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in baseProtocol.ReceiveStreamingAsync(link4.Reader)) { }
        }));
        sw.Stop();

        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ReceiveTimeout_FiresMidFrame_WhenPeerStalls()
    {
        var receiver = new HyperionProtocol(new DefaultSerializer(), ReceiveTimeout(200));
        using var link = new TestTransport.Link();

        var frame = Frames.Chunk(Guid.NewGuid(), 0, 1, new byte[100]);
        await link.WriteRawAsync(frame.AsSpan(0, frame.Length - 10).ToArray()); // everything but the last 10 payload bytes

        AssertTimeout(Assert.ThrowsAsync<HyperionProtocolException>(() => receiver.ReceiveAsync<byte[]>(link.Reader).WaitAsync(Deadline)));
    }

    [Test]
    public async Task ReceiveTimeout_IsIdleBased_SlowButSteadyPeerSucceeds()
    {
        // 300 ms idle timeout; the peer dribbles a 3-chunk message in 8 pieces with 120 ms gaps (960 ms total).
        var receiver = new HyperionProtocol(new DefaultSerializer(), ReceiveTimeout(300, chunkSize: 10));
        using var link = new TestTransport.Link();
        var payload = TestTransport.RandomBytes(25);
        var bytes = Frames.Concat(Frames.Chunks(payload, chunkSize: 10));

        var receiveTask = receiver.ReceiveAsync<byte[]>(link.Reader).WaitAsync(Deadline);

        int pieces = 8;
        int pieceSize = (bytes.Length + pieces - 1) / pieces;
        for (int offset = 0; offset < bytes.Length; offset += pieceSize)
        {
            await link.WriteRawAsync(bytes.AsSpan(offset, Math.Min(pieceSize, bytes.Length - offset)).ToArray());
            await Task.Delay(120);
        }

        Assert.That(await receiveTask, Is.EqualTo(payload));
    }

    [Test]
    public async Task ReceiveTimeout_StreamingConsumer_MayBeSlowBetweenChunks()
    {
        // The idle timer must not run while the consumer holds a chunk: 200 ms timeout, consumer pauses 600 ms.
        var receiver = new HyperionProtocol(new DefaultSerializer(), ReceiveTimeout(200, chunkSize: 10));
        using var link = new TestTransport.Link();
        var payload = TestTransport.RandomBytes(25);
        var frames = Frames.Chunks(payload, chunkSize: 10);

        await link.WriteRawAsync(frames[0]);

        await using var enumerator = receiver.ReceiveStreamingAsync(link.Reader).GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Deadline), Is.True);

        await Task.Delay(600);
        await link.WriteRawAsync(frames[1], frames[2]);

        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Deadline), Is.True);
        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Deadline), Is.True);
        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Deadline), Is.False);
    }

    [Test]
    public async Task ReceiveTimeout_CallerCancellation_ReportsOperationCanceled_NotTimeout()
    {
        var receiver = new HyperionProtocol(new DefaultSerializer(), ReceiveTimeout(5_000));
        using var link = new TestTransport.Link();
        using var cts = new CancellationTokenSource(100);

        Assert.ThrowsAsync<OperationCanceledException>(() => receiver.ReceiveAsync<byte[]>(link.Reader, cts.Token).WaitAsync(Deadline));
        await Task.CompletedTask;
    }

    [Test]
    public async Task NoTimeoutConfigured_WaitsBeyondAnyTimer()
    {
        var receiver = new HyperionProtocol(new DefaultSerializer());
        using var link = new TestTransport.Link();

        var receiveTask = receiver.ReceiveAsync<byte[]>(link.Reader);
        await Task.Delay(400);
        Assert.That(receiveTask.IsCompleted, Is.False);

        await link.WriteRawAsync(Frames.Chunk(Guid.NewGuid(), 0, 1, new byte[] { 1, 2, 3 }));
        Assert.That(await receiveTask.WaitAsync(Deadline), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    // --- Send timeout -------------------------------------------------------------------------

    [Test]
    public async Task SendTimeout_PeerNotReading_ThrowsTimeoutCode()
    {
        await using var pair = await TestTransport.TcpPair.CreateAsync();
        var sender = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { SendTimeout = TimeSpan.FromMilliseconds(500) });

        // Nobody reads the server side; socket buffers fill up and the write stalls.
        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => sender.SendAsync(new byte[64 * 1024 * 1024], pair.ClientStream).WaitAsync(Deadline));
        sw.Stop();

        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.Timeout));
        Assert.That(ex.InnerException, Is.InstanceOf<TimeoutException>());
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    // --- Disconnects ---------------------------------------------------------------------------

    [Test]
    public async Task PeerClosed_AtMessageBoundary_OnEveryPath()
    {
        var baseProtocol = new HyperionProtocol(new DefaultSerializer());
        var smart = new SmartHyperionProtocol(new DefaultSerializer());

        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(() => baseProtocol.ReceiveAsync<byte[]>(new MemoryStream())));
        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(() => smart.ReceiveAsync<byte[]>(new MemoryStream())));
        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(() => baseProtocol.ReceiveAsync<byte[]>(PipeReader.Create(new MemoryStream()))));
        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in baseProtocol.ReceiveStreamingAsync(new MemoryStream())) { }
        }));
        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in smart.ReceiveStreamingAsync(new MemoryStream())) { }
        }));
        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in baseProtocol.ReceiveStreamingAsync(PipeReader.Create(new MemoryStream()))) { }
        }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task PeerClosed_AfterLastMessage_FirstMessageStillDelivered()
    {
        var protocol = new SmartHyperionProtocol(new DefaultSerializer());
        var bytes = await Frames.CaptureAsync(protocol, "last words");
        using var stream = new MemoryStream(bytes);

        Assert.That(await protocol.ReceiveAsync<string>(stream), Is.EqualTo("last words"));
        AssertPeerClosed(Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.ReceiveAsync<string>(stream)));
    }

    [Test]
    public async Task UnexpectedEndOfStream_MidFrame_IsDistinguishedFromCleanClose()
    {
        var baseProtocol = new HyperionProtocol(new DefaultSerializer());
        var smart = new SmartHyperionProtocol(new DefaultSerializer());
        var frame = Frames.Chunk(Guid.NewGuid(), 0, 1, TestTransport.RandomBytes(100));

        // Truncated inside the length prefix, the header, and the payload.
        foreach (int cut in new[] { 2, 4 + 10, frame.Length - 1 })
        {
            var truncated = frame.AsSpan(0, cut).ToArray();
            AssertUnexpectedEof(Assert.ThrowsAsync<HyperionProtocolException>(() => baseProtocol.ReceiveAsync<byte[]>(new MemoryStream(truncated))));
            AssertUnexpectedEof(Assert.ThrowsAsync<HyperionProtocolException>(() => smart.ReceiveAsync<byte[]>(new MemoryStream(truncated))));
            AssertUnexpectedEof(Assert.ThrowsAsync<HyperionProtocolException>(() => baseProtocol.ReceiveAsync<byte[]>(PipeReader.Create(new MemoryStream(truncated)))));
        }

        // Smart small frames cut after the magic byte and inside the payload.
        AssertUnexpectedEof(Assert.ThrowsAsync<HyperionProtocolException>(() => smart.ReceiveAsync<byte[]>(new MemoryStream(new[] { SmartHyperionProtocol.LightweightMagic }))));
        AssertUnexpectedEof(Assert.ThrowsAsync<HyperionProtocolException>(() => smart.ReceiveAsync<byte[]>(new MemoryStream(Frames.Direct(new byte[50]).AsSpan(0, 30).ToArray()))));
        await Task.CompletedTask;
    }

    [Test]
    public async Task UnexpectedEndOfStream_DuringStreaming_AfterSomeChunksWereYielded()
    {
        var frames = Frames.Chunks(TestTransport.RandomBytes(25), chunkSize: 10);
        var truncated = Frames.Concat(frames[0], frames[1].AsSpan(0, frames[1].Length - 3).ToArray());

        int yielded = 0;
        var ex = Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            await foreach (var _ in new HyperionProtocol(new DefaultSerializer()).ReceiveStreamingAsync(new MemoryStream(truncated)))
                yielded++;
        });

        AssertUnexpectedEof(ex);
        Assert.That(yielded, Is.EqualTo(1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task PipeWriter_ReaderCompleted_ThrowsPeerClosed()
    {
        var pipe = new Pipe();
        pipe.Reader.Complete();

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => new HyperionProtocol(new DefaultSerializer()).SendAsync("hello", pipe.Writer));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.PeerClosed));
        await Task.CompletedTask;
    }

    [Test]
    public async Task SendAsync_PeerGone_ThrowsSendFailed_OrPeerClosed()
    {
        await using var pair = await TestTransport.TcpPair.CreateAsync();
        pair.ServerStream.Dispose();
        await Task.Delay(50);

        var sender = new HyperionProtocol(new DefaultSerializer());
        // Writing into a closed socket eventually fails; the exact moment depends on buffering, so send plenty.
        var ex = Assert.ThrowsAsync<HyperionProtocolException>(async () =>
        {
            for (int i = 0; i < 50; i++)
                await sender.SendAsync(new byte[1024 * 1024], pair.ClientStream);
        });
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.SendFailed).Or.EqualTo(HyperionErrorCode.PeerClosed));
        Assert.That(ex.InnerException, Is.Not.Null);
    }

    // --- Handshake -----------------------------------------------------------------------------

    [Test]
    public async Task Handshake_PeerClosesConnection_ReportsHandshakeFailed()
    {
        await using var pair = await TestTransport.TcpPair.CreateAsync();
        pair.ServerStream.Dispose();

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => HyperionProtocol.HandshakeAsync(pair.ClientStream).WaitAsync(Deadline));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.HandshakeFailed));
    }

    [Test]
    public async Task Handshake_WrongMagic_ReportsHandshakeFailed()
    {
        await using var pair = await TestTransport.TcpPair.CreateAsync();
        await pair.ServerStream.WriteAsync(new byte[] { 1, 2, 3, 4, 0, 0, 0, 1 });

        var ex = Assert.ThrowsAsync<HyperionProtocolException>(() => HyperionProtocol.HandshakeAsync(pair.ClientStream).WaitAsync(Deadline));
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.HandshakeFailed));
    }

    [Test]
    public async Task Handshake_CanBeBoundedWithACancellationToken()
    {
        await using var pair = await TestTransport.TcpPair.CreateAsync();
        using var cts = new CancellationTokenSource(200);

        Assert.ThrowsAsync<OperationCanceledException>(() => HyperionProtocol.HandshakeAsync(pair.ClientStream, ct: cts.Token).WaitAsync(Deadline));
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static void AssertTimeout(HyperionProtocolException? ex)
    {
        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.Timeout), ex.ToString());
        Assert.That(ex.InnerException, Is.InstanceOf<TimeoutException>());
    }

    private static void AssertPeerClosed(HyperionProtocolException? ex)
    {
        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.PeerClosed), ex.ToString());
        Assert.That(ex.InnerException, Is.InstanceOf<EndOfStreamException>());
    }

    private static void AssertUnexpectedEof(HyperionProtocolException? ex)
    {
        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.ErrorCode, Is.EqualTo(HyperionErrorCode.UnexpectedEndOfStream), ex.ToString());
        Assert.That(ex.InnerException, Is.InstanceOf<EndOfStreamException>());
    }
}
