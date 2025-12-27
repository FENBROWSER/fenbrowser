using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.Host.Theme;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Status bar widget at the bottom of the window.
/// Shows hover URL preview, load state, zoom level.
/// Uses HitTestResult only, no DOM access.
/// </summary>
public class StatusBarWidget : Widget
{
    private string _hoverUrl;
    
    public StatusBarWidget()
    {
        Role = WidgetRole.StatusBar;
        Name = "Status Bar";
    }
    private bool _isLoading;
    private float _zoomLevel = 100;
    private string _statusText;
    
    private const float HEIGHT = 24;
    private const float PADDING = 8;
    
    /// <summary>
    /// Update from hit test result (for hover URL preview).
    /// </summary>
    public void UpdateFromHitTest(HitTestResult result)
    {
        string newUrl = result.IsLink ? result.Href : null;
        if (_hoverUrl != newUrl)
        {
            _hoverUrl = newUrl;
            Invalidate();
        }
    }
    
    /// <summary>
    /// Update loading state.
    /// </summary>
    public void SetLoading(bool loading)
    {
        if (_isLoading != loading)
        {
            _isLoading = loading;
            _statusText = loading ? "Loading..." : null;
            Invalidate();
        }
    }
    
    /// <summary>
    /// Update zoom level display.
    /// </summary>
    public void SetZoomLevel(float percent)
    {
        if (Math.Abs(_zoomLevel - percent) > 0.1f)
        {
            _zoomLevel = percent;
            Invalidate();
        }
    }
    
    /// <summary>
    /// Set custom status text.
    /// </summary>
    public void SetStatusText(string text)
    {
        if (_statusText != text)
        {
            _statusText = text;
            Invalidate();
        }
    }
    
    /// <summary>
    /// Clear hover URL (called when mouse leaves content).
    /// </summary>
    public void ClearHoverUrl()
    {
        if (_hoverUrl != null)
        {
            _hoverUrl = null;
            Invalidate();
        }
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return new SKSize(availableSpace.Width, HEIGHT);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        // StatusBar already has its bounds set by parent
    }
    
    /// <summary>
    /// Layout this widget within the available bounds.
    /// [DEPRECATED] Use Measure/Arrange instead.
    /// </summary>
    public override void Layout(SKRect available)
    {
        Measure(new SKSize(available.Width, available.Height));
        Arrange(available);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        
        // Background
        using var bgPaint = new SKPaint
        {
            Color = theme.Surface,
            IsAntialias = true
        };
        canvas.DrawRect(Bounds, bgPaint);
        
        // Top border
        using var borderPaint = new SKPaint
        {
            Color = theme.Border,
            StrokeWidth = 1
        };
        canvas.DrawLine(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Top, borderPaint);
        
        // Left side: Hover URL or status text
        using var textPaint = new SKPaint
        {
            Color = theme.TextMuted,
            IsAntialias = true,
            TextSize = 12,
            TextAlign = SKTextAlign.Left
        };
        
        string leftText = _hoverUrl ?? _statusText ?? "";
        
        // Truncate long URLs
        float maxWidth = Bounds.Width - 100 - PADDING * 3; // Reserve space for zoom
        if (!string.IsNullOrEmpty(leftText))
        {
            float textWidth = textPaint.MeasureText(leftText);
            if (textWidth > maxWidth)
            {
                // Truncate with ellipsis
                while (textWidth > maxWidth && leftText.Length > 10)
                {
                    leftText = leftText.Substring(0, leftText.Length - 4) + "...";
                    textWidth = textPaint.MeasureText(leftText);
                }
            }
            canvas.DrawText(leftText, Bounds.Left + PADDING, Bounds.MidY + 4, textPaint);
        }
        
        // Right side: Zoom level (if not 100%)
        if (Math.Abs(_zoomLevel - 100) > 0.1f)
        {
            using var zoomPaint = new SKPaint
            {
                Color = theme.TextMuted,
                IsAntialias = true,
                TextSize = 11,
                TextAlign = SKTextAlign.Right
            };
            canvas.DrawText($"{_zoomLevel:0}%", Bounds.Right - PADDING, Bounds.MidY + 4, zoomPaint);
        }
        
        // Loading indicator (small spinner or text)
        if (_isLoading)
        {
            using var loadPaint = new SKPaint
            {
                Color = theme.Accent,
                IsAntialias = true,
                TextSize = 11,
                TextAlign = SKTextAlign.Right
            };
            float x = Math.Abs(_zoomLevel - 100) > 0.1f ? Bounds.Right - 60 : Bounds.Right - PADDING;
            canvas.DrawText("Loading...", x, Bounds.MidY + 4, loadPaint);
        }
    }
    
    /// <summary>
    /// Get the preferred height for layout.
    /// </summary>
    public static float PreferredHeight => HEIGHT;
}
