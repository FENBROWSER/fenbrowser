using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.Tabs;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class BrokeredInputRoutingTests
{
    [Fact]
    public async Task BrowserIntegration_HandleKeyPress_RoutesTextToRendererOnlyInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();

            await tab.Browser.HandleKeyPress("t");
            await tab.Browser.HandleKeyPress("Enter");

            Assert.Collection(
                coordinator.Inputs,
                input =>
                {
                    Assert.Equal(RendererInputEventType.TextInput, input.Type);
                    Assert.Equal("t", input.Text);
                },
                input =>
                {
                    Assert.Equal(RendererInputEventType.KeyDown, input.Type);
                    Assert.Equal("Enter", input.Key);
                });
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public async Task BrowserIntegration_HandleMouseUp_RunsActivationFallbackInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();
            var browserHost = GetBrowserHost(tab);
            SetCurrentUri(browserHost, new System.Uri("about:blank"));

            var form = new Element("form");
            form.SetAttribute("action", "#submitted");
            form.SetAttribute("method", "GET");

            var button = new Element("button");
            button.SetAttribute("type", "submit");
            var span = new Element("span");
            button.AppendChild(span);

            form.AppendChild(button);

            var hit = new HitTestResult(
                TagName: "span",
                Href: null,
                Cursor: CursorType.Pointer,
                IsClickable: true,
                IsFocusable: false,
                IsEditable: false,
                NativeElement: span);

            SetHitTestCache(tab, hit);

            tab.Browser.HandleMouseUp(10, 10, button: 0, emitClick: true);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseUp &&
                inputEvent.Button == 0 &&
                inputEvent.EmitClick);

            await WaitForAsync(
                () => browserHost.CurrentUri?.AbsoluteUri == "about:blank#submitted",
                "brokered submit-button activation did not update host navigation state");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public void BrowserIntegration_HandleMouseMove_RoutesToRendererWithoutLocalInputFrameInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();

            tab.Browser.HandleMouseMove(12, 34);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseMove &&
                inputEvent.X == 12 &&
                inputEvent.Y == 34);

            var pendingReasons = GetPendingInvalidationReasons(tab);
            Assert.True(
                (pendingReasons & RenderFrameInvalidationReason.Input) == 0,
                $"Expected brokered hover to wait for the renderer child frame instead of queuing a local input repaint; got {pendingReasons}.");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    [Fact]
    public void BrowserIntegration_HandleMouseWheel_RoutesThroughIntegrationAndScrollsOnceInBrokeredMode()
    {
        var previousAutoStart = System.Environment.GetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES");
        System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", "0");

        var coordinator = new RecordingCoordinator();
        ProcessIsolationRuntime.SetCoordinator(coordinator);

        try
        {
            var tab = new BrowserTab();
            SetScrollableContent(tab, viewportHeight: 200, contentHeight: 1000);
            var before = tab.Browser.EffectiveScrollY;

            tab.Browser.HandleMouseWheel(20, 30, deltaX: 1, deltaY: -2, viewportOffsetX: 5, viewportOffsetY: 7);

            Assert.Contains(coordinator.Inputs, inputEvent =>
                inputEvent.Type == RendererInputEventType.MouseWheel &&
                inputEvent.X == 15 &&
                inputEvent.Y == 23 &&
                inputEvent.DeltaX == 1 &&
                inputEvent.DeltaY == -2);

            Assert.Equal(before + 80, tab.Browser.EffectiveScrollY);

            var pendingReasons = GetPendingInvalidationReasons(tab);
            Assert.True(
                (pendingReasons & RenderFrameInvalidationReason.Scroll) != 0,
                $"Expected brokered wheel input to request a scroll frame; got {pendingReasons}.");
        }
        finally
        {
            ProcessIsolationRuntime.SetCoordinator(null);
            System.Environment.SetEnvironmentVariable("FEN_AUTO_START_TARGET_PROCESSES", previousAutoStart);
        }
    }

    private static BrowserHost GetBrowserHost(BrowserTab tab)
    {
        var field = typeof(FenBrowser.Host.BrowserIntegration).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<BrowserHost>(field!.GetValue(tab.Browser));
    }

    private static void SetCurrentUri(BrowserHost host, System.Uri uri)
    {
        var currentField = typeof(BrowserHost).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(host, uri);
    }

    private static void SetHitTestCache(BrowserTab tab, HitTestResult hit)
    {
        var lastHitField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_lastHitTest", BindingFlags.Instance | BindingFlags.NonPublic);
        var cachedHitField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_cachedHitTest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lastHitField);
        Assert.NotNull(cachedHitField);
        lastHitField!.SetValue(tab.Browser, hit);
        cachedHitField!.SetValue(tab.Browser, hit);
    }

    private static RenderFrameInvalidationReason GetPendingInvalidationReasons(BrowserTab tab)
    {
        var field = typeof(FenBrowser.Host.BrowserIntegration).GetField("_pendingInvalidationReasons", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (RenderFrameInvalidationReason)field!.GetValue(tab.Browser)!;
    }

    private static void SetScrollableContent(BrowserTab tab, float viewportHeight, float contentHeight)
    {
        var viewportField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_lastViewportSize", BindingFlags.Instance | BindingFlags.NonPublic);
        var contentHeightField = typeof(FenBrowser.Host.BrowserIntegration).GetField("_contentHeight", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(viewportField);
        Assert.NotNull(contentHeightField);
        viewportField!.SetValue(tab.Browser, new SKSize(800, viewportHeight));
        contentHeightField!.SetValue(tab.Browser, contentHeight);
    }

    private static async Task WaitForAsync(System.Func<bool> predicate, string failureMessage)
    {
        for (var i = 0; i < 50; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), failureMessage);
    }

    private sealed class RecordingCoordinator : IProcessIsolationCoordinator
    {
        public string Mode => "test-brokered";
        public bool UsesOutOfProcessRenderer => true;
        public List<RendererInputEvent> Inputs { get; } = new();

        public void Initialize() { }
        public void OnTabCreated(BrowserTab tab) { }
        public void OnTabActivated(BrowserTab tab) { }
        public void OnNavigationRequested(BrowserTab tab, string url, bool isUserInput) { }
        public void OnInputEvent(BrowserTab tab, RendererInputEvent inputEvent) => Inputs.Add(inputEvent);
        public void OnFrameRequested(BrowserTab tab, float viewportWidth, float viewportHeight, float scrollY = 0) { }
        public void OnTabClosed(BrowserTab tab) { }
        public void Shutdown() { }

#pragma warning disable CS0067
        public event System.Action<int, RendererFrameReadyPayload> FrameReceived;
        public event System.Action<int, RendererMetadataChangedPayload> MetadataChanged;
        public event System.Action<int, string> RendererCrashed;
#pragma warning restore CS0067
    }
}
