using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.DevTools; // Added this using statement

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
    private Element _root;
    private Dictionary<Node, CssComputed> _styles;
    private bool _needsRepaint = true;
    private bool _hasFirstStyledRender = false; // Track first styled render to avoid unstyled initial layout
    private DateTime _lastNavigationTime = DateTime.Now; // Track navigation start time for timeout
    private float _scrollY = 0;
    private float _contentHeight = 0;
    private float _dpiScale = 1.0f;
    private SKSize _lastViewportSize;
    
    // Threading & Event Queue
    private readonly ConcurrentQueue<Action> _eventQueue = new ConcurrentQueue<Action>();
    private readonly Thread _engineThread;
    private readonly AutoResetEvent _wakeEvent = new AutoResetEvent(false);
    private bool _running = true;
    // Rendering Buffers (Double-Buffered Display List)
    private SKPicture _currentFrame;
    private readonly object _frameLock = new object();
    private readonly SKPictureRecorder _recorder = new SKPictureRecorder();
    private bool _hasLoggedFrameNull = false; // To reduce warning spam
    
    // Last hit test result (for status bar display)
    private HitTestResult _lastHitTest = HitTestResult.None;
    
    private string _overrideUrl;
    private Element? _highlightedElement;
    
    // Safety Net: Polls for DOM updates if events are missed
    private System.Threading.Timer _domPoller;
    
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
    
    public BrowserIntegration()
    {
        _browser = new BrowserHost();
        _renderer = new SkiaDomRenderer();
        
        // Wire browser events
        _browser.Navigated += (s, e) => 
        {
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
            // CRITICAL FIX: Sync DOM immediately when RenderAsync fires RepaintReady
            _root = _browser.GetDomRoot();
            _styles = _browser.ComputedStyles;
            
            FenLogger.Info($"[BrowserIntegration] RepaintReady: Root={(_root?.TagName ?? "NULL")}, Styles={(_styles?.Count.ToString() ?? "NULL")}", LogCategory.Rendering);
            
            // THREAD SAFETY FIX: Do NOT call RecordFrame directly from this event handler.
            // RepaintReady can fire from any thread (including thread pool continuations),
            // but SKPictureRecorder is NOT thread-safe. Always signal the engine thread
            // to perform the actual rendering work on its dedicated thread.
            _needsRepaint = true;
            _wakeEvent.Set();
            
            // Signal UI that a repaint is pending (optional immediate feedback)
            NeedsRepaint?.Invoke();
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
                var actualDom = _browser.GetDomRoot();
                var actualStyles = _browser.ComputedStyles;
                
                bool needsSync = false;
                
                // Sync DOM if changed
                if (actualDom != null && actualDom != _root)
                {
                    _root = actualDom;
                    needsSync = true;
                }
                
                // Sync Styles if changed or if we have DOM but no styles
                if (actualStyles != null && (_styles == null || _styles.Count == 0 || actualStyles != _styles))
                {
                    _styles = actualStyles;
                    needsSync = true;
                }
                
                if (needsSync)
                {
                    _needsRepaint = true;
                    _wakeEvent.Set();
                }
            }
            catch {}
        }, null, 1000, 500);
    }

    public void HighlightElement(Element? element)
    {
        if (_highlightedElement != element)
        {
            _highlightedElement = element;
            _needsRepaint = true;
            _wakeEvent.Set();
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
        _needsRepaint = true;
        _wakeEvent.Set();
        NeedsRepaint?.Invoke();
    }
    
    public object EvaluateScript(string script)
    {
        try
        {
            // For now, run sync to satisfy legacy DevTools API
            var task = _browser.ExecuteScriptAsync(script);
            task.Wait(2000);
            return task.IsCompletedSuccessfully ? task.Result : "Error: Script execution failed or timed out.";
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
            // 1. Process Input Events (Host -> Engine)
            while (_eventQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { FenLogger.Error($"[EngineLoop] Action error: {ex.Message}", LogCategory.General); }
            }
            
            // 2. Pump JS Event Loop (Tasks & Microtasks)
            // Execute one slice of work per frame tick to keep UI responsive
            bool loopsToRun = true;
            int sliceCount = 0;
            while (loopsToRun && sliceCount < 50) // Cap to avoid starving render
            {
                try 
                {
                    if (!coordinator.ProcessNextTask()) 
                    {
                        loopsToRun = false;
                    }
                    else
                    {
                        sliceCount++;
                    }
                }
                catch (Exception ex) 
                { 
                    FenLogger.Error($"[EngineLoop] Coordinator error: {ex}", LogCategory.JavaScript);
                    loopsToRun = false; 
                }
            }
            
            try
            {
                // 3. Perform Layout & Record Frame if needed
                if ((_needsRepaint || coordinator.CurrentPhase == FenBrowser.Core.Engine.EnginePhase.Layout) && _lastViewportSize.Width > 0)
                {
                    FenLogger.Info($"[EngineLoop] Repainting. Viewport: {_lastViewportSize}", LogCategory.General);
                    // Sync latest state from browser host
                    _root = _browser.GetDomRoot();
                    _styles = _browser.ComputedStyles;
                    
                    // Debug: Log synced state
                    FenLogger.Info($"[EngineLoop] After sync: Root={(_root != null ? _root.TagName : "NULL")}, Styles={(_styles != null ? _styles.Count.ToString() : "NULL")}", LogCategory.Rendering);
                    
                    // CRITICAL FIX: Skip layout if CSS styles not yet attached to THIS DOM
                    // Use root.ComputedStyle as the check (set by CSS cascade on this DOM)
                    // BUT don't block forever - allow render after timeout
                    if (!_hasFirstStyledRender && _root != null)
                    {
                        // Update styles reference in case engine created a new dictionary
                        _styles = _browser.ComputedStyles;
                        
                        if (_styles == null || _styles.Count == 0)
                        {
                            // Check if we've been waiting too long
                            var elapsed = DateTime.Now - _lastNavigationTime;
                            if (elapsed.TotalMilliseconds < 5000)
                            {
                                FenLogger.Debug($"[EngineLoop] Styles not ready ({_styles?.Count ?? 0}). PROCEEDING ANYWAY to debug Whitescreen. ({elapsed.TotalMilliseconds:F0}ms)", LogCategory.Rendering);
                                _needsRepaint = true; // Keep requesting repaint until CSS is ready
                                // return; // FORCE PROCEED
                            }
                            else
                            {
                                FenLogger.Warn($"[EngineLoop] TIMEOUT: Allowing unstyled layout after {elapsed.TotalMilliseconds:F0}ms. Styles still null/empty.", LogCategory.Rendering);
                            }
                        }
                    }
                    if (_root != null && _root.ComputedStyle != null)
                    {
                        _hasFirstStyledRender = true; // Mark that we now have styles attached to this DOM
                    }
                    
                    lock (_rendererLock)
                    {
                        RecordFrame(_lastViewportSize);
                    }
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[EngineLoop] CRASH: {ex}", LogCategory.Rendering);
            }
            
            // 4. Adaptive Wait (CPU Optimization)
            // If needs repaint (animations/scroll), poll fast (16ms).
            // If idle, wait indefinitely until an event wakes us.
            // NEW: If coordinator has work, don't sleep!
            bool hasWork = _needsRepaint || coordinator.HasPendingTasks || coordinator.HasPendingMicrotasks || sliceCount > 0;
            int timeoutKey = hasWork ? 0 : -1; // 0 = Yield, -1 = Wait
            
            // Limit invalidation speed slightly if just painting (16ms cap approx)
            if (_needsRepaint) timeoutKey = 16;
            else if (sliceCount > 0) timeoutKey = 1; // Yield briefly for JS
            
            _wakeEvent.WaitOne(timeoutKey);
        }
    }
    
    private void PostToEngine(Action action)
    {
        _eventQueue.Enqueue(action);
        _wakeEvent.Set();
    }
    
    /// <summary>
    /// Navigate to the given URL.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        
        // Reset navigation timing for unstyled layout skip
        _lastNavigationTime = DateTime.Now;
        _hasFirstStyledRender = false;
        
        // Add protocol if missing
        if (!url.StartsWith("http://") && !url.StartsWith("https://") && 
            !url.StartsWith("file://") && !url.StartsWith("data:") && 
            !url.StartsWith("fen://") && !url.StartsWith("view-source:"))
        {
            // Check for local file path (e.g. C:\... or /...)
            bool isLocalPath = (url.Length >= 2 && url[1] == ':') || url.StartsWith("/") || url.StartsWith("\\") || url.StartsWith("./") || url.StartsWith("../");
            
            if (isLocalPath)
            {
                try
                {
                    string fullPath = System.IO.Path.GetFullPath(url);
                    url = "file://" + fullPath.Replace("\\", "/");
                }
                catch { url = "file://" + url.Replace("\\", "/"); }
            }
            // Default to http for localhost/127.0.0.1 to facilitate debugging
            else if (url.StartsWith("localhost") || url.StartsWith("127.0.0.1") || url.StartsWith("[::1]"))
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
            await _browser.NavigateAsync(url);
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[BrowserIntegration] Navigation failed: {ex.Message}", LogCategory.General);
        }
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
        await _browser.RefreshAsync();
    }
    
    /// <summary>
    /// Render the current display list (picture) to the UI canvas.
    /// This is called on the UI thread and is extremely fast.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport)
    {
        lock (_frameLock)
        {
            if (_currentFrame != null)
            {
                canvas.DrawPicture(_currentFrame);
                return;
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
        
        // If no frame exists, draw placeholder
        DrawPlaceholder(canvas, viewport);
    }
    
    /// <summary>
    /// Update the display list by recording a new frame.
    /// This can be called on a background thread.
    /// </summary>
    public void RecordFrame(SKSize viewportSize)
    {
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
            _needsRepaint = false; 
            FenLogger.Warn("[BrowserIntegration] RecordFrame skipped: no root and no styles. Clearing Repaint flag.", LogCategory.General);
            return; 
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
        
        // === DOM → Layout → Frame transition begins ===
        FenLogger.Info($"[TRANSITION] DOM built (HTML). Running layout on real DOM. Viewport={viewportSize}", LogCategory.Layout);
        
        try
        {
            var viewport = new SKRect(0, 0, viewportSize.Width, viewportSize.Height);
            
            // Start recording
            var canvas = _recorder.BeginRecording(viewport);
            
            // Adjust for scroll
            var scrolledViewport = new SKRect(
                viewport.Left,
                viewport.Top + _scrollY,
                viewport.Right,
                viewport.Bottom + _scrollY
            );
            
            canvas.Save();
            canvas.Translate(0, -_scrollY);
            
            _renderer.Render(
                _root,
                canvas,
                _styles,
                scrolledViewport,
                _browser.CurrentUri?.AbsoluteUri,
                (contentSize, overlays) =>
                {
                    _contentHeight = contentSize.Height;
                    
                    // Manually render input overlays (text/placeholders) on top of the DOM
                    if (overlays != null)
                    {
                        foreach (var overlay in overlays)
                        {
                            DrawInputOverlay(canvas, overlay);
                        }
                    }
                },
                viewportSize
            );
            
            // (Debug overlay removed)
            
            // Draw highlight overlay
            if (_highlightedElement != null)
            {
                DrawHighlight(canvas, _highlightedElement);
            }
            
            canvas.Restore();
            
            // Finish recording and swap buffers
            var newFrame = _recorder.EndRecording();
            
            lock (_frameLock)
            {
                _currentFrame?.Dispose();
                _currentFrame = newFrame;
            }
            
            FenLogger.Info($"[BrowserIntegration] Frame Recorded. Size: {viewportSize}", LogCategory.Rendering); // Log success
            
            _needsRepaint = false; // Set to false HERE        
            // Signal UI to redraw with new frame
            NeedsRepaint?.Invoke();
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[BrowserIntegration] Recording error: {ex.Message}", LogCategory.General);
        }
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
            lock (_frameLock)
            {
                if (_currentFrame == null) return "";
                
                var size = new SKSizeI((int)_lastViewportSize.Width, (int)_lastViewportSize.Height);
                if (size.Width <= 0 || size.Height <= 0) return "";
                
                using var image = SKImage.FromPicture(_currentFrame, size);
                if (image == null) return "";
                
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data == null) return "";
                
                return Convert.ToBase64String(data.ToArray());
            }
        });
    }

    /// <summary>
    /// Focus a specific DOM node.
    /// </summary>
    public void FocusNode(Element element)
    {
        // Internal engine focus logic (placeholder for now as engine doesn't track focus yet)
        // But we can signal it if needed.
        _highlightedElement = element; // For visual feedback in WebDriver
        _needsRepaint = true;
        NeedsRepaint?.Invoke();
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
            
            _needsRepaint = true;
            // CRITICAL: Wake the engine thread to process the scroll immediately
            _wakeEvent.Set();
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
                _ = NavigateAsync(href);
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Hit test to find element at position.
    /// </summary>
    private Element HitTestElement(Element element, float x, float y)
    {
        if (element == null) return null;
        
        // Get element's box if available in styles
        if (_styles.TryGetValue(element, out var computed))
        {
            // Check if point is within element bounds
            // Use box model from renderer if available
        }
        
        // Check children (in reverse order for z-index)
        for (int i = element.Children.Count - 1; i >= 0; i--)
        {
            var child = element.Children[i];
                var hit = HitTestElement(child as Element, x, y);
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
            if (current.Tag?.ToLowerInvariant() == "a")
            {
                string href = null;
                if (current.Attr != null && current.Attr.TryGetValue("href", out href) && !string.IsNullOrWhiteSpace(href))
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
        if (_lastViewportSize != size)
        {
            _lastViewportSize = size;
            _needsRepaint = true;
            _wakeEvent.Set();
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
        // 1. Immediate Hit Test on Main Thread (safe due to lock) for snappy UI feedback
        // This is required for cursor changes to feel instant
        return PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
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
    public async Task<bool> HandleClick(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        var result = PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
        
        FenLogger.Info($"[Debug] Click at {windowX},{windowY} hit: {result.TagName ?? "None"} (ID: {result.ElementId ?? "None"}) Link: {result.IsLink}", LogCategory.General);
        
        if (result.IsLink && !string.IsNullOrEmpty(result.Href))
        {
            FenLogger.Info($"[BrowserIntegration] Link clicked: {result.Href}", LogCategory.General);
            
            // Resolve relative URL
            string href = result.Href;
            if (_browser.CurrentUri != null && !href.StartsWith("http") && !href.StartsWith("data:") && !href.StartsWith("javascript:"))
            {
                try
                {
                    if (Uri.TryCreate(_browser.CurrentUri, href, out var resolved))
                    {
                        href = resolved.AbsoluteUri;
                    }
                }
                catch { }
            }
            
            LinkClicked?.Invoke(href);
            await NavigateAsync(href);
            return true;
        }

        // Handle generic element click (Focus, etc)
        if (result.NativeElement is FenBrowser.Core.Dom.Element nativeEl)
        {
             await _browser.HandleElementClick(nativeEl);
        }
        else if (!string.IsNullOrEmpty(result.ElementId))
        {
            var element = FindElementById(_root, result.ElementId);
            if (element != null)
            {
                await _browser.HandleElementClick(element);
            }
        }
        else
        {
            // Clicked nothing/background -> Clear focus
            await _browser.HandleElementClick(null);
        }
        
        return false;
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
                _needsRepaint = true;
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
