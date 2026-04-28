using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class BrowserIntegrationViewportHintTests
    {
        [Fact]
        public void BrowserHost_UsesExplicitViewportHint_BeforeFirstRendererFrame()
        {
            using var host = new BrowserHost();
            host.UpdateViewportHint(1440, 900);

            var method = typeof(BrowserHost).GetMethod("GetRenderViewportHint", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var result = ((double? Width, double? Height))method!.Invoke(host, null)!;

            Assert.Equal(1440, result.Width);
            Assert.Equal(900, result.Height);
        }

        [Fact]
        public void BrowserIntegration_UpdateViewport_ForwardsViewportHintToBrowserHost()
        {
            var integration = new BrowserIntegration();

            try
            {
                integration.UpdateViewport(new SKSize(1600, 900));

                var method = typeof(BrowserHost).GetMethod("GetRenderViewportHint", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(method);

                var result = ((double? Width, double? Height))method!.Invoke(integration.Host, null)!;

                Assert.Equal(1600, result.Width);
                Assert.Equal(900, result.Height);
            }
            finally
            {
                var runningField = typeof(BrowserIntegration).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
                var wakeEventField = typeof(BrowserIntegration).GetField("_wakeEvent", BindingFlags.Instance | BindingFlags.NonPublic);
                var engineThreadField = typeof(BrowserIntegration).GetField("_engineThread", BindingFlags.Instance | BindingFlags.NonPublic);

                runningField?.SetValue(integration, false);
                (wakeEventField?.GetValue(integration) as System.Threading.AutoResetEvent)?.Set();
                (engineThreadField?.GetValue(integration) as System.Threading.Thread)?.Join(System.TimeSpan.FromSeconds(2));
            }
        }
    }
}