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
    
    // Last hit test result (for status bar display)
    private HitTestResult _lastHitTest = HitTestResult.None;
    
    private string _overrideUrl;
    private Element? _highlightedElement;
    
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
    public event Action<bool> LoadingChanged;
    public event Action NeedsRepaint;
    public event Action<string> LinkClicked;
    public event Action<string> ConsoleMessage;
    public event Action<HitTestResult> HitTestChanged;
    public event Action<float, float> ScrollChanged;
    
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
            LoadingChanged?.Invoke(loading);
        };
        
        _browser.RepaintReady += (s, e) =>
        {
            _needsRepaint = true;
            _wakeEvent.Set(); // Wake engine thread to record new frame
            NeedsRepaint?.Invoke(); // Signal UI to redraw using last frame
        };
        
        _browser.TitleChanged += (s, title) => TitleChanged?.Invoke(title);
        
        _browser.ConsoleMessage += msg => ConsoleMessage?.Invoke(msg);
        
        // Start Engine Thread
        _engineThread = new Thread(EngineLoop) { IsBackground = true, Name = "FenEngine-Render" };
        _engineThread.Start();
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
    
    private void EngineLoop()
    {
        while (_running)
        {
            // 1. Process Input Events
            while (_eventQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { FenLogger.Error($"[EngineLoop] Action error: {ex.Message}", LogCategory.General); }
            }
            
            // 2. Perform Layout & Record Frame if needed
            if (_needsRepaint && _lastViewportSize.Width > 0)
            {
                FenLogger.Info($"[EngineLoop] Repainting. Viewport: {_lastViewportSize}", LogCategory.General);
                // Sync latest state from browser host
                _root = _browser.GetDomRoot();
                _styles = _browser.ComputedStyles;
                
                RecordFrame(_lastViewportSize);
            }
            
            // 3. Wait for work
            _wakeEvent.WaitOne(16); // ~60fps poll or event-driven
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
        
        // Add protocol if missing
        if (!url.StartsWith("http://") && !url.StartsWith("https://") && 
            !url.StartsWith("file://") && !url.StartsWith("data:") && 
            !url.StartsWith("fen://") && !url.StartsWith("view-source:"))
        {
            // Default to http for localhost/127.0.0.1 to facilitate debugging
            if (url.StartsWith("localhost") || url.StartsWith("127.0.0.1") || url.StartsWith("[::1]"))
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
            _overrideUrl = url;
            UrlChanged?.Invoke(url);
            NeedsRepaint?.Invoke();
            return;
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
                 // Trace once per second to avoid spam?
                 if (DateTime.Now.Second % 5 == 0) 
                     FenLogger.Warn("[BrowserIntegration] Render: _currentFrame is null! Drawing Placeholder.", LogCategory.Rendering);
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
        if (_root == null || _styles == null) 
        {
            FenLogger.Warn($"[BrowserIntegration] RecordFrame skipped: Root={_root != null}, Styles={_styles != null}", LogCategory.General);
            _needsRepaint = false; // Stop the loop!
            return;
        }
        
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
                },
                viewportSize
            );
            
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
    public HitTestResult HandleMouseMove(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
    {
        return PerformHitTest(windowX, windowY, viewportOffsetX, viewportOffsetY);
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
    public bool HandleClick(float windowX, float windowY, float viewportOffsetX = 0, float viewportOffsetY = 0)
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
            _ = NavigateAsync(href);
            return true;
        }
        
        return false;
    }
}

