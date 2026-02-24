using FenBrowser.Host.Tabs;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Defines the browser-host process model boundary.
    /// Current implementation is in-process with explicit extension points for brokered renderers.
    /// </summary>
    public interface IProcessIsolationCoordinator
    {
        string Mode { get; }
        bool UsesOutOfProcessRenderer { get; }
        void Initialize();
        void OnTabCreated(BrowserTab tab);
        void OnTabActivated(BrowserTab tab);
        void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput);
        void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent);
        void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight);
        void OnTabClosed(BrowserTab tab);
        void Shutdown();

        // Server-to-Host events for pushing back state
        event Action<int, RendererFrameReadyPayload> FrameReceived;
        
        // Fired when the renderer crashes and cannot be automatically restarted
        event Action<int, string> RendererCrashed;
    }
}
