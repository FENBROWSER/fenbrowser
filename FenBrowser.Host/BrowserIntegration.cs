using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
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
    private DateTime _lastNavigationTime = DateTime.Now; // Track navigation start time for timeout
    private float _scrollY = 0;
    private float _contentHeight = 0;
    private float _dpiScale = 1.0f;
    private SKSize _lastViewportSize;
    private bool _hasReceivedViewportSize = false;
    
    // Threading & Event Queue
    private readonly ConcurrentQueue<Action> _eventQueue = new ConcurrentQueue<Action>();
    private readonly Thread _engineThread;
    private readonly AutoResetEvent _wakeEvent = new AutoResetEvent(false);
    private bool _running = true;
    // Rendering Buffers (Double-Buffered Display List)
    private SKPicture _currentFrame;
    private SKImage _currentFrameSeedImage;
    private readonly object _frameLock = new object();
    private readonly SKPictureRecorder _recorder = new SKPictureRecorder();
    private bool _hasLoggedFrameNull = false; // To reduce warning spam
    private List<InputOverlayData> _currentOverlays = new();
    private SKSize _lastCommittedFrameViewport;
    private float _lastCommittedFrameScrollY;
    private DateTime _currentFrameSeedCreatedUtc = DateTime.MinValue;
    private int _consecutiveBaseFrameReuseCount;
    private const int MaxConsecutiveBaseFrameReuseCount = 120;
    private const double MaxBaseFrameAgeMs = 2000;
    private RenderFrameInvalidationReason _pendingInvalidationReasons =
        RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Viewport;
    private string _pendingInvalidationSource = "startup";
    // Remote frame bitmap delivered from a brokered renderer child via shared memory.
    private SKBitmap _remoteFrameBitmap;
    private readonly object _remoteFrameLock = new object();
    
    // Last hit test result (for status bar display)
    private HitTestResult _lastHitTest = HitTestResult.None;
    
    private string _overrideUrl;
    private Element? _highlightedElement;
    private string _pendingFragmentTargetId;
    private string _pendingFragmentSourceUrl;
    
    // Safety Net: Polls for DOM updates if events are missed
    private System.Threading.Timer _domPoller;
    private EventLoopSliceTelemetry _lastEventLoopSliceTelemetry = EventLoopSliceTelemetry.Empty;
    
    public string CurrentUrl => _overrideUrl ?? _browser.CurrentUri?.AbsoluteUri ?? "";
    public bool IsLoading { get; private set; }
    public bool CanGoBack => _browser.CanGoBack;
    public bool CanGoForward => _browser.CanGoForward;
    public HitTestResult LastHitTest => _lastHitTest;
    public Element Document => _root;
    public Dictionary<Node, CssComputed> ComputedStyles => _styles;
    public List<CssLoader.CssSource> CssSources => _browser.Engine.LastCssSources;
    
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
    
    // --- NEW: Structured Navigation Events (10/10) ---
    public event Action<NavigationEventArgs> OnNavigationStarted;
    public event Action<NavigationEventArgs> OnNavigationCompleted;
    public event Action<NavigationErrorArgs> OnNavigationFailed;
    
    // --- Scroll Physics ---
    private readonly ScrollPhysics _scrollPhysics = new();

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
        OwnerTab = ownerTab;
        _browser = new BrowserHost(options: BrowserIntegrationRuntime.CreateBrowserHostOptions());
        _renderer = new SkiaDomRenderer();

        // Inject the actual renderer into BrowserHost so its InputManager
        // hit tests use the populated paint tree instead of a stale copy.
        _browser.SetActiveRenderer(_renderer);
        
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
                CssLoader.ClearCaches();
                FenLogger.Info("[BrowserIntegration] Cleared CSS caches for new navigation", LogCategory.General);
            }
            LoadingChanged?.Invoke(loading);
        };
        
        _browser.RepaintReady += (s, e) =>
        {
            if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current?.UsesOutOfProcessRenderer == true) return;

            // Sync DOM root early so EngineLoop has it on next tick.
            // Sync styles only when snapshot reports a stable DOM+style pair. This avoids
            // adopting transitional style states during refresh/cascade churn.
            var snapshot = _browser.GetRenderSnapshot();
            _root = snapshot.Root;
            if (snapshot.HasStableStyles && snapshot.Styles != null)
            {
                _styles = snapshot.Styles;
            }
            // Seed fragment intent before the first post-navigation frame.
            // BrowserHost raises RepaintReady before Navigated, and Acid2 relies on
            // initial `#top` fragment scrolling to land the face inside the viewport.
            UpdatePendingFragmentNavigation(_browser.CurrentUri);

            FenLogger.Info($"[BrowserIntegration] RepaintReady: Root={(_root?.TagName ?? "NULL")}", LogCategory.Rendering);

            // Wake the engine thread. RecordFrame will call NeedsRepaint?.Invoke() only
            // after a valid frame is committed — never before _currentFrame is ready.
            RequestFrame(
                _hasFirstStyledRender
                    ? RenderFrameInvalidationReason.Dom | RenderFrameInvalidationReason.Style
                    : RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom | RenderFrameInvalidationReason.Style,
                "BrowserHost.RepaintReady");
        };
        
        _browser.TitleChanged += (s, title) => TitleChanged?.Invoke(title);
        
        // [NEW] Wire favicon
        _browser.FaviconChanged += (s, icon) => FaviconChanged?.Invoke(icon);
        
        _browser.ConsoleMessage += msg => ConsoleMessage?.Invoke(msg);
        
        // Start Engine Thread
        _engineThread = new Thread(EngineLoop) { IsBackground = true, Name = "FenEngine-Render" };
        _engineThread.Start();
        
        // Timer to poll for missed updates
        _domPoller = new System.Threading.Timer(_ => 
        {
            try
            {
                if (_browser == null) return;
                var snapshot = _browser.GetRenderSnapshot();
                var actualDom = snapshot.Root;
                var actualStyles = snapshot.Styles;
                var snapshotStable = snapshot.HasStableStyles;
                
                bool needsSync = false;
                bool localStylesMissing = _styles == null || _styles.Count == 0;
                bool committedStyledFrameReady = _hasFirstStyledRender && HasCommittedFrame();

                if (committedStyledFrameReady && _root != null && !localStylesMissing)
                {
                    return;
                }

                bool rootChanged = actualDom != null && actualDom != _root;
                bool actualStylesReady = snapshotStable && actualStyles != null && actualStyles.Count > 0;
                
                // Sync DOM if changed
                if (rootChanged)
                {
                    _root = actualDom;
                    needsSync = true;
                }
                
                // Bootstrap-time healing still adopts polled styles aggressively, but once a
                // styled frame has committed we must not treat late dictionary reference churn
                // as a real content change for the same settled root.
                bool shouldAdoptPolledStyles =
                    actualStylesReady &&
                    (rootChanged || localStylesMissing || !_hasFirstStyledRender);

                if (shouldAdoptPolledStyles && !ReferenceEquals(actualStyles, _styles))
                {
                    _styles = actualStyles;
                    needsSync = true;
                }
                else if (!rootChanged && committedStyledFrameReady && actualStylesReady && !ReferenceEquals(actualStyles, _styles))
                {
                    FenLogger.Debug(
                        "[BrowserIntegration] DomPoller ignored late style snapshot churn after first styled render.",
                        LogCategory.Rendering);
                }
                
                if (needsSync)
                {
                    RequestFrame(
                        RenderFrameInvalidationReason.Dom | RenderFrameInvalidationReason.Style | RenderFrameInvalidationReason.Diagnostics,
                        "BrowserIntegration.DomPoller");
                }
            }
            catch {}
        }, null, 1000, 500);

        if (FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current != null)
        {
            FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.Current.FrameReceived += OnFrameReceivedFromRenderer;
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

        // If the payload carries raw BGRA pixels (from shared memory), decode into an SKBitmap
        // so Render() can composite it directly.
        if (payload?.PixelData != null && payload.PixelData.Length > 0 &&
            payload.SurfaceWidth > 0 && payload.SurfaceHeight > 0)
        {
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

                    lock (_remoteFrameLock)
                    {
                        _remoteFrameBitmap?.Dispose();
                        _remoteFrameBitmap = newBitmap;
                    }

                    FenLogger.Debug($"[BrowserIntegration] Remote frame decoded: {w}x{h} seq={payload.FrameSequenceNumber} for tab={tabId}", LogCategory.Rendering);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[BrowserIntegration] Failed to decode remote frame pixels for tab={tabId}: {ex.Message}", LogCategory.Rendering);
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
    
    private readonly object _rendererLock = new object();

    private void EngineLoop()
    {
        var coordinator = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
        coordinator.OnWorkEnqueued += () => _wakeEvent.Set();

        while (_running)
        {
            // Each iteration is one frame tick with a 16.6ms budget (60fps target)
            var deadline = new FenBrowser.Core.Deadlines.FrameDeadline(16.6, "Frame");

            // Stage 1: Drain host → engine input events
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
            bool hasWork = _needsRepaint || coordinator.HasPendingTasks || coordinator.HasPendingMicrotasks || sliceTelemetry.ProcessedTaskCount > 0;
            int waitMs;
            if (_needsRepaint) waitMs = 16;
            else if (sliceTelemetry.ProcessedTaskCount > 0) waitMs = 1;
            else if (hasWork) waitMs = 0;
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
            catch (Exception ex) { FenLogger.Error($"[EngineLoop] Action error: {ex.Message}", LogCategory.General); }
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
                FenLogger.Error($"[EngineLoop] Coordinator error: {ex}", LogCategory.JavaScript);
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
            _styles = snapshot.HasStableStyles ? snapshot.Styles : null;

            // Gate: wait for a stable DOM+style snapshot before first new frame.
            if (!_hasFirstStyledRender && _root != null)
            {
                bool hasStyles = snapshot.HasStableStyles;
                if (!hasStyles)
                {
                    var elapsed = DateTime.Now - _lastNavigationTime;
                    if (elapsed.TotalMilliseconds < 60000)
                    {
                        DeferPendingFrame();
                        _wakeEvent.WaitOne(250);
                        return false;
                    }
                    FenLogger.Warn($"[EngineLoop] CSS timeout after {elapsed.TotalMilliseconds:F0}ms — rendering unstyled.", LogCategory.Rendering);
                }
            }
            if (_root != null && snapshot.HasStableStyles)
                _hasFirstStyledRender = true;

            lock (_rendererLock)
            {
                RecordFrame(_lastViewportSize);
            }
            return true;
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[EngineLoop] CRASH: {ex}", LogCategory.Rendering);
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
        // Navigation must start from top; carrying prior page scroll causes blank/shifted first paints
        // on short documents (e.g., Acid2 reference page).
        _scrollY = 0f;
        _contentHeight = 0f;
        lock (_frameLock)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
            _currentFrameSeedImage?.Dispose();
            _currentFrameSeedImage = null;
            _currentOverlays = new List<InputOverlayData>();
            _lastCommittedFrameViewport = SKSize.Empty;
            _lastCommittedFrameScrollY = 0f;
            _currentFrameSeedCreatedUtc = DateTime.MinValue;
            _consecutiveBaseFrameReuseCount = 0;
        }
        ScrollChanged?.Invoke(_scrollY, _contentHeight);
        RequestFrame(RenderFrameInvalidationReason.Navigation, "BrowserIntegration.Navigate");

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
                    FenLogger.Warn("[BrowserIntegration] Programmatic local-path normalization blocked; delegating to navigation policy.", LogCategory.Navigation);
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

        FenLogger.Info($"[BrowserIntegration] Navigating to: {url}", LogCategory.General);

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

            if (isUserInput)
            {
                await _browser.NavigateUserInputAsync(url);
            }
            else
            {
                await _browser.NavigateAsync(url);
            }
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[BrowserIntegration] Navigation failed: {ex.Message}", LogCategory.General);
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
        await _browser.GoBackAsync();
    }
    
    /// <summary>
    /// Navigate forward in history.
    /// </summary>
    public async Task GoForwardAsync()
    {
        await _browser.GoForwardAsync();
    }
    
    /// <summary>
    /// Refresh the current page.
    /// </summary>
    public async Task RefreshAsync()
    {
        _lastNavigationTime = DateTime.Now;
        _hasFirstStyledRender = false;
        RequestFrame(RenderFrameInvalidationReason.Navigation, "BrowserIntegration.Refresh");
        StartPostNavigationRepaintPulse();
        await _browser.RefreshAsync();
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
                    canvas.DrawBitmap(_remoteFrameBitmap, 0, 0);
                    return;
                }
            }
            // No remote frame yet; draw placeholder while waiting for first delivery.
            DrawPlaceholder(canvas, viewport);
            return;
        }

        List<InputOverlayData> overlays = null;
        Element? highlight = _highlightedElement;
        bool hasFrame = false;

        lock (_frameLock)
        {
            if (_currentFrame != null)
            {
                hasFrame = true;
                canvas.DrawPicture(_currentFrame);
                if (_currentOverlays.Count > 0)
                {
                    overlays = new List<InputOverlayData>(_currentOverlays);
                }
            }
            else
            {
                 // Log only once per session to avoid spam during startup
                 if (!_hasLoggedFrameNull)
                 {
                     _hasLoggedFrameNull = true;
                     FenLogger.Debug("[BrowserIntegration] Render: _currentFrame is null, drawing placeholder.", LogCategory.Rendering);
                 }
            }
        }

        if (!hasFrame)
        {
            DrawPlaceholder(canvas, viewport);
            return;
        }

        if ((overlays == null || overlays.Count == 0) && highlight == null)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(0, -_scrollY);
        if (overlays != null)
        {
            foreach (var overlay in overlays)
            {
                DrawInputOverlay(canvas, overlay);
            }
        }

        if (highlight != null)
        {
            lock (_rendererLock)
            {
                DrawHighlight(canvas, highlight);
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
            FenLogger.Warn($"[BrowserIntegration] RecordFrame skipped: invalid size {viewportSize}", LogCategory.Rendering);
            return;
        }
        
        // RELAXED GATING: Allow early structural frames
        // Only skip when BOTH root AND styles are missing
        if (_root == null && _styles == null) 
        {
            // STOP THE HOT LOOP: If we can't record, stop asking to repaint immediately.
            // We will repaint again when state changes (RepaintReady/Loading/etc) trigger a wake.
            ClearPendingFrameRequest();
            FenLogger.Warn("[BrowserIntegration] RecordFrame skipped: no root and no styles. Clearing Repaint flag.", LogCategory.General);
            return; 
        }

        // Always defer when CSS styles are not yet ready. This prevents committing a blank
        // structural frame (which would become the persistent _currentFrame base). After the
        // 60-second CSS timeout we fall through so the page is at least partially visible.
        if (_styles == null || _styles.Count == 0)
        {
            var cssElapsed = (DateTime.Now - _lastNavigationTime).TotalMilliseconds;
            if (cssElapsed < 60000)
            {
                DeferPendingFrame();
                FenLogger.Debug($"[BrowserIntegration] RecordFrame deferred: waiting for CSS styles ({cssElapsed:F0}ms).", LogCategory.Rendering);
                return;
            }
            // Timeout exceeded — render structurally unstyled so the page isn't blank forever.
            _styles = new Dictionary<Node, CssComputed>();
            FenLogger.Warn("[BrowserIntegration] RecordFrame: CSS timeout — rendering unstyled.", LogCategory.Rendering);
        }
        
        // Guard: Ensure we have a valid HTML element
        string rootTag = _root?.TagName?.ToUpperInvariant() ?? "";
        if (_root != null && rootTag != "HTML")
        {
            FenLogger.Warn($"[BrowserIntegration] RecordFrame skipped: root is '{rootTag}' not HTML", LogCategory.General);
            return;
        }
        
        // Create empty styles dictionary if null but root exists
        if (_styles == null)
        {
            _styles = new Dictionary<Node, CssComputed>();
            FenLogger.Info("[BrowserIntegration] Recording structural frame (no styles yet)", LogCategory.Rendering);
        }
        
        // === DOM → Layout → Paint → Present pipeline ===
        // Note: SkiaDomRenderer.Render() manages its own PipelineContext frame scope internally,
        // so we don't wrap with scoped stages here to avoid nested frame conflicts.
        if (FenBrowser.Core.Logging.DebugConfig.EnableDeepDebug && FenBrowser.Core.Logging.DebugConfig.LogFrameTiming)
        {
            FenLogger.Info($"[TRANSITION] DOM built (HTML). Running layout on real DOM. Viewport={viewportSize}", LogCategory.Layout);
        }

        try
        {
            var viewport = new SKRect(0, 0, viewportSize.Width, viewportSize.Height);
            SKImage reusableSeedImage = null;
            bool canReuseBaseFrame;
            var nowUtc = DateTime.UtcNow;

            lock (_frameLock)
            {
                canReuseBaseFrame = BaseFrameReusePolicy.CanReuseBaseFrame(
                    _currentFrameSeedImage != null,
                    _lastCommittedFrameViewport,
                    viewportSize,
                    _lastCommittedFrameScrollY,
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
            }

            // Start recording
            var canvas = _recorder.BeginRecording(viewport);
            if (canReuseBaseFrame && reusableSeedImage != null)
            {
                lock (_frameLock)
                {
                    if (ReferenceEquals(reusableSeedImage, _currentFrameSeedImage))
                    {
                        canvas.DrawImage(reusableSeedImage, 0, 0);
                    }
                }
            }

            // Adjust for scroll
            var scrolledViewport = new SKRect(
                viewport.Left,
                viewport.Top + _scrollY,
                viewport.Right,
                viewport.Bottom + _scrollY
            );

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

            List<InputOverlayData> frameOverlays = new();
            canvas.Save();
            canvas.Translate(0, -_scrollY);

            RenderFrameResult frameResult;
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

            canvas.Restore();

            // Finish recording and swap buffers
            var newFrame = _recorder.EndRecording();
            var newSeedImage = CreateSeedImageFromFrame(newFrame, viewportSize);

            lock (_frameLock)
            {
                _currentFrame?.Dispose();
                _currentFrame = newFrame;
                _currentFrameSeedImage?.Dispose();
                _currentFrameSeedImage = newSeedImage;
                _currentFrameSeedCreatedUtc = newSeedImage != null ? DateTime.UtcNow : DateTime.MinValue;
                _currentOverlays = frameOverlays;
                _lastCommittedFrameViewport = viewportSize;
                _lastCommittedFrameScrollY = _scrollY;
                _consecutiveBaseFrameReuseCount = (canReuseBaseFrame && newSeedImage != null)
                    ? _consecutiveBaseFrameReuseCount + 1
                    : 0;
            }

            if (_styles != null && _styles.Count > 0)
            {
                _hasFirstStyledRender = true;
            }

            LogCommittedFrame(frameResult, viewportSize);
            bool requestedFollowupFrame = TryApplyPendingFragmentNavigation();

            if (!requestedFollowupFrame)
            {
                ClearPendingFrameRequest();
            }
            NeedsRepaint?.Invoke();
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[BrowserIntegration] Recording error: {ex.Message}", LogCategory.General);
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
            FenLogger.Warn($"[BrowserIntegration] Failed to build seed image for base-frame reuse: {ex.Message}", LogCategory.Rendering);
            return null;
        }
    }

    private void LogCommittedFrame(RenderFrameResult frameResult, SKSize viewportSize)
    {
        var telemetry = frameResult?.Telemetry;
        FenLogger.Info(
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
            FenLogger.Info(
                $"[FragmentNav] Skipped: target='{_pendingFragmentTargetId ?? "<null>"}' source='{_pendingFragmentSourceUrl ?? "<null>"}' root={_root?.TagName ?? "<null>"}",
                LogCategory.Navigation);
            return false;
        }

        if (!string.Equals(CurrentUrl, _pendingFragmentSourceUrl, StringComparison.OrdinalIgnoreCase))
        {
            FenLogger.Info(
                $"[FragmentNav] Skipped: currentUrl='{CurrentUrl}' pendingUrl='{_pendingFragmentSourceUrl}'",
                LogCategory.Navigation);
            return false;
        }

        var target = FindElementById(_root, _pendingFragmentTargetId);
        if (target == null)
        {
            FenLogger.Info($"[FragmentNav] Skipped: target element '#{_pendingFragmentTargetId}' not found", LogCategory.Navigation);
            return false;
        }

        var rect = GetElementRect(target);
        if (!rect.HasValue)
        {
            FenLogger.Info($"[FragmentNav] Skipped: target element '#{_pendingFragmentTargetId}' has no layout box yet", LogCategory.Navigation);
            return false;
        }

        float viewportHeight = Math.Max(1f, _lastViewportSize.Height);
        float maxScroll = Math.Max(0f, _contentHeight - viewportHeight);
        float targetScroll = Math.Max(0f, Math.Min(maxScroll, rect.Value.Top));

        _pendingFragmentTargetId = null;
        _pendingFragmentSourceUrl = null;

        if (Math.Abs(targetScroll - _scrollY) <= 0.5f)
        {
            FenLogger.Info($"[FragmentNav] No-op: '#{_pendingFragmentTargetId}' targetScroll={targetScroll:F1} current={_scrollY:F1}", LogCategory.Navigation);
            return false;
        }

        _scrollY = targetScroll;
        _scrollPhysics.SetPosition(_scrollY);
        ScrollChanged?.Invoke(_scrollY, _contentHeight);
        FenLogger.Info($"[FragmentNav] Applied '#{target.Id}' -> scrollY={_scrollY:F1}", LogCategory.Navigation);
        RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.Navigation, "BrowserIntegration.FragmentNavigation");
        return true;
    }

    private bool HasCommittedFrame()
    {
        lock (_frameLock)
        {
            return _currentFrame != null;
        }
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
            // Fire every 300 ms for up to 10 seconds, but stop once the first styled
            // frame is committed. Wake only the render thread; the Compositor should
            // invalidate from committed frames, not speculative pulses.
            while (_running && (DateTime.Now - pulseStart).TotalSeconds < 10)
            {
                await Task.Delay(300).ConfigureAwait(false);
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
        
        lock (_rendererLock)
        {
            var box = _renderer.GetElementBox(element);
            if (box == null) return null;
            return box.BorderBox;
        }
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
    /// Scroll the viewport to bring a target element near center and request repaint.
    /// </summary>
    public void ScrollToElement(Element element)
    {
        if (element == null)
        {
            return;
        }

        var rect = GetElementRect(element);
        if (!rect.HasValue)
        {
            return;
        }

        var viewportHeight = Math.Max(1f, _lastViewportSize.Height);
        var desiredScroll = rect.Value.MidY - (viewportHeight * 0.5f);
        var maxScroll = Math.Max(0f, _contentHeight - viewportHeight);
        _scrollY = Math.Max(0f, Math.Min(maxScroll, desiredScroll));
        _scrollPhysics.SetPosition(_scrollY);

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
            using var labelPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 12,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
            };
            
            using var labelBgPaint = new SKPaint
            {
                Color = new SKColor(111, 168, 220, 255),
                Style = SKPaintStyle.Fill
            };
            
            float labelWidth = labelPaint.MeasureText(label);
            float labelHeight = 18;
            var labelRect = new SKRect(x, y - labelHeight, x + labelWidth + 8, y);
            
            // Adjust if too close to top
            if (y < labelHeight)
            {
                labelRect = new SKRect(x, y + h, x + labelWidth + 8, y + h + labelHeight);
            }
            
            canvas.DrawRect(labelRect, labelBgPaint);
            canvas.DrawText(label, labelRect.Left + 4, labelRect.Bottom - 4, labelPaint);
        }
    }
    
    private void DrawPlaceholder(SKCanvas canvas, SKRect viewport)
    {
        using var bgPaint = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(viewport, bgPaint);
        
        if (IsLoading)
        {
            using var textPaint = new SKPaint
            {
                Color = SKColors.Gray,
                IsAntialias = true,
                TextSize = 18,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText("Loading...", viewport.MidX, viewport.MidY, textPaint);
        }
        else
        {
            using var textPaint = new SKPaint
            {
                Color = SKColors.Gray,
                IsAntialias = true,
                TextSize = 16,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText("Enter a URL to browse", viewport.MidX, viewport.MidY, textPaint);
        }
    }
    
    /// <summary>
    /// Scroll the content by the given delta.
    /// </summary>
    public void Scroll(float deltaY)
    {
        PostToEngine(() => 
        {
            FenLogger.Debug($"[Scroll] DeltaY={deltaY}, OldY={_scrollY}, ContentHeight={_contentHeight}, Viewport={_lastViewportSize.Height}", LogCategory.Rendering);
            
            float scrollSpeed = 40f;
            _scrollY -= deltaY * scrollSpeed;
            
            // Clamp scroll - use actual viewport height instead of hardcoded 600
            _scrollY = Math.Max(0, _scrollY);
            float maxScroll = Math.Max(0, _contentHeight - _lastViewportSize.Height);
            
            if (_scrollY > maxScroll) _scrollY = maxScroll;

            FenLogger.Debug($"[Scroll] NewY={_scrollY}, MaxScroll={maxScroll}", LogCategory.Rendering);
            
            RequestFrame(RenderFrameInvalidationReason.Scroll, "BrowserIntegration.Scroll");
        });
        
        // Immediate UI feedback (optional, we wait for engine to re-record)
        NeedsRepaint?.Invoke();
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
                FenLogger.Info($"[BrowserIntegration] Link clicked: {href}", LogCategory.General);
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
                        try
                        {
                            Uri resolved;
                            if (Uri.TryCreate(_browser.CurrentUri, href, out resolved))
                            {
                                return resolved.AbsoluteUri;
                            }
                        }
                        catch { }
                    }
                    return href;
                }
            }
            current = current.Parent as Element;
        }
        return null;
    }
    
    public bool NeedsRender => _needsRepaint;
    
    /// <summary>
    /// Get the current scroll position.
    /// </summary>
    public float ScrollY => _scrollY;
    
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
            FenLogger.Info($"[BrowserIntegration] Ignoring small viewport update: {size}", LogCategory.General);
            return;
        }

        FenLogger.Info($"[BrowserIntegration] UpdateViewport: {size}", LogCategory.General);
        _hasReceivedViewportSize = true;
        if (_lastViewportSize != size)
        {
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
    public HitTestResult PerformHitTest(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
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
        
        // Perform hit test in document space
        lock (_rendererLock)
        {
            if (_renderer.HitTest(docX, docY, out var result))
            {
                // Update last hit test (for status bar)
                if (!result.Equals(_lastHitTest))
                {
                    _lastHitTest = result;
                    HitTestChanged?.Invoke(result);
                }

                return result;
            }
        }

        // No hit - reset if changed
        if (_lastHitTest.HasHit)
        {
            _lastHitTest = HitTestResult.None;
            HitTestChanged?.Invoke(_lastHitTest);
        }
        
        return HitTestResult.None;
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

    private Element ResolveActivationTarget(HitTestResult result, float windowX, float windowY, float viewportOffsetX, float viewportOffsetY)
    {
        var target = result.NativeElement as Element ?? _lastHitTest.NativeElement as Element;
        if (target != null)
        {
            return target;
        }

        var (viewportX, viewportY) = TranslateWindowToViewport(windowX, windowY, viewportOffsetX, viewportOffsetY);
        return _browser.HitTestElementAtViewportPoint(viewportX, viewportY);
    }

    /// <summary>
    /// Handle mouse move for cursor updates and status bar.
    /// </summary>
    private bool _isMouseMovePending = false;
    private (float X, float Y, float VX, float VY) _pendingMouseMove;

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

        _browser.OnMouseMove(docX, docY);
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
        _browser.OnMouseDown(docX, docY, 2);
        _browser.OnMouseUp(docX, docY, 2);

        var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
        ContextMenuRequested?.Invoke(new ContextMenuRequest(windowX, windowY, result));
    }

    public event Action<ContextMenuRequest> ContextMenuRequested;
    
    public class ContextMenuRequest
    {
        public float X { get; }
        public float Y { get; }
        public HitTestResult Hit { get; }
        
        public ContextMenuRequest(float x, float y, HitTestResult hit)
        {
            X = x;
            Y = y;
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

        _browser.OnMouseDown(docX, docY, button);
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
            }
            
            if (emitClick && button == 0 && result.IsLink && !string.IsNullOrEmpty(result.Href))
            {
                LinkClicked?.Invoke(ResolveHrefForUi(result.Href));
            }
            return result;
        }

        _browser.OnMouseUp(docX, docY, button);
        if (emitClick && button == 0)
        {
            var activationTarget = ResolveActivationTarget(result, windowX, windowY, viewportOffsetX, viewportOffsetY);
            var effectiveHref = result.Href;
            if (string.IsNullOrEmpty(effectiveHref) && _lastHitTest.IsLink)
            {
                effectiveHref = _lastHitTest.Href;
            }

            if (!string.IsNullOrEmpty(effectiveHref))
            {
                LinkClicked?.Invoke(ResolveHrefForUi(effectiveHref));
            }

            _browser.OnClick(docX, docY, button);

            // Fallback: when low-level event processing lags, execute direct activation/focus
            // using already resolved hit-test element from this same pointer release.
            if (activationTarget != null)
            {
                _ = _browser.HandleElementClick(activationTarget);
            }
        }
        return result;
    }

    public Task<bool> HandleClick(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        HandleMouseDown(windowX, windowY, 0, viewportOffsetX, viewportOffsetY);
        var result = HandleMouseUp(windowX, windowY, 0, true, viewportOffsetX, viewportOffsetY);

        FenLogger.Info($"[Debug] Click at {windowX},{windowY} hit: {result.TagName ?? "None"} (ID: {result.ElementId ?? "None"}) Link: {result.IsLink}", LogCategory.General);
        return Task.FromResult(result.IsLink && !string.IsNullOrEmpty(result.Href));
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

        try
        {
            if (Uri.TryCreate(_browser.CurrentUri, href, out var resolved))
            {
                return resolved.AbsoluteUri;
            }
        }
        catch { }

        return href;
    }

    public async Task HandleKeyPress(string key)
    {
        await _browser.HandleKeyPress(key);
    }
    
    public async Task HandleClipboardCommand(string command, string data = null)
    {
        await _browser.HandleClipboardCommand(command, data);
    }
    
    public string GetSelectedText()
    {
        return _browser.GetSelectedText();
    }
    
    public void DeleteSelection()
    {
        _browser.DeleteSelection();
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
        if (_scrollPhysics.IsAnimating)
        {
            _scrollPhysics.Update((float)deltaTime);
            var newScrollY = _scrollPhysics.CurrentPosition;
            
            // Clamp to valid range
            float maxScroll = Math.Max(0, _contentHeight - _lastViewportSize.Height);
            newScrollY = Math.Max(0, Math.Min(maxScroll, newScrollY));
            
            if (Math.Abs(newScrollY - _scrollY) > 0.5f)
            {
                _scrollY = newScrollY;
                RequestFrame(RenderFrameInvalidationReason.Scroll | RenderFrameInvalidationReason.Animation, "BrowserIntegration.UpdateScrollPhysics");
                ScrollChanged?.Invoke(_scrollY, _contentHeight);
            }
        }
    }
    
    /// <summary>
    /// Start smooth scrolling with momentum.
    /// </summary>
    public void StartMomentumScroll(float velocity)
    {
        _scrollPhysics.StartMomentum(_scrollY, velocity);
    }
    
    /// <summary>
    /// Stop any ongoing smooth scroll animation.
    /// </summary>
    public void StopMomentumScroll()
    {
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

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.FromFamilyName(overlay.FontFamily),
            TextSize = overlay.FontSize,
            IsAntialias = true,
            Color = isPlaceholder ? SKColors.Gray : (overlay.TextColor ?? SKColors.Black)
        };

        var metrics = paint.FontMetrics;
        // Vertically center based on font metrics
        float textHeight = metrics.Descent - metrics.Ascent;
        float y = overlay.Bounds.MidY + textHeight / 2 - metrics.Descent;
        
        // Horizontal alignment
        float x = overlay.Bounds.Left + 10; 
        if (overlay.TextAlign == "center")
        {
            x = overlay.Bounds.MidX - paint.MeasureText(text) / 2;
        }
        else if (overlay.TextAlign == "right")
        {
            x = overlay.Bounds.Right - paint.MeasureText(text) - 10;
        }

        canvas.Save();
        canvas.ClipRect(overlay.Bounds);
        canvas.DrawText(text, x, y, paint);
        canvas.Restore();
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
