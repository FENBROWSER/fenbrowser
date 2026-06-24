using System;
using System.Reflection;
using System.Threading;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class BrowserTabDisplayUrlTests
    {
        [Fact]
        public void StartInitialNavigation_ExposesPendingDisplayUrlBeforeCommit()
        {
            var tab = new BrowserTab();

            try
            {
                tab.StartInitialNavigation("fen://newtab");

                Assert.Equal(string.Empty, tab.Url);
                Assert.Equal("fen://newtab", tab.DisplayUrl);
            }
            finally
            {
                ShutdownEngineLoop(tab.Browser);
            }
        }

        [Fact]
        public void CreateTab_ActiveTabChangedSeesInitialDisplayUrl()
        {
            var manager = new TabManager();
            BrowserTab observed = null;
            manager.ActiveTabChanged += tab => observed = tab;

            var tab = manager.CreateTab("fen://newtab");

            try
            {
                Assert.Same(tab, observed);
                Assert.Equal("fen://newtab", observed.DisplayUrl);
            }
            finally
            {
                ShutdownEngineLoop(tab.Browser);
            }
        }

        [Fact]
        public void GetAddressBarText_ShowsInternalNewTabUrl()
        {
            var tab = new BrowserTab();

            try
            {
                tab.StartInitialNavigation("fen://newtab");

                Assert.Equal("fen://newtab", tab.DisplayUrl);
                Assert.Equal("fen://newtab", ChromeManager.GetAddressBarText(tab));
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
            var currentSeedField = typeof(BrowserIntegration).GetField("_currentFrameSeedImage", BindingFlags.Instance | BindingFlags.NonPublic);

            runningField?.SetValue(integration, false);
            (wakeEventField?.GetValue(integration) as AutoResetEvent)?.Set();
            (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(2));
            (currentFrameField?.GetValue(integration) as SKPicture)?.Dispose();
            (currentSeedField?.GetValue(integration) as SKImage)?.Dispose();
        }
    }
}
