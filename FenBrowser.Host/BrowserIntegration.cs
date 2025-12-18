using System;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Host;

/// <summary>
/// Integration layer connecting BrowserHost to the Host render loop.
/// Manages page loading, rendering, and input coordination.
/// </summary>
public class BrowserIntegration
{
    private readonly BrowserHost _browser;
    private readonly SkiaDomRenderer _renderer;
    private LiteElement _root;
    private Dictionary<LiteElement, CssComputed> _styles;
    private bool _needsRepaint = true;
    private float _scrollY = 0;
    private float _contentHeight = 0;
    
    public string CurrentUrl => _browser.CurrentUri?.AbsoluteUri ?? "";
    public bool IsLoading { get; private set; }
    public bool CanGoBack => _browser.CanGoBack;
    public bool CanGoForward => _browser.CanGoForward;
    
    public event Action<string> TitleChanged;
    public event Action<string> UrlChanged;
    public event Action<bool> LoadingChanged;
    public event Action NeedsRepaint;
    
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
    /// </summary>
    public void HandleClick(float x, float y)
    {
        // Adjust for scroll
        float adjustedY = y + _scrollY;
        
        // Find element at position and handle click
        // This would involve hit testing using the renderer's box model
        // For now, log the click
        FenLogger.Debug($"[BrowserIntegration] Click at ({x}, {adjustedY})", LogCategory.General);
    }
    
    public bool NeedsRender => _needsRepaint;
}
