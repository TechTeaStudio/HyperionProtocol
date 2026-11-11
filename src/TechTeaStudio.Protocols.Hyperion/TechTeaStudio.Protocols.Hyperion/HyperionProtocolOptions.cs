namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Configuration for <see cref="HyperionProtocol"/> and its derivatives. Immutable after construction.
/// </summary>
/// <remarks>
/// <para>Both ends of a connection must agree on <see cref="ChunkSize"/> and <see cref="MaxHeaderLength"/>:
/// a receiver rejects chunks whose <c>DataLength</c> exceeds its <see cref="ChunkSize"/> and headers larger
/// than its <see cref="MaxHeaderLength"/>. (A sender with a <i>smaller</i> chunk size than the receiver is fine.)</para>
/// <para><see cref="MaxMessageSize"/>, the timeouts and <see cref="ChunkChecksum"/> are local policy and may differ per side.
/// <see cref="ChunkCompression"/> is local policy too, with one constraint: a side set to
/// <see cref="ChunkCompressionMode.Compress"/> needs a peer that is not <see cref="ChunkCompressionMode.Off"/>.</para>
/// </remarks>
public sealed class HyperionProtocolOptions
{
    /// <summary>Default chunk size: 1 MiB.</summary>
    public const int DefaultChunkSize = 1024 * 1024;

    /// <summary>Default maximum header length: 64 KiB.</summary>
    public const int DefaultMaxHeaderLength = 64 * 1024;

    /// <summary>Default maximum message size: 1 GiB.</summary>
    public const long DefaultMaxMessageSize = 1024L * 1024 * 1024;

    /// <summary>Maximum payload bytes per chunk. Must be &gt; 0. Default: 1 MiB.</summary>
    public int ChunkSize { get; init; } = DefaultChunkSize;

    /// <summary>Maximum JSON header length in bytes. Must be &gt; 0. Default: 64 KiB.</summary>
    public int MaxHeaderLength { get; init; } = DefaultMaxHeaderLength;

    /// <summary>
    /// Maximum size of one logical message (sum of its chunk payloads, uncompressed). Must be &gt; 0. Default: 1 GiB.
    /// Enforced on send (before anything is written) and on receive (before payload bytes are read, using
    /// the chunk count announced in the first header, and again as chunks accumulate). Applies to every
    /// receive path including <see cref="HyperionProtocol.ReceiveStreamingAsync(Stream, CancellationToken)"/>; raise it (up to
    /// <see cref="long.MaxValue"/>) for larger streamed transfers. Buffered receives are additionally bound
    /// by the 2 GiB array limit of the runtime. With <see cref="ChunkCompression"/> accepting compressed chunks, a peer
    /// can make a buffered receive hold up to this many bytes while transmitting far fewer, so size it for what one
    /// connection may hold.
    /// </summary>
    public long MaxMessageSize { get; init; } = DefaultMaxMessageSize;

    /// <summary>
    /// Idle timeout for receive operations: the maximum time to wait for the peer to deliver <i>any</i> bytes.
    /// The timer restarts whenever data arrives and pauses while control is in user code (for example between
    /// streamed chunks). <c>null</c> (default) waits forever. On expiry the operation throws
    /// <see cref="HyperionProtocolException"/> with <see cref="HyperionErrorCode.Timeout"/> and the connection must be closed.
    /// </summary>
    public TimeSpan? ReceiveTimeout { get; init; }

    /// <summary>
    /// Timeout for send operations: the maximum time one chunk (at most <see cref="ChunkSize"/> bytes) or the final flush
    /// may take to be accepted by the transport. Protects against peers that stop reading. <c>null</c> (default) waits forever.
    /// </summary>
    public TimeSpan? SendTimeout { get; init; }

    /// <summary>Per-chunk CRC-32C policy for chunked frames. Default: <see cref="ChunkChecksumMode.Off"/>.</summary>
    public ChunkChecksumMode ChunkChecksum { get; init; } = ChunkChecksumMode.Off;

    /// <summary>Default <see cref="CompressionThreshold"/>: 1024 bytes.</summary>
    public const int DefaultCompressionThreshold = 1024;

    /// <summary>
    /// Per-chunk Zstandard compression policy for chunked frames. Default: <see cref="ChunkCompressionMode.Off"/>.
    /// Any other mode requires <see cref="HyperionProtocol.IsCompressionSupported"/> (.NET 11 or later) and makes the
    /// constructor throw <see cref="PlatformNotSupportedException"/> otherwise. Compression is not negotiated: enable
    /// <see cref="ChunkCompressionMode.Compress"/> only towards a peer known to run with
    /// <see cref="ChunkCompressionMode.Accept"/> or <see cref="ChunkCompressionMode.Compress"/>.
    /// </summary>
    public ChunkCompressionMode ChunkCompression { get; init; } = ChunkCompressionMode.Off;

    /// <summary>
    /// With <see cref="ChunkCompressionMode.Compress"/>, messages whose uncompressed length is below this many bytes
    /// are sent exactly as with compression off. Must be &gt;= 0. Default: 1024.
    /// </summary>
    public int CompressionThreshold { get; init; } = DefaultCompressionThreshold;

    internal void Validate()
    {
        if (ChunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), ChunkSize, "ChunkSize must be > 0.");
        if (MaxHeaderLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxHeaderLength), MaxHeaderLength, "MaxHeaderLength must be > 0.");
        if (MaxMessageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMessageSize), MaxMessageSize, "MaxMessageSize must be > 0.");
        ValidateTimeout(ReceiveTimeout, nameof(ReceiveTimeout));
        ValidateTimeout(SendTimeout, nameof(SendTimeout));
        if (!Enum.IsDefined(typeof(ChunkChecksumMode), ChunkChecksum))
            throw new ArgumentOutOfRangeException(nameof(ChunkChecksum), ChunkChecksum, "Unknown ChunkChecksumMode.");
        if (!Enum.IsDefined(typeof(ChunkCompressionMode), ChunkCompression))
            throw new ArgumentOutOfRangeException(nameof(ChunkCompression), ChunkCompression, "Unknown ChunkCompressionMode.");
        if (CompressionThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(CompressionThreshold), CompressionThreshold, "CompressionThreshold must be >= 0.");
        ZstdCodec.EnsureSupported(ChunkCompression);
    }

    private static void ValidateTimeout(TimeSpan? value, string name)
    {
        if (value is null)
            return;
        if (value.Value <= TimeSpan.Zero || value.Value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive and at most {int.MaxValue} ms, or null to disable.");
    }

    internal static readonly HyperionProtocolOptions Default = new();
}
