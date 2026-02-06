using SkiaSharp;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Dock position for DevTools panel.
/// </summary>
public enum DockPosition
{
    Bottom,
    Right,
    Undocked
}

/// <summary>
/// Main controller for DevTools.
/// Manages tabs, layout, and panel switching.
/// 10/10 Spec: Keyboard shortcuts, docking, element picker, IDisposable.
/// </summary>
public class DevToolsController : IDisposable
{
    private readonly List<IDevToolsPanel> _panels = new();
    private IDevToolsPanel? _activePanel;
    private IDevToolsHost? _host;
    private int _activePanelIndex;
    
    // Layout
    private SKRect _bounds;
    private SKRect _tabBarBounds;
    private SKRect _contentBounds;
    private int _hoveredTabIndex = -1;
    private bool _isCloseHovered = false;
    
    // State
    public bool IsVisible { get; private set; }
    public bool IsDragging => _activePanel?.IsDragging ?? false;
    private float _height = 300f;
    public float Height 
    { 
        get => _height; 
        set 
        { 
            if (_height != value)
            {
                _height = value;
                LayoutChanged?.Invoke();
            }
        }
    }
    public float MinHeight { get; } = 200f;
    public float MaxHeight { get; } = 600f;
    
    // --- 10/10: Docking Support ---
    private DockPosition _dock = DockPosition.Bottom;
    public DockPosition Dock
    {
        get => _dock;
        set
        {
            if (_dock != value)
            {
                _dock = value;
                DockChanged?.Invoke(value);
                LayoutChanged?.Invoke();
            }
        }
    }
    
    // --- 10/10: Element Picker Mode ---
    public bool IsElementPickerActive { get; private set; }
    
    // Events
    public event Action? Invalidated;
    public event Action? CloseRequested;
    public event Action? LayoutChanged;
    public event Action<DockPosition>? DockChanged;
    public event Action<bool>? ElementPickerChanged;
    
    // Selected element (for Elements panel)
    private Element? _selectedElement;
    
    private bool _disposed;
    
    public DevToolsController()
    {
        // Panels will be added by RegisterPanel
    }

    
    /// <summary>
    /// Register a panel (tab) in DevTools.
    /// </summary>
    public void RegisterPanel(IDevToolsPanel panel)
    {
        _panels.Add(panel);
        panel.Invalidated += () => Invalidated?.Invoke();
        
        if (_host != null)
        {
            panel.SetHost(_host);
        }
        
        // Activate first panel by default
        if (_panels.Count == 1)
        {
            ActivatePanel(0);
        }
    }
    
    /// <summary>
    /// Attach to a browser host.
    /// </summary>
    public void Attach(IDevToolsHost host)
    {
        _host = host;
        foreach (var panel in _panels)
        {
            panel.SetHost(host);
        }
    }
    
    /// <summary>
    /// Show DevTools.
    /// </summary>
    public void Show()
    {
        IsVisible = true;
        _activePanel?.OnActivate();
        Invalidated?.Invoke();
        LayoutChanged?.Invoke();
    }
    
    /// <summary>
    /// Hide DevTools.
    /// </summary>
    public void Hide()
    {
        IsVisible = false;
        _host?.RequestCursorChange(CursorType.Default);
        _activePanel?.OnDeactivate();
        Invalidated?.Invoke();
        LayoutChanged?.Invoke();
    }
    
    /// <summary>
    /// Toggle visibility.
    /// </summary>
    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }
    
    /// <summary>
    /// Select and highlight an element.
    /// </summary>
    public void SelectElement(Element? element)
    {
        _selectedElement = element;
        _host?.HighlightElement(element);
        
        // Notify Elements panel if it exists
        // (Will be implemented in ElementsPanel)
        
        Invalidated?.Invoke();
    }
    
    /// <summary>
    /// Get the currently selected element.
    /// </summary>
    public Element? GetSelectedElement() => _selectedElement;
    
    /// <summary>
    /// Activate a panel by index.
    /// </summary>
    public void ActivatePanel(int index)
    {
        if (index < 0 || index >= _panels.Count) return;
        
        _activePanel?.OnDeactivate();
        _activePanelIndex = index;
        _activePanel = _panels[index];
        _activePanel.OnActivate();
        Invalidated?.Invoke();
    }
    
    /// <summary>
    /// Activate a panel by type.
    /// </summary>
    public void ActivatePanel<T>() where T : IDevToolsPanel
    {
        for (int i = 0; i < _panels.Count; i++)
        {
            if (_panels[i] is T)
            {
                ActivatePanel(i);
                return;
            }
        }
    }
    
    /// <summary>
    /// Paint DevTools to canvas.
    /// </summary>
    public void Paint(SKCanvas canvas, SKRect bounds)
    {
        if (!IsVisible) return;
        
        _bounds = bounds;
        _tabBarBounds = new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + DevToolsTheme.TabHeight);
        _contentBounds = new SKRect(bounds.Left, _tabBarBounds.Bottom, bounds.Right, bounds.Bottom);
        
        // Draw background
        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        canvas.DrawRect(bounds, bgPaint);
        
        // Draw top border
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(bounds.Left, bounds.Top, bounds.Right, bounds.Top, borderPaint);
        
        // Draw tab bar
        DrawTabBar(canvas);
        
        // Draw active panel content
        _activePanel?.Paint(canvas, _contentBounds);
    }
    
    private void DrawTabBar(SKCanvas canvas)
    {
        // Tab bar background
        using var tabBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        canvas.DrawRect(_tabBarBounds, tabBgPaint);
        
        float x = _tabBarBounds.Left + DevToolsTheme.PaddingNormal;
        float tabPadding = DevToolsTheme.PaddingLarge * 2;
        
        using var textPaint = DevToolsTheme.CreateUITextPaint(size: DevToolsTheme.FontSizeMedium);
        
        for (int i = 0; i < _panels.Count; i++)
        {
            var panel = _panels[i];
            float textWidth = textPaint.MeasureText(panel.Title);
            float tabWidth = textWidth + tabPadding;
            
            var tabRect = new SKRect(x, _tabBarBounds.Top, x + tabWidth, _tabBarBounds.Bottom);
            
            // Draw tab background
            bool isActive = i == _activePanelIndex;
            bool isHovered = i == _hoveredTabIndex;
            
            SKColor tabColor;
            if (isActive) tabColor = DevToolsTheme.TabActive;
            else if (isHovered) tabColor = DevToolsTheme.TabHover;
            else tabColor = DevToolsTheme.TabInactive;
            
            using var tabPaint = DevToolsTheme.CreateFillPaint(tabColor);
            canvas.DrawRect(tabRect, tabPaint);
            
            // Draw active indicator
            if (isActive)
            {
                using var activePaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.TabBorder);
                canvas.DrawRect(new SKRect(x, _tabBarBounds.Bottom - 2, x + tabWidth, _tabBarBounds.Bottom), activePaint);
            }
            
            // Draw tab text
            textPaint.Color = isActive ? DevToolsTheme.TextPrimary : DevToolsTheme.TextSecondary;
            float textY = _tabBarBounds.MidY + textPaint.TextSize / 3;
            canvas.DrawText(panel.Title, x + tabPadding / 2, textY, textPaint);
            
            x += tabWidth + 2;
        }
        
        // Draw close button
        float closeX = _tabBarBounds.Right - 20;
        float closeY = _tabBarBounds.MidY;
        
        // Draw hover highlight
        if (_isCloseHovered)
        {
            using var hoverPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.ConsoleError.WithAlpha(40));
            canvas.DrawCircle(closeX, closeY, 14, hoverPaint);
            
            // Draw "Close DevTools" text
            using var hintPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeSmall);
            string hint = "Close DevTools";
            float hintWidth = hintPaint.MeasureText(hint);
            canvas.DrawText(hint, closeX - 30 - hintWidth, closeY + 5, hintPaint);
        }

        using var closePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeLarge * 1.2f);
        closePaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText("×", closeX, closeY + 6, closePaint);
        
        // Draw bottom border of tab bar
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(_tabBarBounds.Left, _tabBarBounds.Bottom, _tabBarBounds.Right, _tabBarBounds.Bottom, borderPaint);
    }
    
    public void OnMouseMove(float x, float y)
    {
        if (!IsVisible) return;
        
        // Check tab bar
        if (_tabBarBounds.Contains(x, y))
        {
            // Reset panel cursor when moving over tab bar
            _host?.RequestCursorChange(CursorType.Default);

            // Check close button hover (right 40px)
            bool wasCloseHovered = _isCloseHovered;
            _isCloseHovered = x >= _tabBarBounds.Right - 40;
            
            int newHovered = _isCloseHovered ? -1 : GetTabIndexAt(x);
            if (newHovered != _hoveredTabIndex || wasCloseHovered != _isCloseHovered)
            {
                _hoveredTabIndex = newHovered;
                Invalidated?.Invoke();
            }
        }
        else
        {
            if (_hoveredTabIndex != -1 || _isCloseHovered)
            {
                _hoveredTabIndex = -1;
                _isCloseHovered = false;
                Invalidated?.Invoke();
            }
            
            _activePanel?.OnMouseMove(x, y);
        }
    }
    
    public bool OnMouseDown(float x, float y, bool isRightButton)
    {
        if (!IsVisible) return false;
        
        // Check close button
        if (y >= _tabBarBounds.Top && y <= _tabBarBounds.Bottom && x >= _tabBarBounds.Right - 40)
        {
            Hide();
            CloseRequested?.Invoke();
            return true;
        }
        
        // Check tab click
        if (_tabBarBounds.Contains(x, y))
        {
            int tabIndex = GetTabIndexAt(x);
            if (tabIndex >= 0)
            {
                ActivatePanel(tabIndex);
                return true;
            }
        }
        
        // Delegate to panel
        return _activePanel?.OnMouseDown(x, y, isRightButton) ?? false;
    }
    
    public void OnMouseUp(float x, float y)
    {
        _activePanel?.OnMouseUp(x, y);
    }
    
    public void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        _activePanel?.OnMouseWheel(x, y, deltaX, deltaY);
    }
    
    public bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt)
    {
        // Escape to close
        if (keyCode == 27) // Escape
        {
            Hide();
            return true;
        }
        
        return _activePanel?.OnKeyDown(keyCode, ctrl, shift, alt) ?? false;
    }
    
    public void OnTextInput(char c)
    {
        _activePanel?.OnTextInput(c);
    }
    
    private int GetTabIndexAt(float x)
    {
        float tabX = _tabBarBounds.Left + DevToolsTheme.PaddingNormal;
        float tabPadding = DevToolsTheme.PaddingLarge * 2;
        
        using var textPaint = DevToolsTheme.CreateUITextPaint(size: DevToolsTheme.FontSizeMedium);
        
        for (int i = 0; i < _panels.Count; i++)
        {
            float textWidth = textPaint.MeasureText(_panels[i].Title);
            float tabWidth = textWidth + tabPadding;
            
            if (x >= tabX && x <= tabX + tabWidth)
            {
                return i;
            }
            
            tabX += tabWidth + 2;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Check if a point is within DevTools bounds.
    /// </summary>
    public bool HitTest(float x, float y)
    {
        return IsVisible && _bounds.Contains(x, y);
    }
    
    // --- 10/10: Keyboard Shortcuts ---
    
    /// <summary>
    /// Handle keyboard shortcuts for DevTools.
    /// Returns true if the shortcut was handled.
    /// </summary>
    public bool HandleKeyboardShortcut(int keyCode, bool ctrl, bool shift, bool alt)
    {
        if (!IsVisible) return false;
        
        // Ctrl+1/2/3/... for panel switching
        if (ctrl && !shift && !alt && keyCode >= 49 && keyCode <= 57)
        {
            int index = keyCode - 49; // 1 = index 0, 2 = index 1, etc.
            if (index < _panels.Count)
            {
                ActivatePanel(index);
                return true;
            }
        }
        
        // Ctrl+Shift+C for element picker
        if (ctrl && shift && keyCode == 67)
        {
            ToggleElementPicker();
            return true;
        }
        
        // Ctrl+Shift+D for dock position toggle
        if (ctrl && shift && keyCode == 68)
        {
            CycleDockPosition();
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Toggle element picker mode.
    /// </summary>
    public void ToggleElementPicker()
    {
        IsElementPickerActive = !IsElementPickerActive;
        ElementPickerChanged?.Invoke(IsElementPickerActive);
        Invalidated?.Invoke();
    }
    
    /// <summary>
    /// Cycle through dock positions.
    /// </summary>
    public void CycleDockPosition()
    {
        Dock = Dock switch
        {
            DockPosition.Bottom => DockPosition.Right,
            DockPosition.Right => DockPosition.Undocked,
            DockPosition.Undocked => DockPosition.Bottom,
            _ => DockPosition.Bottom
        };
    }
    
    // --- 10/10: IDisposable Implementation ---
    
    /// <summary>
    /// Get panel by index.
    /// </summary>
    public IDevToolsPanel? GetPanel(int index)
    {
        return index >= 0 && index < _panels.Count ? _panels[index] : null;
    }
    
    /// <summary>
    /// Get panel by type.
    /// </summary>
    public T? GetPanel<T>() where T : class, IDevToolsPanel
    {
        foreach (var panel in _panels)
        {
            if (panel is T typed) return typed;
        }
        return null;
    }
    
    /// <summary>
    /// Number of registered panels.
    /// </summary>
    public int PanelCount => _panels.Count;
    
    /// <summary>
    /// Index of active panel.
    /// </summary>
    public int ActivePanelIndex => _activePanelIndex;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        foreach (var panel in _panels)
        {
            if (panel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _panels.Clear();
        _activePanel = null;
        _host = null;
        
        GC.SuppressFinalize(this);
    }
}

