namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// JSON-serialized header preceding every chunked payload.
/// </summary>
/// <remarks>
/// <para><see cref="Version"/> was added in protocol revision 1. Headers without
/// the field (emitted by 0.2.x and earlier senders) deserialize to <c>Version == 0</c>
/// and are accepted as legacy v1 by the validator.</para>
/// </remarks>
public sealed class PacketHeader
{
    /// <summary>Protocol revision. <c>0</c> means legacy/unversioned and is treated as <c>1</c>.</summary>
    public int Version { get; set; }

    /// <summary>Magic identifier ("TTS").</summary>
    public string Magic { get; set; } = "TTS";

    /// <summary>Unique identifier shared by every chunk of one logical message.</summary>
    public Guid PacketId { get; set; }

    /// <summary>Zero-based chunk index within the logical message.</summary>
    public int ChunkNumber { get; set; }

    /// <summary>Total number of chunks for this message.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Length in bytes of the payload following this header.</summary>
    public int DataLength { get; set; }

    /// <summary>Bitfield. Bit 0 is set on the final chunk; other bits reserved.</summary>
    public byte Flags { get; set; }
}
