using System.Text.Json.Serialization;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// JSON-serialized header preceding every chunk of a chunked message.
/// </summary>
/// <remarks>
/// <para>Receivers ignore members they do not know, which is how the header evolves without a version bump:
/// <see cref="Version"/> was added in protocol revision 1 (library 0.3.0) and <see cref="Checksum"/>
/// in library 0.4.0; both are optional on the wire.</para>
/// <para>Headers without <see cref="Version"/> (0.2.x and earlier senders) deserialize to <c>Version == 0</c>
/// and are accepted as legacy v1 by the validator.</para>
/// </remarks>
public sealed class PacketHeader
{
    /// <summary>Bit 0 of <see cref="Flags"/>: set on the final chunk of a message.</summary>
    public const byte EndFlag = 0x01;

    /// <summary>Protocol revision. <c>0</c> means legacy/unversioned and is treated as <c>1</c>.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Magic identifier; must be <see cref="HyperionProtocol.ProtocolMagic"/> ("TTS"). Defaults to empty so that a
    /// header missing the member on the wire is rejected; set it explicitly when building headers by hand.
    /// </summary>
    public string Magic { get; set; } = string.Empty;

    /// <summary>Unique identifier shared by every chunk of one logical message.</summary>
    public Guid PacketId { get; set; }

    /// <summary>Zero-based chunk index within the logical message.</summary>
    public int ChunkNumber { get; set; }

    /// <summary>Total number of chunks for this message.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Length in bytes of the payload following this header.</summary>
    public int DataLength { get; set; }

    /// <summary>Bitfield. <see cref="EndFlag"/> (bit 0) is set on the final chunk; other bits are reserved and must be zero.</summary>
    public byte Flags { get; set; }

    /// <summary>
    /// CRC-32C of the chunk payload (see <see cref="Crc32C"/>). Present when the sender's
    /// <see cref="HyperionProtocolOptions.ChunkChecksum"/> is not <see cref="ChunkChecksumMode.Off"/>;
    /// absent (<c>null</c>) for 0.3.x and earlier senders. Serialized as <c>"Crc32C"</c>.
    /// </summary>
    [JsonPropertyName("Crc32C")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Checksum { get; set; }

    /// <summary>True when <see cref="Flags"/> has <see cref="EndFlag"/> set.</summary>
    [JsonIgnore]
    public bool IsLastChunk => (Flags & EndFlag) != 0;
}
