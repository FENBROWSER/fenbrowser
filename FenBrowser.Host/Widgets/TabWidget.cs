using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Theme;

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
        Role = WidgetRole.TabItem;
        Name = tab.Title;
        // Initial bounds will be set by parent via Arrange
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        return new SKSize(PreferredWidth, PreferredHeight);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        // Leaf widget
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var theme = ThemeManager.Current;
        var bounds = Bounds;
        
        // Background
        SKColor bgColor = IsActive ? theme.Background : 
                         _isHovered ? theme.SurfaceHover : 
                         theme.Surface;
                         
        using var bgPaint = new SKPaint
        {
            Color = bgColor,
            IsAntialias = true
        };
        
        // Only round top corners for tabs
        float radius = 8;
        var rect = new SKRoundRect();
        rect.SetRectRadii(bounds, new[] 
        { 
            new SKPoint(radius, radius), new SKPoint(radius, radius), 
            new SKPoint(0, 0), new SKPoint(0, 0) 
        });
        canvas.DrawRoundRect(rect, bgPaint);
        
        // Active Tab Accent (Glow or bottom bar)
        if (IsActive)
        {
            using var activePaint = new SKPaint
            {
                Color = theme.Accent,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            // Small indicator at the top
            canvas.DrawRect(bounds.Left + radius, bounds.Top, bounds.Width - radius * 2, 2, activePaint);
        }
        
        // Subtle Border for inactive tabs
        if (!IsActive)
        {
            using var borderPaint = new SKPaint
            {
                Color = theme.Border,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1
            };
            canvas.DrawRoundRect(rect, borderPaint);
        }
        
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
            Color = IsActive ? theme.Text : theme.TextMuted,
            IsAntialias = true,
            TextSize = 12,
            TextAlign = SKTextAlign.Left,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", IsActive ? SKFontStyle.Bold : SKFontStyle.Normal)
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
            Color = _isCloseHovered ? SKColors.Red : theme.TextMuted.WithAlpha(120),
            IsAntialias = true,
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };
        
        float closeY = bounds.MidY - CLOSE_BUTTON_SIZE / 2;
        float margin = 5;
        canvas.DrawLine(closeX + margin, closeY + margin, 
                       closeX + CLOSE_BUTTON_SIZE - margin, closeY + CLOSE_BUTTON_SIZE - margin, closePaint);
        canvas.DrawLine(closeX + margin, closeY + CLOSE_BUTTON_SIZE - margin, 
                       closeX + CLOSE_BUTTON_SIZE - margin, closeY + margin, closePaint);
        
        // Loading indicator
        if (_tab.IsLoading)
        {
            using var loadPaint = new SKPaint 
            { 
                Color = theme.Accent, 
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2
            };
            // Half-circle for "spinner" feel
            canvas.DrawArc(new SKRect(faviconX, faviconY, faviconX + FAVICON_SIZE, faviconY + FAVICON_SIZE), 
                           (float)(DateTime.Now.Millisecond / 1000.0 * 360), 270, false, loadPaint);
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

    public override bool CanFocus => true;
}
