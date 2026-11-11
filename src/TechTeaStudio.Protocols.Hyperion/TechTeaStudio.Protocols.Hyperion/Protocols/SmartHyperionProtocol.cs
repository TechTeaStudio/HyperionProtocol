using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace TechTeaStudio.Protocols.Hyperion.Protocols;

/// <summary>
/// Adaptive protocol that picks the smallest framing for each payload:
/// <list type="bullet">
///   <item><description>&lt; 1 KiB: lightweight <c>[magic:1 = 0xFF][length:2 BE][data]</c> (3 bytes of overhead)</description></item>
///   <item><description>&lt; 64 KiB: direct <c>[magic:1 = 0xFE][length:4 BE][data]</c> (5 bytes of overhead)</description></item>
///   <item><description>otherwise: chunked, the same wire format as <see cref="HyperionProtocol"/></description></item>
/// </list>
/// The chunked path starts with a 4-byte big-endian header length whose first byte can never be 0xFE/0xFF
/// (a header length is a positive <see cref="int"/>, so its high byte is at most 0x7F), which lets the receiver
/// pick the mode after reading a single byte.
/// </summary>
/// <remarks>
/// The <see cref="Stream"/> overloads of <c>SendAsync</c>, <c>ReceiveAsync</c> and <c>ReceiveStreamingAsync</c> speak all three
/// framings. The <see cref="System.IO.Pipelines"/> overloads are inherited unchanged and speak chunked framing only.
/// <para>Compression (<see cref="HyperionProtocolOptions.ChunkCompression"/>) applies to the chunked path only, which
/// this class inherits as is: with <see cref="ChunkCompressionMode.Compress"/> a payload of 64 KiB or more is
/// compressed chunk by chunk, while lightweight and direct frames are never compressed and stay byte-identical to
/// compression off. Through the <see cref="Stream"/> overloads <see cref="HyperionProtocolOptions.CompressionThreshold"/>
/// therefore only matters when it is above <see cref="DirectThreshold"/>.</para>
/// </remarks>
public class SmartHyperionProtocol : HyperionProtocol
{
    /// <summary>Payloads below this size (1 KiB) use lightweight framing.</summary>
    public const int LightweightThreshold = 1024;

    /// <summary>Payloads below this size (64 KiB) and at or above <see cref="LightweightThreshold"/> use direct framing.</summary>
    public const int DirectThreshold = 64 * 1024;

    /// <summary>First byte of a lightweight frame.</summary>
    public const byte LightweightMagic = 0xFF;

    /// <summary>First byte of a direct frame.</summary>
    public const byte DirectMagic = 0xFE;

    internal const string LightweightMode = "lightweight";
    internal const string DirectMode = "direct";

    // Rough size of a chunked frame's framing (4-byte prefix + JSON header) used for the TotalBytesSaved estimate.
    private const int EstimatedChunkedOverhead = 154;

    private long _lightweight;
    private long _direct;
    private long _chunked;
    private long _bytesSaved;

    /// <summary>Counters for picked framing modes; updated on each successful send.</summary>
    public ProtocolStats Stats { get; } = new();

    /// <inheritdoc />
    public SmartHyperionProtocol(ISerializer serializer)
        : base(serializer) { }

    /// <inheritdoc />
    public SmartHyperionProtocol(ISerializer serializer, HyperionProtocolOptions options)
        : base(serializer, options) { }

    /// <inheritdoc />
    public override async Task SendAsync<T>(T message, Stream stream, CancellationToken ct = default)
    {
        ValidateWritable(stream);

        var data = SerializePayload(message);
        PrepareSend(data.Length);

        if (data.Length < LightweightThreshold)
        {
            await SendSmallAsync(data, stream, lightweight: true, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _lightweight);
            Interlocked.Add(ref _bytesSaved, EstimatedChunkedOverhead - 3);
        }
        else if (data.Length < DirectThreshold)
        {
            await SendSmallAsync(data, stream, lightweight: false, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _direct);
            Interlocked.Add(ref _bytesSaved, EstimatedChunkedOverhead - 5);
        }
        else
        {
            await SendChunkedAsync(data, stream, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _chunked);
        }
    }

    /// <inheritdoc />
    public override async Task<T> ReceiveAsync<T>(Stream stream, CancellationToken ct = default)
    {
        ValidateReadable(stream);

        using var activity = HyperionDiagnostics.StartReceive();
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);
        try
        {
            var source = new ByteSource(stream);
            byte mode = await ReadModeByteAsync(source, guard).ConfigureAwait(false);

            if (mode is LightweightMagic or DirectMagic)
            {
                var payload = await ReadSmallPayloadAsync(source, mode, guard).ConfigureAwait(false);
                HyperionDiagnostics.RecordReceived(activity, ModeName(mode), payload.Length, 1);
                return DeserializePayload<T>(payload);
            }

            var preset = await ReadRemainingHeaderLengthAsync(source, mode, guard).ConfigureAwait(false);
            var chunks = await ReceiveChunksAsync(source, preset, new ReassemblyState(), guard).ConfigureAwait(false);
            var combined = CombineChunks(chunks);

            HyperionDiagnostics.RecordReceived(activity, ChunkedMode, combined.Length, chunks.Count);
            return DeserializePayload<T>(combined);
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
                new HyperionProtocolException(HyperionErrorCode.ReceiveFailed, "Failed to receive message via smart protocol.", ex), "receive");
        }
    }

    /// <summary>
    /// Yields the payload of the next message chunk by chunk. Lightweight and direct frames are yielded as a single
    /// item; chunked frames are yielded as they arrive, exactly like <see cref="HyperionProtocol.ReceiveStreamingAsync(Stream, CancellationToken)"/>.
    /// </summary>
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateReadable(stream);

        using var activity = HyperionDiagnostics.StartReceive();
        using var guard = OperationGuard.Create(_options.ReceiveTimeout, ct);
        var source = new ByteSource(stream);

        byte mode;
        byte[]? preset = null;
        byte[]? small = null;
        try
        {
            mode = await ReadModeByteAsync(source, guard).ConfigureAwait(false);

            if (mode is LightweightMagic or DirectMagic)
                small = await ReadSmallPayloadAsync(source, mode, guard).ConfigureAwait(false);
            else
                preset = await ReadRemainingHeaderLengthAsync(source, mode, guard).ConfigureAwait(false);
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
                new HyperionProtocolException(HyperionErrorCode.ReceiveFailed, "Failed to receive message via smart protocol.", ex), "receive");
        }

        if (small is not null)
        {
            HyperionDiagnostics.RecordReceived(activity, ModeName(mode), small.Length, 1);
            yield return small;
            yield break;
        }

        await foreach (var chunk in ReceiveStreamingCoreAsync(source, preset, activity, guard).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Returns a snapshot of <see cref="Stats"/>.</summary>
    public ProtocolStats GetStatsSnapshot()
    {
        SyncStats();
        return new ProtocolStats
        {
            LightweightMessagesSent = Stats.LightweightMessagesSent,
            DirectMessagesSent = Stats.DirectMessagesSent,
            ChunkedMessagesSent = Stats.ChunkedMessagesSent,
            TotalBytesSaved = Stats.TotalBytesSaved,
        };
    }

    /// <summary>Resets counters.</summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _lightweight, 0);
        Interlocked.Exchange(ref _direct, 0);
        Interlocked.Exchange(ref _chunked, 0);
        Interlocked.Exchange(ref _bytesSaved, 0);
        Stats.Reset();
    }

    /// <summary>Refreshes <see cref="Stats"/> from internal counters.</summary>
    private void SyncStats()
    {
        Stats.LightweightMessagesSent = Interlocked.Read(ref _lightweight);
        Stats.DirectMessagesSent = Interlocked.Read(ref _direct);
        Stats.ChunkedMessagesSent = Interlocked.Read(ref _chunked);
        Stats.TotalBytesSaved = Interlocked.Read(ref _bytesSaved);
    }

    #region Lightweight (< 1 KiB) and direct (< 64 KiB) frames

    /// <summary>Writes <c>[magic][length][data]</c> as a single transport write.</summary>
    private async Task SendSmallAsync(byte[] data, Stream stream, bool lightweight, CancellationToken ct)
    {
        string mode = lightweight ? LightweightMode : DirectMode;
        int prefixLength = lightweight ? 3 : 5;
        int frameLength = prefixLength + data.Length;

        using var activity = HyperionDiagnostics.StartSend(mode, data.Length);
        using var guard = OperationGuard.Create(_options.SendTimeout, ct);
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            if (lightweight)
            {
                frame[0] = LightweightMagic;
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)data.Length);
            }
            else
            {
                frame[0] = DirectMagic;
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), data.Length);
            }
            data.CopyTo(frame.AsSpan(prefixLength));

            guard.Arm();
            await stream.WriteAsync(frame.AsMemory(0, frameLength), guard.Token).ConfigureAwait(false);
            await stream.FlushAsync(guard.Token).ConfigureAwait(false);
            guard.Disarm();

            HyperionDiagnostics.RecordSent(mode, data.Length, 1);
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
                new HyperionProtocolException(HyperionErrorCode.SendFailed, "Failed to send message via smart protocol.", ex), "send");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Reads the first byte of a frame. EOF here is a clean disconnect.</summary>
    private static async ValueTask<byte> ReadModeByteAsync(ByteSource source, OperationGuard guard)
    {
        var modeBuffer = new byte[1];
        int read = await source.ReadExactlyAsync(modeBuffer, guard).ConfigureAwait(false);
        if (read < 1)
            throw EndOfStreamError(0, 1, "frame mode byte", atMessageBoundary: true);
        return modeBuffer[0];
    }

    /// <summary>Reads the length and payload of a lightweight or direct frame.</summary>
    private async ValueTask<byte[]> ReadSmallPayloadAsync(ByteSource source, byte mode, OperationGuard guard)
    {
        bool lightweight = mode == LightweightMagic;
        int lengthSize = lightweight ? 2 : 4;

        var lengthBuffer = new byte[lengthSize];
        int read = await source.ReadExactlyAsync(lengthBuffer, guard).ConfigureAwait(false);
        if (read < lengthSize)
            throw EndOfStreamError(read, lengthSize, $"{ModeName(mode)} length", atMessageBoundary: false);

        int dataLength = lightweight
            ? BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer)
            : BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        int limit = lightweight ? LightweightThreshold : DirectThreshold;

        if (dataLength < 0 || dataLength >= limit)
        {
            throw new HyperionProtocolException(HyperionErrorCode.InvalidDataLength,
                $"Invalid {ModeName(mode)} frame length: {dataLength} (expected 0..{limit - 1}).");
        }
        EnsureReceivable(dataLength);

        if (dataLength == 0)
            return Array.Empty<byte>();

        var data = new byte[dataLength];
        read = await source.ReadExactlyAsync(data, guard).ConfigureAwait(false);
        if (read < dataLength)
            throw EndOfStreamError(read, dataLength, $"{ModeName(mode)} payload", atMessageBoundary: false);

        return data;
    }

    /// <summary>The first byte was the high byte of a chunked header length; reads the remaining three.</summary>
    private static async ValueTask<byte[]> ReadRemainingHeaderLengthAsync(ByteSource source, byte firstByte, OperationGuard guard)
    {
        var headerLengthBuffer = new byte[4];
        headerLengthBuffer[0] = firstByte;

        int read = await source.ReadExactlyAsync(headerLengthBuffer.AsMemory(1, 3), guard).ConfigureAwait(false);
        if (read < 3)
            throw EndOfStreamError(1 + read, 4, "chunk header length", atMessageBoundary: false);

        return headerLengthBuffer;
    }

    private static string ModeName(byte mode) => mode == LightweightMagic ? LightweightMode : DirectMode;

    #endregion
}
