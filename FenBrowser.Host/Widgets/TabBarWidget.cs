using SkiaSharp;
using FenBrowser.Host.Tabs;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Container widget for tabs with horizontal layout.
/// Shows tabs, new tab button, and handles overflow.
/// </summary>
public class TabBarWidget : Widget
{
    private readonly List<TabWidget> _tabWidgets = new();
    private float _scrollOffset = 0;
    private const float NEW_TAB_BUTTON_WIDTH = 32;
    private const float TAB_HEIGHT = 32;
    
    /// <summary>
    /// Event when new tab button is clicked.
    /// </summary>
    public event Action NewTabRequested;
    
    /// <summary>
    /// Event when a tab is clicked.
    /// </summary>
    public event Action<BrowserTab> TabActivated;
    
    /// <summary>
    /// Event when a tab close is clicked.
    /// </summary>
    public event Action<BrowserTab> TabCloseRequested;
    
    public TabBarWidget()
    {
        // Subscribe to TabManager changes
        TabManager.Instance.TabAdded += OnTabAdded;
        TabManager.Instance.TabRemoved += OnTabRemoved;
        TabManager.Instance.ActiveTabChanged += OnActiveTabChanged;
    }
    
    private void OnTabAdded(BrowserTab tab)
    {
        var widget = new TabWidget(tab);
        widget.Clicked += w => TabActivated?.Invoke(w.Tab);
        widget.CloseClicked += w => TabCloseRequested?.Invoke(w.Tab);
        _tabWidgets.Add(widget);
        AddChild(widget);
        LayoutTabs();
        Invalidate();
    }
    
    private void OnTabRemoved(BrowserTab tab)
    {
        var widget = _tabWidgets.Find(w => w.Tab == tab);
        if (widget != null)
        {
            _tabWidgets.Remove(widget);
            Children.Remove(widget);
            LayoutTabs();
            Invalidate();
        }
    }
    
    private void OnActiveTabChanged(BrowserTab tab)
    {
        foreach (var widget in _tabWidgets)
        {
            widget.IsActive = widget.Tab == tab;
        }
        Invalidate();
    }
    
    public override void Layout(SKRect available)
    {
        Bounds = new SKRect(available.Left, available.Top, available.Right, available.Top + TAB_HEIGHT);
        LayoutTabs();
    }
    
    private void LayoutTabs()
    {
        float x = Bounds.Left + 4 - _scrollOffset;
        float y = Bounds.Top;
        
        foreach (var widget in _tabWidgets)
        {
            widget.Bounds = new SKRect(x, y, x + widget.PreferredWidth, y + widget.PreferredHeight);
            x += widget.PreferredWidth + 2;
        }
    }
    
    public override void Paint(SKCanvas canvas)
    {
        // Background
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(222, 222, 222),
            IsAntialias = true
        };
        canvas.DrawRect(Bounds, bgPaint);
        
        // Clip to bounds for tab overflow
        canvas.Save();
        canvas.ClipRect(new SKRect(Bounds.Left, Bounds.Top, Bounds.Right - NEW_TAB_BUTTON_WIDTH, Bounds.Bottom));
        
        // Paint tabs
        foreach (var widget in _tabWidgets)
        {
            widget.Paint(canvas);
        }
        
        canvas.Restore();
        
        // New tab button
        float btnX = Bounds.Right - NEW_TAB_BUTTON_WIDTH;
        float btnY = Bounds.Top;
        
        using var btnPaint = new SKPaint
        {
            Color = new SKColor(235, 235, 235),
            IsAntialias = true
        };
        var btnRect = new SKRect(btnX + 2, btnY + 2, btnX + NEW_TAB_BUTTON_WIDTH - 2, btnY + TAB_HEIGHT - 2);
        canvas.DrawRoundRect(btnRect, 4, 4, btnPaint);
        
        // Plus sign
        using var plusPaint = new SKPaint
        {
            Color = SKColors.Gray,
            IsAntialias = true,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke
        };
        float centerX = btnRect.MidX;
        float centerY = btnRect.MidY;
        float size = 8;
        canvas.DrawLine(centerX - size, centerY, centerX + size, centerY, plusPaint);
        canvas.DrawLine(centerX, centerY - size, centerX, centerY + size, plusPaint);
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        if (!Bounds.Contains(x, y)) return;
        
        // Check new tab button
        float btnX = Bounds.Right - NEW_TAB_BUTTON_WIDTH;
        if (x >= btnX)
        {
            NewTabRequested?.Invoke();
            return;
        }
        
        // Check tabs
        foreach (var widget in _tabWidgets)
        {
            if (widget.Bounds.Contains(x, y))
            {
                widget.OnMouseDown(x, y, button);
                return;
            }
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        foreach (var widget in _tabWidgets)
        {
            widget.OnMouseMove(x, y);
        }
    }
    
    /// <summary>
    /// Handle scroll for tab overflow.
    /// </summary>
    public void HandleScroll(float delta)
    {
        float totalWidth = _tabWidgets.Sum(w => w.PreferredWidth + 2);
        float visibleWidth = Bounds.Width - NEW_TAB_BUTTON_WIDTH - 8;
        
        if (totalWidth > visibleWidth)
        {
            _scrollOffset -= delta * 30;
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, totalWidth - visibleWidth));
            LayoutTabs();
            Invalidate();
        }
    }
}
