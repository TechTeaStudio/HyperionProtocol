using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;

namespace TechTeaStudio.Protocols.Hyperion.Protocols;

/// <summary>
/// Adaptive protocol that picks the smallest framing for each payload:
/// <list type="bullet">
///   <item><description>&lt; 1 KiB — lightweight: [magic:1=0xFF][length:2 BE][data]</description></item>
///   <item><description>&lt; 64 KiB — direct: [magic:1=0xFE][length:4 BE][data]</description></item>
///   <item><description>otherwise — chunked: same wire format as <see cref="HyperionProtocol"/></description></item>
/// </list>
/// The chunked path starts with a 4-byte big-endian header length whose first byte is always
/// in the low range (header is JSON, never starts with 0xFE/0xFF), so the receiver can disambiguate
/// by reading just one byte.
/// </summary>
public class SmartHyperionProtocol : HyperionProtocol
{
    private const int LightweightThreshold = 1024;      // < 1 KiB
    private const int DirectThreshold = 64 * 1024;      // < 64 KiB
    private const byte LightweightMagic = 0xFF;
    private const byte DirectMagic = 0xFE;

    /// <summary>Counters for picked framing modes; updated on each successful send.</summary>
    public ProtocolStats Stats { get; } = new();

    /// <inheritdoc />
    public SmartHyperionProtocol(ISerializer serializer)
        : base(serializer) { }

    /// <inheritdoc />
    public SmartHyperionProtocol(ISerializer serializer, HyperionProtocolOptions options)
        : base(serializer, options) { }

    /// <inheritdoc />
    public override async Task SendAsync<T>(T message, NetworkStream stream, CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite)
            throw new InvalidOperationException("Stream is not writable.");

        var data = _serializer.Serialize(message) ?? Array.Empty<byte>();

        try
        {
            if (data.Length < LightweightThreshold)
            {
                await SendLightweightAsync(data, stream, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _lightweight);
                // Saved vs chunked: 4 (length) + ~150 (JSON header) ≈ 154; lightweight uses 3 bytes.
                Interlocked.Add(ref _bytesSaved, Math.Max(0, 154 - 3));
            }
            else if (data.Length < DirectThreshold)
            {
                await SendDirectAsync(data, stream, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _direct);
                // Saved vs chunked: ~154; direct uses 5 bytes.
                Interlocked.Add(ref _bytesSaved, Math.Max(0, 154 - 5));
            }
            else
            {
                await base.SendAsync(message, stream, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _chunked);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HyperionProtocolException("Failed to send message via smart protocol", ex);
        }
    }

    private long _lightweight;
    private long _direct;
    private long _chunked;
    private long _bytesSaved;

    /// <summary>Refreshes <see cref="Stats"/> from internal counters.</summary>
    private void SyncStats()
    {
        Stats.LightweightMessagesSent = Interlocked.Read(ref _lightweight);
        Stats.DirectMessagesSent = Interlocked.Read(ref _direct);
        Stats.ChunkedMessagesSent = Interlocked.Read(ref _chunked);
        Stats.TotalBytesSaved = Interlocked.Read(ref _bytesSaved);
    }

    /// <inheritdoc />
    public override async Task<T> ReceiveAsync<T>(NetworkStream stream, CancellationToken ct = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead)
            throw new InvalidOperationException("Stream is not readable.");

        try
        {
            var modeBuffer = new byte[1];
            if (!await ReadExactlyAsync(stream, modeBuffer, ct).ConfigureAwait(false))
                throw new EndOfStreamException("Stream ended while reading mode byte.");

            var mode = modeBuffer[0];

            if (mode == LightweightMagic)
                return await ReceiveLightweightAsync<T>(stream, ct).ConfigureAwait(false);

            if (mode == DirectMagic)
                return await ReceiveDirectAsync<T>(stream, ct).ConfigureAwait(false);

            // Chunked: that first byte is actually the high byte of header-length:4.
            var headerLengthBuffer = new byte[4];
            headerLengthBuffer[0] = mode;
            if (!await ReadExactlyAsync(stream, headerLengthBuffer.AsMemory(1, 3), ct).ConfigureAwait(false))
                throw new EndOfStreamException("Stream ended while reading header length.");

            var chunks = await ReceiveChunksAsync(stream, headerLengthBuffer, ct).ConfigureAwait(false);
            var completeData = CombineChunks(chunks);
            var result = _serializer.Deserialize<T>(completeData);
            return result ?? throw new HyperionProtocolException("Chunked deserialization returned null.");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or HyperionProtocolException))
        {
            throw new HyperionProtocolException("Failed to receive message via smart protocol", ex);
        }
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

    #region Lightweight (< 1 KiB)

    private static async Task SendLightweightAsync(byte[] data, NetworkStream stream, CancellationToken ct)
    {
        if (data.Length >= LightweightThreshold)
            throw new ArgumentException($"Data too large for lightweight mode: {data.Length}");

        // [magic:1][length:2 BE][data:N]
        var buffer = ArrayPool<byte>.Shared.Rent(3 + data.Length);
        try
        {
            var span = buffer.AsSpan(0, 3 + data.Length);
            span[0] = LightweightMagic;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(1), (ushort)data.Length);
            data.CopyTo(span.Slice(3));

            await stream.WriteAsync(buffer, 0, 3 + data.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<T> ReceiveLightweightAsync<T>(NetworkStream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[2];
        if (!await ReadExactlyAsync(stream, lengthBuffer, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream ended while reading lightweight length.");

        var dataLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);

        var data = new byte[dataLength];
        if (dataLength > 0 && !await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream ended while reading lightweight data.");

        var result = _serializer.Deserialize<T>(data);
        return result ?? throw new HyperionProtocolException("Lightweight deserialization returned null.");
    }

    #endregion

    #region Direct (< 64 KiB)

    private static async Task SendDirectAsync(byte[] data, NetworkStream stream, CancellationToken ct)
    {
        if (data.Length >= DirectThreshold)
            throw new ArgumentException($"Data too large for direct mode: {data.Length}");

        // [magic:1][length:4 BE][data:N]
        var headerBuffer = new byte[5];
        headerBuffer[0] = DirectMagic;
        BinaryPrimitives.WriteInt32BigEndian(headerBuffer.AsSpan(1), data.Length);

        await stream.WriteAsync(headerBuffer, 0, 5, ct).ConfigureAwait(false);

        if (data.Length > 0)
            await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> ReceiveDirectAsync<T>(NetworkStream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];
        if (!await ReadExactlyAsync(stream, lengthBuffer, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream ended while reading direct length.");

        var dataLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

        if (dataLength < 0 || dataLength >= DirectThreshold)
            throw new HyperionProtocolException($"Invalid direct data length: {dataLength}");

        var data = new byte[dataLength];
        if (dataLength > 0 && !await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream ended while reading direct data.");

        var result = _serializer.Deserialize<T>(data);
        return result ?? throw new HyperionProtocolException("Direct deserialization returned null.");
    }

    #endregion
}
