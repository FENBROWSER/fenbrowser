using SkiaSharp;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.ProcessIsolation;
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
    private bool _hasArrangedBounds;

    // Viewport-scrollbar drag state.
    private bool _scrollbarDragging;
    private float _scrollbarDragStartMouseY;
    private float _scrollbarDragStartScrollY;
    private const float ScrollbarTrackWidth = 12f;

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
            _subscribedTab.Browser.NeedsRepaint -= OnBrowserNeedsRepaint;
            _subscribedTab.Browser.ClipboardWriteRequested -= OnClipboardWriteRequested;
        }

        _subscribedTab = tab;

        if (_subscribedTab != null)
        {
            _subscribedTab.Browser.NeedsRepaint += OnBrowserNeedsRepaint;
            _subscribedTab.Browser.ClipboardWriteRequested += OnClipboardWriteRequested;
            
            // Cold launch can activate a tab before this widget has been arranged.
            // Treat OnArrange as the authoritative source for the first usable viewport.
            if (_hasArrangedBounds && Bounds.Width > 1 && Bounds.Height > 1)
            {
                _subscribedTab.Browser.UpdateViewport(new SKSize(Bounds.Width, Bounds.Height));
                _subscribedTab.NotifyViewportReady();
            }

            // Critical for tab-switch responsiveness: force the newly active tab's
            // engine loop to produce/commit a frame immediately instead of waiting
            // for the next input-driven invalidation (e.g., mouse move).
            _subscribedTab.Browser.RequestRepaint();
        }
        Invalidate();
    }

    private void OnBrowserNeedsRepaint()
    {
        // Wake Host compositor directly from engine frame commits so presentation
        // does not depend solely on UI-thread invalidation timing.
        ChromeManager.Instance.RequestCompositorFrame();

        // Force dirty propagation for the full content surface to avoid
        // stale compositor snapshots when only non-content widgets are dirty.
        if (Bounds.Width > 1 && Bounds.Height > 1)
        {
            Invalidate(Bounds);
            return;
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
        // Hot path: avoid per-frame logging/file writes to keep pointer/hover interactions responsive.
        var hadArrangedBounds = _hasArrangedBounds;
        _hasArrangedBounds = finalRect.Width > 1 && finalRect.Height > 1;

        // Bounds set by parent
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null && _hasArrangedBounds)
        {
            activeTab.Browser.UpdateViewport(new SKSize(finalRect.Width, finalRect.Height));
            activeTab.NotifyViewportReady();

            // Startup guarantee: tab activation can happen before we have a usable viewport.
            // Kick a repaint once bounds become valid so first visible web frame is not missed.
            if (!hadArrangedBounds)
            {
                activeTab.Browser.RequestRepaint();
                Invalidate();
            }
        }
        
        // Arrange settings page (always full fill)
        // Must call Measure first since _settingsPage is not a regular child
        _settingsPage.Measure(new SKSize(finalRect.Width, finalRect.Height));
        _settingsPage.Arrange(finalRect);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        using var backgroundPaint = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(Bounds, backgroundPaint);

        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            // Check for internal protocols
            if (activeTab.DisplayUrl.StartsWith("fen://settings", StringComparison.OrdinalIgnoreCase))
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

            ProcessIsolationRuntime.Current?.OnFrameRequested(activeTab, Bounds.Width, Bounds.Height, activeTab.Browser.EffectiveScrollY);
            
            // Route through BrowserTab.Render so crash-state rendering is honored.
            // Direct Browser.Render bypasses BrowserTab crash UI and can present a
            // silent black surface when renderer startup fails.
            activeTab.Render(canvas, localViewport);

            // Viewport scrollbar overlay (vertical only for now).
            PaintViewportScrollbar(canvas, localViewport, activeTab);

            canvas.Restore();
        }
    }

    private static void PaintViewportScrollbar(SKCanvas canvas, SKRect viewport, BrowserTab tab)
    {
        var browser = tab?.Browser;
        if (browser == null) return;

        float contentHeight = browser.ContentHeight;
        float viewportHeight = viewport.Height;
        if (contentHeight <= viewportHeight + 0.5f || viewportHeight <= 0f)
        {
            return;
        }

        const float ScrollbarWidth = 12f;
        const float ScrollbarPadding = 2f;
        const float MinThumb = 30f;
        const float CornerRadius = 4f;

        float trackLeft = viewport.Right - ScrollbarWidth;
        var trackRect = new SKRect(trackLeft, viewport.Top, viewport.Right, viewport.Bottom);

        float scrollY = Math.Max(0f, browser.ScrollY);
        float maxScroll = Math.Max(0f, contentHeight - viewportHeight);
        float scrollFraction = maxScroll > 0f ? Math.Min(1f, scrollY / maxScroll) : 0f;
        float thumbHeight = Math.Max(MinThumb, viewportHeight * (viewportHeight / contentHeight));
        thumbHeight = Math.Min(thumbHeight, viewportHeight);
        float thumbTop = viewport.Top + (viewportHeight - thumbHeight) * scrollFraction;
        var thumbRect = new SKRect(
            trackLeft + ScrollbarPadding,
            thumbTop + ScrollbarPadding,
            viewport.Right - ScrollbarPadding,
            thumbTop + thumbHeight - ScrollbarPadding);

        using (var trackPaint = new SKPaint { Color = new SKColor(240, 240, 240, 220), IsAntialias = true })
        {
            canvas.DrawRoundRect(trackRect, CornerRadius, CornerRadius, trackPaint);
        }
        using (var thumbPaint = new SKPaint { Color = new SKColor(170, 170, 170, 235), IsAntialias = true })
        {
            canvas.DrawRoundRect(thumbRect, CornerRadius, CornerRadius, thumbPaint);
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
            if (button == Silk.NET.Input.MouseButton.Left && TryHitScrollbarThumb(activeTab, x, y))
            {
                _scrollbarDragging = true;
                _scrollbarDragStartMouseY = y;
                _scrollbarDragStartScrollY = activeTab.Browser.EffectiveScrollY;
                FenBrowser.Host.Input.InputManager.Instance.SetCapture(this);
                return;
            }

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
        if (button == Silk.NET.Input.MouseButton.Left && _scrollbarDragging)
        {
            _scrollbarDragging = false;
            FenBrowser.Host.Input.InputManager.Instance.ReleaseCapture();
            return;
        }

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

    private static void OnClipboardWriteRequested(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            ClipboardHelper.SetText(text);
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
                     _ = activeTab.Browser.HandleClipboardCommand("Copy");
                     return;
                }
                else if (key == Silk.NET.Input.Key.X)
                {
                     _ = activeTab.Browser.HandleClipboardCommand("Cut");
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
        if (_scrollbarDragging)
        {
            var activeTab = TabManager.Instance.ActiveTab;
            var browser = activeTab?.Browser;
            if (browser == null) { _scrollbarDragging = false; return; }

            float viewportHeight = Bounds.Height;
            float contentHeight = browser.ContentHeight;
            float maxScroll = Math.Max(0f, contentHeight - viewportHeight);
            if (maxScroll <= 0f) return;

            float thumbHeight = Math.Max(30f, viewportHeight * (viewportHeight / contentHeight));
            float trackTravel = Math.Max(1f, viewportHeight - thumbHeight);
            float scrollPerPixel = maxScroll / trackTravel;

            float mouseDelta = y - _scrollbarDragStartMouseY;
            float targetScroll = _scrollbarDragStartScrollY + mouseDelta * scrollPerPixel;
            browser.ScrollToY(targetScroll);
            return;
        }
        // Intentionally no direct browser call here.
        // ChromeManager is the single authoritative dispatcher for web mouse-move,
        // including cursor/status updates based on hit-test results.
    }

    private bool TryHitScrollbarThumb(BrowserTab tab, float x, float y)
    {
        var browser = tab?.Browser;
        if (browser == null) return false;

        float contentHeight = browser.ContentHeight;
        float viewportHeight = Bounds.Height;
        if (contentHeight <= viewportHeight + 0.5f || viewportHeight <= 0f) return false;

        float trackLeft = Bounds.Right - ScrollbarTrackWidth;
        if (x < trackLeft || x > Bounds.Right) return false;
        if (y < Bounds.Top || y > Bounds.Bottom) return false;

        // Whole track is grabbable; click outside the thumb jumps the thumb to
        // the click position before drag continues (matches platform behaviour).
        float scrollY = Math.Max(0f, browser.EffectiveScrollY);
        float maxScroll = Math.Max(0f, contentHeight - viewportHeight);
        float scrollFraction = maxScroll > 0f ? Math.Min(1f, scrollY / maxScroll) : 0f;
        float thumbHeight = Math.Max(30f, viewportHeight * (viewportHeight / contentHeight));
        thumbHeight = Math.Min(thumbHeight, viewportHeight);
        float thumbTop = Bounds.Top + (viewportHeight - thumbHeight) * scrollFraction;
        float thumbBottom = thumbTop + thumbHeight;

        if (y < thumbTop || y > thumbBottom)
        {
            // Clicked the track outside the thumb: jump-scroll so the thumb centres on the click.
            float trackTravel = Math.Max(1f, viewportHeight - thumbHeight);
            float scrollPerPixel = maxScroll / trackTravel;
            float jumpTo = (y - Bounds.Top - thumbHeight / 2f) * scrollPerPixel;
            browser.ScrollToY(jumpTo);
        }
        return true;
    }
    
    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            activeTab.Browser.HandleMouseWheel(x, y, deltaX, deltaY, Bounds.Left, Bounds.Top);
            Invalidate(); // Trigger repaint for scroll
        }
    }
}

