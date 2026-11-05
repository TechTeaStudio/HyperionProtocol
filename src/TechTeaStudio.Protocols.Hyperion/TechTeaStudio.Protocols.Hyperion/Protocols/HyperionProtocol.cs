using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Chunked message framing over any <see cref="Stream"/> (a TCP <see cref="System.Net.Sockets.NetworkStream"/>,
/// an <see cref="System.Net.Security.SslStream"/>, a named pipe, ...) or over a <see cref="PipeReader"/> /
/// <see cref="PipeWriter"/> pair. A serialized payload is split into fixed-size chunks and every chunk is framed as
/// <c>[headerLength:4 BE][JSON PacketHeader][payload]</c>. The receiver validates magic, version, order, end flag,
/// packet-id continuity, size limits and (optionally) a CRC-32C per chunk, then reassembles the payload.
/// </summary>
/// <remarks>
/// <para>Instances hold no per-connection state: one instance can serve any number of connections from any number
/// of threads. Within a single connection, however, do not run two sends (or two receives) concurrently; frames
/// would interleave. Serialize them with a lock or a channel.</para>
/// <para>See <c>docs/SPECIFICATION.md</c> for the wire format and <c>docs/DESIGN_CHECKLIST.md</c> for the design review.</para>
/// </remarks>
public class HyperionProtocol
{
    /// <summary>Protocol magic identifier ("TTS").</summary>
    public const string ProtocolMagic = "TTS";

    /// <summary>Current wire-protocol revision emitted by this library.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Minimum wire-protocol revision this library accepts. <c>0</c> means legacy/unversioned.</summary>
    public const int MinSupportedProtocolVersion = 0;

    internal const string ChunkedMode = "chunked";

    private const uint HandshakeMagic = 0x54545348; // "TTSH" big-endian
    private const int CoalesceThreshold = 64 * 1024;  // header + payload below this go out in one write

    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    /// <summary>Underlying serializer used for payloads.</summary>
    protected readonly ISerializer _serializer;

    /// <summary>Active protocol options (chunk size, limits, timeouts, checksum policy).</summary>
    protected readonly HyperionProtocolOptions _options;

    /// <summary>Maximum payload bytes per chunk.</summary>
    public int ChunkSize => _options.ChunkSize;

    /// <summary>Maximum JSON header length in bytes.</summary>
    public int MaxHeaderLength => _options.MaxHeaderLength;

    /// <summary>Maximum size of one logical message in bytes.</summary>
    public long MaxMessageSize => _options.MaxMessageSize;

    /// <summary>The options this instance was created with.</summary>
    public HyperionProtocolOptions Options => _options;

    /// <summary>Creates a protocol using default options.</summary>
    public HyperionProtocol(ISerializer serializer)
        : this(serializer, HyperionProtocolOptions.Default) { }

    /// <summary>Creates a protocol with custom options.</summary>
    public HyperionProtocol(ISerializer serializer, HyperionProtocolOptions options)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    #region Stream API

    /// <summary>Serializes <paramref name="message"/> and sends it as one or more framed chunks.</summary>
    /// <exception cref="HyperionProtocolException">Serialization failed or the message exceeds <see cref="MaxMessageSize"/>
    /// (nothing written, connection still usable), or the transport failed / timed out (connection must be closed).</exception>
    public virtual async Task SendAsync<T>(T message, Stream stream, CancellationToken ct = default)
    {
        ValidateWritable(stream);
        var data = SerializePayload(message);
        await SendChunkedAsync(data, stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams <paramref name="length"/> bytes from <paramref name="source"/> to <paramref name="destination"/> as one
    /// chunked message without buffering it in memory. Pair it with <see cref="ReceiveStreamingAsync(Stream, CancellationToken)"/> on the other
    /// side for file transfers of any size.
    /// </summary>
    /// <param name="source">Readable stream positioned at the first payload byte.</param>
    /// <param name="length">Exact number of bytes to send. If <paramref name="source"/> ends earlier the message is
    /// truncated on the wire and the connection must be closed.</param>
    /// <param name="destination">Writable transport stream.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task SendStreamingAsync(Stream source, long length, Stream destination, CancellationToken ct = default)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!source.CanRead) throw new InvalidOperationException("Source stream is not readable.");
        ValidateWritable(destination);
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be >= 0.");

        int totalChunks = PrepareSend(length);

        using var activity = HyperionDiagnostics.StartSend(ChunkedMode, length);
        using var guard = OperationGuard.Create(_options.SendTimeout, ct);
        var buffer = BufferPool.Rent(_options.ChunkSize);
        try
        {
            var packetId = Guid.NewGuid();
            long remaining = length;

            for (int i = 0; i < totalChunks; i++)
            {
                guard.Token.ThrowIfCancellationRequested();

                int size = (int)Math.Min(_options.ChunkSize, remaining);
                var slice = buffer.AsMemory(0, size);

                int read = await ReadExactlyOrEofAsync(source, slice, ct).ConfigureAwait(false);
                if (read != size)
                {
                    throw new HyperionProtocolException(HyperionErrorCode.SendFailed,
                        $"Source stream ended after {length - remaining + read} bytes but {length} were announced. " +
                        "The message is truncated on the wire; close the connection.");
                }
                remaining -= size;

                var header = BuildHeader(packetId, i, totalChunks, slice.Span);
                guard.Arm();
                await SendChunkAsync(destination, header, slice, guard.Token).ConfigureAwait(false);
                guard.Disarm();
            }

            guard.Arm();
            await destination.FlushAsync(guard.Token).ConfigureAwait(false);
            guard.Disarm();

            HyperionDiagnostics.RecordSent(ChunkedMode, length, totalChunks);
        }
        catch (OperationCanceledException) when (guard.TimedOut)
        {
            throw HyperionDiagnostics.RecordError(activity, TimeoutError(guard, "Send"), "send");
        }
        catch (HyperionProtocolException ex)
        {
            throw HyperionDiagnostics.RecordError(activity, ex, "send");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw HyperionDiagnostics.RecordError(activity,
                new HyperionProtocolException(HyperionErrorCode.SendFailed, "Failed to send streamed message.", ex), "send");
        }
        finally
        {
            BufferPool.Return(buffer);
        }
    }

    /// <summary>Writes a single framed chunk: <c>[headerLength:4 BE][headerJson][payload]</c>.</summary>
    public virtual async Task SendChunkAsync(Stream stream, PacketHeader header, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (header is null) throw new ArgumentNullException(nameof(header));

        var headerJson = SerializeHeader(header);
        int prefixLength = 4 + headerJson.Length;

        if (prefixLength + data.Length <= CoalesceThreshold)
        {
            // Small frame: one write for prefix + header + payload.
            int frameLength = prefixLength + data.Length;
            var frame = BufferPool.Rent(frameLength);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(frame, headerJson.Length);
                headerJson.CopyTo(frame.AsSpan(4));
                data.Span.CopyTo(frame.AsSpan(prefixLength));
                await stream.WriteAsync(frame.AsMemory(0, frameLength), ct).ConfigureAwait(false);
            }
            finally
            {
                BufferPool.Return(frame);
            }
            return;
        }

        var prefix = BufferPool.Rent(prefixLength);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(prefix, headerJson.Length);
            headerJson.CopyTo(prefix.AsSpan(4));
            await stream.WriteAsync(prefix.AsMemory(0, prefixLength), ct).ConfigureAwait(false);
        }
        finally
        {
            BufferPool.Return(prefix);
        }

        await stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    /// <summary>Receives chunks until the full payload is assembled and deserializes it to <typeparamref name="T"/>.</summary>
    /// <exception cref="HyperionProtocolException">See <see cref="HyperionErrorCode"/>. <see cref="HyperionErrorCode.PeerClosed"/>
    /// signals a clean disconnect at a message boundary.</exception>
    public virtual async Task<T> ReceiveAsync<T>(Stream stream, CancellationToken ct = default)
    {
        ValidateReadable(stream);

        using var activity = HyperionDiagnostics.StartReceive();
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);
        try
        {
            var state = new ReassemblyState();
            var chunks = await ReceiveChunksAsync(new ByteSource(stream), null, state, guard).ConfigureAwait(false);
            var payload = CombineChunks(chunks);

            HyperionDiagnostics.RecordReceived(activity, ChunkedMode, payload.Length, chunks.Count);
            return DeserializePayload<T>(payload);
        }
        catch (OperationCanceledException) when (guard.TimedOut)
        {
            throw HyperionDiagnostics.RecordError(activity, TimeoutError(guard, "Receive"), "receive");
        }
        catch (HyperionProtocolException ex)
        {
            throw HyperionDiagnostics.RecordError(activity, ex, "receive");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw HyperionDiagnostics.RecordError(activity,
                new HyperionProtocolException(HyperionErrorCode.ReceiveFailed, "Failed to receive message.", ex), "receive");
        }
    }

    /// <summary>
    /// Yields each chunk payload as it arrives, without buffering the full message in memory.
    /// Use this for very large payloads that you want to stream to disk or another stream.
    /// </summary>
    /// <remarks>
    /// Validation (magic, version, chunk order, end flag, packet-id continuity, size limits, checksum) is performed
    /// before each <c>yield</c>. If a chunk is invalid the enumerator throws <see cref="HyperionProtocolException"/>.
    /// </remarks>
    public virtual async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateReadable(stream);

        using var activity = HyperionDiagnostics.StartReceive();
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);

        await foreach (var chunk in ReceiveStreamingCoreAsync(new ByteSource(stream), null, activity, guard).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// Reads all chunks of one message from <paramref name="stream"/>. When <paramref name="firstHeaderLengthBuffer"/>
    /// is non-null, the first 4 bytes of the first chunk are taken from it instead of the stream
    /// (used by <see cref="Protocols.SmartHyperionProtocol"/> after it peeks one byte for mode detection).
    /// </summary>
    protected internal Task<List<ChunkData>> ReceiveChunksAsync(Stream stream, byte[]? firstHeaderLengthBuffer, CancellationToken ct)
    {
        ValidateReadable(stream);
        return ReceiveChunksWithGuardAsync(stream, firstHeaderLengthBuffer, ct);
    }

    private async Task<List<ChunkData>> ReceiveChunksWithGuardAsync(Stream stream, byte[]? firstHeaderLengthBuffer, CancellationToken ct)
    {
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);
        return await ReceiveChunksAsync(new ByteSource(stream), firstHeaderLengthBuffer, new ReassemblyState(), guard).ConfigureAwait(false);
    }

    #endregion

    #region Pipelines API

    /// <summary>Serializes <paramref name="message"/> and writes it through a <see cref="PipeWriter"/>.</summary>
    public virtual async Task SendAsync<T>(T message, PipeWriter writer, CancellationToken ct = default)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var data = SerializePayload(message);
        int totalChunks = PrepareSend(data.Length);

        using var activity = HyperionDiagnostics.StartSend(ChunkedMode, data.Length);
        using var guard = OperationGuard.Create(_options.SendTimeout, ct);
        try
        {
            var packetId = Guid.NewGuid();

            for (int i = 0; i < totalChunks; i++)
            {
                guard.Token.ThrowIfCancellationRequested();

                int offset = i * _options.ChunkSize;
                int size = Math.Min(_options.ChunkSize, data.Length - offset);
                WriteFrame(writer, packetId, i, totalChunks, data.AsSpan(offset, size));

                guard.Arm();
                var flush = await writer.FlushAsync(guard.Token).ConfigureAwait(false);
                guard.Disarm();

                if (flush.IsCanceled)
                    throw new OperationCanceledException("The pending flush was canceled.");
                if (flush.IsCompleted)
                {
                    throw new HyperionProtocolException(HyperionErrorCode.PeerClosed,
                        "The reader completed the pipe before the message was fully written.");
                }
            }

            HyperionDiagnostics.RecordSent(ChunkedMode, data.Length, totalChunks);
        }
        catch (OperationCanceledException) when (guard.TimedOut)
        {
            throw HyperionDiagnostics.RecordError(activity, TimeoutError(guard, "Send"), "send");
        }
        catch (HyperionProtocolException ex)
        {
            throw HyperionDiagnostics.RecordError(activity, ex, "send");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw HyperionDiagnostics.RecordError(activity,
                new HyperionProtocolException(HyperionErrorCode.SendFailed, "Failed to send via PipeWriter.", ex), "send");
        }
    }

    /// <summary>Reads chunks from a <see cref="PipeReader"/> and deserializes the payload to <typeparamref name="T"/>.</summary>
    public virtual async Task<T> ReceiveAsync<T>(PipeReader reader, CancellationToken ct = default)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        using var activity = HyperionDiagnostics.StartReceive();
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);
        try
        {
            var state = new ReassemblyState();
            var chunks = await ReceiveChunksAsync(new ByteSource(reader), null, state, guard).ConfigureAwait(false);
            var payload = CombineChunks(chunks);

            HyperionDiagnostics.RecordReceived(activity, ChunkedMode, payload.Length, chunks.Count);
            return DeserializePayload<T>(payload);
        }
        catch (OperationCanceledException) when (guard.TimedOut)
        {
            throw HyperionDiagnostics.RecordError(activity, TimeoutError(guard, "Receive"), "receive");
        }
        catch (HyperionProtocolException ex)
        {
            throw HyperionDiagnostics.RecordError(activity, ex, "receive");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw HyperionDiagnostics.RecordError(activity,
                new HyperionProtocolException(HyperionErrorCode.ReceiveFailed, "Failed to receive via PipeReader.", ex), "receive");
        }
    }

    /// <summary>Yields each chunk payload of one message read from a <see cref="PipeReader"/>, without buffering the full message.</summary>
    public virtual async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(
        PipeReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        using var activity = HyperionDiagnostics.StartReceive();
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);

        await foreach (var chunk in ReceiveStreamingCoreAsync(new ByteSource(reader), null, activity, guard).ConfigureAwait(false))
            yield return chunk;
    }

    private void WriteFrame(PipeWriter writer, Guid packetId, int chunkIndex, int totalChunks, ReadOnlySpan<byte> payload)
    {
        var header = BuildHeader(packetId, chunkIndex, totalChunks, payload);
        var headerJson = SerializeHeader(header);

        var span = writer.GetSpan(4 + headerJson.Length);
        BinaryPrimitives.WriteInt32BigEndian(span, headerJson.Length);
        headerJson.CopyTo(span.Slice(4));
        writer.Advance(4 + headerJson.Length);

        if (payload.Length > 0)
            writer.Write(payload);
    }

    #endregion

    #region Handshake

    /// <summary>
    /// Performs a simple version handshake over <paramref name="stream"/>: writes <c>[magic:4 "TTSH"][version:4 BE]</c>
    /// and reads the same from the peer, returning the minimum of the two versions (the version both sides agree to speak).
    /// Both peers must call it at the same point of the connection, before any message is exchanged.
    /// </summary>
    /// <param name="stream">A bidirectional, already-connected stream.</param>
    /// <param name="localVersion">Local protocol version. Defaults to <see cref="ProtocolVersion"/>.</param>
    /// <param name="ct">Cancellation token. Combine with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> for a handshake timeout.</param>
    /// <returns>Negotiated version: <c>min(localVersion, remoteVersion)</c>.</returns>
    /// <exception cref="HyperionProtocolException"><see cref="HyperionErrorCode.HandshakeFailed"/> on bad magic, unsupported version or a lost connection.</exception>
    public static async Task<int> HandshakeAsync(Stream stream, int? localVersion = null, CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        int local = localVersion ?? ProtocolVersion;
        if (local < MinSupportedProtocolVersion)
            throw new ArgumentOutOfRangeException(nameof(localVersion), local, $"Version must be >= {MinSupportedProtocolVersion}.");

        try
        {
            var outBuf = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(outBuf, HandshakeMagic);
            BinaryPrimitives.WriteInt32BigEndian(outBuf.AsSpan(4), local);

            // Write and read concurrently so two peers handshaking simultaneously cannot deadlock.
            var sendTask = stream.WriteAsync(outBuf, ct).AsTask();
            var inBuf = new byte[8];
            var recvTask = ReadExactlyAsync(stream, inBuf, ct);

            await sendTask.ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            if (!await recvTask.ConfigureAwait(false))
            {
                throw new HyperionProtocolException(HyperionErrorCode.HandshakeFailed,
                    "The peer closed the connection during the handshake.", new EndOfStreamException());
            }

            var remoteMagic = BinaryPrimitives.ReadUInt32BigEndian(inBuf);
            if (remoteMagic != HandshakeMagic)
            {
                throw new HyperionProtocolException(HyperionErrorCode.HandshakeFailed,
                    $"Handshake magic mismatch. Expected 0x{HandshakeMagic:X8}, got 0x{remoteMagic:X8}.");
            }

            var remoteVersion = BinaryPrimitives.ReadInt32BigEndian(inBuf.AsSpan(4));
            if (remoteVersion < MinSupportedProtocolVersion)
            {
                throw new HyperionProtocolException(HyperionErrorCode.HandshakeFailed,
                    $"Remote protocol version {remoteVersion} is below the minimum supported version {MinSupportedProtocolVersion}.");
            }

            return Math.Min(local, remoteVersion);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or HyperionProtocolException))
        {
            throw new HyperionProtocolException(HyperionErrorCode.HandshakeFailed, "Handshake failed.", ex);
        }
    }

    #endregion

    #region Shared send/receive core (also used by SmartHyperionProtocol)

    /// <summary>Sends an already-serialized payload as chunked frames over <paramref name="stream"/>.</summary>
    private protected async Task SendChunkedAsync(byte[] data, Stream stream, CancellationToken ct)
    {
        int totalChunks = PrepareSend(data.Length);

        using var activity = HyperionDiagnostics.StartSend(ChunkedMode, data.Length);
        using var guard = OperationGuard.Create(_options.SendTimeout, ct);
        try
        {
            var packetId = Guid.NewGuid();

            for (int i = 0; i < totalChunks; i++)
            {
                guard.Token.ThrowIfCancellationRequested();

                int offset = i * _options.ChunkSize;
                int size = Math.Min(_options.ChunkSize, data.Length - offset);
                var slice = data.AsMemory(offset, size);
                var header = BuildHeader(packetId, i, totalChunks, slice.Span);

                guard.Arm();
                await SendChunkAsync(stream, header, slice, guard.Token).ConfigureAwait(false);
                guard.Disarm();
            }

            guard.Arm();
            await stream.FlushAsync(guard.Token).ConfigureAwait(false);
            guard.Disarm();

            HyperionDiagnostics.RecordSent(ChunkedMode, data.Length, totalChunks);
        }
        catch (OperationCanceledException) when (guard.TimedOut)
        {
            throw HyperionDiagnostics.RecordError(activity, TimeoutError(guard, "Send"), "send");
        }
        catch (HyperionProtocolException ex)
        {
            throw HyperionDiagnostics.RecordError(activity, ex, "send");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw HyperionDiagnostics.RecordError(activity,
                new HyperionProtocolException(HyperionErrorCode.SendFailed, "Failed to send message.", ex), "send");
        }
    }

    /// <summary>Reads every chunk of one message into memory.</summary>
    private protected async Task<List<ChunkData>> ReceiveChunksAsync(ByteSource source, byte[]? firstHeaderLengthBuffer, ReassemblyState state, OperationGuard guard)
    {
        var chunks = new List<ChunkData>();
        byte[]? preset = firstHeaderLengthBuffer;

        do
        {
            var (header, data) = await ReadChunkAsync(source, state, preset, guard).ConfigureAwait(false);
            preset = null;
            chunks.Add(new ChunkData(header.ChunkNumber, data));
        }
        while (!state.IsComplete);

        return chunks;
    }

    /// <summary>Yields every chunk of one message; records the activity and metrics on completion.</summary>
    private protected async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingCoreAsync(
        ByteSource source, byte[]? firstHeaderLengthBuffer, Activity? activity, OperationGuard guard)
    {
        var state = new ReassemblyState();
        byte[]? preset = firstHeaderLengthBuffer;
        long totalBytes = 0;

        do
        {
            byte[] data;
            try
            {
                (_, data) = await ReadChunkAsync(source, state, preset, guard).ConfigureAwait(false);
                preset = null;
            }
            catch (HyperionProtocolException ex)
            {
                throw HyperionDiagnostics.RecordError(activity, ex, "receive");
            }

            totalBytes += data.Length;
            yield return data;
        }
        while (!state.IsComplete);

        HyperionDiagnostics.RecordReceived(activity, ChunkedMode, totalBytes, state.Received);
    }

    /// <summary>
    /// Reads one chunk: length prefix (unless preset), header, payload. Validates everything, verifies the checksum,
    /// and advances <paramref name="state"/>. Every failure surfaces as <see cref="HyperionProtocolException"/>.
    /// </summary>
    private async ValueTask<(PacketHeader Header, byte[] Data)> ReadChunkAsync(
        ByteSource source, ReassemblyState state, byte[]? presetHeaderLength, OperationGuard guard)
    {
        try
        {
            int headerLength;
            if (presetHeaderLength is not null)
            {
                headerLength = BinaryPrimitives.ReadInt32BigEndian(presetHeaderLength);
            }
            else
            {
                var lengthBuf = new byte[4];
                int read = await source.ReadExactlyAsync(lengthBuf, guard).ConfigureAwait(false);
                if (read < 4)
                    throw EndOfStreamError(read, 4, "chunk header length", atMessageBoundary: state.Received == 0);
                headerLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
            }

            ValidateHeaderLength(headerLength, _options.MaxHeaderLength);

            PacketHeader header;
            var headerBuf = BufferPool.Rent(headerLength);
            try
            {
                var headerMemory = headerBuf.AsMemory(0, headerLength);
                int read = await source.ReadExactlyAsync(headerMemory, guard).ConfigureAwait(false);
                if (read < headerLength)
                    throw EndOfStreamError(read, headerLength, "chunk header", atMessageBoundary: false);

                header = DeserializeHeader(headerMemory.Span);
            }
            finally
            {
                BufferPool.Return(headerBuf);
            }

            TrackChunk(state, header);

            byte[] data;
            if (header.DataLength > 0)
            {
                data = new byte[header.DataLength];
                int read = await source.ReadExactlyAsync(data, guard).ConfigureAwait(false);
                if (read < data.Length)
                    throw EndOfStreamError(read, data.Length, "chunk payload", atMessageBoundary: false);
            }
            else
            {
                data = Array.Empty<byte>();
            }

            VerifyChecksum(header, data);
            state.Received++;
            return (header, data);
        }
        catch (OperationCanceledException) when (guard.TimedOut)
        {
            throw TimeoutError(guard, "Receive");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or HyperionProtocolException))
        {
            throw new HyperionProtocolException(HyperionErrorCode.ReceiveFailed, "Failed to receive chunk.", ex);
        }
    }

    /// <summary>Applies the per-message rules on top of <see cref="ValidateHeader"/> and advances the reassembly state.</summary>
    private void TrackChunk(ReassemblyState state, PacketHeader header)
    {
        ValidateHeader(header, state.PacketId, state.TotalChunks, state.Received, _options.ChunkSize);

        if (state.PacketId is null)
        {
            state.PacketId = header.PacketId;
            state.TotalChunks = header.TotalChunks;
            state.FirstChunkLength = header.DataLength;

            if (header.TotalChunks > 1)
            {
                if (header.DataLength == 0)
                {
                    throw new HyperionProtocolException(HyperionErrorCode.InvalidDataLength,
                        "The first chunk of a multi-chunk message must not be empty.");
                }

                // Non-final chunks all carry FirstChunkLength bytes and the final one at least 1, so the smallest
                // message this header can describe is known before a single payload byte is read.
                long minimumTotal = ((long)header.TotalChunks - 1) * header.DataLength + 1;
                if (minimumTotal > _options.MaxMessageSize)
                {
                    throw new HyperionProtocolException(HyperionErrorCode.MessageTooLarge,
                        $"Message announces {header.TotalChunks} chunks of {header.DataLength} bytes (at least {minimumTotal} bytes), " +
                        $"exceeding MaxMessageSize ({_options.MaxMessageSize} bytes).");
                }
            }
        }
        else if (!header.IsLastChunk)
        {
            if (header.DataLength != state.FirstChunkLength)
            {
                throw new HyperionProtocolException(HyperionErrorCode.InvalidDataLength,
                    $"Chunk {header.ChunkNumber} carries {header.DataLength} bytes but every non-final chunk must carry " +
                    $"{state.FirstChunkLength} bytes (the size of chunk 0).");
            }
        }
        else if (header.DataLength == 0 || header.DataLength > state.FirstChunkLength)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidDataLength,
                $"Final chunk {header.ChunkNumber} carries {header.DataLength} bytes; expected 1..{state.FirstChunkLength}.");
        }

        if (state.TotalBytes + header.DataLength > _options.MaxMessageSize)
        {
            throw new HyperionProtocolException(HyperionErrorCode.MessageTooLarge,
                $"Message exceeds MaxMessageSize ({_options.MaxMessageSize} bytes) at chunk {header.ChunkNumber}.");
        }
        state.TotalBytes += header.DataLength;

        if (header.Checksum is null && _options.ChunkChecksum == ChunkChecksumMode.Required)
        {
            throw new HyperionProtocolException(HyperionErrorCode.ChecksumMissing,
                $"Chunk {header.ChunkNumber} carries no checksum but ChunkChecksum is Required.");
        }
    }

    private static void VerifyChecksum(PacketHeader header, ReadOnlySpan<byte> data)
    {
        if (header.Checksum is not uint expected)
            return;

        uint actual = Crc32C.Compute(data);
        if (actual != expected)
        {
            throw new HyperionProtocolException(HyperionErrorCode.ChecksumMismatch,
                $"Chunk {header.ChunkNumber} checksum mismatch: header says 0x{expected:X8}, payload hashes to 0x{actual:X8}.");
        }
    }

    private PacketHeader BuildHeader(Guid packetId, int chunkIndex, int totalChunks, ReadOnlySpan<byte> payload)
    {
        return new PacketHeader
        {
            Version = ProtocolVersion,
            Magic = ProtocolMagic,
            PacketId = packetId,
            ChunkNumber = chunkIndex,
            TotalChunks = totalChunks,
            DataLength = payload.Length,
            Flags = chunkIndex == totalChunks - 1 ? PacketHeader.EndFlag : (byte)0,
            Checksum = _options.ChunkChecksum == ChunkChecksumMode.Off ? null : Crc32C.Compute(payload),
        };
    }

    private byte[] SerializeHeader(PacketHeader header)
    {
        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header, HyperionJsonContext.Default.PacketHeader);
        if (headerJson.Length <= 0 || headerJson.Length > _options.MaxHeaderLength)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidHeaderLength,
                $"Header length out of range: {headerJson.Length} (max {_options.MaxHeaderLength}).");
        }
        return headerJson;
    }

    private protected byte[] SerializePayload<T>(T message)
    {
        try
        {
            return _serializer.Serialize(message) ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            throw HyperionDiagnostics.RecordError(null,
                new HyperionProtocolException(HyperionErrorCode.SerializationFailed, $"Failed to serialize message of type {typeof(T)}.", ex),
                "send");
        }
    }

    private protected T DeserializePayload<T>(byte[] payload)
    {
        T? result;
        try
        {
            result = _serializer.Deserialize<T>(payload);
        }
        catch (Exception ex)
        {
            throw new HyperionProtocolException(HyperionErrorCode.SerializationFailed,
                $"Failed to deserialize a {payload.Length}-byte payload to {typeof(T)}.", ex);
        }

        if (result is null)
        {
            var target = typeof(T);
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
            {
                throw new HyperionProtocolException(HyperionErrorCode.SerializationFailed,
                    $"Deserialized result is null but target type {target} is a non-nullable value type.");
            }
            if (!target.IsValueType && target != typeof(string))
            {
                throw new HyperionProtocolException(HyperionErrorCode.SerializationFailed,
                    $"Deserialized result is null for reference type {target}.");
            }
        }

        return result!;
    }

    /// <summary>
    /// Validates an outbound message against <see cref="HyperionProtocolOptions.MaxMessageSize"/> before anything is written
    /// and returns the number of chunks it will take. Failures are counted on the error metric.
    /// </summary>
    private protected int PrepareSend(long payloadLength)
    {
        if (payloadLength > _options.MaxMessageSize)
        {
            throw HyperionDiagnostics.RecordError(null,
                new HyperionProtocolException(HyperionErrorCode.MessageTooLarge,
                    $"Message of {payloadLength} bytes exceeds MaxMessageSize ({_options.MaxMessageSize} bytes). Nothing was written."),
                "send");
        }

        long count = Math.Max(1, (payloadLength + _options.ChunkSize - 1) / _options.ChunkSize);
        if (count > int.MaxValue)
        {
            throw HyperionDiagnostics.RecordError(null,
                new HyperionProtocolException(HyperionErrorCode.MessageTooLarge,
                    $"A {payloadLength}-byte message needs {count} chunks of {_options.ChunkSize} bytes; the limit is {int.MaxValue}. Nothing was written."),
                "send");
        }
        return (int)count;
    }

    /// <summary>Throws <see cref="HyperionErrorCode.MessageTooLarge"/> for an announced inbound frame length.</summary>
    private protected void EnsureReceivable(long payloadLength)
    {
        if (payloadLength > _options.MaxMessageSize)
        {
            throw new HyperionProtocolException(HyperionErrorCode.MessageTooLarge,
                $"Incoming message of {payloadLength} bytes exceeds MaxMessageSize ({_options.MaxMessageSize} bytes).");
        }
    }

    private protected static void ValidateWritable(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite) throw new InvalidOperationException("Stream is not writable.");
    }

    private protected static void ValidateReadable(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new InvalidOperationException("Stream is not readable.");
    }

    private protected static HyperionProtocolException TimeoutError(OperationGuard guard, string operation)
    {
        var message = $"{operation} timed out: no progress within {guard.IdleTimeout}. The connection is in an undefined state and must be closed.";
        return new HyperionProtocolException(HyperionErrorCode.Timeout, message, new TimeoutException(message));
    }

    private protected static HyperionProtocolException EndOfStreamError(int read, int expected, string what, bool atMessageBoundary)
    {
        if (read == 0 && atMessageBoundary)
        {
            const string message = "The peer closed the connection; no further messages.";
            return new HyperionProtocolException(HyperionErrorCode.PeerClosed, message, new EndOfStreamException(message));
        }

        var text = $"Stream ended while reading {what} ({read} of {expected} bytes).";
        return new HyperionProtocolException(HyperionErrorCode.UnexpectedEndOfStream, text, new EndOfStreamException(text));
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends; returns the number of bytes read.</summary>
    private static async ValueTask<int> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.Slice(filled), ct).ConfigureAwait(false);
            if (n == 0) break;
            filled += n;
        }
        return filled;
    }

    /// <summary>Per-message reassembly state.</summary>
    private protected sealed class ReassemblyState
    {
        public Guid? PacketId;
        public int TotalChunks = int.MaxValue;
        public int Received;
        public int FirstChunkLength;
        public long TotalBytes;

        public bool IsComplete => Received >= TotalChunks;
    }

    /// <summary>Uniform "read exactly N bytes" over either a <see cref="Stream"/> or a <see cref="PipeReader"/>, under an <see cref="OperationGuard"/>.</summary>
    private protected readonly struct ByteSource
    {
        private readonly Stream? _stream;
        private readonly PipeReader? _reader;

        public ByteSource(Stream stream)
        {
            _stream = stream;
            _reader = null;
        }

        public ByteSource(PipeReader reader)
        {
            _reader = reader;
            _stream = null;
        }

        /// <summary>Returns the number of bytes read; fewer than <c>buffer.Length</c> only when the source ended.</summary>
        public ValueTask<int> ReadExactlyAsync(Memory<byte> buffer, OperationGuard guard)
            => _stream is not null
                ? ReadFromStreamAsync(_stream, buffer, guard)
                : ReadFromPipeAsync(_reader!, buffer, guard);

        private static async ValueTask<int> ReadFromStreamAsync(Stream stream, Memory<byte> buffer, OperationGuard guard)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                guard.Arm();
                int n = await stream.ReadAsync(buffer.Slice(filled), guard.Token).ConfigureAwait(false);
                guard.Disarm();

                if (n == 0) break;
                filled += n;
            }
            return filled;
        }

        private static async ValueTask<int> ReadFromPipeAsync(PipeReader reader, Memory<byte> buffer, OperationGuard guard)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                guard.Arm();
                var result = await reader.ReadAsync(guard.Token).ConfigureAwait(false);
                guard.Disarm();

                var available = result.Buffer;
                if (result.IsCanceled)
                {
                    reader.AdvanceTo(available.Start);
                    throw new OperationCanceledException("The pending read was canceled.");
                }

                if (available.IsEmpty)
                {
                    reader.AdvanceTo(available.Start, available.End);
                    if (result.IsCompleted) break;
                    continue;
                }

                int take = (int)Math.Min(buffer.Length - filled, available.Length);
                var slice = available.Slice(0, take);
                slice.CopyTo(buffer.Span.Slice(filled, take));
                filled += take;
                reader.AdvanceTo(slice.End);
            }
            return filled;
        }
    }

    #endregion

    #region Validation & helpers

    /// <summary>Validates a chunk-header length against <see cref="HyperionProtocolOptions.MaxHeaderLength"/>.</summary>
    public static void ValidateHeaderLength(int headerLength, int maxHeaderLength)
    {
        if (headerLength <= 0 || headerLength > maxHeaderLength)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidHeaderLength,
                $"Invalid header length: {headerLength} (expected 1..{maxHeaderLength}).");
        }
    }

    /// <summary>Deserializes the JSON header from a span. Unknown members are ignored.</summary>
    public static PacketHeader DeserializeHeader(ReadOnlySpan<byte> headerBytes)
    {
        try
        {
            return JsonSerializer.Deserialize(headerBytes, HyperionJsonContext.Default.PacketHeader)
                ?? throw new HyperionProtocolException(HyperionErrorCode.MalformedHeader, "Header deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new HyperionProtocolException(HyperionErrorCode.MalformedHeader, "Failed to deserialize packet header.", ex);
        }
    }

    /// <summary>Validates an incoming chunk header against accumulated state.</summary>
    public static void ValidateHeader(
        PacketHeader header,
        Guid? expectedPacketId,
        int expectedTotalChunks,
        int receivedChunkCount,
        int chunkSize)
    {
        if (header is null) throw new ArgumentNullException(nameof(header));

        if (header.Magic != ProtocolMagic)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidMagic,
                $"Invalid protocol magic. Expected '{ProtocolMagic}', got '{header.Magic}'.");
        }

        // Version 0 is legacy (pre-0.3.0 senders that did not emit the field).
        int effectiveVersion = header.Version == 0 ? 1 : header.Version;
        if (effectiveVersion < MinSupportedProtocolVersion || effectiveVersion > ProtocolVersion)
        {
            throw new HyperionProtocolException(HyperionErrorCode.UnsupportedVersion,
                $"Unsupported protocol version {header.Version}. This library accepts {MinSupportedProtocolVersion}..{ProtocolVersion}.");
        }

        if (header.TotalChunks <= 0)
            throw new HyperionProtocolException(HyperionErrorCode.InvalidChunkCount, $"Invalid total chunks: {header.TotalChunks}.");

        if (header.ChunkNumber < 0 || header.ChunkNumber >= header.TotalChunks)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidChunkNumber,
                $"Invalid chunk number {header.ChunkNumber} for {header.TotalChunks} total chunks.");
        }

        if (header.DataLength < 0 || header.DataLength > chunkSize)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidDataLength,
                $"Invalid data length: {header.DataLength}. Max: {chunkSize}.");
        }

        bool isLastChunk = header.ChunkNumber == header.TotalChunks - 1;
        if (header.IsLastChunk != isLastChunk)
            throw new HyperionProtocolException(HyperionErrorCode.EndFlagMismatch, "End flag mismatch with chunk position.");

        if (expectedPacketId.HasValue)
        {
            if (header.PacketId != expectedPacketId)
                throw new HyperionProtocolException(HyperionErrorCode.PacketIdMismatch, "Packet ID mismatch between chunks.");

            if (header.TotalChunks != expectedTotalChunks)
                throw new HyperionProtocolException(HyperionErrorCode.InvalidChunkCount, "Total chunks mismatch between chunks.");
        }

        if (header.ChunkNumber != receivedChunkCount)
        {
            throw new HyperionProtocolException(HyperionErrorCode.ChunkOutOfOrder,
                $"Chunk received out of order. Expected {receivedChunkCount}, got {header.ChunkNumber}.");
        }
    }

    /// <summary>Concatenates ordered chunk payloads into a single byte array.</summary>
    public static byte[] CombineChunks(List<ChunkData> chunks)
    {
        if (chunks is null) throw new ArgumentNullException(nameof(chunks));

        if (chunks.Count == 1)
            return chunks[0].Data;

        long totalLength = 0;
        foreach (var chunk in chunks)
            totalLength += chunk.Data.Length;

        var result = new byte[totalLength];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Data.CopyTo(result, offset);
            offset += chunk.Data.Length;
        }
        return result;
    }

    /// <summary>
    /// Reads exactly <c>buffer.Length</c> bytes into <paramref name="buffer"/>.
    /// Returns false if EOF is encountered before the buffer is completely filled.
    /// </summary>
    public static Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
        => ReadExactlyAsync(stream, buffer.AsMemory(), ct);

    /// <summary>
    /// Reads exactly <c>buffer.Length</c> bytes into <paramref name="buffer"/> (memory overload).
    /// Returns false if EOF is encountered before the buffer is completely filled.
    /// </summary>
    public static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        return await ReadExactlyOrEofAsync(stream, buffer, ct).ConfigureAwait(false) == buffer.Length;
    }

    #endregion
}
