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
    private Widget _contentWidget;
    private Widget _overlay;
    private Widget _separator;

    public RootWidget(TabBarWidget tabBar, ToolbarWidget toolbar, StatusBarWidget statusBar)
    {
        _tabBar = tabBar;
        _toolbar = toolbar;
        _statusBar = statusBar;
        _separator = new SeparatorWidget();
        
        AddChild(_tabBar, Dock.Top);
        AddChild(_toolbar, Dock.Top);
        AddChild(_separator, Dock.Top); // Add separator after toolbar
        AddChild(_statusBar, Dock.Bottom);
        
        LastChildFill = true;
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
