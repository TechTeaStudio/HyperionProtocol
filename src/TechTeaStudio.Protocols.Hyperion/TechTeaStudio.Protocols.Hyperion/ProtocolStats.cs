namespace TechTeaStudio.Protocols.Hyperion.Protocols;

/// <summary>Counters for <see cref="SmartHyperionProtocol"/> framing-mode picks.</summary>
public sealed class ProtocolStats
{
    /// <summary>Messages sent using lightweight framing (&lt; 1 KiB).</summary>
    public long LightweightMessagesSent { get; internal set; }

    /// <summary>Messages sent using direct framing (&lt; 64 KiB).</summary>
    public long DirectMessagesSent { get; internal set; }

    /// <summary>Messages sent using chunked framing.</summary>
    public long ChunkedMessagesSent { get; internal set; }

    /// <summary>Approximate bytes saved versus framing every message as chunked.</summary>
    public long TotalBytesSaved { get; internal set; }

    /// <summary>Total messages sent across all modes.</summary>
    public long TotalMessagesSent =>
        LightweightMessagesSent + DirectMessagesSent + ChunkedMessagesSent;

    /// <summary>Zeroes all counters.</summary>
    public void Reset()
    {
        LightweightMessagesSent = 0;
        DirectMessagesSent = 0;
        ChunkedMessagesSent = 0;
        TotalBytesSaved = 0;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var total = Math.Max(1L, TotalMessagesSent);
        return $"Protocol Stats:\n" +
               $"Lightweight: {LightweightMessagesSent} ({100.0 * LightweightMessagesSent / total:F1}%)\n" +
               $"Direct:      {DirectMessagesSent} ({100.0 * DirectMessagesSent / total:F1}%)\n" +
               $"Chunked:     {ChunkedMessagesSent} ({100.0 * ChunkedMessagesSent / total:F1}%)\n" +
               $"Bytes saved: {TotalBytesSaved:N0}";
    }
}
