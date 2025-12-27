using SkiaSharp;
using FenBrowser.Core.Dom;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Main controller for DevTools.
/// Manages tabs, layout, and panel switching.
/// </summary>
public class DevToolsController
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
    
    // State
    public bool IsVisible { get; private set; }
    public float Height { get; set; } = 300f;
    public float MinHeight { get; } = 200f;
    public float MaxHeight { get; } = 600f;
    
    // Events
    public event Action? Invalidated;
    public event Action? CloseRequested;
    
    // Selected element (for Elements panel)
    private Element? _selectedElement;
    
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
    }
    
    /// <summary>
    /// Hide DevTools.
    /// </summary>
    public void Hide()
    {
        IsVisible = false;
        _activePanel?.OnDeactivate();
        Invalidated?.Invoke();
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
        float closeX = _tabBarBounds.Right - 30;
        float closeY = _tabBarBounds.MidY;
        using var closePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeLarge);
        closePaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText("×", closeX, closeY + 5, closePaint);
        
        // Draw bottom border of tab bar
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(_tabBarBounds.Left, _tabBarBounds.Bottom, _tabBarBounds.Right, _tabBarBounds.Bottom, borderPaint);
    }
    
    public void OnMouseMove(float x, float y)
    {
        if (!IsVisible) return;
        
        // Check tab hover
        if (_tabBarBounds.Contains(x, y))
        {
            int newHovered = GetTabIndexAt(x);
            if (newHovered != _hoveredTabIndex)
            {
                _hoveredTabIndex = newHovered;
                Invalidated?.Invoke();
            }
        }
        else
        {
            if (_hoveredTabIndex != -1)
            {
                _hoveredTabIndex = -1;
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
}
