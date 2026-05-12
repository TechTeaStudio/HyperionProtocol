namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Configuration for <see cref="HyperionProtocol"/> and its derivatives.
/// </summary>
/// <remarks>
/// <para>Both ends of a connection must agree on <see cref="ChunkSize"/> and
/// <see cref="MaxHeaderLength"/> — a receiver rejects chunks whose <c>DataLength</c>
/// exceeds <see cref="ChunkSize"/> and headers larger than <see cref="MaxHeaderLength"/>.</para>
/// </remarks>
public sealed class HyperionProtocolOptions
{
    /// <summary>Default chunk size: 1 MiB.</summary>
    public const int DefaultChunkSize = 1024 * 1024;

    /// <summary>Default maximum header length: 64 KiB.</summary>
    public const int DefaultMaxHeaderLength = 64 * 1024;

    /// <summary>Maximum payload bytes per chunk. Must be &gt; 0. Default: 1 MiB.</summary>
    public int ChunkSize { get; init; } = DefaultChunkSize;

    /// <summary>Maximum JSON header length in bytes. Must be &gt; 0. Default: 64 KiB.</summary>
    public int MaxHeaderLength { get; init; } = DefaultMaxHeaderLength;

    internal void Validate()
    {
        if (ChunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), ChunkSize, "ChunkSize must be > 0.");
        if (MaxHeaderLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxHeaderLength), MaxHeaderLength, "MaxHeaderLength must be > 0.");
    }

    internal static readonly HyperionProtocolOptions Default = new();
}
