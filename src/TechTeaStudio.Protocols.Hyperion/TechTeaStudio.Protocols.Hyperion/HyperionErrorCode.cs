namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Machine-readable classification carried by every <see cref="HyperionProtocolException"/>.
/// </summary>
/// <remarks>
/// <para>Values are stable: a code never changes meaning once published, and new codes are only appended.
/// Use them for logging, metrics and for deciding what to do with the connection.</para>
/// <para>Unless noted otherwise, after an exception is thrown the connection is in an undefined
/// position inside a frame and <b>must be closed</b>. The exceptions are:
/// <see cref="PeerClosed"/> (the peer already closed it cleanly),
/// <see cref="SerializationFailed"/> and <see cref="MessageTooLarge"/> raised on the <i>send</i> side
/// (they are detected before anything is written, so the connection is still usable).</para>
/// </remarks>
public enum HyperionErrorCode
{
    /// <summary>No specific classification (legacy constructor or an unexpected condition).</summary>
    Unspecified = 0,

    // --- Frame validation: the peer sent something this receiver does not accept ---------------

    /// <summary>The chunk header's magic was not <see cref="HyperionProtocol.ProtocolMagic"/>.</summary>
    InvalidMagic = 1,

    /// <summary>The header's protocol version is outside the supported range.</summary>
    UnsupportedVersion = 2,

    /// <summary>The 4-byte header-length prefix is zero, negative or above <see cref="HyperionProtocolOptions.MaxHeaderLength"/>.</summary>
    InvalidHeaderLength = 3,

    /// <summary>The header bytes are not a valid JSON <see cref="PacketHeader"/>.</summary>
    MalformedHeader = 4,

    /// <summary><c>TotalChunks</c> is not positive, or changed between chunks of the same message.</summary>
    InvalidChunkCount = 5,

    /// <summary><c>ChunkNumber</c> is negative or not below <c>TotalChunks</c>.</summary>
    InvalidChunkNumber = 6,

    /// <summary><c>DataLength</c> is negative, above <see cref="HyperionProtocolOptions.ChunkSize"/>, or violates the uniform-chunk rule.</summary>
    InvalidDataLength = 7,

    /// <summary>The end flag is set on a non-final chunk, or missing on the final one.</summary>
    EndFlagMismatch = 8,

    /// <summary>A chunk belongs to a different <c>PacketId</c> than the message being reassembled.</summary>
    PacketIdMismatch = 9,

    /// <summary>A chunk arrived with an unexpected <c>ChunkNumber</c> (chunks must be sequential).</summary>
    ChunkOutOfOrder = 10,

    /// <summary>The message exceeds <see cref="HyperionProtocolOptions.MaxMessageSize"/>.</summary>
    MessageTooLarge = 11,

    /// <summary>The CRC-32C in the header does not match the chunk payload.</summary>
    ChecksumMismatch = 12,

    /// <summary>The header carries no checksum but <see cref="HyperionProtocolOptions.ChunkChecksum"/> is <see cref="ChunkChecksumMode.Required"/>.</summary>
    ChecksumMissing = 13,

    // --- Connection / transport -----------------------------------------------------------------

    /// <summary>
    /// The peer closed the connection at a message boundary (receive side) or completed the pipe before a
    /// message was written (send side). Nothing was corrupted; this is the normal end of a session.
    /// </summary>
    PeerClosed = 20,

    /// <summary>The connection ended in the middle of a frame.</summary>
    UnexpectedEndOfStream = 21,

    /// <summary>No progress within <see cref="HyperionProtocolOptions.ReceiveTimeout"/> / <see cref="HyperionProtocolOptions.SendTimeout"/>.</summary>
    Timeout = 22,

    /// <summary><see cref="HyperionProtocol.HandshakeAsync"/> failed: wrong magic, unsupported version or connection lost.</summary>
    HandshakeFailed = 23,

    // --- Local ----------------------------------------------------------------------------------

    /// <summary>The <see cref="ISerializer"/> threw, or produced <c>null</c> for a type that cannot be null.</summary>
    SerializationFailed = 30,

    /// <summary>An I/O error occurred while sending. See <see cref="System.Exception.InnerException"/>.</summary>
    SendFailed = 31,

    /// <summary>An I/O error occurred while receiving. See <see cref="System.Exception.InnerException"/>.</summary>
    ReceiveFailed = 32,
}
