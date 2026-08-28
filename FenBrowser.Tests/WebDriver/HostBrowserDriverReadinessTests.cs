using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core.Engine;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;
using FenBrowser.WebDriver.Commands;
using Xunit;

namespace FenBrowser.Tests.WebDriver;

// WD-004 out-of-process: with a brokered renderer the navigation (and its
// lifecycle) runs in the renderer child, so the WebDriver readiness wait must
// consume the coordinator's renderer-forwarded lifecycle stream instead of the
// local BrowserHost tracker.
public sealed class HostBrowserDriverReadinessTests
{
    [Fact]
    public async Task NormalStrategy_CompletesWhenRendererReportsComplete()
    {
        var coordinator = new LifecycleCoordinator();
        using var scope = UseCoordinator(coordinator);
        using var tabScope = CreateActiveTab();

        var driver = new HostBrowserDriver();
        var wait = driver.WaitForDocumentReadinessAsync(
            WdDocumentReadinessStage.Complete, timeoutMs: 5000);

        coordinator.RaiseLifecycle(tabScope.Tab.Id, nameof(NavigationLifecyclePhase.Interactive));
        Assert.False(wait.IsCompleted, "complete-stage wait finished on Interactive");

        coordinator.RaiseLifecycle(tabScope.Tab.Id, nameof(NavigationLifecyclePhase.Complete));
        Assert.Equal(WdReadinessWaitStatus.Reached, await wait);
    }

    [Fact]
    public async Task EagerStrategy_CompletesWhenRendererReportsInteractive()
    {
        var coordinator = new LifecycleCoordinator();
        using var scope = UseCoordinator(coordinator);
        using var tabScope = CreateActiveTab();

        var driver = new HostBrowserDriver();
        var wait = driver.WaitForDocumentReadinessAsync(
            WdDocumentReadinessStage.Interactive, timeoutMs: 5000);

        coordinator.RaiseLifecycle(tabScope.Tab.Id, nameof(NavigationLifecyclePhase.Interactive));
        Assert.Equal(WdReadinessWaitStatus.Reached, await wait);
    }

    [Fact]
    public async Task RendererFailure_AbortsWait()
    {
        var coordinator = new LifecycleCoordinator();
        using var scope = UseCoordinator(coordinator);
        using var tabScope = CreateActiveTab();

        var driver = new HostBrowserDriver();
        var wait = driver.WaitForDocumentReadinessAsync(
            WdDocumentReadinessStage.Complete, timeoutMs: 5000);

        coordinator.RaiseLifecycle(tabScope.Tab.Id, nameof(NavigationLifecyclePhase.Failed));
        Assert.Equal(WdReadinessWaitStatus.NavigationAborted, await wait);
    }

    [Fact]
    public async Task OtherTabLifecycleEventsDoNotCompleteWait()
    {
        var coordinator = new LifecycleCoordinator();
        using var scope = UseCoordinator(coordinator);
        using var tabScope = CreateActiveTab();

        var driver = new HostBrowserDriver();
        var wait = driver.WaitForDocumentReadinessAsync(
            WdDocumentReadinessStage.Complete, timeoutMs: 2000);

        coordinator.RaiseLifecycle(tabScope.Tab.Id + 1, nameof(NavigationLifecyclePhase.Complete));
        await Task.Delay(100);
        Assert.False(wait.IsCompleted, "a lifecycle event from another tab completed the wait");

        coordinator.RaiseLifecycle(tabScope.Tab.Id, nameof(NavigationLifecyclePhase.Complete));
        Assert.Equal(WdReadinessWaitStatus.Reached, await wait);
    }

    [Fact]
    public async Task MissingRendererEventsTimeOut()
    {
        var coordinator = new LifecycleCoordinator();
        using var scope = UseCoordinator(coordinator);
        using var tabScope = CreateActiveTab();

        var driver = new HostBrowserDriver();
        var wait = driver.WaitForDocumentReadinessAsync(
            WdDocumentReadinessStage.Complete, timeoutMs: 300);

        Assert.Equal(WdReadinessWaitStatus.TimedOut, await wait);
    }

    private static IDisposable UseCoordinator(LifecycleCoordinator coordinator)
    {
        var previousAutoStart = Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");
        ProcessIsolationRuntime.SetCoordinator(coordinator);
        return new DelegateScope(() =>
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        });
    }

    private static TabScope CreateActiveTab()
    {
        var manager = TabManager.Instance;
        var activeBefore = manager.ActiveTab;
        var tabIndex = manager.Tabs.Count;
        var tab = manager.CreateTab();
        return new TabScope(tab, () =>
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
        });
    }

    private sealed class DelegateScope : IDisposable
    {
        private readonly Action _dispose;

        public DelegateScope(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose() => _dispose();
    }

    private sealed class TabScope : IDisposable
    {
        public BrowserTab Tab { get; }
        private readonly Action _dispose;

        public TabScope(BrowserTab tab, Action dispose)
        {
            Tab = tab;
            _dispose = dispose;
        }

        public void Dispose() => _dispose();
    }

    private sealed class LifecycleCoordinator : IProcessIsolationCoordinator
    {
        private readonly List<(int TabId, RendererNavigationLifecyclePayload Payload)> _raised = new();

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

        public void RaiseLifecycle(int tabId, string phase)
        {
            var payload = new RendererNavigationLifecyclePayload
            {
                NavigationCorrelationId = string.Empty,
                NavigationId = 1,
                Phase = phase,
                EffectiveUrl = "https://example.test/a"
            };
            NavigationLifecycleReceived?.Invoke(tabId, payload);
        }

#pragma warning disable CS0067
        public event Action<int, RendererFrameReadyPayload> FrameReceived;
        public event Action<int, RendererMetadataChangedPayload> MetadataChanged;
        public event Action<int, RendererNavigationLifecyclePayload> NavigationLifecycleReceived;
        public event Action<int, string> RendererCrashed;
#pragma warning restore CS0067
    }
}
