namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Configuration for <see cref="HyperionProtocol"/> and its derivatives. Immutable after construction.
/// </summary>
/// <remarks>
/// <para>Both ends of a connection must agree on <see cref="ChunkSize"/> and <see cref="MaxHeaderLength"/>:
/// a receiver rejects chunks whose <c>DataLength</c> exceeds its <see cref="ChunkSize"/> and headers larger
/// than its <see cref="MaxHeaderLength"/>. (A sender with a <i>smaller</i> chunk size than the receiver is fine.)</para>
/// <para><see cref="MaxMessageSize"/>, the timeouts and <see cref="ChunkChecksum"/> are local policy and may differ per side.</para>
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
    /// Maximum size of one logical message (sum of its chunk payloads). Must be &gt; 0. Default: 1 GiB.
    /// Enforced on send (before anything is written) and on receive (before payload bytes are read, using
    /// the chunk count announced in the first header, and again as chunks accumulate). Applies to every
    /// receive path including <see cref="HyperionProtocol.ReceiveStreamingAsync(Stream, CancellationToken)"/>; raise it (up to
    /// <see cref="long.MaxValue"/>) for larger streamed transfers. Buffered receives are additionally bound
    /// by the 2 GiB array limit of the runtime.
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
