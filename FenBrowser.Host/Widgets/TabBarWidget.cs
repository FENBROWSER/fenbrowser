using SkiaSharp;
using FenBrowser.Host.Tabs;
using Silk.NET.Input;
using FenBrowser.Host.Theme;

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
    
    /// <summary>
    /// Event when minimize is clicked.
    /// </summary>
    public event Action MinimizeRequested;

    /// <summary>
    /// Event when maximize is clicked.
    /// </summary>
    public event Action MaximizeRequested;

    /// <summary>
    /// Event when close is clicked.
    /// </summary>
    public event Action CloseRequested;
    
    private readonly ButtonWidget _newTabButton;
    private readonly ButtonWidget _minimizeButton;
    private readonly ButtonWidget _maximizeButton;
    private readonly ButtonWidget _closeButton;
    
    private const float WINDOW_CONTROL_WIDTH = 45;
    
    public TabBarWidget()
    {
        Role = WidgetRole.Tab;
        Name = "Tab Bar";
        
        _newTabButton = new ButtonWidget() { Text = "+", CornerRadius = 4, Name = "New Tab", Role = WidgetRole.Button };
        _newTabButton.Clicked += () => NewTabRequested?.Invoke();
        AddChild(_newTabButton);
        
        // Window Controls
        _minimizeButton = new ButtonWidget() { Text = "_", CornerRadius = 0, Name = "Minimize", Role = WidgetRole.Button };
        _minimizeButton.Clicked += () => MinimizeRequested?.Invoke();
        AddChild(_minimizeButton);
        
        _maximizeButton = new ButtonWidget() { Text = "□", CornerRadius = 0, Name = "Maximize", Role = WidgetRole.Button };
        _maximizeButton.Clicked += () => MaximizeRequested?.Invoke();
        AddChild(_maximizeButton);
        
        _closeButton = new ButtonWidget() { Text = "×", CornerRadius = 0, Name = "Close", Role = WidgetRole.Button, BackgroundColor = new SKColor(232, 17, 35) }; // Windows Red
        _closeButton.Clicked += () => CloseRequested?.Invoke();
        AddChild(_closeButton);
        
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
        InvalidateLayout();
        Invalidate();
    }
    
    private void OnTabRemoved(BrowserTab tab)
    {
        var widget = _tabWidgets.Find(w => w.Tab == tab);
        if (widget != null)
        {
            _tabWidgets.Remove(widget);
            Children.Remove(widget);
            InvalidateLayout();
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
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        foreach (var child in Children)
        {
            if (child == _newTabButton)
            {
                child.Measure(new SKSize(NEW_TAB_BUTTON_WIDTH - 4, TAB_HEIGHT - 4));
            }
            else if (child == _minimizeButton || child == _maximizeButton || child == _closeButton)
            {
                child.Measure(new SKSize(WINDOW_CONTROL_WIDTH, TAB_HEIGHT));
            }
            else
            {
                child.Measure(availableSpace);
            }
        }
        return new SKSize(availableSpace.Width, TAB_HEIGHT);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        float x = finalRect.Left + 4 - _scrollOffset;
        float y = finalRect.Top;
        
        foreach (var widget in _tabWidgets)
        {
            float width = widget.PreferredWidth;
            float height = widget.PreferredHeight;
            widget.Arrange(new SKRect(x, y, x + width, y + height));
            x += width + 2;
        }
        
        // Arrange new tab button after the last tab
        _newTabButton.Arrange(new SKRect(x, y + 2, x + NEW_TAB_BUTTON_WIDTH - 4, y + TAB_HEIGHT - 2));
        
        // Arrange window controls on the right
        float controlsWidth = WINDOW_CONTROL_WIDTH * 3;
        float cx = finalRect.Right - controlsWidth;
        
        _minimizeButton.Arrange(new SKRect(cx, y, cx + WINDOW_CONTROL_WIDTH, y + TAB_HEIGHT));
        cx += WINDOW_CONTROL_WIDTH;
        
        _maximizeButton.Arrange(new SKRect(cx, y, cx + WINDOW_CONTROL_WIDTH, y + TAB_HEIGHT));
        cx += WINDOW_CONTROL_WIDTH;
        
        _closeButton.Arrange(new SKRect(cx, y, cx + WINDOW_CONTROL_WIDTH, y + TAB_HEIGHT));
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
        
        // Clip to bounds for tab overflow
        canvas.Save();
        canvas.ClipRect(new SKRect(Bounds.Left, Bounds.Top, Bounds.Right - NEW_TAB_BUTTON_WIDTH, Bounds.Bottom));
        
        // Paint tabs
        foreach (var widget in _tabWidgets)
        {
            widget.PaintAll(canvas);
        }
        
        canvas.Restore();
        
        canvas.Restore();
        
        // Paint new tab button (it's a child)
        _newTabButton.PaintAll(canvas);
        
        // Paint window controls
        _minimizeButton.PaintAll(canvas);
        _maximizeButton.PaintAll(canvas);
        _closeButton.PaintAll(canvas);
    }
    
    public override void OnMouseDown(float x, float y, MouseButton button)
    {
        // Let base handle event routing to children (TabWidgets and NewTabButton)
    }
    
    public override void OnMouseMove(float x, float y)
    {
        // Let base handle child updates
    }
    
    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        HandleScroll(deltaY);
    }
    
    /// <summary>
    /// Handle scroll for tab overflow.
    /// [DEPRECATED] Use OnMouseWheel instead.
    /// </summary>
    public void HandleScroll(float delta)
    {
        float totalWidth = _tabWidgets.Sum(w => w.PreferredWidth + 2);
        float visibleWidth = Bounds.Width - NEW_TAB_BUTTON_WIDTH - 8;
        
        if (totalWidth > visibleWidth)
        {
            _scrollOffset -= delta * 30;
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, totalWidth - visibleWidth));
            InvalidateLayout();
            Invalidate();
        }
    }
}
