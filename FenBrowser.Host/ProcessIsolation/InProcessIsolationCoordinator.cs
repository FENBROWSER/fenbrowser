using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Tabs;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Baseline process model: browser + renderer run in a single process.
    /// </summary>
    public sealed class InProcessIsolationCoordinator : IProcessIsolationCoordinator
    {
        public string Mode => "in-process";
        public bool UsesOutOfProcessRenderer => false;

        public void Initialize()
        {
            FenLogger.Info("[ProcessIsolation] Mode=in-process (single-process renderer)", LogCategory.General);
        }

        public void OnTabCreated(BrowserTab tab)
        {
        }

        public void OnTabActivated(BrowserTab tab)
        {
        }

        public void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput)
        {
        }

        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent)
        {
        }

        public void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight)
        {
        }

        public void OnTabClosed(BrowserTab tab)
        {
        }

        public void Shutdown()
        {
        }

        public event Action<int, RendererFrameReadyPayload> FrameReceived;
        public event Action<int, string> RendererCrashed;
    }
}
