using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;
using Xunit;

namespace FenBrowser.Tests.WebDriver;

public sealed class HostBrowserDriverCurrentUrlTests
{
    [Fact]
    public async Task GetCurrentUrl_ReportsSyncedRendererUrlInBrokeredMode()
    {
        var previousAutoStart = Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");
        ProcessIsolationRuntime.SetCoordinator(new OutOfProcessRendererCoordinator());

        try
        {
            var manager = TabManager.Instance;
            var activeBefore = manager.ActiveTab;
            var tabIndex = manager.Tabs.Count;
            var tab = manager.CreateTab();
            try
            {
                // Simulate the renderer metadata sync that publishes the committed
                // URL to the UI while the local engine still holds the previous
                // document URI.
                tab.Browser.OnMetadataChangedFromRenderer(tab.Id, new RendererMetadataChangedPayload
                {
                    Url = "https://example.test/committed"
                });

                var driver = new HostBrowserDriver();

                Assert.Equal("https://example.test/committed", await driver.GetCurrentUrlAsync());
            }
            finally
            {
                if (manager.Tabs.Count > tabIndex)
                {
                    manager.CloseTab(tabIndex);
                }

                var restoreIndex = -1;
                if (activeBefore != null)
                {
                    for (var i = 0; i < manager.Tabs.Count; i++)
                    {
                        if (ReferenceEquals(manager.Tabs[i], activeBefore))
                        {
                            restoreIndex = i;
                            break;
                        }
                    }
                }

                if (restoreIndex >= 0)
                {
                    manager.SwitchToTab(restoreIndex);
                }
            }
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    private sealed class OutOfProcessRendererCoordinator : IProcessIsolationCoordinator
    {
        public string Mode => "test-brokered";
        public bool UsesOutOfProcessRenderer => true;

        public void Initialize() { }
        public void OnTabCreated(BrowserTab tab) { }
        public void OnTabActivated(BrowserTab tab) { }
        public void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput) { }
        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent) { }
        public void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight, float scrollY = 0) { }
        public void OnTabClosed(BrowserTab tab) { }
        public void Shutdown() { }

#pragma warning disable CS0067
        public event Action<int, RendererFrameReadyPayload> FrameReceived;
        public event Action<int, RendererMetadataChangedPayload> MetadataChanged;
        public event Action<int, string> RendererCrashed;
#pragma warning restore CS0067
    }
}
