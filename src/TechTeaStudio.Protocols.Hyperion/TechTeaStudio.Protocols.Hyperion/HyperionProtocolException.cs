namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Raised for every framing, validation, transport, timeout or serialization failure inside the protocol.
/// Inspect <see cref="ErrorCode"/> to react programmatically; see <see cref="HyperionErrorCode"/> for the
/// rules on whether the connection is still usable afterwards.
/// </summary>
public class HyperionProtocolException : Exception
{
    /// <summary>Creates an exception with <see cref="HyperionErrorCode.Unspecified"/>.</summary>
    public HyperionProtocolException(string message)
        : this(HyperionErrorCode.Unspecified, message) { }

    /// <summary>Creates an exception with <see cref="HyperionErrorCode.Unspecified"/> and an inner exception.</summary>
    public HyperionProtocolException(string message, Exception innerException)
        : this(HyperionErrorCode.Unspecified, message, innerException) { }

    /// <summary>Creates an exception classified by <paramref name="errorCode"/>.</summary>
    public HyperionProtocolException(HyperionErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Creates an exception classified by <paramref name="errorCode"/> with an inner exception.</summary>
    public HyperionProtocolException(HyperionErrorCode errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Stable classification of the failure.</summary>
    public HyperionErrorCode ErrorCode { get; }

    /// <inheritdoc />
    public override string ToString() => $"[{ErrorCode}] {base.ToString()}";
}
