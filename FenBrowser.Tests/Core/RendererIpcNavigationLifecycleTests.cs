using FenBrowser.Core.Engine;
using FenBrowser.Host;
using FenBrowser.Host.ProcessIsolation;

namespace FenBrowser.Tests.Core;

public class RendererIpcNavigationLifecycleTests
{
    [Fact]
    public void BrokerAllowlist_AcceptsNavigationLifecycle()
    {
        Assert.True(RendererIpc.IsAllowedBrokerInboundMessageType(RendererIpcMessageType.NavigationLifecycle));
    }

    [Fact]
    public void NavigationLifecyclePayload_RoundTripsThroughEnvelope()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var envelope = new RendererIpcEnvelope
        {
            Type = RendererIpcMessageType.NavigationLifecycle.ToString(),
            TabId = 7,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Payload = RendererIpc.SerializePayload(new RendererNavigationLifecyclePayload
            {
                NavigationCorrelationId = correlationId,
                NavigationId = 42,
                Phase = nameof(NavigationLifecyclePhase.Complete),
                EffectiveUrl = "https://example.test/redirected",
                Detail = "load fired"
            })
        };

        var serialized = RendererIpc.SerializeEnvelope(envelope);
        Assert.True(RendererIpc.TryDeserializeEnvelope(serialized, out var inbound));
        Assert.True(RendererIpc.TryValidateInboundEnvelope(inbound, expectedTabId: 7, out var messageType, out _));
        Assert.Equal(RendererIpcMessageType.NavigationLifecycle, messageType);

        var payload = RendererIpc.DeserializePayload<RendererNavigationLifecyclePayload>(inbound);
        Assert.NotNull(payload);
        Assert.Equal(correlationId, payload.NavigationCorrelationId);
        Assert.Equal(42, payload.NavigationId);
        Assert.Equal(nameof(NavigationLifecyclePhase.Complete), payload.Phase);
        Assert.Equal("https://example.test/redirected", payload.EffectiveUrl);
        Assert.Equal("load fired", payload.Detail);
    }

    [Theory]
    [InlineData(NavigationLifecyclePhase.Interactive, true)]
    [InlineData(NavigationLifecyclePhase.Complete, true)]
    [InlineData(NavigationLifecyclePhase.Failed, true)]
    [InlineData(NavigationLifecyclePhase.Cancelled, true)]
    [InlineData(NavigationLifecyclePhase.Idle, false)]
    [InlineData(NavigationLifecyclePhase.Requested, false)]
    [InlineData(NavigationLifecyclePhase.Fetching, false)]
    [InlineData(NavigationLifecyclePhase.ResponseReceived, false)]
    [InlineData(NavigationLifecyclePhase.Committing, false)]
    public void ShouldForwardRendererLifecyclePhase_ForwardsOnlyPageVisibleTransitions(
        NavigationLifecyclePhase phase,
        bool expected)
    {
        Assert.Equal(expected, Program.ShouldForwardRendererLifecyclePhase(phase));
    }
}
