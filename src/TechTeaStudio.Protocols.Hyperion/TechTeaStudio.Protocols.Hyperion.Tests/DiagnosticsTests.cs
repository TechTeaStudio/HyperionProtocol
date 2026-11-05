using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using TechTeaStudio.Protocols.Hyperion.Protocols;

namespace TechTeaStudio.Protocols.Hyperion.Tests;

[TestFixture]
[NonParallelizable]
public class DiagnosticsTests
{
    [Test]
    public async Task Metrics_CountMessagesBytesAndFramesPerMode()
    {
        var counts = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HyperionDiagnostics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string mode = "";
            foreach (var tag in tags)
                if (tag.Key == "hyperion.mode") mode = tag.Value?.ToString() ?? "";
            counts.AddOrUpdate($"{instrument.Name}|{mode}", value, (_, old) => old + value);
        });
        listener.Start();

        var smart = new SmartHyperionProtocol(new DefaultSerializer());
        var large = TestTransport.RandomBytes(2 * 1024 * 1024 + 1);

        var smallBytes = await Frames.CaptureAsync(smart, new byte[] { 1, 2, 3 });
        var largeBytes = await Frames.CaptureAsync(smart, large);
        _ = await smart.ReceiveAsync<byte[]>(new MemoryStream(smallBytes));
        _ = await smart.ReceiveAsync<byte[]>(new MemoryStream(largeBytes));

        listener.RecordObservableInstruments();

        Assert.Multiple(() =>
        {
            Assert.That(counts["hyperion.messages.sent|lightweight"], Is.GreaterThanOrEqualTo(1));
            Assert.That(counts["hyperion.bytes.sent|lightweight"], Is.GreaterThanOrEqualTo(3));
            Assert.That(counts["hyperion.chunks.sent|lightweight"], Is.GreaterThanOrEqualTo(1));
            Assert.That(counts["hyperion.messages.sent|chunked"], Is.GreaterThanOrEqualTo(1));
            Assert.That(counts["hyperion.chunks.sent|chunked"], Is.GreaterThanOrEqualTo(3));
            Assert.That(counts["hyperion.bytes.sent|chunked"], Is.GreaterThanOrEqualTo(large.Length));
            Assert.That(counts["hyperion.messages.received|lightweight"], Is.GreaterThanOrEqualTo(1));
            Assert.That(counts["hyperion.messages.received|chunked"], Is.GreaterThanOrEqualTo(1));
            Assert.That(counts["hyperion.chunks.received|chunked"], Is.GreaterThanOrEqualTo(3));
            Assert.That(counts["hyperion.bytes.received|chunked"], Is.GreaterThanOrEqualTo(large.Length));
        });
    }

    [Test]
    public async Task Metrics_CountErrorsByCodeAndDirection()
    {
        var errors = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HyperionDiagnostics.MeterName && instrument.Name == "hyperion.errors")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string code = "", direction = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "hyperion.error.code") code = tag.Value?.ToString() ?? "";
                if (tag.Key == "hyperion.direction") direction = tag.Value?.ToString() ?? "";
            }
            errors.AddOrUpdate($"{code}|{direction}", value, (_, old) => old + value);
        });
        listener.Start();

        var protocol = new HyperionProtocol(new DefaultSerializer(), new HyperionProtocolOptions { MaxMessageSize = 10 });
        Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.ReceiveAsync<byte[]>(new MemoryStream()));
        Assert.ThrowsAsync<HyperionProtocolException>(() => protocol.SendAsync(new byte[11], new MemoryStream()));

        await Task.CompletedTask;
        Assert.That(errors["PeerClosed|receive"], Is.GreaterThanOrEqualTo(1));
        Assert.That(errors["MessageTooLarge|send"], Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task Activities_AreStartedPerMessage_AndMarkFailures()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HyperionDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var smart = new SmartHyperionProtocol(new DefaultSerializer());
        var bytes = await Frames.CaptureAsync(smart, "traced");
        _ = await smart.ReceiveAsync<string>(new MemoryStream(bytes));
        Assert.ThrowsAsync<HyperionProtocolException>(() => smart.ReceiveAsync<string>(new MemoryStream()));

        var send = stopped.FirstOrDefault(a => a.OperationName == "Hyperion.Send");
        var receive = stopped.FirstOrDefault(a => a.OperationName == "Hyperion.Receive" && a.Status != ActivityStatusCode.Error);
        var failed = stopped.FirstOrDefault(a => a.OperationName == "Hyperion.Receive" && a.Status == ActivityStatusCode.Error);

        Assert.Multiple(() =>
        {
            Assert.That(send, Is.Not.Null);
            Assert.That(send!.Kind, Is.EqualTo(ActivityKind.Producer));
            Assert.That(send.GetTagItem("hyperion.mode"), Is.EqualTo("lightweight"));
            Assert.That(send.GetTagItem("hyperion.payload.bytes"), Is.EqualTo(6L));

            Assert.That(receive, Is.Not.Null);
            Assert.That(receive!.Kind, Is.EqualTo(ActivityKind.Consumer));
            Assert.That(receive.GetTagItem("hyperion.mode"), Is.EqualTo("lightweight"));

            Assert.That(failed, Is.Not.Null);
            Assert.That(failed!.GetTagItem("hyperion.error.code"), Is.EqualTo("PeerClosed"));
        });
    }

    [Test]
    public void WithoutListeners_NoActivityIsCreated()
    {
        // ActivitySource.StartActivity returns null when nobody listens, so the hot path stays allocation-free.
        Assert.That(HyperionDiagnostics.ActivitySource.HasListeners(), Is.False);
    }
}
