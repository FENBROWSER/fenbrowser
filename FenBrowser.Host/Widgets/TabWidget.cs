using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Tabs;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Single tab visual representation.
/// Visual only, no engine knowledge.
/// </summary>
public class TabWidget : Widget
{
    private readonly BrowserTab _tab;
    private bool _isHovered;
    private bool _isCloseHovered;
    
    private const float TAB_WIDTH = 180;
    private const float TAB_HEIGHT = 32;
    private const float CLOSE_BUTTON_SIZE = 16;
    private const float FAVICON_SIZE = 16;
    private const float PADDING = 8;
    
    /// <summary>
    /// The tab this widget represents.
    /// </summary>
    public BrowserTab Tab => _tab;
    
    /// <summary>
    /// Whether this is the active tab.
    /// </summary>
    public bool IsActive { get; set; }
    
    /// <summary>
    /// Event when tab is clicked (to activate).
    /// </summary>
    public event Action<TabWidget> Clicked;
    
    /// <summary>
    /// Event when close button is clicked.
    /// </summary>
    public event Action<TabWidget> CloseClicked;
    
    public TabWidget(BrowserTab tab)
    {
        _tab = tab;
        Bounds = new SKRect(0, 0, TAB_WIDTH, TAB_HEIGHT);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var bounds = Bounds;
        
        // Background
        using var bgPaint = new SKPaint
        {
            Color = IsActive ? new SKColor(255, 255, 255) : 
                    _isHovered ? new SKColor(245, 245, 245) : 
                    new SKColor(235, 235, 235),
            IsAntialias = true
        };
        
        var rect = new SKRoundRect(bounds, 6, 6);
        // Only round top corners for tabs
        rect.SetRectRadii(bounds, new[] 
        { 
            new SKPoint(6, 6), new SKPoint(6, 6), 
            new SKPoint(0, 0), new SKPoint(0, 0) 
        });
        canvas.DrawRoundRect(rect, bgPaint);
        
        // Favicon
        float faviconX = bounds.Left + PADDING;
        float faviconY = bounds.MidY - FAVICON_SIZE / 2;
        
        if (_tab.Favicon != null)
        {
            var faviconRect = new SKRect(faviconX, faviconY, faviconX + FAVICON_SIZE, faviconY + FAVICON_SIZE);
            canvas.DrawBitmap(_tab.Favicon, faviconRect);
        }
        else
        {
            // Default icon placeholder
            using var iconPaint = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
            canvas.DrawCircle(faviconX + FAVICON_SIZE / 2, faviconY + FAVICON_SIZE / 2, 6, iconPaint);
        }
        
        // Title
        float textX = faviconX + FAVICON_SIZE + PADDING;
        float closeX = bounds.Right - CLOSE_BUTTON_SIZE - PADDING;
        float maxTextWidth = closeX - textX - PADDING;
        
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            TextSize = 12,
            TextAlign = SKTextAlign.Left
        };
        
        string displayTitle = _tab.Title ?? "New Tab";
        float textWidth = textPaint.MeasureText(displayTitle);
        if (textWidth > maxTextWidth)
        {
            // Truncate with ellipsis
            while (textWidth > maxTextWidth && displayTitle.Length > 3)
            {
                displayTitle = displayTitle.Substring(0, displayTitle.Length - 4) + "...";
                textWidth = textPaint.MeasureText(displayTitle);
            }
        }
        
        canvas.DrawText(displayTitle, textX, bounds.MidY + 4, textPaint);
        
        // Close button
        using var closePaint = new SKPaint
        {
            Color = _isCloseHovered ? SKColors.Red : SKColors.Gray,
            IsAntialias = true,
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke
        };
        
        float closeY = bounds.MidY - CLOSE_BUTTON_SIZE / 2;
        float margin = 4;
        canvas.DrawLine(closeX + margin, closeY + margin, 
                       closeX + CLOSE_BUTTON_SIZE - margin, closeY + CLOSE_BUTTON_SIZE - margin, closePaint);
        canvas.DrawLine(closeX + margin, closeY + CLOSE_BUTTON_SIZE - margin, 
                       closeX + CLOSE_BUTTON_SIZE - margin, closeY + margin, closePaint);
        
        // Loading indicator
        if (_tab.IsLoading)
        {
            using var loadPaint = new SKPaint { Color = SKColors.Blue, IsAntialias = true };
            canvas.DrawCircle(faviconX + FAVICON_SIZE / 2, faviconY + FAVICON_SIZE / 2, 4, loadPaint);
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        bool wasHovered = _isHovered;
        bool wasCloseHovered = _isCloseHovered;
        
        _isHovered = Bounds.Contains(x, y);
        
        // Check close button
        if (_isHovered)
        {
            float closeX = Bounds.Right - CLOSE_BUTTON_SIZE - PADDING;
            float closeY = Bounds.MidY - CLOSE_BUTTON_SIZE / 2;
            var closeRect = new SKRect(closeX, closeY, closeX + CLOSE_BUTTON_SIZE, closeY + CLOSE_BUTTON_SIZE);
            _isCloseHovered = closeRect.Contains(x, y);
        }
        else
        {
            _isCloseHovered = false;
        }
        
        if (wasHovered != _isHovered || wasCloseHovered != _isCloseHovered)
        {
            Invalidate();
        }
    }
    
    public override void OnMouseDown(float x, float y, Silk.NET.Input.MouseButton button)
    {
        if (!Bounds.Contains(x, y)) return;
        
        // Check close button first
        float closeX = Bounds.Right - CLOSE_BUTTON_SIZE - PADDING;
        float closeY = Bounds.MidY - CLOSE_BUTTON_SIZE / 2;
        var closeRect = new SKRect(closeX, closeY, closeX + CLOSE_BUTTON_SIZE, closeY + CLOSE_BUTTON_SIZE);
        
        if (closeRect.Contains(x, y))
        {
            CloseClicked?.Invoke(this);
        }
        else
        {
            Clicked?.Invoke(this);
        }
    }
    
    /// <summary>
    /// Get the preferred width for this tab.
    /// </summary>
    public float PreferredWidth => TAB_WIDTH;
    
    /// <summary>
    /// Get the preferred height for this tab.
    /// </summary>
    public float PreferredHeight => TAB_HEIGHT;
}
