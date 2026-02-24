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
    
    /// <summary>
    /// Event when tabs are reordered via drag-and-drop.
    /// </summary>
    public event Action<BrowserTab, int> TabReordered;
    
    private readonly ButtonWidget _newTabButton;
    private readonly ButtonWidget _minimizeButton;
    private readonly ButtonWidget _maximizeButton;
    private readonly ButtonWidget _closeButton;
    
    private const float WINDOW_CONTROL_WIDTH = 38;
    
    // --- Drag-and-Drop State (10/10) ---
    private int _draggingTabIndex = -1;
    private float _dragStartX;
    private float _dragOffsetX;
    private bool _isDragging;
    
    public TabBarWidget()
    {
        Role = WidgetRole.Tab;
        Name = "Tab Bar";
        
        _newTabButton = new ButtonWidget() { Text = "+", CornerRadius = 4, Name = "New Tab", Role = WidgetRole.Button };
        _newTabButton.Clicked += () => NewTabRequested?.Invoke();
        AddChild(_newTabButton);
        
        // Window Controls
        _minimizeButton = new ButtonWidget() { CornerRadius = 0, Name = "Minimize", Role = WidgetRole.Button };
        _minimizeButton.BackgroundColor = SKColors.Transparent;
        _minimizeButton.BorderColor = SKColors.Transparent;
        _minimizeButton.IconPath = CreateMinimizePath();
        _minimizeButton.IconPaintStyle = SKPaintStyle.Stroke;
        _minimizeButton.IconStrokeWidth = 0.7f;
        _minimizeButton.FontSize = 9;
        _minimizeButton.Clicked += () => MinimizeRequested?.Invoke();
        AddChild(_minimizeButton);
        
        _maximizeButton = new ButtonWidget() { CornerRadius = 0, Name = "Maximize", Role = WidgetRole.Button };
        _maximizeButton.BackgroundColor = SKColors.Transparent;
        _maximizeButton.BorderColor = SKColors.Transparent;
        _maximizeButton.IconPath = CreateMaximizePath();
        _maximizeButton.IconPaintStyle = SKPaintStyle.Stroke;
        _maximizeButton.IconStrokeWidth = 0.7f;
        _maximizeButton.FontSize = 9;
        _maximizeButton.Clicked += () => MaximizeRequested?.Invoke();
        AddChild(_maximizeButton);
        
        _closeButton = new ButtonWidget() { CornerRadius = 0, Name = "Close", Role = WidgetRole.Button };
        _closeButton.BackgroundColor = SKColors.Transparent;
        _closeButton.BorderColor = SKColors.Transparent;
        _closeButton.HoverBackgroundColor = new SKColor(232, 17, 35); // Edge Red
        _closeButton.HoverTextColor = SKColors.White;
        _closeButton.IconPath = CreateClosePath();
        _closeButton.IconPaintStyle = SKPaintStyle.Stroke;
        _closeButton.IconStrokeWidth = 0.7f;
        _closeButton.FontSize = 9;
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
            widget.Detach();
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
        float visibleWidth = Bounds.Width - NEW_TAB_BUTTON_WIDTH - 8 - (WINDOW_CONTROL_WIDTH * 3);
        
        if (totalWidth > visibleWidth)
        {
            _scrollOffset -= delta * 30;
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, totalWidth - visibleWidth));
            InvalidateLayout();
            Invalidate();
        }
    }

    private SKPath CreateMinimizePath()
    {
        var path = new SKPath();
        path.MoveTo(0, 5);
        path.LineTo(8, 5);
        return path;
    }

    private SKPath CreateMaximizePath()
    {
        var path = new SKPath();
        path.AddRect(new SKRect(0, 0, 8, 8));
        return path;
    }

    private SKPath CreateClosePath()
    {
        var path = new SKPath();
        path.MoveTo(0, 0);
        path.LineTo(8, 8);
        path.MoveTo(8, 0);
        path.LineTo(0, 8);
        return path;
    }
    
    // --- Drag-and-Drop Tab Reordering (10/10) ---
    
    /// <summary>
    /// Begin dragging a tab at the given mouse position.
    /// </summary>
    public void BeginTabDrag(float x, float y)
    {
        // Find which tab was clicked
        for (int i = 0; i < _tabWidgets.Count; i++)
        {
            if (_tabWidgets[i].Bounds.Contains(x, y))
            {
                _draggingTabIndex = i;
                _dragStartX = x;
                _dragOffsetX = x - _tabWidgets[i].Bounds.Left;
                _isDragging = true;
                return;
            }
        }
    }
    
    /// <summary>
    /// Update the tab position during drag.
    /// </summary>
    public void UpdateTabDrag(float x)
    {
        if (!_isDragging || _draggingTabIndex < 0) return;
        
        // Calculate new position
        float draggedTabX = x - _dragOffsetX;
        
        // Find target index based on position
        int targetIndex = 0;
        float checkX = Bounds.Left + 4 - _scrollOffset;
        
        for (int i = 0; i < _tabWidgets.Count; i++)
        {
            float tabWidth = _tabWidgets[i].PreferredWidth;
            float tabCenter = checkX + tabWidth / 2;
            
            if (draggedTabX > tabCenter)
            {
                targetIndex = i + 1;
            }
            
            checkX += tabWidth + 2;
        }
        
        // Clamp to valid range
        targetIndex = Math.Max(0, Math.Min(_tabWidgets.Count - 1, targetIndex));
        
        // Perform swap if needed
        if (targetIndex != _draggingTabIndex)
        {
            var tab = _tabWidgets[_draggingTabIndex];
            _tabWidgets.RemoveAt(_draggingTabIndex);
            _tabWidgets.Insert(targetIndex, tab);
            _draggingTabIndex = targetIndex;
            
            InvalidateLayout();
            Invalidate();
        }
    }
    
    /// <summary>
    /// End the tab drag operation.
    /// </summary>
    public void EndTabDrag()
    {
        if (_isDragging && _draggingTabIndex >= 0)
        {
            var tab = _tabWidgets[_draggingTabIndex].Tab;
            TabReordered?.Invoke(tab, _draggingTabIndex);
        }
        
        _isDragging = false;
        _draggingTabIndex = -1;
        InvalidateLayout();
        Invalidate();
    }
    
    /// <summary>
    /// Check if currently dragging a tab.
    /// </summary>
    public bool IsDraggingTab => _isDragging;
    
    /// <summary>
    /// Get the currently dragged tab index, or -1 if not dragging.
    /// </summary>
    public int DraggingTabIndex => _draggingTabIndex;
}
