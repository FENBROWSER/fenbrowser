using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Theme;

namespace FenBrowser.Host;

/// <summary>
/// Root container for the entire browser UI.
/// Manages shell layout (TabBar, Toolbar, Content, StatusBar).
/// </summary>
public class RootWidget : DockPanel
{
    private readonly TabBarWidget _tabBar;
    private readonly ToolbarWidget _toolbar;
    private readonly StatusBarWidget _statusBar;
    private readonly BookmarksBarWidget _bookmarksBar;
    private Widget _contentWidget;
    private Widget _overlay;
    private Widget _separator;
    private readonly DevToolsWidget _devToolsWidget;
    private readonly SiteInfoPopupWidget _siteInfoPopup;

    public RootWidget(TabBarWidget tabBar, ToolbarWidget toolbar, StatusBarWidget statusBar, DevToolsWidget devToolsWidget)
    {
        _tabBar = tabBar;
        _toolbar = toolbar;
        _statusBar = statusBar;
        _bookmarksBar = new BookmarksBarWidget();
        _devToolsWidget = devToolsWidget;
        _separator = new SeparatorWidget();
        
        _siteInfoPopup = new SiteInfoPopupWidget();
        _siteInfoPopup.CloseRequested += () => SetPopup(null);
        
        // Wire up security icon click
        _toolbar.AddressBar.SecurityIconClicked += ToggleSiteInfoPopup;
        
        AddChild(_tabBar, Dock.Top);
        AddChild(_toolbar, Dock.Top);
        AddChild(_bookmarksBar, Dock.Top);
        AddChild(_separator, Dock.Top); // Add separator after bookmarks bar
        AddChild(_statusBar, Dock.Bottom);
        AddChild(_devToolsWidget, Dock.Bottom);
        
        LastChildFill = true;
    }

    private void ToggleSiteInfoPopup()
    {
        if (_popup == _siteInfoPopup && _siteInfoPopup.IsVisible)
        {
            SetPopup(null);
            return;
        }

        // Calculate position
        // Toolbar is docked Top, so its Y is likely TabBarHeight.
        // AddressBar is inside Toolbar.
        
        float x = _toolbar.Bounds.Left + _toolbar.AddressBar.Bounds.Left;
        float y = _toolbar.Bounds.Top + _toolbar.AddressBar.Bounds.Bottom + 5; // Below address bar
        
        // Only valid if layout has happened
        if (x == 0 && y == 0) return; 

        _siteInfoPopup.Show(x, y, "google.com", true); // Mock data for now
        SetPopup(_siteInfoPopup);
    }
    
    public BookmarksBarWidget BookmarksBar => _bookmarksBar;
    
    /// <summary>
    /// Find a widget of a specific type in the tree.
    /// </summary>
    public T FindWidget<T>() where T : Widget
    {
        if (_devToolsWidget is T dt) return dt;
        if (_tabBar is T tb) return tb;
        if (_toolbar is T tl) return tl;
        if (_statusBar is T sb) return sb;
        if (_contentWidget is T cw) return cw;
        return null;
    }
    
    public void SetContent(Widget content)
    {
        // Add content as the last child to fill the remaining space
        if (_contentWidget != null) Children.Remove(_contentWidget);
        _contentWidget = content;
        if (_contentWidget != null) AddChild(_contentWidget, Dock.Fill);
    }

    public void SetOverlay(Widget overlay)
    {
        if (_overlay != null) RemoveChild(_overlay);
        _overlay = overlay;
        if (_overlay != null) 
        {
             // Add last so it paints on top (usually)
             AddChild(_overlay, Dock.Fill); // Use Fill, but we override layout in OnArrange anyway
        }
    }

    private Widget _popup;

    public void SetPopup(Widget popup)
    {
        if (_popup != null) RemoveChild(_popup);
        _popup = popup;
        if (_popup != null) 
        {
             // Add last so it paints on top. Do NOT set Dock property, handled manually.
             AddChild(_popup, Dock.None); 
        }
    }

    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Sync visibility with settings
        if (_bookmarksBar != null)
            _bookmarksBar.IsVisible = FenBrowser.Core.BrowserSettings.Instance.ShowFavoritesBar;
        return base.OnMeasure(availableSpace);
    }

    protected override void OnArrange(SKRect finalRect)
    {
        base.OnArrange(finalRect);
        
        // Manual layout for overlay to cover everything
        if (_overlay != null)
        {
            _overlay.Arrange(finalRect);
        }
        
        // Manual layout for popup (floating)
        if (_popup != null)
        {
            // Popup manages its own size/position via its Bounds
            // We just ensure it's arranged using its desired bounds
            // But usually Arrange takes the FINAL rect.
            // If the widget is floating, it should have set its Bounds in Measure or manually.
            _popup.Arrange(_popup.Bounds);
        }
    }

    /// <summary>
    /// A simple 1px visual separator.
    /// </summary>
    private class SeparatorWidget : Widget
    {
        public SeparatorWidget()
        {
            Name = "Separator";
        }

        protected override SKSize OnMeasure(SKSize availableSpace)
        {
            return new SKSize(availableSpace.Width, 1);
        }
        
        protected override void OnArrange(SKRect finalRect)
        {
            // Leaf
        }

        public override void Paint(SKCanvas canvas)
        {
            var theme = ThemeManager.Current;
            using var paint = new SKPaint
            {
                Color = theme.Border,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(Bounds, paint);
        }
    }
}
