using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Memory;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.DevTools; // Added this using statement
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Typography;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.Host.Input;

namespace FenBrowser.Host;

/// <summary>
/// Integration layer connecting BrowserHost to the Host render loop.
/// Manages page loading, rendering, and input coordination.
/// Handles Window → UI → Document coordinate translation.
/// </summary>
public class BrowserIntegration
{
    private readonly BrowserHost _browser;
    private readonly SkiaDomRenderer _renderer;
    private FenBrowser.Core.Dom.V2.Element _root;
    private Dictionary<Node, CssComputed> _styles;
    private bool _needsRepaint = true;
    private bool _hasFirstStyledRender = false; // Track first styled render to avoid unstyled initial layout
    private bool _hasStableStyleSnapshot = false;
    private DateTime _lastNavigationTime = DateTime.Now; // Track navigation start time for timeout
    private float _scrollY = 0;
    private float _contentHeight = 0;
    private float _dpiScale = 1.0f;
    private SKSize _lastViewportSize;
    private System.Diagnostics.Stopwatch _frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private bool _hasReceivedViewportSize = false;
    
    // Threading & Event Queue
    private readonly ConcurrentQueue<Action> _eventQueue = new ConcurrentQueue<Action>();
    private readonly BrowserInputQueue _inputQueue;
    private readonly ConcurrentDictionary<long, ContextMenuRequest> _pendingContextMenus = new();
    private readonly Thread _engineThread;
    private readonly AutoResetEvent _wakeEvent = new AutoResetEvent(false);
    private bool _running = true;
    private long _inputSequence;
    private readonly int _maxInputEventsPerFrame;
    private readonly TimeSpan _inputDrainBudget;
    private readonly double _slowInputThresholdMs;
    private long _inputAwaitingFrameSequence;
    private long _inputAwaitingFrameReceiptTimestamp;
    private BrowserInputType _inputAwaitingFrameType;
    private int _inputAwaitingFrameReceiptThreadId;
    private long _lastInputSequencePublishedInFrame;
    // ── Content Snapshot (lock-free read path for compositor/UI thread) ──
    // The engine thread publishes an immutable snapshot after each RecordFrame.
    // The compositor and UI thread read _latestSnapshot directly — no lock needed.
    // C# reference reads are atomic, so the compositor always sees a consistent
    // (though possibly slightly stale) view of the last committed frame.
    private ContentSnapshot _latestSnapshot;

    // Deferred-disposal slot: the engine thread cannot safely Dispose() the
    // Frame SKPicture immediately after publishing a new ContentSnapshot,
    // because the compositor thread may have already loaded the old reference
    // and be about to call DrawPicture on it.  We retire the previous frame
    // here and dispose it one publish later, guaranteeing the compositor has
    // moved on.  Engine-thread-only — no lock needed.
    private SKPicture _pendingDisposeFrame;

    // Frame seed image for incremental-damage base-frame reuse.
    // Only accessed by the engine thread — no lock needed.
    private SKImage _currentFrameSeedImage;
    private DateTime _currentFrameSeedCreatedUtc = DateTime.MinValue;
    private int _consecutiveBaseFrameReuseCount;
    private const int MaxConsecutiveBaseFrameReuseCount = 120;
    private const double MaxBaseFrameAgeMs = 2000;

    // Compositor scroll preview: written by the UI thread (during wheel/scrollbar drag)
    // and read by both threads.  A simple lock is sufficient — hold time is nanoseconds.
    private bool _hasCompositorScrollPreview;
    private float _compositorPreviewScrollY;
    private readonly object _compositorScrollLock = new();
    private float _lastCompositorScrollDirectionY;

    private readonly SKPictureRecorder _recorder = new SKPictureRecorder();
    private RenderFrameInvalidationReason _pendingInvalidationReasons =
        RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Viewport;
    private string _pendingInvalidationSource = "startup";
    // Remote frame bitmap delivered from a brokered renderer child via shared memory.
    private SKBitmap _remoteFrameBitmap;
    private float _remoteFrameScrollY;
    private uint _remoteFrameSequenceNumber;
    private readonly object _remoteFrameLock = new object();
    
    // Last hit test result (for status bar display)
    private HitTestResult _lastHitTest = HitTestResult.None;
    
    private string _overrideUrl;
    private Element? _highlightedElement;
    private Element? _deferredScrollTarget;
    private string _pendingFragmentTargetId;
    private string _pendingFragmentSourceUrl;
    
    // Safety Net: Polls for DOM updates if events are missed
    private System.Threading.Timer _domPoller;
    private EventLoopSliceTelemetry _lastEventLoopSliceTelemetry = EventLoopSliceTelemetry.Empty;
    private RenderFrameTelemetry? _lastFrameTelemetry;
    
    public string CurrentUrl => _overrideUrl ?? _browser.CurrentUri?.AbsoluteUri ?? "";
    public bool IsLoading { get; private set; }
    public bool CanGoBack => _browser.CanGoBack;
    public bool CanGoForward => _browser.CanGoForward;
    public HitTestResult LastHitTest => _lastHitTest;
    public Element Document => _root;
    public Dictionary<Node, CssComputed> ComputedStyles => _styles;
    public List<CssLoader.CssSource> CssSources => _browser.Engine.LastCssSources;
    public RenderFrameTelemetry? LastFrameTelemetry => _lastFrameTelemetry;
    
    public event Action<string> TitleChanged;
    public event Action<string> UrlChanged;
    
    public BrowserHost Host => _browser;
    public event Action<bool> LoadingChanged;
    public event Action<SKBitmap> FaviconChanged; // [NEW]
    public event Action NeedsRepaint;
    public event Action<string> LinkClicked;
    public event Action<string> ConsoleMessage;
    public event Action<HitTestResult> HitTestChanged;
    public event Action<float, float> ScrollChanged;
    public event Action<string> ClipboardWriteRequested;
    
    // --- NEW: Structured Navigation Events (10/10) ---
    // Reserved for external subscribers; raising sites are pending in the navigation refactor.
#pragma warning disable CS0067
    public event Action<NavigationEventArgs> OnNavigationStarted;
    public event Action<NavigationEventArgs> OnNavigationCompleted;
    public event Action<NavigationErrorArgs> OnNavigationFailed;
#pragma warning restore CS0067
    
    // --- Scroll Physics ---
    private readonly ScrollPhysics _scrollPhysics = new();
    private const float WheelScrollStepPixels = 40f;
    private const float SmoothWheelScrollResponse = 9f;
    private const float SmoothWheelScrollSnapPixels = 0.5f;
    private const float RemoteFrameFutureScrollTolerancePixels = 0.5f;
    private bool _smoothWheelScrollActive;
    private float _smoothWheelScrollTargetY;

    private readonly record struct EventLoopSliceTelemetry(
        int ProcessedTaskCount,
        int InteractiveTaskCount,
        int UserVisibleTaskCount,
        int BackgroundTaskCount,
        int DeferredBackgroundTaskCount,
        bool PrioritizedInteractive,
        double ReservedRenderBudgetMs,
        TaskQueueSnapshot QueueSnapshot)
    {
        public static EventLoopSliceTelemetry Empty => new(
            0,
            0,
            0,
            0,
            0,
            false,
            0,
            new TaskQueueSnapshot(0, 0, 0, 0, 0));
    }
    
    public FenBrowser.Host.Tabs.BrowserTab OwnerTab { get; }
    
    public BrowserIntegration(FenBrowser.Host.Tabs.BrowserTab ownerTab = null)
    {
        _inputQueue = new BrowserInputQueue(Math.Max(4, ReadPositiveIntEnvironment("FEN_INPUT_QUEUE_CAPACITY", 256)));
        _maxInputEventsPerFrame = ReadPositiveIntEnvironment("FEN_INPUT_MAX_EVENTS_PER_FRAME", 32);
        _inputDrainBudget = TimeSpan.FromMilliseconds(
            ReadPositiveIntEnvironment("FEN_INPUT_DRAIN_BUDGET_MS", 2));
        _slowInputThresholdMs = ReadPositiveIntEnvironment("FEN_INPUT_SLOW_THRESHOLD_MS", 50);
        OwnerTab = ownerTab;
        _browser = new BrowserHost(options: BrowserIntegrationRuntime.CreateBrowserHostOptions());
        _renderer = new SkiaDomRenderer();

        _browser.GetWindowRectDelegate = GetWebDriverWindowRect;
        _browser.SetWindowRectDelegate = SetWebDriverWindowRect;
        _browser.MaximizeWindowDelegate = MaximizeWebDriverWindow;
        _browser.MinimizeWindowDelegate = MinimizeWebDriverWindow;
        _browser.FullscreenWindowDelegate = FullscreenWebDriverWindow;

        // Inject the actual renderer into BrowserHost so its InputManager
        // hit tests use the populated paint tree instead of a stale copy.
        _browser.SetActiveRenderer(_renderer);

        // Wire up the visual rect provider so JavaScript's getBoundingClientRect()
        // can access layout geometry from the renderer.
        // This bridges DOM Element -> LayoutBox -> absolute viewport coordinates -> DOMRect.
        FenBrowser.FenEngine.Scripting.JavaScriptEngine.SetVisualRectProvider(element =>
        {
            if (element == null || _renderer == null)
                return null;

            if (!_rendererLock.TryEnterReadLock(2))
                return null; // engine is mid-layout; element has no box yet
            try
            {
                var box = _renderer.GetElementBox(element);
                if (box == null)
                    return null;

                return box.BorderBox;
            }
            finally
            {
                _rendererLock.ExitReadLock();
            }
        });

        // CRITICAL: Inject our renderer into CustomHtmlEngine so it uses the same
        // renderer instance for visual rect lookups. Otherwise CustomHtmlEngine
        // creates its own stale renderer that never has layout data.
        _browser.Engine.SetExternalRenderer(_renderer);

        // Let JS scrollIntoView() drive the document scroll the host owns.
        // scrollIntoView() defaults to block:"start" (align the element's top to
        // the viewport top), unlike ScrollToElement's nearest/bottom policy, so
        // resolve the element rect and scroll its top to the top of the viewport.
        FenBrowser.FenEngine.Scripting.JavaScriptEngine.SetScrollToElementProvider(element =>
        {
            if (element == null)
            {
                return;
            }

            // An element inside a nested browsing context must scroll *that* frame's
            // viewport, not the top-level page. The frame's Document is attached as a
            // child of its <iframe> host, so the host appears in the element's ancestor
            // chain. Acid2's reftest depends on this: #top.scrollIntoView() inside the
            // 400x300 frame reveals the face within the frame, leaving the outer page put.
            var iframeHost = FindContainingIframe(element);
            if (iframeHost != null)
            {
                ScrollIframeToElement(iframeHost, element);
                return;
            }

            var rect = GetElementRect(element);
            if (rect.HasValue)
            {
                ScrollToY(rect.Value.Top);
            }
            else
            {
                ScrollToElement(element);
            }
        });

        // Wire browser events
        _browser.Navigated += (s, e) => 
        {
            UpdatePendingFragmentNavigation(e);
            if (_overrideUrl == null)
            {
                UrlChanged?.Invoke(CurrentUrl);
            }
        };
        
        _browser.LoadingChanged += (s, loading) =>
        {
            IsLoading = loading;
            // Clear CSS caches on start of navigation to prevent memory buildup
            if (loading)
            {
                _lastNavigationTime = DateTime.Now;
                _hasFirstStyledRender = false;
                _hasStableStyleSnapshot = false;
                _root = null;
                _styles = null;
                _deferredScrollTarget = null;
                _scrollY = 0f;
                ResetCompositorScrollPreview();
                ClearRemoteFrame();
                _contentHeight = 0f;
                ScrollChanged?.Invoke(_scrollY, _contentHeight);

                // Defensive reset: page-driven navigations can bypass NavigateInternal.
                // Clear committed frame/seed so first frame for the new document cannot reuse stale pixels.
                var oldSnapshot = _latestSnapshot;
                _latestSnapshot = null;
                oldSnapshot?.Frame?.Dispose();
                _pendingDisposeFrame?.Dispose();
                _pendingDisposeFrame = null;
                _currentFrameSeedImage?.Dispose();
                _currentFrameSeedImage = null;
                _currentFrameSeedCreatedUtc = DateTime.MinValue;
                _consecutiveBaseFrameReuseCount = 0;

                // Abort any pending JS modal dialog (alert/confirm/prompt)
                // so the blocked JS worker thread can unwind during navigation.
                FenBrowser.FenEngine.Scripting.JsDialogBridge.AbortPending?.Invoke();

                CssLoader.ClearCaches();
                EngineLogBridge.Info("[BrowserIntegration] Cleared CSS caches for new navigation", LogCategory.General);
                RequestFrame(RenderFrameInvalidationReason.Navigation, "BrowserHost.LoadingChanged");
            }
            LoadingChanged?.Invoke(loading);
        };
        
        _browser.RepaintReady += (s, e) =>
        {
            if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true) return;

            // Sync DOM root and styles immediately on every RepaintReady.
            // PROGRESSIVE: Always adopt latest styles, even if they change incrementally.
            var snapshot = _browser.GetRenderSnapshot();
            bool hadFirstStyledRender = _hasFirstStyledRender;
            _hasStableStyleSnapshot = snapshot.HasStableStyles;
            bool rootChanged = snapshot.Root != null && !ReferenceEquals(snapshot.Root, _root);
            _root = snapshot.Root;
            
            // Use the snapshot styles directly - the renderer detects changes by reference
            // and the engine provides a new dictionary reference when styles actually change.
            // Creating copies here causes reference ping-pong between poller and RepaintReady.
            bool stylesChanged = snapshot.Styles != null && !ReferenceEquals(snapshot.Styles, _styles);
            if (snapshot.Styles != null && !ReferenceEquals(snapshot.Styles, _styles))
            {
                _styles = snapshot.Styles;
                EngineLogBridge.Info($"[BrowserIntegration] RepaintReady: Styles updated ({snapshot.Styles.Count} rules)", LogCategory.Rendering);
            }
            
            // Seed fragment intent before the first post-navigation frame.
            UpdatePendingFragmentNavigation(_browser.CurrentUri);

            EngineLogBridge.Info($"[BrowserIntegration] RepaintReady: Root={(_root?.TagName ?? "NULL")}, Styles={_styles?.Count ?? 0}", LogCategory.Rendering);

            // Wake the engine thread. RecordFrame will call NeedsRepaint?.Invoke() only
            // after a valid frame is committed. Many repaint-ready signals are paint-only
            // wakeups, especially image/animation callbacks; avoid upgrading those to
            // DOM+style work unless the snapshot or dirty flags require it.
            RequestFrame(
                ClassifyRepaintReadyInvalidation(_root, rootChanged, stylesChanged, hadFirstStyledRender),
                "BrowserHost.RepaintReady");
        };
        
        _browser.TitleChanged += (s, title) => TitleChanged?.Invoke(title);
        
        // [NEW] Wire favicon
        _browser.FaviconChanged += (s, icon) => FaviconChanged?.Invoke(icon);
        
        _browser.ConsoleMessage += msg => ConsoleMessage?.Invoke(msg);
        
        // Start Engine Thread.
        // Use a 16 MB stack instead of the .NET default (~1 MB on Windows). Browser
        // engines routinely run page JS whose execution chains through host callbacks
        // (event dispatch → DOM mutation → style/layout invalidation → user JS again),
        // and large React/Angular bundles can produce native-recursion depths well
        // past 1 MB before the engine's own heap-frame stack ever notices. V8 uses
        // 1 MB but JS recursion stays on its own internal stack; our cross-cutting
        // C# helpers cannot. 16 MB matches what Chromium reserves for its renderer
        // threads on Windows.
        const int EngineThreadStackBytes = 16 * 1024 * 1024;
        _engineThread = new Thread(EngineLoop, EngineThreadStackBytes) { IsBackground = true, Name = "FenEngine-Render" };
        _engineThread.Start();
        
        // Timer to poll for missed updates (conservative 500ms - event-driven is primary)
        // PROGRESSIVE: RepaintReady event triggers immediate updates, polling is just fallback
        _domPoller = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_browser == null) return;
                var snapshot = _browser.GetRenderSnapshot();
                var actualDom = snapshot.Root;
                var actualStyles = snapshot.Styles;
                var snapshotStable = snapshot.HasStableStyles;
                _hasStableStyleSnapshot = snapshotStable;

                bool needsSync = false;

                // PROGRESSIVE: Always sync DOM if available, don't wait for styles
                bool rootChanged = actualDom != null && actualDom != _root;
                if (rootChanged)
                {
                    _root = actualDom;
                    needsSync = true;
                }

            // PROGRESSIVE: Adopt styles whenever they arrive, even if partial
            // Use ReferenceEquals for dictionary identity check - the engine provides
            // new dictionary instances when styles actually change via UpdateRenderState
            bool stylesChanged = actualStyles != null && !ReferenceEquals(actualStyles, _styles);
            if (stylesChanged)
            {
                _styles = actualStyles;
                needsSync = true;
                EngineLogBridge.Info($"[BrowserIntegration] Poller: Styles updated ({actualStyles.Count} rules)", LogCategory.Rendering);
            }

                if (needsSync)
                {
                    RequestFrame(
                        RenderFrameInvalidationReason.Dom | RenderFrameInvalidationReason.Style | RenderFrameInvalidationReason.Diagnostics,
                        "BrowserIntegration.DomPoller");
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Debug($"[BrowserIntegration] DOM poller callback failed: {ex.Message}", LogCategory.Rendering);
            }
        }, null, 100, 500); // Start after 100ms, poll every 500ms (event-driven is primary)

        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current != null)
        {
            FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.FrameReceived += OnFrameReceivedFromRenderer;
            FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.MetadataChanged += OnMetadataChangedFromRenderer;
        }

        // Wire CSS animation/transition engine → repaint loop.
        // CssAnimationEngine runs a 16ms timer that interpolates values but never signals
        // the engine thread on its own. Subscribe here so each animation tick wakes the
        // engine loop and the interpolated frame is actually rendered.
        CssAnimationEngine.Instance.OnAnimationFrame += _ =>
        {
            RequestFrame(RenderFrameInvalidationReason.Animation, "CssAnimationEngine");
        };
    }

    private (int Left, int Top, int Right, int Bottom) GetViewportInsets()
    {
        var wm = WindowManager.Instance;
        var bounds = ChromeManager.Instance.GetWebContentBounds();

        int left = Math.Max(0, (int)Math.Round(bounds.Left));
        int top = Math.Max(0, (int)Math.Round(bounds.Top));
        int right = Math.Max(0, wm.LogicalWidth - (int)Math.Round(bounds.Right));
        int bottom = Math.Max(0, wm.LogicalHeight - (int)Math.Round(bounds.Bottom));
        return (left, top, right, bottom);
    }

    private WindowRect GetWebDriverWindowRect()
    {
        var wm = WindowManager.Instance;
        var window = wm.Window;
        var insets = GetViewportInsets();

        int width = Math.Max(1, wm.LogicalWidth - insets.Left - insets.Right);
        int height = Math.Max(1, wm.LogicalHeight - insets.Top - insets.Bottom);
        int x = (window?.Position.X ?? 0) + insets.Left;
        int y = (window?.Position.Y ?? 0) + insets.Top;

        return new WindowRect
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
    }

    private WindowRect SetWebDriverWindowRect(int? x, int? y, int? width, int? height)
    {
        var wm = WindowManager.Instance;
        var window = wm.Window;
        if (window == null)
        {
            return GetWebDriverWindowRect();
        }

        var insets = GetViewportInsets();
        int currentViewportWidth = Math.Max(1, wm.LogicalWidth - insets.Left - insets.Right);
        int currentViewportHeight = Math.Max(1, wm.LogicalHeight - insets.Top - insets.Bottom);
        int targetViewportWidth = Math.Max(1, width ?? currentViewportWidth);
        int targetViewportHeight = Math.Max(1, height ?? currentViewportHeight);

        int outerWidth = Math.Max(1, targetViewportWidth + insets.Left + insets.Right);
        int outerHeight = Math.Max(1, targetViewportHeight + insets.Top + insets.Bottom);
        window.Size = new Silk.NET.Maths.Vector2D<int>(outerWidth, outerHeight);

        if (x.HasValue || y.HasValue)
        {
            int targetX = x ?? ((window.Position.X) + insets.Left);
            int targetY = y ?? ((window.Position.Y) + insets.Top);
            window.Position = new Silk.NET.Maths.Vector2D<int>(targetX - insets.Left, targetY - insets.Top);
        }

        UpdateViewport(new SKSize(targetViewportWidth, targetViewportHeight));
        return GetWebDriverWindowRect();
    }

    private WindowRect MaximizeWebDriverWindow()
    {
        var window = WindowManager.Instance.Window;
        if (window != null)
        {
            window.WindowState = Silk.NET.Windowing.WindowState.Maximized;
        }

        return GetWebDriverWindowRect();
    }

    private WindowRect MinimizeWebDriverWindow()
    {
        var window = WindowManager.Instance.Window;
        if (window != null)
        {
            window.WindowState = Silk.NET.Windowing.WindowState.Minimized;
        }

        return GetWebDriverWindowRect();
    }

    private WindowRect FullscreenWebDriverWindow()
    {
        var window = WindowManager.Instance.Window;
        if (window != null)
        {
            window.WindowState = Silk.NET.Windowing.WindowState.Fullscreen;
        }

        return GetWebDriverWindowRect();
    }

    private void RequestFrame(RenderFrameInvalidationReason reason, string source, bool notifyUi = false)
    {
        if (reason == RenderFrameInvalidationReason.None)
        {
            reason = RenderFrameInvalidationReason.Unknown;
        }

        _pendingInvalidationReasons |= reason;
        _pendingInvalidationSource = MergeInvalidationSource(_pendingInvalidationSource, source);
        _needsRepaint = true;
        _wakeEvent.Set();

        if (notifyUi)
        {
            NeedsRepaint?.Invoke();
        }
    }

    internal static RenderFrameInvalidationReason ClassifyRepaintReadyInvalidation(
        Node root,
        bool rootChanged,
        bool stylesChanged,
        bool hasFirstStyledRender)
    {
        if (!hasFirstStyledRender)
        {
            return RenderFrameInvalidationReason.Navigation |
                   RenderFrameInvalidationReason.Dom |
                   RenderFrameInvalidationReason.Style;
        }

        if (rootChanged || stylesChanged ||
            root?.StyleDirty == true ||
            root?.ChildStyleDirty == true)
        {
            return RenderFrameInvalidationReason.Dom |
                   RenderFrameInvalidationReason.Style;
        }

        if (root?.LayoutDirty == true || root?.ChildLayoutDirty == true)
        {
            return RenderFrameInvalidationReason.Layout |
                   RenderFrameInvalidationReason.Paint;
        }

        return RenderFrameInvalidationReason.Paint;
    }

    internal static bool IsFirstRenderSnapshotPresentable(
        Node root,
        Dictionary<Node, CssComputed> styles,
        bool hasStableStyles)
    {
        return root != null && styles != null && hasStableStyles;
    }

    internal static bool IsFirstContentFrameReady(
        Node root,
        Dictionary<Node, CssComputed> styles,
        bool hasStableStyles,
        string url,
        bool isLoading)
    {
        if (!IsFirstRenderSnapshotPresentable(root, styles, hasStableStyles))
        {
            return false;
        }

        return !IsNewTabSurfaceUrl(url) || !isLoading;
    }

    private void DeferPendingFrame()
    {
        _needsRepaint = false;
    }

    private void ClearPendingFrameRequest()
    {
        _needsRepaint = false;
        _pendingInvalidationReasons = RenderFrameInvalidationReason.None;
        _pendingInvalidationSource = "idle";
    }

    private static string MergeInvalidationSource(string existing, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.IsNullOrWhiteSpace(existing) ? "unknown" : existing;
        }

        if (string.IsNullOrWhiteSpace(existing) || string.Equals(existing, "idle", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        if (string.Equals(existing, source, StringComparison.Ordinal))
        {
            return existing;
        }

        return existing.Contains(source, StringComparison.Ordinal)
            ? existing
            : existing + "|" + source;
    }

    private static bool ShouldEmitVerificationReport(RenderFrameInvalidationReason invalidationReasons)
    {
        var lowSignalReasons =
            RenderFrameInvalidationReason.Timer |
            RenderFrameInvalidationReason.Animation |
            RenderFrameInvalidationReason.Scroll |
            RenderFrameInvalidationReason.Input |
            RenderFrameInvalidationReason.Overlay;

        return (invalidationReasons & ~lowSignalReasons) != RenderFrameInvalidationReason.None;
    }

    private void OnFrameReceivedFromRenderer(int tabId, FenBrowser.Host.ProcessIsolation.RendererFrameReadyPayload payload)
    {
        if (OwnerTab != null && OwnerTab.Id != tabId) return;

        // Track the renderer-reported content height so the host can draw a viewport
        // scrollbar and clamp wheel scrolling without re-running layout in-process.
        if (payload != null && payload.ContentHeight > 0f &&
            Math.Abs(_contentHeight - payload.ContentHeight) > 0.5f)
        {
            _contentHeight = payload.ContentHeight;
            ScrollChanged?.Invoke(_scrollY, _contentHeight);
        }

        // If the payload carries raw BGRA pixels (from shared memory), decode into an SKBitmap
        // so Render() can composite it directly.
        if (payload?.PixelData != null && payload.PixelData.Length > 0 &&
            payload.SurfaceWidth > 0 && payload.SurfaceHeight > 0)
        {
            if (IsRemoteFrameAheadOfLiveScroll(payload.ScrollY))
            {
                EngineLogBridge.Debug(
                    $"[BrowserIntegration] Ignored future remote frame during compositor scroll: frameScrollY={payload.ScrollY:F1} liveScrollY={GetEffectiveScrollY():F1} tab={tabId}",
                    LogCategory.Rendering);
                RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.ProcessIsolation, "RendererChild.FutureFrameIgnored", notifyUi: true);
                return;
            }

            int w = (int)payload.SurfaceWidth;
            int h = (int)payload.SurfaceHeight;
            int expectedBytes = w * h * 4;

            if (payload.PixelData.Length >= expectedBytes)
            {
                try
                {
                    // Wrap the raw BGRA bytes in a GCHandle so SkiaSharp can reference them
                    // without an additional copy, then rasterize into an owned SKBitmap.
                    var imageInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                    SKBitmap newBitmap;
                    var handle = System.Runtime.InteropServices.GCHandle.Alloc(payload.PixelData, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        var ptr = handle.AddrOfPinnedObject();
                        // FromPixels copies the data into an owned bitmap so we can free the pin immediately.
                        newBitmap = SKBitmap.Decode(SKData.Create(ptr, expectedBytes))
                            ?? InstallPixelsCopy(imageInfo, ptr);
                    }
                    finally
                    {
                        handle.Free();
                    }

                    bool acceptedRemoteFrame = false;
                    lock (_remoteFrameLock)
                    {
                        if (_remoteFrameBitmap != null &&
                            payload.FrameSequenceNumber != 0 &&
                            payload.FrameSequenceNumber <= _remoteFrameSequenceNumber)
                        {
                            newBitmap.Dispose();
                            EngineLogBridge.Debug($"[BrowserIntegration] Ignored out-of-order remote frame: seq={payload.FrameSequenceNumber} lastSeq={_remoteFrameSequenceNumber} tab={tabId}", LogCategory.Rendering);
                        }
                        else
                        {
                            _remoteFrameBitmap?.Dispose();
                            _remoteFrameBitmap = newBitmap;
                            _remoteFrameScrollY = Math.Max(0f, payload.ScrollY);
                            if (payload.FrameSequenceNumber != 0)
                            {
                                _remoteFrameSequenceNumber = payload.FrameSequenceNumber;
                            }

                            acceptedRemoteFrame = true;
                        }
                    }

                    if (acceptedRemoteFrame)
                    {
                        EngineLogBridge.Debug($"[BrowserIntegration] Remote frame decoded: {w}x{h} seq={payload.FrameSequenceNumber} scrollY={payload.ScrollY:F1} for tab={tabId}", LogCategory.Rendering);
                        if (Math.Abs(payload.ScrollY - _scrollY) <= 0.5f)
                        {
                            ResetCompositorScrollPreview();
                        }
                    }
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Warn($"[BrowserIntegration] Failed to decode remote frame pixels for tab={tabId}: {ex.Message}", LogCategory.Rendering);
                }
            }
        }

        if (payload != null && payload.TotalDurationMs > 0)
        {
            var remoteEntry = new LogEntry
            {
                Category = LogCategory.Performance,
                Level = payload.WatchdogTriggered ? FenBrowser.Core.Logging.LogLevel.Warn : FenBrowser.Core.Logging.LogLevel.Info,
                Component = "BrowserIntegration.RemoteFrame",
                Message = "[FRAME] RemoteCommit",
                DurationMs = (long)Math.Round(Math.Max(0d, payload.TotalDurationMs)),
                Data = new Dictionary<string, object>
                {
                    ["url"] = payload.Url ?? "about:blank",
                    ["frameSequence"] = payload.FrameSequenceNumber,
                    ["scrollY"] = payload.ScrollY,
                    ["requestedBy"] = payload.RequestedBy ?? "RendererChild.FrameRequest",
                    ["invalidationReason"] = payload.InvalidationReason ?? RenderFrameInvalidationReason.ProcessIsolation.ToString(),
                    ["rasterMode"] = payload.RasterMode ?? RenderFrameRasterMode.Full.ToString(),
                    ["usedDamageRasterization"] = payload.UsedDamageRasterization,
                    ["damageAreaRatio"] = payload.DamageAreaRatio,
                    ["layoutUpdated"] = payload.LayoutUpdated,
                    ["paintTreeRebuilt"] = payload.PaintTreeRebuilt,
                    ["domNodeCount"] = payload.DomNodeCount,
                    ["boxCount"] = payload.BoxCount,
                    ["paintNodeCount"] = payload.PaintNodeCount,
                    ["watchdogTriggered"] = payload.WatchdogTriggered,
                    ["watchdogReason"] = payload.WatchdogReason ?? string.Empty
                }
            };

            LogManager.Log(remoteEntry);
        }

        RequestFrame(RenderFrameInvalidationReason.ProcessIsolation, "RendererChild.FrameReady", notifyUi: true);
    }

    private void OnMetadataChangedFromRenderer(int tabId, FenBrowser.Host.ProcessIsolation.RendererMetadataChangedPayload payload)
    {
        if (OwnerTab != null && OwnerTab.Id != tabId)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload?.Title))
        {
            TitleChanged?.Invoke(payload.Title.Trim());
        }

        if (payload?.FaviconChanged == true)
        {
            if (payload.FaviconPngBytes == null || payload.FaviconPngBytes.Length == 0)
            {
                FaviconChanged?.Invoke(null);
                NeedsRepaint?.Invoke();
            }
            else
            {
                try
                {
                    var bitmap = SKBitmap.Decode(payload.FaviconPngBytes);
                    if (bitmap != null)
                    {
                        FaviconChanged?.Invoke(bitmap);
                        NeedsRepaint?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Warn($"[BrowserIntegration] Failed to decode renderer favicon for tab={tabId}: {ex.Message}", LogCategory.Rendering);
                }
            }
        }
    }

    /// <summary>
    /// Creates an owned SKBitmap by copying raw BGRA pixels from an unmanaged pointer.
    /// Used as a fallback when SKBitmap.Decode cannot interpret raw pixel data.
    /// </summary>
    private static SKBitmap InstallPixelsCopy(SKImageInfo imageInfo, IntPtr src)
    {
        var bm = new SKBitmap(imageInfo);
        unsafe
        {
            int bytes = imageInfo.BytesSize;
            Buffer.MemoryCopy((void*)src, (void*)bm.GetPixels(), bytes, bytes);
        }
        return bm;
    }

    public void HighlightElement(Element? element)
    {
        if (_highlightedElement != element)
        {
            _highlightedElement = element;
            NeedsRepaint?.Invoke();
        }
    }
    
    /// <summary>
    /// Invalidate computed style cache for an element (for live CSS editing).
    /// </summary>
    public void InvalidateComputedStyle(Element element)
    {
        // Remove from cache so it gets recomputed on next paint
        _styles.Remove(element);
        // Also clear browser's internal cache
        _browser.ComputedStyles.Remove(element);
    }
    
    /// <summary>
    /// Request a repaint (for live CSS editing).
    /// </summary>
    public void RequestRepaint()
    {
        RequestFrame(RenderFrameInvalidationReason.HostRequest, "BrowserIntegration.RequestRepaint", notifyUi: true);
    }

    public RenderContext? TryCreateRenderContextSnapshot(int timeoutMs = 10)
    {
        if (!_rendererLock.TryEnterReadLock(Math.Max(0, timeoutMs)))
        {
            return null;
        }

        try
        {
            return _renderer.CreateRenderContext();
        }
        finally
        {
            _rendererLock.ExitReadLock();
        }
    }
    
    public async Task<object?> EvaluateScriptAsync(string script)
    {
        try
        {
            return await _browser.ExecuteScriptAsync(script)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "Error: Script execution failed or timed out.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Guards the SkiaDomRenderer across the engine thread (writer — layout/paint/raster, can hold for 100ms+)
    /// and the UI/compositor thread (reader — hit-testing, element rects, highlights).
    /// ReaderWriterLockSlim allows concurrent reads (e.g. rapid mouse-move hit-tests) while blocking
    /// readers only when the writer needs exclusive access.  Readers use TryEnterReadLock with a
    /// short timeout so the UI thread never blocks waiting for a long layout pass.
    /// </summary>
    private readonly ReaderWriterLockSlim _rendererLock = new(LockRecursionPolicy.SupportsRecursion);

    // Hit-test cache: when the UI thread cannot acquire the renderer read lock (engine is mid-layout),
    // we return the last known result.  This keeps the cursor responsive during page load.
    private HitTestResult _cachedHitTest = HitTestResult.None;
    private readonly object _hitTestCacheLock = new();

    private void EngineLoop()
    {
        var coordinator = _browser.Engine.EventLoopCoordinator;
        coordinator.OnWorkEnqueued += () => _wakeEvent.Set();

        while (_running)
        {
            // Each iteration is one frame tick with a 16.6ms budget (60fps target)
            var deadline = new FenBrowser.Core.Deadlines.FrameDeadline(16.6, "Frame");

            // Stage 1: Drain host → engine input events
            DrainInputQueue();
            DrainEventQueue();

            if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
            {
                _wakeEvent.WaitOne(100);
                continue;
            }

            // Stage 2: Pump JS event loop (budget-gated: reserve 8ms for render)
            var sliceTelemetry = PumpJSEventLoop(deadline, coordinator);
            _lastEventLoopSliceTelemetry = sliceTelemetry;

            // Stage 3: Style sync + Layout + Paint + Present
            bool rendered = SyncAndRender(deadline, coordinator);

            // Stage 4: Adaptive wait
            bool inputPending = _inputQueue.Count > 0;
            bool hasWork = _needsRepaint ||
                           inputPending ||
                           coordinator.HasPendingTasks ||
                           coordinator.HasPendingMicrotasks ||
                           coordinator.HasPendingDelayedTasks ||
                           sliceTelemetry.ProcessedTaskCount > 0;
            int waitMs;
            if (inputPending) waitMs = 0;
            else if (_needsRepaint) waitMs = 16;
            else if (sliceTelemetry.ProcessedTaskCount > 0) waitMs = 1;
            else if (hasWork) waitMs = coordinator.GetSuggestedWaitMilliseconds();
            else waitMs = -1; // Block until woken

            _wakeEvent.WaitOne(waitMs);
        }
    }

    /// <summary>
    /// Stage 1: Drain queued host→engine input events (mouse, keyboard, resize).
    /// </summary>
    private void DrainEventQueue()
    {
        while (_eventQueue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { EngineLogBridge.Error($"[EngineLoop] Action error: {ex.Message}", LogCategory.General); }
        }
    }

    /// <summary>
    /// Stage 2: Pump JS tasks/microtasks, budget-gated to leave time for rendering.
    /// Returns the number of tasks processed.
    /// </summary>
    private EventLoopSliceTelemetry PumpJSEventLoop(FenBrowser.Core.Deadlines.FrameDeadline deadline, FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator coordinator)
    {
        var config = RenderPerformanceConfiguration.Current;
        config.Normalize();

        int processed = 0;
        int interactive = 0;
        int userVisible = 0;
        int background = 0;
        bool prioritizedInteractive = false;
        double reservedRenderBudgetMs = _needsRepaint
            ? config.BusyFrameReservedRenderBudgetMs
            : config.DefaultReservedRenderBudgetMs;

        while (!deadline.IsExpired &&
               deadline.Remaining.TotalMilliseconds > reservedRenderBudgetMs &&
               processed < config.MaxTasksPerFrame)
        {
            try
            {
                var snapshotBefore = coordinator.GetTaskSnapshot();
                bool interactivePending = snapshotBefore.InteractiveCount > 0;
                int nonInteractiveProcessed = userVisible + background;

                if (_needsRepaint &&
                    background >= config.MaxBackgroundTasksPerBusyFrame &&
                    snapshotBefore.BackgroundCount > 0 &&
                    !interactivePending)
                {
                    break;
                }

                if (_needsRepaint &&
                    nonInteractiveProcessed >= config.MaxNonInteractiveTasksPerBusyFrame &&
                    !interactivePending)
                {
                    break;
                }

                bool preferInteractive = _needsRepaint || interactivePending;
                prioritizedInteractive |= preferInteractive;

                var result = coordinator.ProcessNextTaskDetailed(preferInteractive);
                if (!result.Processed)
                {
                    break;
                }

                processed++;
                switch (result.PriorityGroup)
                {
                    case TaskPriorityGroup.Interactive:
                        interactive++;
                        break;
                    case TaskPriorityGroup.UserVisible:
                        userVisible++;
                        break;
                    default:
                        background++;
                        break;
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Error($"[EngineLoop] Coordinator error: {ex}", LogCategory.JavaScript);
                break;
            }
        }

        var snapshotAfter = coordinator.GetTaskSnapshot();
        return new EventLoopSliceTelemetry(
            processed,
            interactive,
            userVisible,
            background,
            _needsRepaint ? snapshotAfter.BackgroundCount : 0,
            prioritizedInteractive,
            reservedRenderBudgetMs,
            snapshotAfter);
    }

    /// <summary>
    /// Stage 3: Sync DOM/styles from browser host, gate on CSS readiness, then render.
    /// Returns true if a frame was recorded.
    /// </summary>
    private bool SyncAndRender(FenBrowser.Core.Deadlines.FrameDeadline deadline, FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator coordinator)
    {
        try
        {
            if ((!_needsRepaint && coordinator.CurrentPhase != FenBrowser.Core.Engine.EnginePhase.Layout) || _lastViewportSize.Width <= 0)
            {
                if (_needsRepaint && _lastViewportSize.Width <= 0 && _hasReceivedViewportSize)
                    Console.WriteLine("[DBG-EL] WARNING: _needsRepaint=true but _lastViewportSize.Width=0!");
                return false;
            }

            // Sync latest state from browser host
            var snapshot = _browser.GetRenderSnapshot();
            _root = snapshot.Root;
            _styles = snapshot.Styles;
            _hasStableStyleSnapshot = snapshot.HasStableStyles;

            if (!_hasFirstStyledRender && IsFirstContentFrameReady(_root, _styles, _hasStableStyleSnapshot, CurrentUrl, IsLoading))
            {
                _hasFirstStyledRender = true;
            }

        _rendererLock.EnterWriteLock();
        try
        {
            RecordFrame(_lastViewportSize);
        }
        finally
        {
            _rendererLock.ExitWriteLock();
        }
            return true;
        }
        catch (Exception ex)
        {
            EngineLogBridge.Error($"[EngineLoop] CRASH: {ex}", LogCategory.Rendering);
            return false;
        }
    }
    
    private void PostToEngine(Action action)
    {
        _eventQueue.Enqueue(action);
        _wakeEvent.Set();
    }
    
    /// <summary>
    /// Navigate from trusted user input (address bar/search box).
    /// </summary>
    public Task NavigateAsync(string url) => NavigateInternalAsync(url, isUserInput: true);

    /// <summary>
    /// Navigate from programmatic sources (WebDriver/script/callback paths).
    /// </summary>
    public Task NavigateProgrammaticAsync(string url) => NavigateInternalAsync(url, isUserInput: false);

    private async Task NavigateInternalAsync(string url, bool isUserInput)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        url = NormalizeInternalFenUrl(url);

        // Reset navigation timing for unstyled layout skip
        _lastNavigationTime = DateTime.Now;
        _hasFirstStyledRender = false;
        _hasStableStyleSnapshot = false;
        // Navigation must start from top; carrying prior page scroll causes blank/shifted first paints
        // on short documents (e.g., Acid2 reference page).
        _scrollY = 0f;
        ResetCompositorScrollPreview(resetDirection: true);
        ClearRemoteFrame();
        _contentHeight = 0f;
        var oldSnapshot = _latestSnapshot;
        _latestSnapshot = null;
        oldSnapshot?.Frame?.Dispose();
        // Clear the DOM root so the engine thread cannot render the old
        // page while the new document is loading.  RecordFrame skips when
        // both root and styles are null, and RepaintReady restores them
        // when the new document is committed.
        _root = null;
        _styles = null;
        _hasStableStyleSnapshot = false;
        _currentFrameSeedImage?.Dispose();
        _currentFrameSeedImage = null;
        _currentFrameSeedCreatedUtc = DateTime.MinValue;
        _consecutiveBaseFrameReuseCount = 0;
        ScrollChanged?.Invoke(_scrollY, _contentHeight);
        // Defer RequestFrame until RepaintReady fires with the new DOM.
        // Calling RequestFrame here while _activeDom still references the
        // old page causes RecordFrame to render and publish the previous
        // page as _latestSnapshot, which the compositor then draws — the
        // screen appears frozen.  RepaintReady fires when the new document
        // is committed and styles are available.
        _needsRepaint = true;
        _pendingInvalidationReasons |= RenderFrameInvalidationReason.Navigation;
        _pendingInvalidationSource = MergeInvalidationSource(_pendingInvalidationSource, "BrowserIntegration.Navigate");
        _wakeEvent.Set();

        // Post-navigation repaint pulse: ensure the engine keeps waking up during the
        // critical window after navigation so content is displayed as soon as it is ready.
        // Without CSS animations there is no other periodic wake trigger.
        StartPostNavigationRepaintPulse();

        // Add protocol if missing
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("fen://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            // Check for local file path (e.g. C:\... or /...)
            bool isLocalPath = (url.Length >= 2 && url[1] == ':') ||
                               url.StartsWith("/") ||
                               url.StartsWith("\\") ||
                               url.StartsWith("./") ||
                               url.StartsWith("../");

            if (isLocalPath)
            {
                if (isUserInput)
                {
                    try
                    {
                        string fullPath = System.IO.Path.GetFullPath(url);
                        url = "file://" + fullPath.Replace("\\", "/");
                    }
                    catch
                    {
                        url = "file://" + url.Replace("\\", "/");
                    }
                }
                else
                {
                    EngineLogBridge.Warn("[BrowserIntegration] Programmatic local-path normalization blocked; delegating to navigation policy.", LogCategory.Navigation);
                }
            }
            // Default to http for localhost/127.0.0.1 to facilitate debugging
            else if (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            else
            {
                url = "https://" + url;
            }
        }

        EngineLogBridge.Info($"[BrowserIntegration] Navigating to: {url}", LogCategory.General);

        if (url.StartsWith("fen://", StringComparison.OrdinalIgnoreCase))
        {
            // fen://settings is handled by a special widget overlay, not the engine
            if (url.Equals("fen://settings", StringComparison.OrdinalIgnoreCase))
            {
                _overrideUrl = url;
                UrlChanged?.Invoke(url);
                NeedsRepaint?.Invoke();
                return;
            }

            // fen://newtab and other fen:// URLs should be rendered by the engine
            _overrideUrl = null;
            // Fall through to navigate via the browser engine
        }

        _overrideUrl = null;

        try
        {
            if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
            {
                if (OwnerTab != null)
                {
                    FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnNavigationRequested(OwnerTab, url, isUserInput);
                }
                return;
            }

            // Navigation timeout: prevent a stuck network request from wedging
            // the tab indefinitely.  30s matches the default in major browsers.
            using var navCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (isUserInput)
                {
                    await _browser.NavigateUserInputAsync(url).WaitAsync(navCts.Token).ConfigureAwait(false);
                }
                else
                {
                    await _browser.NavigateAsync(url).WaitAsync(navCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (navCts.IsCancellationRequested)
            {
                EngineLogBridge.Warn($"[BrowserIntegration] Navigation timed out (30s): {url}", LogCategory.Navigation);
            }
        }
        catch (Exception ex)
        {
            EngineLogBridge.Error($"[BrowserIntegration] Navigation failed: {ex.Message}", LogCategory.General);
        }
    }

    private static string NormalizeInternalFenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.Equals("fen://newtab/", StringComparison.OrdinalIgnoreCase))
        {
            return "fen://newtab";
        }

        if (url.Equals("fen://settings/", StringComparison.OrdinalIgnoreCase))
        {
            return "fen://settings";
        }

        return url;
    }

    private void UpdatePendingFragmentNavigation(Uri uri)
    {
        var fragment = uri?.Fragment;
        if (string.IsNullOrWhiteSpace(fragment) || fragment == "#")
        {
            _pendingFragmentTargetId = null;
            _pendingFragmentSourceUrl = null;
            return;
        }

        _pendingFragmentTargetId = fragment.TrimStart('#');
        _pendingFragmentSourceUrl = uri.AbsoluteUri;
    }
    
    /// <summary>
    /// Navigate back in history.
    /// </summary>
    public async Task GoBackAsync()
    {
        await _browser.GoBackAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Navigate forward in history.
    /// </summary>
    public async Task GoForwardAsync()
    {
        await _browser.GoForwardAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Refresh the current page.
    /// </summary>
    public async Task RefreshAsync()
    {
        var refreshStartTime = DateTime.Now;
        EngineLogBridge.Info("[BrowserIntegration] RefreshAsync: Starting refresh operation", LogCategory.Navigation);

        _lastNavigationTime = DateTime.Now;
        _hasFirstStyledRender = false;
        _hasStableStyleSnapshot = false;
        RequestFrame(RenderFrameInvalidationReason.Navigation, "BrowserIntegration.Refresh");
        StartPostNavigationRepaintPulse();

        using var navCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            EngineLogBridge.Info($"[BrowserIntegration] RefreshAsync: Calling _browser.RefreshAsync() at {refreshStartTime:O}", LogCategory.Navigation);

            await _browser.RefreshAsync().WaitAsync(navCts.Token).ConfigureAwait(false);

            var refreshDuration = DateTime.Now - refreshStartTime;
            EngineLogBridge.Info($"[BrowserIntegration] RefreshAsync: Completed successfully in {refreshDuration.TotalSeconds:F2}s", LogCategory.Navigation);
        }
        catch (OperationCanceledException) when (navCts.IsCancellationRequested)
        {
            var timeoutDuration = DateTime.Now - refreshStartTime;
            EngineLogBridge.Error(
                $"[BrowserIntegration] RefreshAsync: TIMEOUT after {timeoutDuration.TotalSeconds:F2}s (30s limit). " +
                $"CurrentUrl={CurrentUrl}, IsLoading={IsLoading}", 
                LogCategory.Navigation);
            throw;
        }
        catch (TaskCanceledException ex)
        {
            var timeoutDuration = DateTime.Now - refreshStartTime;
            EngineLogBridge.Error(
                $"[BrowserIntegration] RefreshAsync: Task CANCELLED after {timeoutDuration.TotalSeconds:F2}s. " +
                $"Message: {ex.Message}", 
                LogCategory.Navigation);
            throw;
        }
        catch (Exception ex)
        {
            var duration = DateTime.Now - refreshStartTime;
            EngineLogBridge.Error(
                $"[BrowserIntegration] RefreshAsync: ERROR after {duration.TotalSeconds:F2}s - {ex.GetType().Name}: {ex.Message}", 
                LogCategory.Navigation);
            throw;
        }
    }
    
    /// <summary>
    /// Render the current display list (picture) to the UI canvas.
    /// This is called on the UI thread and is extremely fast.
    /// In brokered mode, draws the remote frame bitmap delivered via shared memory.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport)
    {
        // Brokered renderer path: draw the bitmap received from the child process.
        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
        {
            lock (_remoteFrameLock)
            {
                if (_remoteFrameBitmap != null)
                {
                    float effectiveRemoteScrollY;
                    lock (_compositorScrollLock)
                    {
                        effectiveRemoteScrollY = _hasCompositorScrollPreview ? _compositorPreviewScrollY : _scrollY;
                    }

                    var scrollDelta = effectiveRemoteScrollY - _remoteFrameScrollY;
                    if (Math.Abs(scrollDelta) > 0.5f)
                    {
                        canvas.Save();
                        canvas.Translate(0, -scrollDelta);
                        canvas.DrawBitmap(_remoteFrameBitmap, 0, 0);
                        canvas.Restore();
                    }
                    else
                    {
                        canvas.DrawBitmap(_remoteFrameBitmap, 0, 0);
                    }

                    return;
                }
            }
            // No remote frame yet; draw placeholder while waiting for first delivery.
            DrawPlaceholder(canvas, viewport);
            return;
        }

        // ── Lock-free read path ──
        // The snapshot is published atomically by the engine thread.  C# reference
        // reads are atomic, so we always see a consistent frame (or null).  The
        // compositor scroll preview is protected by a short-duration lock.
        var snapshot = _latestSnapshot;
        List<InputOverlayData> overlays = null;
        Element? highlight = _highlightedElement;

        float effectiveScrollY;
        lock (_compositorScrollLock)
        {
            effectiveScrollY = _hasCompositorScrollPreview ? _compositorPreviewScrollY : _scrollY;
        }

        if (snapshot?.Frame != null)
        {
            var scrollDelta = effectiveScrollY - snapshot.CommittedScrollY;
            if (Math.Abs(scrollDelta) > 0.5f)
            {
                canvas.Save();
                canvas.Translate(0, -scrollDelta);
                canvas.DrawPicture(snapshot.Frame);
                canvas.Restore();
            }
            else
            {
                canvas.DrawPicture(snapshot.Frame);
            }
            if (snapshot.Overlays.Count > 0)
            {
                overlays = snapshot.Overlays;
            }
        }
        else
        {
            DrawPlaceholder(canvas, viewport);
            return;
        }

        if ((overlays == null || overlays.Count == 0) && highlight == null)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(0, -effectiveScrollY);
        if (overlays != null)
        {
            foreach (var overlay in overlays)
            {
                DrawInputOverlay(canvas, overlay);
            }
        }

        if (highlight != null && _rendererLock.TryEnterReadLock(0))
        {
            try
            {
                DrawHighlight(canvas, highlight);
            }
            finally
            {
                _rendererLock.ExitReadLock();
            }
        }
        canvas.Restore();
    }
    
    /// <summary>
    /// Update the display list by recording a new frame.
    /// This can be called on a background thread.
    /// </summary>
    public void RecordFrame(SKSize viewportSize)
    {
        using var frameTimeline = TimelineTracer.Instance.Begin("BrowserIntegration.RecordFrame", "host");
        var invalidationReasons = _pendingInvalidationReasons == RenderFrameInvalidationReason.None
            ? RenderFrameInvalidationReason.Unknown
            : _pendingInvalidationReasons;
        var requestedBy = string.IsNullOrWhiteSpace(_pendingInvalidationSource)
            ? "unspecified"
            : _pendingInvalidationSource;

        // Guard: Prevent recording during/after shutdown
        if (!_running) return;
        
        // Guard: Prevent recording invalid/minimized frames
        if (viewportSize.Width <= 1 || viewportSize.Height <= 1)
        {
            EngineLogBridge.Warn($"[BrowserIntegration] RecordFrame skipped: invalid size {viewportSize}", LogCategory.Rendering);
            return;
        }
        
        // RELAXED GATING: Allow early structural frames
        // Only skip when BOTH root AND styles are missing
        if (_root == null && _styles == null)
        {
            bool outOfProcess = FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true;
            var navAge = DateTime.Now - _lastNavigationTime;

            // Keep the repaint request alive indefinitely while waiting for
            // the DOM after a navigation.  RepaintReady will provide the new
            // root+styles when the document is committed; clearing _needsRepaint
            // here would prevent the engine from ever picking them up.
            // The post-navigation repaint pulse ensures the engine keeps waking.
            EngineLogBridge.Debug("[BrowserIntegration] RecordFrame skipped: awaiting DOM/style snapshot after navigation.", LogCategory.Rendering);
            return;
        }

        if (!_hasFirstStyledRender && !IsFirstContentFrameReady(_root, _styles, _hasStableStyleSnapshot, CurrentUrl, IsLoading))
        {
            EngineLogBridge.Debug("[BrowserIntegration] RecordFrame skipped: awaiting presentable first content frame.", LogCategory.Rendering);
            return;
        }

        // PROGRESSIVE RENDERING: Always render with whatever we have.
        // If styles aren't ready yet, use empty styles - the page will appear with default styling
        // and progressively improve as CSS arrives. This eliminates the "blank page" problem.
        if (_styles == null)
        {
            _styles = new Dictionary<Node, CssComputed>();
            if (_hasFirstStyledRender)
            {
                // Only log after first render - not during initial startup
                EngineLogBridge.Debug("[BrowserIntegration] RecordFrame: rendering with default styles (CSS pending)", LogCategory.Rendering);
            }
        }

        // Guard: Ensure we have a valid HTML element
        string rootTag = _root?.TagName?.ToUpperInvariant() ?? "";
        if (_root != null && rootTag != "HTML")
        {
            EngineLogBridge.Warn($"[BrowserIntegration] RecordFrame skipped: root is '{rootTag}' not HTML", LogCategory.General);
            return;
        }
        
        // Create empty styles dictionary if null but root exists
        if (_styles == null)
        {
            _styles = new Dictionary<Node, CssComputed>();
            EngineLogBridge.Info("[BrowserIntegration] Recording structural frame (no styles yet)", LogCategory.Rendering);
        }
        
        // === DOM → Layout → Paint → Present pipeline ===
        // Note: SkiaDomRenderer.Render() manages its own PipelineContext frame scope internally,
        // so we don't wrap with scoped stages here to avoid nested frame conflicts.
        if (FenBrowser.Core.Logging.DebugConfig.EnableDeepDebug && FenBrowser.Core.Logging.DebugConfig.LogFrameTiming)
        {
            EngineLogBridge.Info($"[TRANSITION] DOM built (HTML). Running layout on real DOM. Viewport={viewportSize}", LogCategory.Layout);
        }

        try
        {
            var viewport = new SKRect(0, 0, viewportSize.Width, viewportSize.Height);
            SKImage reusableSeedImage = null;
            bool canReuseBaseFrame;
            // Guard against carrying an early unstyled/blank frame forward via incremental damage.
            // Reuse is re-enabled automatically once we have produced a styled frame.
            var allowBaseFrameReuse = _hasFirstStyledRender && _styles != null && _styles.Count > 0;
            var nowUtc = DateTime.UtcNow;

            // Base-frame reuse is engine-thread-only; the previous snapshot provides
            // the last committed viewport and scroll for comparison.
            var previousSnapshot = _latestSnapshot;
            canReuseBaseFrame = allowBaseFrameReuse && BaseFrameReusePolicy.CanReuseBaseFrame(
                _currentFrameSeedImage != null,
                previousSnapshot?.ViewportSize ?? SKSize.Empty,
                viewportSize,
                previousSnapshot?.CommittedScrollY ?? 0f,
                _scrollY,
                invalidationReasons,
                _consecutiveBaseFrameReuseCount,
                MaxConsecutiveBaseFrameReuseCount,
                _currentFrameSeedImage != null
                    ? Math.Max(0d, (nowUtc - _currentFrameSeedCreatedUtc).TotalMilliseconds)
                    : double.PositiveInfinity,
                MaxBaseFrameAgeMs);

            if (canReuseBaseFrame)
            {
                reusableSeedImage = _currentFrameSeedImage;
            }

            // Start recording the next presentable frame on the display-list buffer.
            var canvas = _recorder.BeginRecording(viewport);
            if (canReuseBaseFrame && reusableSeedImage != null)
            {
                // Shift the seed image by the scroll delta so that content which
                // overlaps between the old and new viewport appears at the correct
                // screen position.  Without this, the seed (recorded at the previous
                // scroll offset) is drawn at (0,0) and the overlapping region shows
                // stale document positions — every scroll step drifts further from
                // the true content, eventually showing a white viewport.
                // The compositor applies the identical transform in its lock-free
                // read path (see the scrollDelta / Translate block above).
                float scrollDelta = _scrollY - (previousSnapshot?.CommittedScrollY ?? 0f);
                canvas.DrawImage(reusableSeedImage, 0, -scrollDelta);
            }

            // Adjust for scroll
            var scrolledViewport = new SKRect(
                viewport.Left,
                viewport.Top + _scrollY,
                viewport.Right,
                viewport.Bottom + _scrollY
            );

            List<InputOverlayData> frameOverlays = new();
            RenderFrameResult frameResult;
            _rendererLock.EnterWriteLock();
            try
            {
                _renderer.SetGpuRasterContext(
                    WindowManager.Instance.IsOnMainThread
                        ? WindowManager.Instance.GraphicsContext
                        : null);

                // Keep the engine's root viewport scroll state synchronized with the Host's
                // outer document scroll so fixed-position/fixed-background paint logic uses
                // the same viewport origin as the recorded frame.
                _renderer.ScrollManager.SetScrollBounds(
                    null,
                    viewportSize.Width,
                    Math.Max(_contentHeight, viewportSize.Height),
                    viewportSize.Width,
                    viewportSize.Height);
                _renderer.ScrollManager.SetScrollPosition(null, 0, _scrollY);

                canvas.Save();
                canvas.Translate(0, -_scrollY);
                try
                {
                    using (_browser.EnterImageLoaderContext())
                    {
                        frameResult = _renderer.RenderFrame(new RenderFrameRequest
                        {
                            Root = _root,
                            Canvas = canvas,
                            Styles = _styles,
                            Viewport = scrolledViewport,
                            BaseUrl = _browser.CurrentUri?.AbsoluteUri,
                            OnLayoutUpdated = (contentSize, overlays) =>
                            {
                                _contentHeight = contentSize.Height;
                                frameOverlays = overlays != null
                                    ? new List<InputOverlayData>(overlays)
                                    : new List<InputOverlayData>();
                            },
                            SeparateLayoutViewport = viewportSize,
                            HasBaseFrame = canReuseBaseFrame && reusableSeedImage != null,
                            InvalidationReason = invalidationReasons,
                            RequestedBy = requestedBy,
                            EmitVerificationReport = ShouldEmitVerificationReport(invalidationReasons)
                        });
                    }
                }
                finally
                {
                    canvas.Restore();
                }
            }
            finally
            {
                _rendererLock.ExitWriteLock();
            }

            // Finish recording and derive the next seed image from the committed picture.
            var newFrame = _recorder.EndRecording();
            var newSeedImage = CreateSeedImageFromFrame(newFrame, viewportSize);

            // Publish the new content snapshot FIRST — atomic reference write,
            // visible to the compositor thread immediately without any lock.
            // The compositor always reads _latestSnapshot once at the top of
            // Render(); swapping before dispose guarantees it sees either the
            // old (valid) frame or the new frame, never a disposed one.
            var retiringSnapshot = _latestSnapshot;
            _latestSnapshot = new ContentSnapshot(
                newFrame,
                frameOverlays,
                viewportSize,
                _scrollY,
                _contentHeight);
            RecordFirstFrameAfterInput();

            // Retire the previous frame for deferred disposal.  The compositor
            // may still hold a reference to retiringSnapshot, but by the time
            // we get here it has already entered Render() and loaded its local
            // `snapshot` variable — so it either got the new snapshot or the
            // old one.  We defer the actual Dispose() by one publish cycle to
            // guarantee the compositor won't touch a disposed SKPicture.
            _pendingDisposeFrame?.Dispose();
            _pendingDisposeFrame = retiringSnapshot?.Frame;

            // Seed-image management is engine-thread-only; no lock needed.
            _currentFrameSeedImage?.Dispose();
            _currentFrameSeedImage = newSeedImage;
            _currentFrameSeedCreatedUtc = newSeedImage != null ? DateTime.UtcNow : DateTime.MinValue;
            _consecutiveBaseFrameReuseCount = (canReuseBaseFrame && newSeedImage != null)
                ? _consecutiveBaseFrameReuseCount + 1
                : 0;

            // Reset compositor scroll preview now that the engine has caught up.
            lock (_compositorScrollLock)
            {
                _hasCompositorScrollPreview = false;
                _compositorPreviewScrollY = _scrollY;
            }

            Element? deferredScroll = null;
            if (IsFirstContentFrameReady(_root, _styles, _hasStableStyleSnapshot, CurrentUrl, IsLoading))
            {
                _hasFirstStyledRender = true;
                if (_deferredScrollTarget != null)
                {
                    deferredScroll = _deferredScrollTarget;
                    _deferredScrollTarget = null;
                }
            }

            if (deferredScroll != null)
            {
                ScrollToElement(deferredScroll);
            }

            LogCommittedFrame(frameResult, viewportSize);
            _lastFrameTelemetry = frameResult?.Telemetry;
            bool requestedFollowupFrame = TryApplyPendingFragmentNavigation();

            if (!requestedFollowupFrame)
            {
                ClearPendingFrameRequest();
            }
            NeedsRepaint?.Invoke();
        }
        catch (Exception ex)
        {
            EngineLogBridge.Error($"[BrowserIntegration] Recording error: {ex.Message}", LogCategory.General);
        }
    }

    private static SKImage CreateSeedImageFromFrame(SKPicture frame, SKSize viewportSize)
    {
        if (frame == null ||
            !float.IsFinite(viewportSize.Width) ||
            !float.IsFinite(viewportSize.Height) ||
            viewportSize.Width <= 0 ||
            viewportSize.Height <= 0)
        {
            return null;
        }

        var width = Math.Clamp((int)Math.Ceiling(viewportSize.Width), 1, 16384);
        var height = Math.Clamp((int)Math.Ceiling(viewportSize.Height), 1, 16384);

        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            if (surface == null)
            {
                return null;
            }

            var seedCanvas = surface.Canvas;
            seedCanvas.Clear(SKColors.Transparent);
            seedCanvas.DrawPicture(frame);
            using var snapshot = surface.Snapshot();
            return snapshot?.ToRasterImage();
        }
        catch (Exception ex)
        {
            EngineLogBridge.Warn($"[BrowserIntegration] Failed to build seed image for base-frame reuse: {ex.Message}", LogCategory.Rendering);
            return null;
        }
    }

    private void LogCommittedFrame(RenderFrameResult frameResult, SKSize viewportSize)
    {
        var telemetry = frameResult?.Telemetry;
        EngineLogBridge.Info(
            $"[BrowserIntegration] Frame recorded. Viewport={viewportSize.Width}x{viewportSize.Height} Reasons={frameResult?.InvalidationReason} Raster={frameResult?.RasterMode}",
            LogCategory.Rendering);

        if (telemetry == null)
        {
            return;
        }

        var entry = new LogEntry
        {
            Category = LogCategory.Performance,
            Level = telemetry.WatchdogTriggered ? FenBrowser.Core.Logging.LogLevel.Warn : FenBrowser.Core.Logging.LogLevel.Info,
            Component = "BrowserIntegration.Frame",
            Message = "[FRAME] Commit",
            DurationMs = (long)Math.Round(Math.Max(0d, telemetry.TotalDurationMs)),
            Data = new Dictionary<string, object>
            {
                ["url"] = telemetry.Url ?? CurrentUrl ?? "about:blank",
                ["frameSequence"] = telemetry.FrameSequence,
                ["requestedBy"] = telemetry.RequestedBy ?? "unspecified",
                ["invalidationReason"] = telemetry.InvalidationReason.ToString(),
                ["rasterMode"] = telemetry.RasterMode.ToString(),
                ["baseFrameSeeded"] = telemetry.BaseFrameSeeded,
                ["layoutUpdated"] = telemetry.LayoutUpdated,
                ["paintTreeRebuilt"] = telemetry.PaintTreeRebuilt,
                ["damageRegionCount"] = telemetry.DamageRegionCount,
                ["damageAreaRatio"] = telemetry.DamageAreaRatio,
                ["usedDamageRasterization"] = frameResult.UsedDamageRasterization,
                ["compositedLayerCount"] = telemetry.CompositedLayerCount,
                ["promotedLayerCount"] = telemetry.PromotedLayerCount,
                ["usedIncrementalLayout"] = telemetry.UsedIncrementalLayout,
                ["incrementalLayoutRootCount"] = telemetry.IncrementalLayoutRootCount,
                ["domNodeCount"] = telemetry.DomNodeCount,
                ["boxCount"] = telemetry.BoxCount,
                ["paintNodeCount"] = telemetry.PaintNodeCount,
                ["overlayCount"] = telemetry.OverlayCount,
                ["layoutMs"] = Math.Round(Math.Max(0d, telemetry.LayoutDurationMs), 2),
                ["paintMs"] = Math.Round(Math.Max(0d, telemetry.PaintDurationMs), 2),
                ["rasterMs"] = Math.Round(Math.Max(0d, telemetry.RasterDurationMs), 2),
                ["totalMs"] = Math.Round(Math.Max(0d, telemetry.TotalDurationMs), 2),
                ["watchdogTriggered"] = telemetry.WatchdogTriggered,
                ["watchdogReason"] = telemetry.WatchdogReason ?? string.Empty
            }
        };

        var imageCache = ImageLoader.GetCacheSnapshot();
        var fontCache = SkiaFontService.GetGlobalCacheSnapshot();
        var textMeasureCache = SkiaTextMeasurer.GetGlobalCacheSnapshot();
        var eventLoop = _lastEventLoopSliceTelemetry;

        entry.Data["eventLoopProcessedTasks"] = eventLoop.ProcessedTaskCount;
        entry.Data["eventLoopInteractiveTasks"] = eventLoop.InteractiveTaskCount;
        entry.Data["eventLoopUserVisibleTasks"] = eventLoop.UserVisibleTaskCount;
        entry.Data["eventLoopBackgroundTasks"] = eventLoop.BackgroundTaskCount;
        entry.Data["eventLoopDeferredBackgroundTasks"] = eventLoop.DeferredBackgroundTaskCount;
        entry.Data["eventLoopPrioritizedInteractive"] = eventLoop.PrioritizedInteractive;
        entry.Data["eventLoopReservedRenderBudgetMs"] = Math.Round(eventLoop.ReservedRenderBudgetMs, 2);
        entry.Data["eventLoopPendingInteractive"] = eventLoop.QueueSnapshot.InteractiveCount;
        entry.Data["eventLoopPendingUserVisible"] = eventLoop.QueueSnapshot.UserVisibleCount;
        entry.Data["eventLoopPendingBackground"] = eventLoop.QueueSnapshot.BackgroundCount;

        entry.Data["imageCacheCount"] = imageCache.StaticImageCount + imageCache.AnimatedImageCount;
        entry.Data["imageCacheStaticCount"] = imageCache.StaticImageCount;
        entry.Data["imageCacheAnimatedCount"] = imageCache.AnimatedImageCount;
        entry.Data["imageCacheAnimatedFrames"] = imageCache.AnimatedFrameCount;
        entry.Data["imageCacheBytes"] = imageCache.ApproximateBytes;
        entry.Data["imageCachePendingLoads"] = imageCache.PendingLoadCount;
        entry.Data["imageCacheLazyPending"] = imageCache.LazyPendingCount;
        entry.Data["imageCacheHits"] = imageCache.HitCount;
        entry.Data["imageCacheMisses"] = imageCache.MissCount;
        entry.Data["imageCacheEvictions"] = imageCache.EvictionCount;

        entry.Data["fontCacheMetricsEntries"] = fontCache.MetricsEntries;
        entry.Data["fontCacheWidthEntries"] = fontCache.WidthEntries;
        entry.Data["fontCacheGlyphRunEntries"] = fontCache.GlyphRunEntries;
        entry.Data["fontCacheTypefaceEntries"] = fontCache.TypefaceEntries;
        entry.Data["fontCacheBytes"] = fontCache.ApproximateBytes;
        entry.Data["fontCacheHits"] = fontCache.HitCount;
        entry.Data["fontCacheMisses"] = fontCache.MissCount;
        entry.Data["fontCacheEvictions"] = fontCache.EvictionCount;

        entry.Data["textMeasureWidthEntries"] = textMeasureCache.WidthEntries;
        entry.Data["textMeasureLineHeightEntries"] = textMeasureCache.LineHeightEntries;
        entry.Data["textMeasureBytes"] = textMeasureCache.ApproximateBytes;
        entry.Data["textMeasureHits"] = textMeasureCache.HitCount;
        entry.Data["textMeasureMisses"] = textMeasureCache.MissCount;
        entry.Data["textMeasureEvictions"] = textMeasureCache.EvictionCount;

        LogManager.Log(entry);
    }

    private bool TryApplyPendingFragmentNavigation()
    {
        if (string.IsNullOrWhiteSpace(_pendingFragmentTargetId) ||
            string.IsNullOrWhiteSpace(_pendingFragmentSourceUrl) ||
            _root == null)
        {
            EngineLogBridge.Info(
                $"[FragmentNav] Skipped: target='{_pendingFragmentTargetId ?? "<null>"}' source='{_pendingFragmentSourceUrl ?? "<null>"}' root={_root?.TagName ?? "<null>"}",
                LogCategory.Navigation);
            return false;
        }

        if (!string.Equals(CurrentUrl, _pendingFragmentSourceUrl, StringComparison.OrdinalIgnoreCase))
        {
            EngineLogBridge.Info(
                $"[FragmentNav] Skipped: currentUrl='{CurrentUrl}' pendingUrl='{_pendingFragmentSourceUrl}'",
                LogCategory.Navigation);
            return false;
        }

        var target = FindElementById(_root, _pendingFragmentTargetId);
        if (target == null)
        {
            EngineLogBridge.Info($"[FragmentNav] Skipped: target element '#{_pendingFragmentTargetId}' not found", LogCategory.Navigation);
            return false;
        }

        var rect = GetElementRect(target);
        if (!rect.HasValue)
        {
            EngineLogBridge.Info($"[FragmentNav] Skipped: target element '#{_pendingFragmentTargetId}' has no layout box yet", LogCategory.Navigation);
            return false;
        }

        float viewportHeight = Math.Max(1f, _lastViewportSize.Height);
        float maxScroll = Math.Max(0f, _contentHeight - viewportHeight);
        float targetScroll = Math.Max(0f, Math.Min(maxScroll, rect.Value.Top));

        _pendingFragmentTargetId = null;
        _pendingFragmentSourceUrl = null;

        if (Math.Abs(targetScroll - _scrollY) <= 0.5f)
        {
            EngineLogBridge.Info($"[FragmentNav] No-op: '#{_pendingFragmentTargetId}' targetScroll={targetScroll:F1} current={_scrollY:F1}", LogCategory.Navigation);
            return false;
        }

        _scrollY = targetScroll;
        ResetCompositorScrollPreview();
        CancelSmoothWheelScroll();
        _scrollPhysics.SetPosition(_scrollY);
        ScrollChanged?.Invoke(_scrollY, _contentHeight);
        EngineLogBridge.Info($"[FragmentNav] Applied '#{target.Id}' -> scrollY={_scrollY:F1}", LogCategory.Navigation);
        RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.Navigation, "BrowserIntegration.FragmentNavigation");
        return true;
    }

    private bool HasCommittedFrame()
    {
        return _latestSnapshot?.Frame != null;
    }

    /// <summary>
    /// Fires periodic wake signals for ~10 seconds after navigation so the engine thread
    /// keeps re-recording frames while CSS, JS, and layout settle. Without active CSS
    /// animations there is no other recurring wake source in this window.
    /// </summary>
    private void StartPostNavigationRepaintPulse()
    {
        var pulseStart = DateTime.Now;
        var token = _lastNavigationTime; // capture; if another navigation starts, stops the old pulse
        _ = Task.Run(async () =>
        {
    // FAST STARTUP: Fire every 50ms for up to 5 seconds (instead of 300ms/10s).
    // Stop once the first styled frame is committed.
    while (_running && (DateTime.Now - pulseStart).TotalSeconds < 5)
    {
        await Task.Delay(50).ConfigureAwait(false); // Reduced from 300ms to 50ms
                // Bail if a newer navigation has started
                if (_lastNavigationTime != token) break;
                if (!_running) break;
                if (_hasFirstStyledRender && HasCommittedFrame()) break;
                RequestFrame(RenderFrameInvalidationReason.Timer | RenderFrameInvalidationReason.Navigation, "BrowserIntegration.NavigationPulse");
            }
        });
    }
    
    /// <summary>
    /// Get the screen-space rectangle of an element.
    /// </summary>
    public SKRect? GetElementRect(Element element)
    {
        if (element == null) return null;

        if (!_rendererLock.TryEnterReadLock(2))
            return null; // engine is mid-layout; element rect unavailable
        try
        {
            var box = _renderer.GetElementBox(element);
            if (box == null) return null;
            return box.BorderBox;
        }
        finally
        {
            _rendererLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Walks an element's ancestor chain for the nearest <iframe> host. Returns null
    /// when the element lives in the top-level document.
    /// </summary>
    private static Element FindContainingIframe(Element element)
    {
        for (var node = element?.ParentNode; node != null; node = node.ParentNode)
        {
            if (node is Element ancestor &&
                string.Equals(ancestor.TagName, "IFRAME", StringComparison.OrdinalIgnoreCase))
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>
    /// Scrolls a nested browsing context so <paramref name="target"/>'s top edge aligns
    /// with the top of the frame's viewport (scrollIntoView default block:"start").
    /// </summary>
    private void ScrollIframeToElement(Element iframeHost, Element target)
    {
        // Called from the JS engine thread (scrollIntoView callback), which already
        // holds the write lock during RecordFrame.  This mutates ScrollManager so it
        // requires write access; recursion is supported by the lock policy.
        _rendererLock.EnterWriteLock();
        try
        {
            var frameBox = _renderer.GetElementBox(iframeHost);
            var targetBox = _renderer.GetElementBox(target);
            if (frameBox == null || targetBox == null)
            {
                return;
            }

            float frameContentTop = frameBox.PaddingBox.Top;
            float desired = targetBox.BorderBox.Top - frameContentTop;
            if (desired < 0f) desired = 0f;

            EngineLogBridge.Info(
                $"[ScrollIframe] host=<{iframeHost.TagName}> hash={iframeHost.GetHashCode()} frameTop={frameContentTop:F0} targetTop={targetBox.BorderBox.Top:F0} desired={desired:F0}",
                LogCategory.Rendering);

            // Allow the programmatic scroll to reach the target even though the frame is
            // overflow:hidden; widen the bounds before setting the position so the paint
            // pass (which re-derives bounds from content) does not clamp it short.
            float viewportH = Math.Max(0f, frameBox.PaddingBox.Height);
            _renderer.ScrollManager.SetScrollBounds(
                iframeHost,
                Math.Max(0f, frameBox.PaddingBox.Width),
                desired + viewportH,
                Math.Max(0f, frameBox.PaddingBox.Width),
                viewportH);
            _renderer.ScrollManager.SetScrollPosition(iframeHost, 0, desired);
        }
        finally
        {
            _rendererLock.ExitWriteLock();
        }

        RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.Overlay,
            "BrowserIntegration.ScrollIframeToElement", notifyUi: true);
    }
    
    /// <summary>
    /// Capture a base64-encoded screenshot of the current page.
    /// </summary>
    public async Task<string> CaptureScreenshotAsync()
    {
        return await Task.Run(() =>
        {
            var size = new SKSizeI((int)_lastViewportSize.Width, (int)_lastViewportSize.Height);
            if (size.Width <= 0 || size.Height <= 0)
            {
                return string.Empty;
            }

            using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height));
            if (surface == null)
            {
                return string.Empty;
            }

            Render(surface.Canvas, new SKRect(0, 0, size.Width, size.Height));
            using var image = surface.Snapshot();
            if (image == null) return string.Empty;

            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return string.Empty;

            return Convert.ToBase64String(data.ToArray());
        });
    }

    /// <summary>
    /// Focus a specific DOM node.
    /// </summary>
    public void FocusNode(Element element)
    {
        var ownerDocument = element?.OwnerDocument;
        if (ownerDocument != null)
        {
            ownerDocument.ActiveElement = element;
        }

        ElementStateManager.Instance.SetFocusedElement(element);
        _highlightedElement = element; // For visual feedback in WebDriver
        RequestFrame(RenderFrameInvalidationReason.Input | RenderFrameInvalidationReason.Overlay, "BrowserIntegration.FocusNode", notifyUi: true);
    }

    /// <summary>
    /// Scroll the viewport to bring a target element into view using a
    /// browser-like nearest/start policy (instead of always centering).
    /// </summary>
    public void ScrollToElement(Element element)
    {
        if (element == null)
        {
            return;
        }

        // Keep initial navigation deterministic: script-driven scrollIntoView calls that fire
        // before first styled paint can yank the viewport away from first fold.
        if (IsLoading || !_hasFirstStyledRender)
        {
            _deferredScrollTarget = element;
            EngineLogBridge.Info(
                $"[ScrollToElement] Deferred during startup: loading={IsLoading} firstStyled={_hasFirstStyledRender} tag={element.TagName} id={element.Id ?? "<none>"}",
                LogCategory.Navigation);
            return;
        }

        var rect = GetElementRect(element);
        if (!rect.HasValue)
        {
            return;
        }

        var viewportHeight = Math.Max(1f, _lastViewportSize.Height);
        var viewportTop = _scrollY;
        var viewportBottom = viewportTop + viewportHeight;

        // Already fully visible -> no-op.
        if (rect.Value.Top >= viewportTop && rect.Value.Bottom <= viewportBottom)
        {
            return;
        }

        // If target is above viewport, align top to target top.
        // If target is below viewport, align bottom edge unless target is taller than viewport.
        float desiredScroll;
        if (rect.Value.Top < viewportTop)
        {
            desiredScroll = rect.Value.Top;
        }
        else
        {
            desiredScroll = rect.Value.Height > viewportHeight
                ? rect.Value.Top
                : rect.Value.Bottom - viewportHeight;
        }

        var maxScroll = Math.Max(0f, _contentHeight - viewportHeight);
        var nextScroll = Math.Max(0f, Math.Min(maxScroll, desiredScroll));
        if (Math.Abs(nextScroll - _scrollY) <= 0.5f)
        {
            return;
        }

        EngineLogBridge.Info(
            $"[ScrollToElement] tag={element.TagName} id={element.Id ?? "<none>"} oldY={_scrollY:F1} newY={nextScroll:F1} rectTop={rect.Value.Top:F1} rectBottom={rect.Value.Bottom:F1} vh={viewportHeight:F1}",
            LogCategory.Navigation);

        _scrollY = nextScroll;
        ResetCompositorScrollPreview();
        CancelSmoothWheelScroll();
        _scrollPhysics.SetPosition(_scrollY);
        ScrollChanged?.Invoke(_scrollY, _contentHeight);

        _highlightedElement = element;
        RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.Overlay, "BrowserIntegration.ScrollToElement", notifyUi: true);
    }
    
    private void DrawHighlight(SKCanvas canvas, Element element)
    {
        // Get bounds from layout computer via renderer
        var box = _renderer.GetElementBox(element);
        if (box == null) return;
        
        var rect = box.BorderBox;
        float x = rect.Left;
        float y = rect.Top;
        float w = rect.Width;
        float h = rect.Height;
        
        if (w >= 0 && h >= 0)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(111, 168, 220, 120),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(111, 168, 220, 200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };
            
            canvas.DrawRect(rect, paint);
            canvas.DrawRect(rect, borderPaint);
            
            // Draw label
            string label = $"{element.TagName} | {w:F0}x{h:F0}";
            using var labelFont = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 12);
            using var labelPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            using var labelBgPaint = new SKPaint
            {
                Color = new SKColor(111, 168, 220, 255),
                Style = SKPaintStyle.Fill
            };

            float labelWidth = labelFont.MeasureText(label);
            float labelHeight = 18;
            var labelRect = new SKRect(x, y - labelHeight, x + labelWidth + 8, y);

            // Adjust if too close to top
            if (y < labelHeight)
            {
                labelRect = new SKRect(x, y + h, x + labelWidth + 8, y + h + labelHeight);
            }

            canvas.DrawRect(labelRect, labelBgPaint);
            canvas.DrawText(label, labelRect.Left + 4, labelRect.Bottom - 4, labelFont, labelPaint);
        }
    }
    
    private void DrawPlaceholder(SKCanvas canvas, SKRect viewport)
    {
        bool isNewTabSurface = IsNewTabSurfaceUrl(CurrentUrl);
        using var bgPaint = new SKPaint
        {
            Color = isNewTabSurface ? new SKColor(11, 18, 32) : SKColors.White
        };
        canvas.DrawRect(viewport, bgPaint);
        
        if (IsLoading)
        {
            using var textFont = new SKFont(SKTypeface.Default, 18);
            using var textPaint = new SKPaint
            {
                Color = isNewTabSurface ? new SKColor(148, 163, 184) : SKColors.Gray,
                IsAntialias = true
            };
            string loadMsg = "Loading...";
            float loadW = textFont.MeasureText(loadMsg);
            canvas.DrawText(loadMsg, viewport.MidX - loadW / 2, viewport.MidY, textFont, textPaint);
        }
        else
        {
            using var defaultFont = new SKFont(SKTypeface.Default, 16);
            using var textPaint = new SKPaint
            {
                Color = isNewTabSurface ? new SKColor(148, 163, 184) : SKColors.Gray,
                IsAntialias = true
            };
            string defaultMsg = "Enter a URL to browse";
            float defaultW = defaultFont.MeasureText(defaultMsg);
            canvas.DrawText(defaultMsg, viewport.MidX - defaultW / 2, viewport.MidY, defaultFont, textPaint);
        }
    }

    private void DrainInputQueue()
    {
        _inputQueue.Drain(
            input =>
            {
                try
                {
                    DispatchInputOnEngineThread(input);
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Error(
                        $"[InputLatency] sequence={input.Sequence} type={input.Type} dispatch_failed={ex.Message}",
                        LogCategory.Events);
                }
            },
            _inputDrainBudget,
            _maxInputEventsPerFrame);
    }

    private void RecordFirstFrameAfterInput()
    {
        if (_inputAwaitingFrameSequence <= _lastInputSequencePublishedInFrame ||
            _inputAwaitingFrameReceiptTimestamp <= 0)
        {
            return;
        }

        var inputToFrameMs = System.Diagnostics.Stopwatch
            .GetElapsedTime(_inputAwaitingFrameReceiptTimestamp)
            .TotalMilliseconds;
        _lastInputSequencePublishedInFrame = _inputAwaitingFrameSequence;
        if (inputToFrameMs < _slowInputThresholdMs)
        {
            return;
        }

        EngineLogBridge.Warn(
            $"[InputLatency] sequence={_inputAwaitingFrameSequence} type={_inputAwaitingFrameType} " +
            $"receiptThread={_inputAwaitingFrameReceiptThreadId} engineThread={Environment.CurrentManagedThreadId} " +
            $"firstFrameMs={inputToFrameMs:0.0}",
            LogCategory.Events);
    }

    private void DispatchInputOnEngineThread(BrowserInputEvent input)
    {
        var dispatchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var queueDelayMs = System.Diagnostics.Stopwatch
            .GetElapsedTime(input.Timestamp, dispatchStarted)
            .TotalMilliseconds;
        var activationDurationMs = 0d;
        switch (input.Type)
        {
            case BrowserInputType.MouseMove:
                _browser.OnMouseMove(input.X, input.Y);
                break;
            case BrowserInputType.MouseDown:
                _browser.OnMouseDown(input.X, input.Y, input.Button);
                break;
            case BrowserInputType.MouseUp:
                _browser.OnMouseUp(input.X, input.Y, input.Button);
                break;
            case BrowserInputType.Click:
                var activationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _browser.DispatchClickAndActivate(input.X, input.Y, input.Button).GetAwaiter().GetResult();
                activationDurationMs = System.Diagnostics.Stopwatch
                    .GetElapsedTime(activationStarted)
                    .TotalMilliseconds;
                break;
            case BrowserInputType.DoubleClick:
                _browser.OnDoubleClick(input.X, input.Y, input.Button);
                break;
            case BrowserInputType.ContextMenu:
                _pendingContextMenus.TryRemove(input.Sequence, out var request);
                var contextMenuAllowed = _browser.OnContextMenu(input.X, input.Y, input.Button);
                if (request != null && contextMenuAllowed)
                {
                    _ = WindowManager.Instance.RunOnMainThread(() => ContextMenuRequested?.Invoke(request));
                }
                break;
            case BrowserInputType.MouseWheel:
                var allowWheelDefault = true;
                if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
                {
                    if (OwnerTab != null)
                    {
                        FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, new FenBrowser.Host.ProcessIsolation.RendererInputEvent
                        {
                            Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.MouseWheel,
                            X = input.X,
                            Y = input.Y,
                            DeltaX = input.DeltaX,
                            DeltaY = input.DeltaY
                        });
                    }
                }
                else
                {
                    allowWheelDefault = _browser.OnMouseWheel(input.X, input.Y, input.DeltaX, input.DeltaY);
                }

                if (allowWheelDefault)
                {
                    ApplyWheelScroll(input.DeltaY);
                }
                break;
            case BrowserInputType.ScrollTo:
                ApplyScrollToY(input.Y);
                break;
            case BrowserInputType.ScrollAnimationTick:
                ApplyScrollPhysics(input.DeltaY);
                break;
            case BrowserInputType.ClipboardCommand:
                DispatchClipboardCommandOnEngineThread(input.Command, input.Text);
                break;
            case BrowserInputType.KeyDown:
            case BrowserInputType.TextInput:
                if (!string.IsNullOrEmpty(input.Text))
                {
                    if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
                    {
                        if (OwnerTab != null)
                        {
                            FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(
                                OwnerTab,
                                CreateRendererKeyboardInput(input.Text));
                        }
                    }
                    else
                    {
                        _browser.HandleKeyPress(input.Text).GetAwaiter().GetResult();
                    }
                }
                break;
        }

        var dispatchDurationMs = System.Diagnostics.Stopwatch
            .GetElapsedTime(dispatchStarted)
            .TotalMilliseconds;
        _inputAwaitingFrameSequence = input.Sequence;
        _inputAwaitingFrameReceiptTimestamp = input.Timestamp;
        _inputAwaitingFrameType = input.Type;
        _inputAwaitingFrameReceiptThreadId = input.ReceiptThreadId;

        if (queueDelayMs >= _slowInputThresholdMs ||
            dispatchDurationMs >= _slowInputThresholdMs ||
            activationDurationMs >= _slowInputThresholdMs)
        {
            EngineLogBridge.Warn(
                $"[InputLatency] sequence={input.Sequence} type={input.Type} " +
                $"receiptThread={input.ReceiptThreadId} engineThread={Environment.CurrentManagedThreadId} " +
                $"queueMs={queueDelayMs:0.0} dispatchMs={dispatchDurationMs:0.0} " +
                $"activationMs={activationDurationMs:0.0} pending={_inputQueue.Count}",
                LogCategory.Events);
        }
    }

    private long EnqueueInput(
        BrowserInputType type,
        float x = 0,
        float y = 0,
        int button = 0,
        int buttons = 0,
        string text = null,
        long sequence = 0,
        float deltaX = 0,
        float deltaY = 0,
        string command = null)
    {
        if (sequence <= 0)
        {
            sequence = Interlocked.Increment(ref _inputSequence);
        }
        _inputQueue.Enqueue(new BrowserInputEvent(
            type,
            x,
            y,
            button,
            buttons,
            Modifiers: 0,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            sequence,
            text,
            Environment.CurrentManagedThreadId,
            deltaX,
            deltaY,
            command));
        _wakeEvent.Set();
        return sequence;
    }

    private static int ReadPositiveIntEnvironment(string variableName, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    internal static bool IsNewTabSurfaceUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim().TrimEnd('/');
        return normalized.Equals("fen://newtab", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("about:newtab", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Scroll the content to an absolute Y position in document coordinates.
    /// Drives scrollbar-thumb drag operations where the host computes the target
    /// position directly rather than accumulating deltas.
    /// </summary>
    public void ScrollToY(float targetScrollY)
    {
        EnqueueInput(BrowserInputType.ScrollTo, y: targetScrollY);
    }

    private void ApplyScrollToY(float targetScrollY)
    {
        float clamped = ClampScrollPosition(targetScrollY);
        CancelSmoothWheelScroll();
        float previousScrollY = _scrollY;

        // Apply immediately so EffectiveScrollY and downstream frame requests pick
        // up the new offset on the next paint. Posting through the engine loop
        // would leave _scrollY at the stale value until the worker thread drains,
        // which produces the visible "scrollbar moves, content stays" symptom.
        _scrollY = clamped;
        _scrollPhysics.SetPosition(_scrollY);
        lock (_compositorScrollLock)
        {
            UpdateCompositorScrollDirectionLocked(clamped - previousScrollY);
            _compositorPreviewScrollY = clamped;
            _hasCompositorScrollPreview = true;
        }

        ScrollChanged?.Invoke(_scrollY, _contentHeight);
        RequestFrame(RenderFrameInvalidationReason.Scroll, "BrowserIntegration.ScrollToY");
        NeedsRepaint?.Invoke();
    }

    /// <summary>
    /// Scroll the content by the given delta.
    /// </summary>
    private void ApplyWheelScroll(float deltaY)
    {
        float deltaPixels = -(deltaY * WheelScrollStepPixels);
        if (Math.Abs(deltaPixels) <= 0.01f)
        {
            return;
        }

        float start = _smoothWheelScrollActive ? _smoothWheelScrollTargetY : _scrollY;
        _smoothWheelScrollTargetY = ClampScrollPosition(start + deltaPixels);
        _smoothWheelScrollActive = Math.Abs(_smoothWheelScrollTargetY - _scrollY) > SmoothWheelScrollSnapPixels;
        AdvanceSmoothWheelScroll(1d / 240d);

        RequestFrame(RenderFrameInvalidationReason.Scroll, "BrowserIntegration.Scroll");

        NeedsRepaint?.Invoke();
    }

    public void HandleMouseWheel(float windowX, float windowY, float deltaX, float deltaY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        var (docX, docY) = TranslateWindowToDocument(windowX, windowY, viewportOffsetX, viewportOffsetY);
        EnqueueInput(BrowserInputType.MouseWheel, docX, docY, deltaX: deltaX, deltaY: deltaY);
    }

    private bool AdvanceSmoothWheelScroll(double deltaTime)
    {
        if (!_smoothWheelScrollActive)
        {
            return false;
        }

        _smoothWheelScrollTargetY = ClampScrollPosition(_smoothWheelScrollTargetY);
        float distance = _smoothWheelScrollTargetY - _scrollY;
        if (Math.Abs(distance) <= SmoothWheelScrollSnapPixels)
        {
            _smoothWheelScrollActive = false;
            return SetAnimatedScrollPosition(_smoothWheelScrollTargetY);
        }

        float dt = (float)Math.Clamp(deltaTime, 1d / 240d, 1d / 15d);
        float fraction = 1f - MathF.Exp(-SmoothWheelScrollResponse * dt);
        float nextScrollY = _scrollY + distance * fraction;

        if (Math.Abs(_smoothWheelScrollTargetY - nextScrollY) <= SmoothWheelScrollSnapPixels)
        {
            nextScrollY = _smoothWheelScrollTargetY;
            _smoothWheelScrollActive = false;
        }

        return SetAnimatedScrollPosition(nextScrollY);
    }

    private bool SetAnimatedScrollPosition(float scrollY, bool syncPhysics = true)
    {
        float clamped = ClampScrollPosition(scrollY);
        if (Math.Abs(clamped - _scrollY) <= 0.01f)
        {
            return false;
        }

        float previousScrollY = _scrollY;
        _scrollY = clamped;
        if (syncPhysics)
        {
            _scrollPhysics.SetPosition(_scrollY);
        }

        lock (_compositorScrollLock)
        {
            UpdateCompositorScrollDirectionLocked(clamped - previousScrollY);
            _compositorPreviewScrollY = _scrollY;
            _hasCompositorScrollPreview = true;
        }

        ScrollChanged?.Invoke(_scrollY, _contentHeight);
        return true;
    }

    private void CancelSmoothWheelScroll()
    {
        _smoothWheelScrollActive = false;
        _smoothWheelScrollTargetY = _scrollY;
    }

    private float ClampScrollPosition(float value)
    {
        var maxScroll = Math.Max(0f, _contentHeight - _lastViewportSize.Height);
        return Math.Max(0f, Math.Min(maxScroll, value));
    }

    private void ResetCompositorScrollPreview(bool resetDirection = false)
    {
        lock (_compositorScrollLock)
        {
            _compositorPreviewScrollY = _scrollY;
            _hasCompositorScrollPreview = false;
            if (resetDirection)
            {
                _lastCompositorScrollDirectionY = 0f;
            }
        }
    }

    private float GetEffectiveScrollY()
    {
        lock (_compositorScrollLock)
        {
            return _hasCompositorScrollPreview ? _compositorPreviewScrollY : _scrollY;
        }
    }

    private bool IsRemoteFrameAheadOfLiveScroll(float remoteScrollY)
    {
        float effectiveScrollY;
        float directionY;
        lock (_compositorScrollLock)
        {
            effectiveScrollY = _hasCompositorScrollPreview ? _compositorPreviewScrollY : _scrollY;
            directionY = _lastCompositorScrollDirectionY;
        }

        if (directionY > 0f)
        {
            return remoteScrollY > effectiveScrollY + RemoteFrameFutureScrollTolerancePixels;
        }

        if (directionY < 0f)
        {
            return remoteScrollY < effectiveScrollY - RemoteFrameFutureScrollTolerancePixels;
        }

        return false;
    }

    private void UpdateCompositorScrollDirectionLocked(float deltaY)
    {
        if (Math.Abs(deltaY) > 0.01f)
        {
            _lastCompositorScrollDirectionY = MathF.Sign(deltaY);
        }
    }

    private void ClearRemoteFrame()
    {
        lock (_remoteFrameLock)
        {
            _remoteFrameBitmap?.Dispose();
            _remoteFrameBitmap = null;
            _remoteFrameScrollY = 0f;
            _remoteFrameSequenceNumber = 0;
        }
    }

    
    /// <summary>
    /// Handle mouse click at the given position.
    /// Returns true if a link was clicked.
    /// </summary>
    public bool HandleClick(float x, float y, float viewportHeight)
    {
        if (_root == null || _styles == null) return false;
        
        // Adjust for scroll
        float adjustedY = y + _scrollY;
        
        // Find element at position using hit testing
        var element = HitTestElement(_root, x, adjustedY);
        
        if (element != null)
        {
            // Check if it's a link
            var href = GetLinkHref(element);
            if (!string.IsNullOrEmpty(href))
            {
                EngineLogBridge.Info($"[BrowserIntegration] Link clicked: {href}", LogCategory.General);
                LinkClicked?.Invoke(href);
                _ = NavigateProgrammaticAsync(href);
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Hit test to find element at position.
    /// </summary>
    private FenBrowser.Core.Dom.V2.Element HitTestElement(FenBrowser.Core.Dom.V2.Element element, float x, float y)
    {
        if (element == null) return null;
        
        // Get element's box if available in styles
        if (_styles.TryGetValue(element, out var computed))
        {
            // Check if point is within element bounds
            // Use box model from renderer if available
        }
        
        // Check children (in reverse order for z-index)
        for (int i = element.ChildNodes.Length - 1; i >= 0; i--)
        {
            var child = element.ChildNodes[i];
                var hit = HitTestElement(child as FenBrowser.Core.Dom.V2.Element, x, y);
            if (hit != null) return hit;
        }
        
        // For now, return null - proper hit testing requires box model integration
        return null;
    }
    
    /// <summary>
    /// Get href from link element or its ancestors.
    /// </summary>
    private string GetLinkHref(Element element)
    {
        var current = element;
        while (current != null)
        {
            if (current.TagName?.ToLowerInvariant() == "a")
            {
                var href = current.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    // Resolve relative URLs
                    if (_browser.CurrentUri != null && !href.StartsWith("http") && !href.StartsWith("data:"))
                    {
                        if (Uri.TryCreate(_browser.CurrentUri, href, out var resolved))
                        {
                            return resolved.AbsoluteUri;
                        }
                    }
                    return href;
                }
            }
            current = current.Parent as Element;
        }
        return null;
    }
    
    public bool NeedsRender => _needsRepaint;
    public bool HasViewport => _hasReceivedViewportSize && _lastViewportSize.Width > 1 && _lastViewportSize.Height > 1;
    public SKSize ViewportSize => _lastViewportSize;
    
    /// <summary>
    /// Get the current scroll position.
    /// </summary>
    public float ScrollY => _scrollY;

    /// <summary>
    /// Scroll position as observed by the compositor, including the latest
    /// preview offset applied synchronously before the engine task lands.
    /// Use this for paint-time decisions (scrollbar thumb, brokered frame request)
    /// to avoid one-frame lag against fast wheel input.
    /// </summary>
    public float EffectiveScrollY
    {
        get
        {
            lock (_compositorScrollLock)
            {
                return _hasCompositorScrollPreview ? _compositorPreviewScrollY : _scrollY;
            }
        }
    }
    
    /// <summary>
    /// Get the content height for scroll calculation.
    /// </summary>
    public float ContentHeight => _contentHeight;
    
    /// <summary>
    /// Set DPI scale for coordinate translation.
    /// </summary>
    public void SetDpiScale(float scale)
    {
        _dpiScale = Math.Max(0.1f, scale);
    }
    
    public void UpdateViewport(SKSize size)
    {
        // Guard against invalid/minimized sizes to prevent layout collapse/white flash
        if (size.Width <= 1 || size.Height <= 1) 
        {
            EngineLogBridge.Info($"[BrowserIntegration] Ignoring small viewport update: {size}", LogCategory.General);
            return;
        }

        EngineLogBridge.Info($"[BrowserIntegration] UpdateViewport: {size}", LogCategory.General);
        _browser.UpdateViewportHint(size.Width, size.Height);
        var previousViewport = _lastViewportSize;
        _hasReceivedViewportSize = true;
        if (_lastViewportSize != size)
        {
            bool hadBootstrapViewport = previousViewport.Width <= 1 || previousViewport.Height <= 1;
            if (hadBootstrapViewport || !_hasFirstStyledRender)
            {
                var oldSnapshot = _latestSnapshot;
                _latestSnapshot = null;
                oldSnapshot?.Frame?.Dispose();
                _pendingDisposeFrame?.Dispose();
                _pendingDisposeFrame = null;
                _currentFrameSeedImage?.Dispose();
                _currentFrameSeedImage = null;
                _currentFrameSeedCreatedUtc = DateTime.MinValue;
                _consecutiveBaseFrameReuseCount = 0;
            }

            _lastViewportSize = size;
            RequestFrame(RenderFrameInvalidationReason.Viewport, "BrowserIntegration.UpdateViewport");
        }
    }
    
    /// <summary>
    /// Perform hit test at window coordinates (handles coordinate translation).
    /// Window → UI → Document space with scroll offset and DPI scaling.
    /// </summary>
    /// <param name="windowX">X in window/screen coordinates</param>
    /// <param name="windowY">Y in window/screen coordinates</param>
    /// <param name="viewportOffsetX">Content area X offset from window origin</param>
    /// <param name="viewportOffsetY">Content area Y offset from window origin</param>
    /// <returns>Immutable hit test result</returns>

    private HitTestResult GetCachedHitTest()
    {
        lock (_hitTestCacheLock)
        {
            return _cachedHitTest;
        }
    }

    private void SetCachedHitTest(HitTestResult result)
    {
        lock (_hitTestCacheLock)
        {
            _cachedHitTest = result;
        }
    }
    public HitTestResult PerformHitTest(
        float windowX,
        float windowY,
        float viewportOffsetX = 0,
        float viewportOffsetY = 0,
        bool allowCachedFallback = true)
    {
        // Window → UI coordinates (subtract viewport offset)
        float uiX = windowX - viewportOffsetX;
        float uiY = windowY - viewportOffsetY;
        
        // Apply DPI scaling (if needed)
        float scaledX = uiX / _dpiScale;
        float scaledY = uiY / _dpiScale;
        
        // UI → Document coordinates (add scroll offset)
        float docX = scaledX;
        float docY = scaledY + _scrollY;
        
        // Perform hit test in document space.
        // Use TryEnterReadLock(0) — never block the UI thread waiting for the engine
        // thread's layout pass.  If the renderer lock is held (engine is mid-layout),
        // fall back to the last cached result so the cursor stays responsive.
        if (_rendererLock.TryEnterReadLock(0))
        {
            try
            {
                if (_renderer.HitTest(docX, docY, out var result))
                {
                    // Update last hit test (for status bar) and cache for non-blocking fallback.
                    if (!result.Equals(_lastHitTest))
                    {
                        _lastHitTest = result;
                        SetCachedHitTest(result);
                        HitTestChanged?.Invoke(result);
                    }

                    return result;
                }
            }
            finally
            {
                _rendererLock.ExitReadLock();
            }
        }

        // Renderer is busy (engine mid-layout) — use cached result to keep UI responsive.
        if (!allowCachedFallback)
        {
            return HitTestResult.None;
        }

        var cachedHit = GetCachedHitTest();
        if (!cachedHit.Equals(_lastHitTest))
        {
            _lastHitTest = cachedHit;
            HitTestChanged?.Invoke(_lastHitTest);
        }
        return cachedHit;
    }
    
    private (float X, float Y) TranslateWindowToDocument(float windowX, float windowY, float viewportOffsetX, float viewportOffsetY)
    {
        float uiX = (windowX - viewportOffsetX) / Math.Max(_dpiScale, 0.1f);
        float uiY = (windowY - viewportOffsetY) / Math.Max(_dpiScale, 0.1f);
        return (uiX, uiY + _scrollY);
    }

    private (float X, float Y) TranslateWindowToViewport(float windowX, float windowY, float viewportOffsetX, float viewportOffsetY)
    {
        float uiX = (windowX - viewportOffsetX) / Math.Max(_dpiScale, 0.1f);
        float uiY = (windowY - viewportOffsetY) / Math.Max(_dpiScale, 0.1f);
        return (uiX, uiY);
    }

    /// <summary>
    /// Handle mouse move for cursor updates and status bar.
    /// </summary>
    private bool _isMouseMovePending = false;
    private (float X, float Y, float VX, float VY) _pendingMouseMove;
    private const long DoubleClickThresholdMs = 500;
    private const float DoubleClickDistance = 6f;
    private long _lastClickTickMs = -1;
    private float _lastClickWindowX;
    private float _lastClickWindowY;
    private int _lastClickButton = -1;

    /// <summary>
    /// Handle mouse move for cursor updates and status bar.
    /// Coalesces rapid events to prevent queue flooding.
    /// </summary>
    public HitTestResult HandleMouseMove(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
        var (docX, docY) = TranslateWindowToDocument(windowX, windowY, viewportOffsetX, viewportOffsetY);

        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
        {
            if (OwnerTab != null)
            {
                var evt = new FenBrowser.Host.ProcessIsolation.RendererInputEvent 
                { 
                    Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.MouseMove, 
                    X = docX, 
                    Y = docY 
                };
                FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, evt);
            }
            return result;
        }

        EnqueueInput(BrowserInputType.MouseMove, docX, docY);
        return result;
    }
    
    // Internal method to queue costly hover logic if needed
    public void QueueHoverLogic(float windowX, float windowY, float viewportOffsetX, float viewportOffsetY)
    {
         _pendingMouseMove = (windowX, windowY, viewportOffsetX, viewportOffsetY);
         if (!_isMouseMovePending)
         {
             _isMouseMovePending = true;
             PostToEngine(() => 
             {
                 _isMouseMovePending = false;
                 // Process latest coordinates
                 var (px, py, pvx, pvy) = _pendingMouseMove;
                 // Perform heavy hover logic here if we had any (Trigger Hover CSS etc)
                 // Currently we don't have heavy hover logic in engine, so this is just placeholder/future-proof
             });
         }
    }
    
    /// <summary>
    /// Handle right-click for context menu.
    /// </summary>
    public void HandleRightClick(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        var (docX, docY) = TranslateWindowToDocument(windowX, windowY, viewportOffsetX, viewportOffsetY);
        var defaultAllowed = true;
        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
        {
            if (OwnerTab != null)
            {
                FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, new FenBrowser.Host.ProcessIsolation.RendererInputEvent
                {
                    Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.MouseDown,
                    X = docX,
                    Y = docY,
                    Button = 2
                });
                FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, new FenBrowser.Host.ProcessIsolation.RendererInputEvent
                {
                    Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.MouseUp,
                    X = docX,
                    Y = docY,
                    Button = 2
                });
                FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, new FenBrowser.Host.ProcessIsolation.RendererInputEvent
                {
                    Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.ContextMenu,
                    X = docX,
                    Y = docY,
                    Button = 2
                });
            }

            defaultAllowed = false;
        }
        else
        {
            var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
            var immutableHit = result with { NativeElement = null };
            EnqueueInput(BrowserInputType.MouseDown, docX, docY, button: 2, buttons: 4);
            EnqueueInput(BrowserInputType.MouseUp, docX, docY, button: 2);
            var sequence = Interlocked.Increment(ref _inputSequence);
            _pendingContextMenus[sequence] = new ContextMenuRequest(
                windowX,
                windowY,
                viewportOffsetX,
                viewportOffsetY,
                immutableHit);
            EnqueueInput(BrowserInputType.ContextMenu, docX, docY, button: 2, sequence: sequence);
            defaultAllowed = false;
        }

        if (defaultAllowed)
        {
            var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
            ContextMenuRequested?.Invoke(new ContextMenuRequest(windowX, windowY, viewportOffsetX, viewportOffsetY, result));
        }
    }

    public event Action<ContextMenuRequest> ContextMenuRequested;
    
    public class ContextMenuRequest
    {
        public float X { get; }
        public float Y { get; }
        public float ViewportOffsetX { get; }
        public float ViewportOffsetY { get; }
        public HitTestResult Hit { get; }
        
        public ContextMenuRequest(float x, float y, float viewportOffsetX, float viewportOffsetY, HitTestResult hit)
        {
            X = x;
            Y = y;
            ViewportOffsetX = viewportOffsetX;
            ViewportOffsetY = viewportOffsetY;
            Hit = hit;
        }
    }

    /// <summary>
    /// Handle mouse click at window coordinates.
    /// Returns true if a navigable link was clicked.
    /// </summary>
    public HitTestResult HandleMouseDown(float windowX, float windowY, int button = 0, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        var (docX, docY) = TranslateWindowToDocument(windowX, windowY, viewportOffsetX, viewportOffsetY);
        var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
        
        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
        {
            if (OwnerTab != null)
            {
                var evt = new FenBrowser.Host.ProcessIsolation.RendererInputEvent 
                { 
                    Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.MouseDown, 
                    X = docX, 
                    Y = docY, 
                    Button = button 
                };
                FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, evt);
            }
            return result;
        }

        EnqueueInput(
            BrowserInputType.MouseDown,
            docX,
            docY,
            button,
            1 << Math.Min(Math.Max(button, 0), 3));
        return result;
    }

    public HitTestResult HandleMouseUp(float windowX, float windowY, int button = 0, bool emitClick = false, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        var (docX, docY) = TranslateWindowToDocument(windowX, windowY, viewportOffsetX, viewportOffsetY);
        var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
        
        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true)
        {
            if (OwnerTab != null)
            {
                var evt = new FenBrowser.Host.ProcessIsolation.RendererInputEvent 
                { 
                    Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.MouseUp, 
                    X = docX, 
                    Y = docY, 
                    Button = button, 
                    EmitClick = emitClick 
                };
                FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, evt);

                if (emitClick && button == 0 && ShouldEmitDoubleClick(windowX, windowY, button))
                {
                    FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.OnInputEvent(OwnerTab, new FenBrowser.Host.ProcessIsolation.RendererInputEvent
                    {
                        Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.DblClick,
                        X = docX,
                        Y = docY,
                        Button = button
                    });
                }
            }
            
            if (emitClick && button == 0)
            {
                if (result.IsLink && !string.IsNullOrEmpty(result.Href))
                {
                    LinkClicked?.Invoke(ResolveHrefForUi(result.Href));
                }
            }
            return result;
        }

        EnqueueInput(BrowserInputType.MouseUp, docX, docY, button);
        if (emitClick && button == 0)
        {
            var effectiveHref = result.Href;
            if (string.IsNullOrEmpty(effectiveHref) && _lastHitTest.IsLink)
            {
                effectiveHref = _lastHitTest.Href;
            }

            if (!string.IsNullOrEmpty(effectiveHref))
            {
                LinkClicked?.Invoke(ResolveHrefForUi(effectiveHref));
            }

            EnqueueInput(BrowserInputType.Click, docX, docY, button);
            if (ShouldEmitDoubleClick(windowX, windowY, button))
            {
                EnqueueInput(BrowserInputType.DoubleClick, docX, docY, button);
            }

        }
        return result;
    }

    public Task<bool> HandleClick(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        HandleMouseDown(windowX, windowY, 0, viewportOffsetX, viewportOffsetY);
        var result = HandleMouseUp(windowX, windowY, 0, true, viewportOffsetX, viewportOffsetY);

        EngineLogBridge.Info($"[Debug] Click at {windowX},{windowY} hit: {result.TagName ?? "None"} (ID: {result.ElementId ?? "None"}) Link: {result.IsLink}", LogCategory.General);
        return Task.FromResult(result.IsLink && !string.IsNullOrEmpty(result.Href));
    }

    private bool ShouldEmitDoubleClick(float windowX, float windowY, int button)
    {
        var now = Environment.TickCount64;
        var withinTime = _lastClickTickMs >= 0 && now - _lastClickTickMs <= DoubleClickThresholdMs;
        var withinDistance =
            Math.Abs(windowX - _lastClickWindowX) <= DoubleClickDistance &&
            Math.Abs(windowY - _lastClickWindowY) <= DoubleClickDistance;
        var sameButton = button == _lastClickButton;

        _lastClickTickMs = now;
        _lastClickWindowX = windowX;
        _lastClickWindowY = windowY;
        _lastClickButton = button;

        return withinTime && withinDistance && sameButton;
    }

    private string ResolveHrefForUi(string href)
    {
        if (string.IsNullOrEmpty(href)) return href;
        if (_browser.CurrentUri == null) return href;
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        if (Uri.TryCreate(_browser.CurrentUri, href, out var resolved))
        {
            return resolved.AbsoluteUri;
        }

        return href;
    }

    public async Task HandleKeyPress(string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            EnqueueInput(
                key.Length == 1 && !char.IsControl(key[0])
                    ? BrowserInputType.TextInput
                    : BrowserInputType.KeyDown,
                text: key);
        }
        await Task.CompletedTask;
    }

    private static FenBrowser.Host.ProcessIsolation.RendererInputEvent CreateRendererKeyboardInput(string key)
    {
        if (key.Length == 1 && !char.IsControl(key[0]))
        {
            return new FenBrowser.Host.ProcessIsolation.RendererInputEvent
            {
                Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.TextInput,
                Text = key
            };
        }

        return new FenBrowser.Host.ProcessIsolation.RendererInputEvent
        {
            Type = FenBrowser.Host.ProcessIsolation.RendererInputEventType.KeyDown,
            Key = key
        };
    }
    
    public async Task HandleClipboardCommand(string command, string data = null)
    {
        if (!string.IsNullOrWhiteSpace(command))
        {
            EnqueueInput(BrowserInputType.ClipboardCommand, text: data, command: command);
        }
        await Task.CompletedTask;
    }

    private void DispatchClipboardCommandOnEngineThread(string command, string data)
    {
        if (string.Equals(command, "copy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "cut", StringComparison.OrdinalIgnoreCase))
        {
            var selectedText = _browser.GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText) && ClipboardWriteRequested != null)
            {
                _ = WindowManager.Instance.RunOnMainThread(
                    () => ClipboardWriteRequested?.Invoke(selectedText));
            }

            if (string.Equals(command, "cut", StringComparison.OrdinalIgnoreCase))
            {
                _browser.DeleteSelection();
            }
            return;
        }

        _browser.HandleClipboardCommand(command, data).GetAwaiter().GetResult();
    }

    private Element FindElementById(Element root, string id)
    {
        if (root == null) return null;
        if (root.Id == id) return root;

        foreach (var child in root.Descendants())
        {
            if (child is Element el && el.Id == id) return el;
        }
        return null;
    }
    
    // --- NEW: Smooth Scroll with Physics (10/10) ---
    
    /// <summary>
    /// Apply smooth scroll with momentum physics.
    /// Call this each frame to animate scroll deceleration.
    /// </summary>
    public void UpdateScrollPhysics(double deltaTime)
    {
        EnqueueInput(BrowserInputType.ScrollAnimationTick, deltaY: (float)deltaTime);
    }

    private void ApplyScrollPhysics(double deltaTime)
    {
        bool changed = AdvanceSmoothWheelScroll(deltaTime);

        if (_scrollPhysics.IsAnimating)
        {
            _scrollPhysics.Update((float)deltaTime);
            var newScrollY = _scrollPhysics.CurrentPosition;
            
            // Clamp to valid range
            float maxScroll = Math.Max(0, _contentHeight - _lastViewportSize.Height);
            newScrollY = Math.Max(0, Math.Min(maxScroll, newScrollY));
            
            if (Math.Abs(newScrollY - _scrollY) > 0.5f)
            {
                changed |= SetAnimatedScrollPosition(newScrollY, syncPhysics: false);
            }
        }

        if (changed || _smoothWheelScrollActive || _scrollPhysics.IsAnimating)
        {
            RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.Animation, "BrowserIntegration.UpdateScrollPhysics");
            NeedsRepaint?.Invoke();
        }
    }
    
    /// <summary>
    /// Start smooth scrolling with momentum.
    /// </summary>
    public void StartMomentumScroll(float velocity)
    {
        _smoothWheelScrollActive = false;
        _scrollPhysics.StartMomentum(_scrollY, velocity);
    }
    
    /// <summary>
    /// Stop any ongoing smooth scroll animation.
    /// </summary>
    public void StopMomentumScroll()
    {
        _smoothWheelScrollActive = false;
        _scrollPhysics.Stop();
    }
    
    private void DrawInputOverlay(SKCanvas canvas, InputOverlayData overlay)
    {
        if (overlay == null) return;

        string text = overlay.InitialText;
        bool isPlaceholder = false;
        
        if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(overlay.Placeholder))
        {
            text = overlay.Placeholder;
            isPlaceholder = true;
        }
        
        if (string.IsNullOrEmpty(text)) return;

        using var font = new SKFont(SKTypeface.FromFamilyName(overlay.FontFamily), overlay.FontSize);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = isPlaceholder ? SKColors.Gray : (overlay.TextColor ?? SKColors.Black)
        };

        var metrics = font.Metrics;
        // Vertically center based on font metrics
        float textHeight = metrics.Descent - metrics.Ascent;
        float y = overlay.Bounds.MidY + textHeight / 2 - metrics.Descent;

        // Horizontal alignment
        float x = overlay.Bounds.Left + 10;
        if (overlay.TextAlign == "center")
        {
            x = overlay.Bounds.MidX - font.MeasureText(text) / 2;
        }
        else if (overlay.TextAlign == "right")
        {
            x = overlay.Bounds.Right - font.MeasureText(text) - 10;
        }

        canvas.Save();
        canvas.ClipRect(overlay.Bounds);
        canvas.DrawText(text, x, y, font, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Immutable snapshot of the last committed frame produced by the engine thread.
    /// The compositor and UI thread read this without any lock — the engine thread
    /// publishes a new instance after each RecordFrame via atomic reference swap.
    /// </summary>
    private sealed class ContentSnapshot
    {
        /// <summary>Pre-recorded display-list picture. Drawn directly by the compositor.</summary>
        public readonly SKPicture Frame;
        /// <summary>Input overlays (text fields, etc.) painted with this frame.</summary>
        public readonly List<InputOverlayData> Overlays;
        /// <summary>Viewport size at commit time.</summary>
        public readonly SKSize ViewportSize;
        /// <summary>Document scroll Y at commit time.</summary>
        public readonly float CommittedScrollY;
        /// <summary>Total document content height.</summary>
        public readonly float ContentHeight;

        public ContentSnapshot(
            SKPicture frame,
            List<InputOverlayData> overlays,
            SKSize viewportSize,
            float committedScrollY,
            float contentHeight)
        {
            Frame = frame;
            Overlays = overlays ?? new List<InputOverlayData>();
            ViewportSize = viewportSize;
            CommittedScrollY = committedScrollY;
            ContentHeight = contentHeight;
        }
    }
}

// --- Navigation Event Args (10/10 Spec) ---

/// <summary>
/// Event arguments for navigation events.
/// </summary>
public class NavigationEventArgs
{
    public string Url { get; set; }
    public NavigationType Type { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Title { get; set; }
    public bool IsRedirect { get; set; }
}

/// <summary>
/// Type of navigation.
/// </summary>
public enum NavigationType
{
    Link,
    Typed,
    Reload,
    BackForward,
    FormSubmit,
    Other
}

/// <summary>
/// Event arguments for navigation errors.
/// </summary>
public class NavigationErrorArgs
{
    public string Url { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public Exception Exception { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// --- Scroll Physics (10/10 Spec) ---

/// <summary>
/// Smooth scrolling physics with momentum and deceleration.
/// </summary>
public class ScrollPhysics
{
    public float CurrentPosition { get; private set; }
    public float Velocity { get; private set; }
    public float Deceleration { get; set; } = 0.95f;
    public float MinVelocity { get; set; } = 0.5f;
    public bool IsAnimating { get; private set; }
    
    /// <summary>
    /// Start momentum scrolling from current position with initial velocity.
    /// </summary>
    public void StartMomentum(float startPosition, float velocity)
    {
        CurrentPosition = startPosition;
        Velocity = velocity;
        IsAnimating = Math.Abs(velocity) > MinVelocity;
    }
    
    /// <summary>
    /// Update physics for this frame.
    /// </summary>
    public void Update(float deltaTime)
    {
        if (!IsAnimating) return;
        
        // Apply velocity
        CurrentPosition += Velocity * deltaTime * 60f; // Normalize to 60fps
        
        // Apply deceleration (friction)
        Velocity *= Deceleration;
        
        // Stop when velocity is negligible
        if (Math.Abs(Velocity) < MinVelocity)
        {
            Velocity = 0;
            IsAnimating = false;
        }
    }
    
    /// <summary>
    /// Immediately stop scrolling animation.
    /// </summary>
    public void Stop()
    {
        Velocity = 0;
        IsAnimating = false;
    }
    
    /// <summary>
    /// Set position directly (for programmatic scrolling).
    /// </summary>
    public void SetPosition(float position)
    {
        CurrentPosition = position;
        Velocity = 0;
        IsAnimating = false;
    }
}

