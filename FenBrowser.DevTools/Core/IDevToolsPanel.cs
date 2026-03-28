using SkiaSharp;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Interface for DevTools panels.
/// Each tab (Elements, Console, Network, etc.) implements this.
/// </summary>
public interface IDevToolsPanel
{
    /// <summary>
    /// Display name for the tab.
    /// </summary>
    string Title { get; }
    
    /// <summary>
    /// Shortcut hint (e.g., "Ctrl+Shift+E").
    /// </summary>
    string? Shortcut { get; }
    
    /// <summary>
    /// Called when panel becomes active.
    /// </summary>
    void OnActivate();
    
    /// <summary>
    /// Called when panel becomes inactive.
    /// </summary>
    void OnDeactivate();
    
    /// <summary>
    /// Paint the panel content.
    /// </summary>
    void Paint(SKCanvas canvas, SKRect bounds);
    
    /// <summary>
    /// Handle mouse move.
    /// </summary>
    void OnMouseMove(float x, float y);
    
    /// <summary>
    /// Handle mouse down.
    /// </summary>
    bool OnMouseDown(float x, float y, bool isRightButton);
    
    /// <summary>
    /// Handle mouse up.
    /// </summary>
    void OnMouseUp(float x, float y);
    
    /// <summary>
    /// Handle mouse wheel.
    /// </summary>
    void OnMouseWheel(float x, float y, float deltaX, float deltaY);
    
    /// <summary>
    /// Whether the panel is currently dragging an element (e.g., splitter).
    /// </summary>
    bool IsDragging { get; }
    
    /// <summary>
    /// Handle key down.
    /// </summary>
    bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt);
    
    /// <summary>
    /// Handle text input.
    /// </summary>
    void OnTextInput(char c);
    
    /// <summary>
    /// Request repaint.
    /// </summary>
    event Action? Invalidated;
    
    /// <summary>
    /// Set the DevTools host for data access.
    /// </summary>
    void SetHost(IDevToolsHost host);
}

/// <summary>
/// Base class for DevTools panels with common functionality.
/// </summary>
public abstract class DevToolsPanelBase : IDevToolsPanel
{
    protected IDevToolsHost? Host { get; set; }
    protected SKRect Bounds { get; set; }
    protected float ScrollY { get; set; }
    protected float MaxScrollY { get; set; }
    public virtual bool IsDragging => false;
    
    public abstract string Title { get; }
    public virtual string? Shortcut => null;
    
    public event Action? Invalidated;
    
    protected void Invalidate() => Invalidated?.Invoke();
    
    public void SetHost(IDevToolsHost host)
    {
        if (ReferenceEquals(Host, host))
        {
            return;
        }

        OnHostChanging(Host);
        Host = host;
        OnHostChanged();
    }
    
    protected virtual void OnHostChanging(IDevToolsHost? previousHost) { }
    protected virtual void OnHostChanged() { }
    
    public virtual void OnActivate() { }
    public virtual void OnDeactivate() { }
    
    public virtual void Paint(SKCanvas canvas, SKRect bounds)
    {
        Bounds = bounds;
        
        // Clip to panel bounds
        canvas.Save();
        canvas.ClipRect(bounds);
        
        // Draw background
        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.Background);
        canvas.DrawRect(bounds, bgPaint);
        
        // Draw content with scroll offset
        canvas.Translate(0, -ScrollY);
        OnPaint(canvas, bounds);
        canvas.Restore();
        
        // Draw scrollbar if needed
        if (MaxScrollY > 0)
        {
            DrawScrollbar(canvas, bounds);
        }
    }
    
    protected abstract void OnPaint(SKCanvas canvas, SKRect bounds);
    
    protected virtual void DrawScrollbar(SKCanvas canvas, SKRect bounds)
    {
        float scrollbarWidth = 8f;
        float trackHeight = bounds.Height;
        float thumbHeight = Math.Max(20, trackHeight * (trackHeight / (trackHeight + MaxScrollY)));
        float thumbY = bounds.Top + (ScrollY / MaxScrollY) * (trackHeight - thumbHeight);
        
        var thumbRect = new SKRect(
            bounds.Right - scrollbarWidth - 2,
            thumbY,
            bounds.Right - 2,
            thumbY + thumbHeight
        );
        
        using var paint = DevToolsTheme.CreateFillPaint(DevToolsTheme.Scrollbar);
        canvas.DrawRoundRect(thumbRect, 4, 4, paint);
    }
    
    public virtual void OnMouseMove(float x, float y) { }
    public virtual bool OnMouseDown(float x, float y, bool isRightButton) => false;
    public virtual void OnMouseUp(float x, float y) { }
    
    public virtual void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        if (MaxScrollY > 0)
        {
            ScrollY = Math.Clamp(ScrollY - deltaY * 40, 0, MaxScrollY);
            Invalidate();
        }
    }
    
    public virtual bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt) => false;
    public virtual void OnTextInput(char c) { }
}
