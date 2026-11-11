namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Controls per-chunk Zstandard compression of chunked frames (<see cref="PacketHeader.Compression"/>).
/// </summary>
/// <remarks>
/// <para>Compression is <b>not negotiated</b>: the sender must know that its peer accepts it. A receiver whose mode
/// is <see cref="Off"/> and every library older than 0.5.0 fail loudly on the first chunk of a compressed message
/// (<see cref="HyperionErrorCode.CompressionNotSupported"/> and <see cref="HyperionErrorCode.UnsupportedVersion"/>
/// respectively) instead of handing compressed bytes to the serializer.</para>
/// <para><see cref="Accept"/> and <see cref="Compress"/> need the Zstandard codec of .NET 11
/// (<see cref="HyperionProtocol.IsCompressionSupported"/>); on older targets they are rejected when the protocol
/// is constructed. Lightweight and direct frames are never compressed.</para>
/// </remarks>
public enum ChunkCompressionMode
{
    /// <summary>Never emit compressed chunks and reject incoming ones. Works on every target framework.</summary>
    Off = 0,

    /// <summary>Never emit compressed chunks, but decompress incoming ones.</summary>
    Accept = 1,

    /// <summary>
    /// Compress the chunks of messages at or above <see cref="HyperionProtocolOptions.CompressionThreshold"/>
    /// (a chunk that does not shrink is sent raw), and decompress incoming ones.
    /// </summary>
    Compress = 2,
}
