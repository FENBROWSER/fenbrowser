using System.Reflection;
using FenBrowser.Host;
using FenBrowser.Host.ProcessIsolation;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host;

public class RendererIpcViewportTests
{
    [Fact]
    public void NavigatePayload_RoundTripsViewportHint()
    {
        var payload = new RendererNavigatePayload
        {
            Url = "https://www.google.com",
            IsUserInput = true,
            ViewportWidth = 1920,
            ViewportHeight = 899
        };

        var envelope = new RendererIpcEnvelope
        {
            Type = RendererIpcMessageType.Navigate.ToString(),
            TabId = 7,
            Payload = RendererIpc.SerializePayload(payload)
        };

        var roundTrip = RendererIpc.DeserializePayload<RendererNavigatePayload>(envelope);

        Assert.Equal("https://www.google.com", roundTrip.Url);
        Assert.True(roundTrip.IsUserInput);
        Assert.Equal(1920, roundTrip.ViewportWidth);
        Assert.Equal(899, roundTrip.ViewportHeight);
    }

    [Fact]
    public void BrowserIntegration_ExposesLastViewportForProcessIsolation()
    {
        var integration = new BrowserIntegration();

        try
        {
            integration.UpdateViewport(new SKSize(1600, 900));

            Assert.Equal(1600, integration.ViewportSize.Width);
            Assert.Equal(900, integration.ViewportSize.Height);
        }
        finally
        {
            ShutdownEngineLoop(integration);
        }
    }

    private static void ShutdownEngineLoop(BrowserIntegration integration)
    {
        var runningField = typeof(BrowserIntegration).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
        var wakeEventField = typeof(BrowserIntegration).GetField("_wakeEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        var engineThreadField = typeof(BrowserIntegration).GetField("_engineThread", BindingFlags.Instance | BindingFlags.NonPublic);

        runningField?.SetValue(integration, false);
        (wakeEventField?.GetValue(integration) as AutoResetEvent)?.Set();
        (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(2));
    }
}
