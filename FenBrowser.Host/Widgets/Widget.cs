using SkiaSharp;
using Silk.NET.Input;

namespace FenBrowser.Host.Widgets;

/// <summary>
/// Base class for all UI widgets in FenBrowser.Host.
/// Provides common layout, painting, and input handling.
/// </summary>
public abstract class Widget
{
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
    }
    
    /// <summary>
    /// Layout this widget within the available bounds.
    /// Override to implement custom layout.
    /// </summary>
    public virtual void Layout(SKRect available)
    {
        Bounds = available;
        
        // Default: stack children vertically
        float y = available.Top;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var childBounds = new SKRect(available.Left, y, available.Right, y + 40);
            child.Layout(childBounds);
            y += childBounds.Height;
        }
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
    /// Request a repaint of this widget.
    /// </summary>
    protected void Invalidate()
    {
        Invalidated?.Invoke();
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
    public bool HitTest(float x, float y)
    {
        return IsVisible && IsEnabled && Bounds.Contains(x, y);
    }
    
    /// <summary>
    /// Find the deepest widget at the given point.
    /// </summary>
    public Widget HitTestDeep(float x, float y)
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
    public virtual void OnKeyDown(Key key) { }
    public virtual void OnKeyUp(Key key) { }
    public virtual void OnTextInput(char c) { }
    
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
