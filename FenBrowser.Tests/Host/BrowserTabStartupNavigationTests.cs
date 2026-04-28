using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    public class BrowserTabStartupNavigationTests
    {
        [Fact]
        public async Task StartInitialNavigation_WaitsForViewport_BeforeDispatching()
        {
            var tab = new BrowserTab();

            try
            {
                tab.StartInitialNavigation("fen://newtab");

                Assert.Equal(string.Empty, tab.Url);
                Assert.False(tab.Browser.HasViewport);

                tab.Browser.UpdateViewport(new SKSize(1280, 720));
                tab.NotifyViewportReady();

                await WaitUntilAsync(
                    () => string.Equals(tab.Url, "fen://newtab", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(tab.Url, "fen://newtab/", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(3));
            }
            finally
            {
                ShutdownEngineLoop(tab.Browser);
            }
        }

        private static void ShutdownEngineLoop(BrowserIntegration integration)
        {
            var runningField = typeof(BrowserIntegration).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
            var wakeEventField = typeof(BrowserIntegration).GetField("_wakeEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            var engineThreadField = typeof(BrowserIntegration).GetField("_engineThread", BindingFlags.Instance | BindingFlags.NonPublic);
            var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);

            runningField?.SetValue(integration, false);
            (wakeEventField?.GetValue(integration) as AutoResetEvent)?.Set();
            (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(2));
            (currentFrameField?.GetValue(integration) as SKPicture)?.Dispose();
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.True(predicate(), $"Condition was not satisfied within {timeout.TotalMilliseconds:F0}ms.");
        }
    }
}