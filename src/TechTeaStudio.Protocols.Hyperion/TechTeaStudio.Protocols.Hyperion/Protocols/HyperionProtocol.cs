using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Chunked TCP messaging protocol. Splits a serialized payload into fixed-size chunks,
/// frames each chunk with a JSON <see cref="PacketHeader"/>, and writes them to a stream
/// in order. The receiver validates magic/version/order/end-flag and reassembles the payload.
/// </summary>
public class HyperionProtocol
{
    /// <summary>Protocol magic identifier ("TTS").</summary>
    public const string ProtocolMagic = "TTS";

    /// <summary>Current wire-protocol revision emitted by this library.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Minimum wire-protocol revision this library accepts. <c>0</c> means legacy/unversioned.</summary>
    public const int MinSupportedProtocolVersion = 0;

    private const uint HandshakeMagic = 0x54545348; // "TTSH" big-endian

    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    /// <summary>Underlying serializer used for payloads.</summary>
    protected readonly ISerializer _serializer;

    /// <summary>Active protocol options (chunk size, header limits).</summary>
    protected readonly HyperionProtocolOptions _options;

    /// <summary>Maximum payload bytes per chunk.</summary>
    public int ChunkSize => _options.ChunkSize;

    /// <summary>Maximum JSON header length in bytes.</summary>
    public int MaxHeaderLength => _options.MaxHeaderLength;

    /// <summary>Creates a protocol using <see cref="HyperionProtocolOptions.Default"/>.</summary>
    public HyperionProtocol(ISerializer serializer)
        : this(serializer, HyperionProtocolOptions.Default) { }

    /// <summary>Creates a protocol with custom options.</summary>
    public HyperionProtocol(ISerializer serializer, HyperionProtocolOptions options)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    #region NetworkStream API

    /// <summary>Serializes <paramref name="message"/> and sends it as one or more framed chunks.</summary>
    public virtual async Task SendAsync<T>(T message, NetworkStream stream, CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite)
            throw new InvalidOperationException("Stream is not writable.");

        var data = _serializer.Serialize(message) ?? Array.Empty<byte>();
        int chunkSize = _options.ChunkSize;
        int totalChunks = Math.Max(1, (int)Math.Ceiling((double)data.Length / chunkSize));
        var packetId = Guid.NewGuid();

        try
        {
            for (int i = 0; i < totalChunks; i++)
            {
                ct.ThrowIfCancellationRequested();

                int offset = i * chunkSize;
                int size = Math.Min(chunkSize, data.Length - offset);

                var header = BuildHeader(packetId, i, totalChunks, size);
                await SendChunkAsync(stream, header, data.AsMemory(offset, size), ct).ConfigureAwait(false);
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HyperionProtocolException("Failed to send message", ex);
        }
    }

    /// <summary>Writes a single framed chunk: [headerLength:4 BE][headerJson][payload].</summary>
    public virtual async Task SendChunkAsync(NetworkStream stream, PacketHeader header, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header);

        if (headerJson.Length <= 0 || headerJson.Length > _options.MaxHeaderLength)
            throw new HyperionProtocolException($"Header length out of range: {headerJson.Length}");

        var headerLengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(headerLengthBytes, headerJson.Length);

        await stream.WriteAsync(headerLengthBytes, 0, 4, ct).ConfigureAwait(false);
        await stream.WriteAsync(headerJson, 0, headerJson.Length, ct).ConfigureAwait(false);

        if (data.Length > 0)
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    /// <summary>Receives chunks until the full payload is assembled and deserialized to <typeparamref name="T"/>.</summary>
    public virtual async Task<T> ReceiveAsync<T>(NetworkStream stream, CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead)
            throw new InvalidOperationException("Stream is not readable.");

        try
        {
            var chunks = await ReceiveChunksAsync(stream, firstHeaderLengthBuffer: null, ct).ConfigureAwait(false);
            var completeData = CombineChunks(chunks);

            var result = _serializer.Deserialize<T>(completeData);

            if (result is null)
            {
                if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
                    throw new HyperionProtocolException("Deserialized result is null but target type is non-nullable value type.");

                if (!typeof(T).IsValueType && typeof(T) != typeof(string))
                    throw new HyperionProtocolException("Deserialized result is null for reference type.");
            }

            return result!;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or HyperionProtocolException))
        {
            throw new HyperionProtocolException("Failed to receive message", ex);
        }
    }

    /// <summary>
    /// Yields each chunk payload as it arrives, without buffering the full message in memory.
    /// Use this for very large payloads that you want to stream to disk or another stream.
    /// </summary>
    /// <remarks>
    /// Validation (magic, version, chunk order, end flag, packet-id continuity) is performed
    /// before each <c>yield</c>. If a chunk is invalid the enumerator throws
    /// <see cref="HyperionProtocolException"/>.
    /// </remarks>
    public virtual async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(
        NetworkStream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead)
            throw new InvalidOperationException("Stream is not readable.");

        var headerLengthBuf = new byte[4];
        int totalChunks = int.MaxValue;
        int receivedCount = 0;
        Guid? expectedPacketId = null;

        while (receivedCount < totalChunks)
        {
            ct.ThrowIfCancellationRequested();

            if (!await ReadExactlyAsync(stream, headerLengthBuf, ct).ConfigureAwait(false))
                throw new EndOfStreamException("Stream ended while reading header length.");

            int headerLength = BinaryPrimitives.ReadInt32BigEndian(headerLengthBuf);
            ValidateHeaderLength(headerLength, _options.MaxHeaderLength);

            PacketHeader header;
            var headerBytes = BufferPool.Rent(headerLength);
            try
            {
                var headerMemory = headerBytes.AsMemory(0, headerLength);
                if (!await ReadExactlyAsync(stream, headerMemory, ct).ConfigureAwait(false))
                    throw new EndOfStreamException($"Stream ended while reading {headerLength}-byte header.");

                header = DeserializeHeader(headerMemory.Span);
            }
            finally
            {
                BufferPool.Return(headerBytes);
            }

            ValidateHeader(header, expectedPacketId, totalChunks, receivedCount, _options.ChunkSize);

            if (expectedPacketId is null)
            {
                expectedPacketId = header.PacketId;
                totalChunks = header.TotalChunks;
            }

            byte[] data;
            if (header.DataLength > 0)
            {
                data = new byte[header.DataLength];
                if (!await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
                    throw new EndOfStreamException($"Stream ended while reading {header.DataLength}-byte payload.");
            }
            else
            {
                data = Array.Empty<byte>();
            }

            yield return data;
            receivedCount++;
        }
    }

    /// <summary>
    /// Reads all chunks from <paramref name="stream"/>. When <paramref name="firstHeaderLengthBuffer"/>
    /// is non-null, the first 4 bytes of the first chunk are taken from it instead of reading from the stream
    /// (used by <see cref="Protocols.SmartHyperionProtocol"/> after it peeks one byte for mode detection).
    /// </summary>
    protected internal async Task<List<ChunkData>> ReceiveChunksAsync(
        NetworkStream stream,
        byte[]? firstHeaderLengthBuffer,
        CancellationToken ct)
    {
        var chunks = new List<ChunkData>();
        var headerLengthBuf = new byte[4];

        int totalChunks = int.MaxValue;
        Guid? expectedPacketId = null;

        while (chunks.Count < totalChunks)
        {
            ct.ThrowIfCancellationRequested();

            if (chunks.Count == 0 && firstHeaderLengthBuffer is not null)
            {
                Buffer.BlockCopy(firstHeaderLengthBuffer, 0, headerLengthBuf, 0, 4);
            }
            else
            {
                if (!await ReadExactlyAsync(stream, headerLengthBuf, ct).ConfigureAwait(false))
                    throw new EndOfStreamException("Stream ended while reading header length.");
            }

            int headerLength = BinaryPrimitives.ReadInt32BigEndian(headerLengthBuf);
            ValidateHeaderLength(headerLength, _options.MaxHeaderLength);

            var headerBytes = BufferPool.Rent(headerLength);
            try
            {
                var headerMemory = headerBytes.AsMemory(0, headerLength);
                if (!await ReadExactlyAsync(stream, headerMemory, ct).ConfigureAwait(false))
                    throw new EndOfStreamException($"Stream ended while reading {headerLength}-byte header.");

                var header = DeserializeHeader(headerMemory.Span);
                ValidateHeader(header, expectedPacketId, totalChunks, chunks.Count, _options.ChunkSize);

                if (expectedPacketId is null)
                {
                    expectedPacketId = header.PacketId;
                    totalChunks = header.TotalChunks;
                }

                byte[] data;
                if (header.DataLength > 0)
                {
                    data = new byte[header.DataLength];
                    if (!await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
                        throw new EndOfStreamException($"Stream ended while reading {header.DataLength}-byte payload.");
                }
                else
                {
                    data = Array.Empty<byte>();
                }

                chunks.Add(new ChunkData(header.ChunkNumber, data));
            }
            finally
            {
                BufferPool.Return(headerBytes);
            }
        }

        return chunks;
    }

    #endregion

    #region Pipelines API

    /// <summary>Serializes <paramref name="message"/> and writes it through a <see cref="PipeWriter"/>.</summary>
    public virtual async Task SendAsync<T>(T message, PipeWriter writer, CancellationToken ct = default)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var data = _serializer.Serialize(message) ?? Array.Empty<byte>();
        int chunkSize = _options.ChunkSize;
        int totalChunks = Math.Max(1, (int)Math.Ceiling((double)data.Length / chunkSize));
        var packetId = Guid.NewGuid();

        try
        {
            for (int i = 0; i < totalChunks; i++)
            {
                ct.ThrowIfCancellationRequested();

                int offset = i * chunkSize;
                int size = Math.Min(chunkSize, data.Length - offset);
                var header = BuildHeader(packetId, i, totalChunks, size);

                var headerJson = JsonSerializer.SerializeToUtf8Bytes(header);
                if (headerJson.Length <= 0 || headerJson.Length > _options.MaxHeaderLength)
                    throw new HyperionProtocolException($"Header length out of range: {headerJson.Length}");

                var lenSpan = writer.GetSpan(4);
                BinaryPrimitives.WriteInt32BigEndian(lenSpan, headerJson.Length);
                writer.Advance(4);

                var hSpan = writer.GetSpan(headerJson.Length);
                headerJson.CopyTo(hSpan);
                writer.Advance(headerJson.Length);

                if (size > 0)
                {
                    var dSpan = writer.GetSpan(size);
                    data.AsSpan(offset, size).CopyTo(dSpan);
                    writer.Advance(size);
                }

                var flush = await writer.FlushAsync(ct).ConfigureAwait(false);
                if (flush.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HyperionProtocolException("Failed to send via PipeWriter", ex);
        }
    }

    /// <summary>Reads chunks from a <see cref="PipeReader"/> and deserializes to <typeparamref name="T"/>.</summary>
    public virtual async Task<T> ReceiveAsync<T>(PipeReader reader, CancellationToken ct = default)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        try
        {
            var payload = await ReadChunkedPayloadAsync(reader, ct).ConfigureAwait(false);
            var result = _serializer.Deserialize<T>(payload);
            if (result is null)
            {
                if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
                    throw new HyperionProtocolException("Deserialized result is null but target type is non-nullable value type.");
                if (!typeof(T).IsValueType && typeof(T) != typeof(string))
                    throw new HyperionProtocolException("Deserialized result is null for reference type.");
            }
            return result!;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or HyperionProtocolException))
        {
            throw new HyperionProtocolException("Failed to receive via PipeReader", ex);
        }
    }

    private async Task<byte[]> ReadChunkedPayloadAsync(PipeReader reader, CancellationToken ct)
    {
        var chunks = new List<ChunkData>();
        int totalChunks = int.MaxValue;
        Guid? expectedPacketId = null;

        while (chunks.Count < totalChunks)
        {
            ct.ThrowIfCancellationRequested();

            int headerLength = await ReadInt32BigEndianFromPipeAsync(reader, ct).ConfigureAwait(false);
            ValidateHeaderLength(headerLength, _options.MaxHeaderLength);

            var headerBuf = BufferPool.Rent(headerLength);
            PacketHeader header;
            try
            {
                await ReadExactlyFromPipeAsync(reader, headerBuf.AsMemory(0, headerLength), ct).ConfigureAwait(false);
                header = DeserializeHeader(headerBuf.AsSpan(0, headerLength));
            }
            finally
            {
                BufferPool.Return(headerBuf);
            }

            ValidateHeader(header, expectedPacketId, totalChunks, chunks.Count, _options.ChunkSize);
            if (expectedPacketId is null)
            {
                expectedPacketId = header.PacketId;
                totalChunks = header.TotalChunks;
            }

            byte[] data;
            if (header.DataLength > 0)
            {
                data = new byte[header.DataLength];
                await ReadExactlyFromPipeAsync(reader, data, ct).ConfigureAwait(false);
            }
            else
            {
                data = Array.Empty<byte>();
            }

            chunks.Add(new ChunkData(header.ChunkNumber, data));
        }

        return CombineChunks(chunks);
    }

    private static async Task<int> ReadInt32BigEndianFromPipeAsync(PipeReader reader, CancellationToken ct)
    {
        var buf = new byte[4];
        await ReadExactlyFromPipeAsync(reader, buf, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(buf);
    }

    private static async Task ReadExactlyFromPipeAsync(PipeReader reader, Memory<byte> dest, CancellationToken ct)
    {
        int filled = 0;
        while (filled < dest.Length)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty)
            {
                if (result.IsCompleted)
                    throw new EndOfStreamException("Pipe ended before requested bytes were read.");
                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            int need = dest.Length - filled;
            int take = (int)Math.Min(need, buffer.Length);
            var slice = buffer.Slice(0, take);
            slice.CopyTo(dest.Span.Slice(filled, take));
            filled += take;
            reader.AdvanceTo(slice.End);
        }
    }

    #endregion

    #region Handshake

    /// <summary>
    /// Performs a simple version handshake over <paramref name="stream"/>: writes
    /// <c>[magic:4 "TTSH"][version:4 BE]</c> and reads the same from the peer, returning the
    /// minimum of the two versions (the version both sides agree to speak).
    /// </summary>
    /// <param name="stream">A bidirectional, already-connected stream.</param>
    /// <param name="localVersion">Local protocol version. Defaults to <see cref="ProtocolVersion"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Negotiated version: <c>min(localVersion, remoteVersion)</c>.</returns>
    public static async Task<int> HandshakeAsync(NetworkStream stream, int? localVersion = null, CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        int local = localVersion ?? ProtocolVersion;

        var outBuf = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(outBuf, HandshakeMagic);
        BinaryPrimitives.WriteInt32BigEndian(outBuf.AsSpan(4), local);

        var sendTask = stream.WriteAsync(outBuf, 0, 8, ct);
        var inBuf = new byte[8];
        var recvTask = ReadExactlyAsync(stream, inBuf, ct);

        await sendTask.ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        if (!await recvTask.ConfigureAwait(false))
            throw new EndOfStreamException("Stream ended during handshake.");

        var remoteMagic = BinaryPrimitives.ReadUInt32BigEndian(inBuf);
        if (remoteMagic != HandshakeMagic)
            throw new HyperionProtocolException(
                $"Handshake magic mismatch. Expected 0x{HandshakeMagic:X8}, got 0x{remoteMagic:X8}.");

        var remoteVersion = BinaryPrimitives.ReadInt32BigEndian(inBuf.AsSpan(4));
        if (remoteVersion < MinSupportedProtocolVersion)
            throw new HyperionProtocolException(
                $"Remote protocol version {remoteVersion} is below the minimum supported version {MinSupportedProtocolVersion}.");

        return Math.Min(local, remoteVersion);
    }

    #endregion

    #region Validation & helpers

    private static PacketHeader BuildHeader(Guid packetId, int chunkIndex, int totalChunks, int size)
    {
        return new PacketHeader
        {
            Version = ProtocolVersion,
            Magic = ProtocolMagic,
            PacketId = packetId,
            ChunkNumber = chunkIndex,
            TotalChunks = totalChunks,
            DataLength = size,
            Flags = (byte)(chunkIndex == totalChunks - 1 ? 1 : 0)
        };
    }

    /// <summary>Validates a chunk-header length against <see cref="HyperionProtocolOptions.MaxHeaderLength"/>.</summary>
    public static void ValidateHeaderLength(int headerLength, int maxHeaderLength)
    {
        if (headerLength <= 0 || headerLength > maxHeaderLength)
            throw new HyperionProtocolException($"Invalid header length: {headerLength}");
    }

    /// <summary>Deserializes the JSON header from a span.</summary>
    public static PacketHeader DeserializeHeader(ReadOnlySpan<byte> headerBytes)
    {
        try
        {
            return JsonSerializer.Deserialize<PacketHeader>(headerBytes)
                ?? throw new HyperionProtocolException("Header deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new HyperionProtocolException("Failed to deserialize packet header.", ex);
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
        if (header.Magic != ProtocolMagic)
            throw new HyperionProtocolException($"Invalid protocol magic. Expected '{ProtocolMagic}', got '{header.Magic}'.");

        // Version 0 is legacy (pre-0.3.0 senders that did not emit the field).
        int effectiveVersion = header.Version == 0 ? 1 : header.Version;
        if (effectiveVersion < MinSupportedProtocolVersion || effectiveVersion > ProtocolVersion)
            throw new HyperionProtocolException(
                $"Unsupported protocol version {header.Version}. This library accepts {MinSupportedProtocolVersion}..{ProtocolVersion}.");

        if (header.TotalChunks <= 0)
            throw new HyperionProtocolException($"Invalid total chunks: {header.TotalChunks}");

        if (header.ChunkNumber < 0 || header.ChunkNumber >= header.TotalChunks)
            throw new HyperionProtocolException($"Invalid chunk number {header.ChunkNumber} for {header.TotalChunks} total chunks.");

        if (header.DataLength < 0 || header.DataLength > chunkSize)
            throw new HyperionProtocolException($"Invalid data length: {header.DataLength}. Max: {chunkSize}");

        bool isLastChunk = header.ChunkNumber == header.TotalChunks - 1;
        bool hasEndFlag = (header.Flags & 1) != 0;
        if (hasEndFlag != isLastChunk)
            throw new HyperionProtocolException("End flag mismatch with chunk position.");

        if (expectedPacketId.HasValue)
        {
            if (header.PacketId != expectedPacketId)
                throw new HyperionProtocolException("Packet ID mismatch between chunks.");

            if (header.TotalChunks != expectedTotalChunks)
                throw new HyperionProtocolException("Total chunks mismatch between chunks.");
        }

        if (header.ChunkNumber != receivedChunkCount)
            throw new HyperionProtocolException($"Chunk received out of order. Expected {receivedChunkCount}, got {header.ChunkNumber}.");
    }

    /// <summary>Concatenates ordered chunk payloads into a single byte array.</summary>
    public static byte[] CombineChunks(List<ChunkData> chunks)
    {
        int totalLength = 0;
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
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.Slice(totalRead), ct).ConfigureAwait(false);
            if (bytesRead == 0) return false;
            totalRead += bytesRead;
        }
        return true;
    }

    #endregion
}
