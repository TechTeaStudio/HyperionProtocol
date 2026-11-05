using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Telemetry surface of the library: one <see cref="Meter"/> and one <see cref="ActivitySource"/>, both named
/// <c>TechTeaStudio.Protocols.Hyperion</c>. Nothing is emitted unless something listens, so the cost in
/// production is a few nanoseconds per message.
/// </summary>
/// <remarks>
/// <para>Subscribe with OpenTelemetry (<c>.AddMeter(HyperionDiagnostics.MeterName)</c> /
/// <c>.AddSource(HyperionDiagnostics.ActivitySourceName)</c>), with <c>dotnet-counters monitor --counters TechTeaStudio.Protocols.Hyperion</c>,
/// or with a <see cref="MeterListener"/> / <see cref="ActivityListener"/> in-process.</para>
/// <para>Instruments (all counters, tagged with <c>hyperion.mode</c> = <c>lightweight</c> | <c>direct</c> | <c>chunked</c>):
/// <c>hyperion.messages.sent</c>, <c>hyperion.messages.received</c>, <c>hyperion.bytes.sent</c>, <c>hyperion.bytes.received</c>,
/// <c>hyperion.chunks.sent</c>, <c>hyperion.chunks.received</c>, and <c>hyperion.errors</c>
/// (tagged with <c>hyperion.error.code</c> and <c>hyperion.direction</c>).</para>
/// <para>Activities: <c>Hyperion.Send</c> (<see cref="ActivityKind.Producer"/>) and <c>Hyperion.Receive</c>
/// (<see cref="ActivityKind.Consumer"/>), one per message, tagged with mode, payload size and chunk count.
/// Failures set the activity status to <see cref="ActivityStatusCode.Error"/> and tag the error code.</para>
/// </remarks>
public static class HyperionDiagnostics
{
    /// <summary>Name of the <see cref="Meter"/> the library reports counters through.</summary>
    public const string MeterName = "TechTeaStudio.Protocols.Hyperion";

    /// <summary>Name of the <see cref="ActivitySource"/> the library starts activities from.</summary>
    public const string ActivitySourceName = "TechTeaStudio.Protocols.Hyperion";

    private const string ModeTag = "hyperion.mode";
    private const string ErrorCodeTag = "hyperion.error.code";
    private const string DirectionTag = "hyperion.direction";

    private static readonly string Version =
        typeof(HyperionDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static readonly Meter Meter = new(MeterName, Version);
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    internal static readonly Counter<long> MessagesSent =
        Meter.CreateCounter<long>("hyperion.messages.sent", "{message}", "Messages successfully written to the transport.");

    internal static readonly Counter<long> MessagesReceived =
        Meter.CreateCounter<long>("hyperion.messages.received", "{message}", "Messages successfully reassembled from the transport.");

    internal static readonly Counter<long> BytesSent =
        Meter.CreateCounter<long>("hyperion.bytes.sent", "By", "Payload bytes sent (framing overhead excluded).");

    internal static readonly Counter<long> BytesReceived =
        Meter.CreateCounter<long>("hyperion.bytes.received", "By", "Payload bytes received (framing overhead excluded).");

    internal static readonly Counter<long> ChunksSent =
        Meter.CreateCounter<long>("hyperion.chunks.sent", "{chunk}", "Frames written (one per lightweight/direct message, one per chunk otherwise).");

    internal static readonly Counter<long> ChunksReceived =
        Meter.CreateCounter<long>("hyperion.chunks.received", "{chunk}", "Frames read.");

    internal static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("hyperion.errors", "{error}", "Protocol exceptions raised, by error code and direction.");

    internal static Activity? StartSend(string mode, long payloadBytes)
    {
        var activity = ActivitySource.StartActivity("Hyperion.Send", ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag(ModeTag, mode);
            activity.SetTag("hyperion.payload.bytes", payloadBytes);
        }
        return activity;
    }

    internal static Activity? StartReceive()
        => ActivitySource.StartActivity("Hyperion.Receive", ActivityKind.Consumer);

    internal static void RecordSent(string mode, long payloadBytes, int frames)
    {
        if (!MessagesSent.Enabled && !BytesSent.Enabled && !ChunksSent.Enabled)
            return;

        var tag = new KeyValuePair<string, object?>(ModeTag, mode);
        MessagesSent.Add(1, tag);
        BytesSent.Add(payloadBytes, tag);
        ChunksSent.Add(frames, tag);
    }

    internal static void RecordReceived(Activity? activity, string mode, long payloadBytes, int frames)
    {
        if (activity is not null)
        {
            activity.SetTag(ModeTag, mode);
            activity.SetTag("hyperion.payload.bytes", payloadBytes);
            activity.SetTag("hyperion.frames", frames);
        }

        if (!MessagesReceived.Enabled && !BytesReceived.Enabled && !ChunksReceived.Enabled)
            return;

        var tag = new KeyValuePair<string, object?>(ModeTag, mode);
        MessagesReceived.Add(1, tag);
        BytesReceived.Add(payloadBytes, tag);
        ChunksReceived.Add(frames, tag);
    }

    /// <summary>Records <paramref name="exception"/> on the metrics and the activity and returns it for throwing.</summary>
    internal static HyperionProtocolException RecordError(Activity? activity, HyperionProtocolException exception, string direction)
    {
        if (Errors.Enabled)
        {
            Errors.Add(1,
                new KeyValuePair<string, object?>(ErrorCodeTag, exception.ErrorCode.ToString()),
                new KeyValuePair<string, object?>(DirectionTag, direction));
        }

        if (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.SetTag(ErrorCodeTag, exception.ErrorCode.ToString());
        }

        return exception;
    }
}
