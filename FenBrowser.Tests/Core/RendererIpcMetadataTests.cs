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
}
