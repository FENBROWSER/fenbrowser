using FenBrowser.Host.ProcessIsolation;

namespace FenBrowser.Tests.Core;

public class RendererIpcMetadataTests
{
    [Fact]
    public void BrokerAllowlist_AcceptsMetadataChanged()
    {
        var allowed = RendererIpc.IsAllowedBrokerInboundMessageType(RendererIpcMessageType.MetadataChanged);

        Assert.True(allowed);
    }

    [Fact]
    public void BrokerAllowlist_AcceptsAcknowledgement()
    {
        Assert.True(RendererIpc.IsAllowedBrokerInboundMessageType(RendererIpcMessageType.Ack));
    }

    [Fact]
    public void AckTracker_AcceptsOnlyWrittenCommandCorrelationOnce()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var tracker = new RendererIpcAckTracker();
        tracker.Track(new RendererIpcEnvelope
        {
            Type = RendererIpcMessageType.Navigate.ToString(),
            CorrelationId = correlationId
        });

        Assert.True(tracker.TryAcknowledge(correlationId, out var messageType, out var latencyMs));
        Assert.Equal(RendererIpcMessageType.Navigate, messageType);
        Assert.True(latencyMs >= 0);
        Assert.False(tracker.TryAcknowledge(correlationId, out _, out _));
        Assert.False(tracker.TryAcknowledge(Guid.NewGuid().ToString("N"), out _, out _));
    }

    [Fact]
    public void AckTracker_EvictsOldestCorrelationAtCapacity()
    {
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        var tracker = new RendererIpcAckTracker(capacity: 1);
        tracker.Track(new RendererIpcEnvelope { Type = RendererIpcMessageType.Input.ToString(), CorrelationId = first });
        tracker.Track(new RendererIpcEnvelope { Type = RendererIpcMessageType.Input.ToString(), CorrelationId = second });

        Assert.False(tracker.TryAcknowledge(first, out _, out _));
        Assert.True(tracker.TryAcknowledge(second, out var messageType, out _));
        Assert.Equal(RendererIpcMessageType.Input, messageType);
    }
}
