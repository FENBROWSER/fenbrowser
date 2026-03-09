using System.Threading;
using SkiaSharp;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Basic accessibility roles for widgets.
/// </summary>
public enum WidgetRole
{
    None,
    Window,
    Pane,
    Button,
    Edit,
    Link,
    Text,
    Image,
    Tab,
    TabItem,
    Toolbar,
    StatusBar,
    Document
}

/// <summary>
/// Base class for all UI widgets in FenBrowser.Host.
/// Provides common layout, painting, and input handling.
/// </summary>
public abstract class Widget
{
    private int _pendingMainThreadInvalidate;

    /// <summary>
    /// Bounding rectangle of this widget.
    /// </summary>
    public SKRect Bounds { get; set; }
    
    /// <summary>
    /// Whether this widget currently has keyboard focus.
    /// </summary>
    public bool IsFocused { get; set; }
    
    /// <summary>
    /// Whether this widget is visible.
    /// </summary>
    public bool IsVisible { get; set; } = true;
    
    /// <summary>
    /// Whether this widget is enabled for interaction.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    // Accessibility Hooks
    
    /// <summary>
    /// Unique ID for automation finding.
    /// </summary>
    public string AutomationId { get; set; }
    
    /// <summary>
    /// Human-readable name (e.g., for screen readers).
    /// </summary>
    public string Name { get; set; }
    
    /// <summary>
    /// Extended help text or tooltip.
    /// </summary>
    public string HelpText { get; set; }
    
    /// <summary>
    /// Semantic role of this widget.
    /// </summary>
    public WidgetRole Role { get; set; } = WidgetRole.None;
    
    /// <summary>
    /// Parent widget (null for root).
    /// </summary>
    public Widget Parent { get; set; }
    
    /// <summary>
    /// Child widgets.
    /// </summary>
    public List<Widget> Children { get; } = new();
    
    /// <summary>
    /// Event fired when this widget requests a repaint.
    /// </summary>
    public event Action Invalidated;
    
    /// <summary>
    /// Event fired when this widget requests focus.
    /// </summary>
    public event Action<Widget> FocusRequested;
    
    /// <summary>
    /// Add a child widget.
    /// </summary>
    public void AddChild(Widget child)
    {
        child.Parent = this;
        Children.Add(child);
        InvalidateLayout();
        Invalidate();
    }
    
    /// <summary>
    /// Remove a child widget.
    /// </summary>
    public void RemoveChild(Widget child)
    {
        if (Children.Remove(child))
        {
            child.Parent = null;
            InvalidateLayout();
            Invalidate();
        }
    }
    
    /// <summary>
    /// Desired size calculated during Measure pass.
    /// </summary>
    public SKSize DesiredSize { get; protected set; }
    
    /// <summary>
    /// Whether this widget needs layout recalculation.
    /// </summary>
    public bool IsLayoutDirty { get; protected set; } = true;
    
    /// <summary>
    /// Measure this widget and its children to determine desired size.
    /// </summary>
    public void Measure(SKSize availableSpace)
    {
        if (!IsVisible)
        {
            DesiredSize = SKSize.Empty;
            return;
        }
        
        DesiredSize = OnMeasure(availableSpace);
    }
    
    /// <summary>
    /// Arrange this widget and its children within the final rectangle.
    /// </summary>
    public void Arrange(SKRect finalRect)
    {
        if (!IsVisible) return;
        
        Bounds = finalRect;
        OnArrange(finalRect);
        IsLayoutDirty = false;
    }
    
    /// <summary>
    /// Override to implement custom child measurement.
    /// Default: returns full available space if children exist, or zero if none.
    /// </summary>
    protected virtual SKSize OnMeasure(SKSize availableSpace)
    {
        // Default: measure children and return bounding box
        float maxWidth = 0;
        float totalHeight = 0;
        
        foreach (var child in Children)
        {
            child.Measure(availableSpace);
            maxWidth = Math.Max(maxWidth, child.DesiredSize.Width);
            totalHeight += child.DesiredSize.Height;
        }
        
        return new SKSize(maxWidth, totalHeight);
    }
    
    /// <summary>
    /// Override to implement custom child arrangement.
    /// Default: stacks children vertically.
    /// </summary>
    protected virtual void OnArrange(SKRect finalRect)
    {
        float y = finalRect.Top;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var childRect = new SKRect(finalRect.Left, y, finalRect.Right, y + child.DesiredSize.Height);
            child.Arrange(childRect);
            y += childRect.Height;
        }
    }
    
    /// <summary>
    /// Mark this widget and its parents as needing layout.
    /// </summary>
    public void InvalidateLayout()
    {
        IsLayoutDirty = true;
        Parent?.InvalidateLayout();
    }
    
    /// <summary>
    /// Layout this widget within the available bounds.
    /// [DEPRECATED] Use Measure/Arrange instead.
    /// </summary>
    public virtual void Layout(SKRect available)
    {
        Measure(new SKSize(available.Width, available.Height));
        Arrange(available);
    }
    
    /// <summary>
    /// Paint this widget to the canvas.
    /// </summary>
    public abstract void Paint(SKCanvas canvas);
    
    /// <summary>
    /// Paint this widget and all children.
    /// </summary>
    public void PaintAll(SKCanvas canvas)
    {
        if (!IsVisible) return;
        
        Paint(canvas);
        
        foreach (var child in Children)
        {
            child.PaintAll(canvas);
        }
    }
    
    /// <summary>
    /// Current dirty rectangle that needs repainting (relative to this widget).
    /// </summary>
    public SKRect? DirtyRect { get; protected set; }
    
    /// <summary>
    /// Request a repaint of this widget or a specific region.
    /// </summary>
    public void Invalidate(SKRect? bounds = null)
    {
        if (ShouldDispatchInvalidateToUiThread())
        {
            QueueInvalidateOnUiThread();
            return;
        }

        InvalidateCore(bounds);
    }

    private bool ShouldDispatchInvalidateToUiThread()
    {
        var windowManager = FenBrowser.Host.WindowManager.Instance;
        return windowManager.IsMainThreadInitialized && !windowManager.IsOnMainThread;
    }

    private void QueueInvalidateOnUiThread()
    {
        if (Interlocked.Exchange(ref _pendingMainThreadInvalidate, 1) != 0)
        {
            return;
        }

        _ = FenBrowser.Host.WindowManager.Instance.RunOnMainThread(() =>
        {
            Interlocked.Exchange(ref _pendingMainThreadInvalidate, 0);
            InvalidateCore(null);
        });
    }

    private void InvalidateCore(SKRect? bounds)
    {
        var localBounds = bounds ?? new SKRect(0, 0, Bounds.Width, Bounds.Height);
        
        if (DirtyRect == null)
        {
            DirtyRect = localBounds;
        }
        else
        {
            DirtyRect = SKRect.Union(DirtyRect.Value, localBounds);
        }
        
        // Propagate to parent (translating coordinates)
        if (Parent != null)
        {
            var parentBounds = localBounds;
            parentBounds.Offset(Bounds.Left, Bounds.Top);
            Parent.Invalidate(parentBounds);
        }
        
        Invalidated?.Invoke();
    }
    
    /// <summary>
    /// Clear the dirty rect after painting.
    /// </summary>
    public void ClearDirtyRect()
    {
        DirtyRect = null;
        foreach (var child in Children)
        {
            child.ClearDirtyRect();
        }
    }
    
    /// <summary>
    /// Request a repaint of this widget.
    /// [DEPRECATED] Use Invalidate(bounds) instead.
    /// </summary>
    protected void Invalidate()
    {
        Invalidate(null);
    }
    
    /// <summary>
    /// Request keyboard focus.
    /// </summary>
    protected void RequestFocus()
    {
        FocusRequested?.Invoke(this);
    }
    
    /// <summary>
    /// Check if a point is within this widget's bounds.
    /// </summary>
    public virtual bool HitTest(float x, float y)
    {
        return IsVisible && IsEnabled && Bounds.Contains(x, y);
    }
    
    /// <summary>
    /// Find the deepest widget at the given point.
    /// </summary>
    public virtual Widget HitTestDeep(float x, float y)
    {
        if (!HitTest(x, y)) return null;
        
        // Check children in reverse order (topmost first)
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTestDeep(x, y);
            if (hit != null) return hit;
        }
        
        return this;
    }
    
    // Input handlers - override as needed
    public virtual void OnMouseDown(float x, float y, MouseButton button) { }
    public virtual void OnMouseUp(float x, float y, MouseButton button) { }
    public virtual void OnMouseMove(float x, float y) { }
    public virtual void OnKeyDown(Key key, bool ctrl, bool shift, bool alt) { }
    public virtual void OnKeyUp(Key key) { }
    public virtual void OnTextInput(char c, bool ctrl) { }
    public virtual void OnMouseWheel(float x, float y, float deltaX, float deltaY) { }
    
    /// <summary>
    /// Whether this widget can receive keyboard focus.
    /// Override to return true for focusable widgets.
    /// </summary>
    public virtual bool CanFocus => false;
    
    /// <summary>
    /// Called when this widget receives focus.
    /// </summary>
    public virtual void OnFocus() { }
    
    /// <summary>
    /// Called when this widget loses focus.
    /// </summary>
    public virtual void OnBlur() { }
}
