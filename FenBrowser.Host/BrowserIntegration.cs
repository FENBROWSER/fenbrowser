using System;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Interaction;

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
    
    // Last hit test result (for status bar display)
    private HitTestResult _lastHitTest = HitTestResult.None;
    
    public string CurrentUrl => _browser.CurrentUri?.AbsoluteUri ?? "";
    public bool IsLoading { get; private set; }
    public bool CanGoBack => _browser.CanGoBack;
    public bool CanGoForward => _browser.CanGoForward;
    public HitTestResult LastHitTest => _lastHitTest;
    
    public event Action<string> TitleChanged;
    public event Action<string> UrlChanged;
    public event Action<bool> LoadingChanged;
    public event Action NeedsRepaint;
    public event Action<string> LinkClicked;
    public event Action<HitTestResult> HitTestChanged;
    
    public BrowserIntegration()
    {
        _browser = new BrowserHost();
        _renderer = new SkiaDomRenderer();
        
        // Wire browser events
        _browser.Navigated += (s, e) => 
        {
            UrlChanged?.Invoke(CurrentUrl);
        };
        
        _browser.LoadingChanged += (s, loading) =>
        {
            IsLoading = loading;
            LoadingChanged?.Invoke(loading);
        };
        
        _browser.RepaintReady += (s, e) =>
        {
            // Update DOM and styles on repaint
            _root = _browser.GetDomRoot();
            _styles = _browser.ComputedStyles;
            _needsRepaint = true;
            NeedsRepaint?.Invoke();
        };
    }
    
    /// <summary>
    /// Navigate to the given URL.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        
        // Add protocol if missing
        if (!url.StartsWith("http://") && !url.StartsWith("https://") && 
            !url.StartsWith("file://") && !url.StartsWith("data:"))
        {
            url = "https://" + url;
        }
        
        FenLogger.Info($"[BrowserIntegration] Navigating to: {url}", LogCategory.General);
        
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
    /// Render the current page to the canvas.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect viewport)
    {
        if (_root == null || _styles == null)
        {
            // Draw loading or placeholder
            DrawPlaceholder(canvas, viewport);
            return;
        }
        
        // Adjust viewport for scroll
        var scrolledViewport = new SKRect(
            viewport.Left,
            viewport.Top + _scrollY,
            viewport.Right,
            viewport.Bottom + _scrollY
        );
        
        // Apply scroll transform
        canvas.Save();
        canvas.Translate(0, -_scrollY);
        
        try
        {
            FenLogger.Info($"[BrowserIntegration] Render called. Root type: {_root?.GetType().Name}, Styles count: {_styles?.Count}", LogCategory.General);
            
            if (_root == null)
            {
                 FenLogger.Error("[BrowserIntegration] Root is null!", LogCategory.General);
            }
            if (_styles == null || _styles.Count == 0)
            {
                 FenLogger.Error("[BrowserIntegration] Styles dict is empty or null!", LogCategory.General);
            }

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
                new SKSize(viewport.Width, viewport.Height)
            );
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[BrowserIntegration] Render error: {ex.Message}", LogCategory.General);
        }
        
        canvas.Restore();
        _needsRepaint = false;
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
        float scrollSpeed = 40f;
        _scrollY -= deltaY * scrollSpeed;
        
        // Clamp scroll
        _scrollY = Math.Max(0, _scrollY);
        if (_contentHeight > 0)
        {
            _scrollY = Math.Min(_scrollY, _contentHeight - 600); // Leave some visible
        }
        
        _needsRepaint = true;
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

