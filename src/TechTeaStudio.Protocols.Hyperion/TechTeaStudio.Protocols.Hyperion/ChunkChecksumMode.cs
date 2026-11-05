namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Controls the per-chunk CRC-32C carried in <see cref="PacketHeader.Checksum"/> on chunked frames.
/// </summary>
/// <remarks>
/// A receiver <b>always verifies</b> a checksum that is present, whatever its own mode: the sender paid
/// for it, so it is honoured. The mode only decides what this side emits and whether it tolerates peers
/// (0.3.x and earlier) that emit none. Lightweight and direct frames carry no header and therefore no
/// checksum; they rely on the transport (TCP checksum, TLS record MAC).
/// </remarks>
public enum ChunkChecksumMode
{
    /// <summary>Do not emit checksums. Incoming checksums are still verified when present.</summary>
    Off = 0,

    /// <summary>Emit a CRC-32C for every chunk. Incoming chunks without one are tolerated (legacy peers).</summary>
    Emit = 1,

    /// <summary>Emit a CRC-32C for every chunk and reject incoming chunks that carry none.</summary>
    Required = 2,
}
