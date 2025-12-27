using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Tabs;
using FenBrowser.Core.Logging;
using FenBrowser.Core;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Widget that renders the currently active web tab.
/// Bridge between UI tree and Engine.
/// </summary>
public class WebContentWidget : Widget
{
    private SettingsPageWidget _settingsPage;
    private BrowserTab _subscribedTab;

    public WebContentWidget()
    {
        _settingsPage = new SettingsPageWidget();
        // Do NOT AddChild. We manually manage its lifecycle to prevent double-rendering/overlay issues.
        // We set Parent manually so Invalidate() bubbling works.
        _settingsPage.Parent = this; 
        
        // Active tab rendering is dynamic based on TabManager
        TabManager.Instance.ActiveTabChanged += OnActiveTabChanged;
        
        // Initialize with current if any
        if (TabManager.Instance.ActiveTab != null)
        {
            OnActiveTabChanged(TabManager.Instance.ActiveTab);
        }
    }

    private void OnActiveTabChanged(BrowserTab tab)
    {
        if (_subscribedTab != null)
        {
            _subscribedTab.Browser.NeedsRepaint -= Invalidate;
        }

        _subscribedTab = tab;

        if (_subscribedTab != null)
        {
            _subscribedTab.Browser.NeedsRepaint += Invalidate;
            
            // Ensure the new tab knows the current viewport size
            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                _subscribedTab.Browser.UpdateViewport(new SKSize(Bounds.Width, Bounds.Height));
            }
        }
        Invalidate();
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        // Fills whatever space the parent gives it
        return availableSpace;
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        FenLogger.Info($"[WebContentWidget] OnArrange: {finalRect}", FenBrowser.Core.Logging.LogCategory.General);
        // Bounds set by parent
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            activeTab.Browser.UpdateViewport(new SKSize(finalRect.Width, finalRect.Height));
        }
        
        // Arrange settings page (always full fill)
        _settingsPage.Arrange(finalRect);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            // Check for internal protocols
            if (activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
            {
                canvas.Save();
                // Ensure we clip to our content area so settings doesn't bleed out
                canvas.ClipRect(Bounds);
                _settingsPage.Paint(canvas);
                canvas.Restore();
                return;
            }
            
            var localViewport = new SKRect(0, 0, Bounds.Width, Bounds.Height);
            
            canvas.Save();
            canvas.Translate(Bounds.Left, Bounds.Top);
            canvas.ClipRect(localViewport);
            
            // Draw the display list (buffered frame)
            activeTab.Browser.Render(canvas, localViewport);
            
            canvas.Restore();
        }
    }
    
    public override void OnMouseDown(float x, float y, Silk.NET.Input.MouseButton button)
    {
        if (!Bounds.Contains(x, y)) return;
        
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            // Translate absolute window coordinates to document-relative coordinates
            if (activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
            {
                _settingsPage.OnMouseDown(x, y, button);
            }
            else
            {
                if (button == Silk.NET.Input.MouseButton.Right)
                {
                    activeTab.Browser.HandleRightClick(x, y, Bounds.Left, Bounds.Top);
                }
                else
                {
                    activeTab.Browser.HandleClick(x, y, Bounds.Left, Bounds.Top);
                }
            }
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            // InputManager passes window-absolute coordinates
            // BrowserIntegration.HandleMouseMove handles translation to doc-space.
             if (activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
            {
                _settingsPage.OnMouseMove(x, y);
            }
            else
            {
                activeTab.Browser.HandleMouseMove(x, y, Bounds.Left, Bounds.Top);
            }
        }
    }
    
    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            activeTab.Browser.Scroll(deltaY);
            Invalidate(); // Trigger repaint for scroll
        }
    }
}
