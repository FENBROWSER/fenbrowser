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
    private bool _leftPointerDownInWebContent;

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
        try
        {
            string logDir = System.IO.Path.Combine(Environment.CurrentDirectory, "logs");
            System.IO.Directory.CreateDirectory(logDir);
            string logPath = System.IO.Path.Combine(logDir, "click_debug.log");
            System.IO.File.AppendAllText(logPath, $"[WebContentWidget.OnArrange] finalRect={finalRect}, this.Bounds={Bounds}\n");
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[WebContentWidget] click_debug log write failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
        }
        
        // Bounds set by parent
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            activeTab.Browser.UpdateViewport(new SKSize(finalRect.Width, finalRect.Height));
        }
        
        // Arrange settings page (always full fill)
        // Must call Measure first since _settingsPage is not a regular child
        _settingsPage.Measure(new SKSize(finalRect.Width, finalRect.Height));
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
    
    public override Widget HitTestDeep(float x, float y)
    {
        if (!HitTest(x, y)) return null;

        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null && activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
        {
            var hit = _settingsPage.HitTestDeep(x, y);
            if (hit != null) return hit;
        }

        return this;
    }

    public override bool CanFocus => true;

    public override void OnMouseDown(float x, float y, Silk.NET.Input.MouseButton button)
    {
        if (!Bounds.Contains(x, y)) return;
        
        RequestFocus();
        
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            if (activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
            {
                // Route clicks to settings page
                _settingsPage.OnMouseDown(x, y, button);
            }
            else if (button == Silk.NET.Input.MouseButton.Right)
            {
                activeTab.Browser.HandleRightClick(x, y, Bounds.Left, Bounds.Top);
            }
            else
            {
               _leftPointerDownInWebContent = true;
               activeTab.Browser.HandleMouseDown(x, y, 0, Bounds.Left, Bounds.Top);
            }
        }
    }

    public override void OnMouseUp(float x, float y, Silk.NET.Input.MouseButton button)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab == null || activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
            return;

        if (button == Silk.NET.Input.MouseButton.Left)
        {
            bool emitClick = _leftPointerDownInWebContent && Bounds.Contains(x, y);
            _leftPointerDownInWebContent = false;
            activeTab.Browser.HandleMouseUp(x, y, 0, emitClick, Bounds.Left, Bounds.Top);
        }
    }

    public override void OnKeyDown(Silk.NET.Input.Key key, bool ctrl, bool shift, bool alt)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null && !activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
        {
            // Clipboard Shortcuts
            if (ctrl)
            {
                if (key == Silk.NET.Input.Key.A)
                {
                    _ = activeTab.Browser.HandleClipboardCommand("SelectAll");
                    return;
                }
                else if (key == Silk.NET.Input.Key.C)
                {
                     // Get selected text from Browser logic
                     // Note: HandleClipboardCommand("Copy") is async/void, we need synchronous text or a callback.
                     // But wait, the BrowserApi modification I prepared assumes the Host fetches text.
                     // The BrowserApi.GetSelectedText() method I added is synchronous.
                     string text = activeTab.Browser.GetSelectedText();
                     if (!string.IsNullOrEmpty(text))
                     {
                         ClipboardHelper.SetText(text);
                     }
                     return;
                }
                else if (key == Silk.NET.Input.Key.X)
                {
                     // Cut = Copy + Delete
                     string text = activeTab.Browser.GetSelectedText();
                      if (!string.IsNullOrEmpty(text))
                     {
                         ClipboardHelper.SetText(text);
                         activeTab.Browser.DeleteSelection();
                     }
                     return;
                }
                else if (key == Silk.NET.Input.Key.V)
                {
                    string text = ClipboardHelper.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _ = activeTab.Browser.HandleClipboardCommand("Paste", text);
                    }
                    return;
                }
            }

            if (key == Silk.NET.Input.Key.Backspace)
            {
                _ = activeTab.Browser.HandleKeyPress("Backspace");
            }
            else if (key == Silk.NET.Input.Key.Enter)
            {
                _ = activeTab.Browser.HandleKeyPress("Enter");
            }
            else if (key == Silk.NET.Input.Key.Left)
            {
                _ = activeTab.Browser.HandleKeyPress("ArrowLeft");
            }
            else if (key == Silk.NET.Input.Key.Right)
            {
                _ = activeTab.Browser.HandleKeyPress("ArrowRight");
            }
            else if (key == Silk.NET.Input.Key.Home)
            {
                _ = activeTab.Browser.HandleKeyPress("Home");
            }
            else if (key == Silk.NET.Input.Key.End)
            {
                _ = activeTab.Browser.HandleKeyPress("End");
            }
            else if (key == Silk.NET.Input.Key.Delete)
            {
                _ = activeTab.Browser.HandleKeyPress("Delete");
            }
        }
    }

    public override void OnTextInput(char c, bool ctrl)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null && !activeTab.Url.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
        {
            if (!char.IsControl(c))
            {
                _ = activeTab.Browser.HandleKeyPress(c.ToString());
            }
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        // Intentionally no direct browser call here.
        // ChromeManager is the single authoritative dispatcher for web mouse-move,
        // including cursor/status updates based on hit-test results.
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
