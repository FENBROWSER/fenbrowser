using SkiaSharp;
using System.Collections.Generic;

namespace FenBrowser.Host.Widgets;

public enum Dock { Left, Top, Right, Bottom, Fill, None }

/// <summary>
/// Arranges children by docking them to edges. Last child can fill.
/// </summary>
public class DockPanel : Widget
{
    private readonly Dictionary<Widget, Dock> _dockMap = new();
    public bool LastChildFill { get; set; } = true;
    
    public void AddChild(Widget child, Dock dock)
    {
        base.AddChild(child);
        _dockMap[child] = dock;
    }
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        float usedWidth = 0;
        float usedHeight = 0;
        float maxWidth = 0;
        float maxHeight = 0;
        bool hasFillChild = false;
        
        SKSize remaining = availableSpace;
        
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            
            var dock = _dockMap.GetValueOrDefault(child, Dock.Left);
            if (dock == Dock.None) continue; // Skip floating/manual widgets

            child.Measure(remaining);
            var desired = child.DesiredSize;
            
            switch (dock)
            {
                case Dock.Top:
                case Dock.Bottom:
                    usedHeight += desired.Height;
                    remaining = new SKSize(remaining.Width, Math.Max(0, remaining.Height - desired.Height));
                    maxWidth = Math.Max(maxWidth, desired.Width);
                    break;
                case Dock.Left:
                case Dock.Right:
                    usedWidth += desired.Width;
                    remaining = new SKSize(Math.Max(0, remaining.Width - desired.Width), remaining.Height);
                    maxHeight = Math.Max(maxHeight, desired.Height);
                    break;
                case Dock.Fill:
                    // Fill child takes all remaining space
                    hasFillChild = true;
                    break;
            }
        }
        
        // CRITICAL FIX: When LastChildFill is true and we have a Fill child,
        // the DockPanel should request the full available space (like a window root).
        // Otherwise we only return the sum of docked children, causing layout compression.
        if (LastChildFill && hasFillChild)
        {
            return availableSpace;
        }
        
        return new SKSize(Math.Max(maxWidth, usedWidth), Math.Max(maxHeight, usedHeight));
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        SKRect remaining = finalRect;
        int count = Children.Count;
        int lastDockedVisibleIndex = GetLastDockedVisibleIndex();
        
        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            if (!child.IsVisible) continue;
            
            var dock = _dockMap.GetValueOrDefault(child, Dock.Left);
            if (dock == Dock.None) continue; // Skip floating/manual widgets
            
            var desired = child.DesiredSize;
            
            if (LastChildFill && i == lastDockedVisibleIndex)
            {
                child.Arrange(ClampRect(remaining));
                break;
            }
            
            SKRect childRect;
            switch (dock)
            {
                case Dock.Top:
                    var topHeight = MathF.Min(MathF.Max(0, desired.Height), MathF.Max(0, remaining.Height));
                    childRect = new SKRect(remaining.Left, remaining.Top, remaining.Right, remaining.Top + topHeight);
                    remaining = new SKRect(remaining.Left, remaining.Top + topHeight, remaining.Right, remaining.Bottom);
                    break;
                case Dock.Bottom:
                    var bottomHeight = MathF.Min(MathF.Max(0, desired.Height), MathF.Max(0, remaining.Height));
                    childRect = new SKRect(remaining.Left, remaining.Bottom - bottomHeight, remaining.Right, remaining.Bottom);
                    remaining = new SKRect(remaining.Left, remaining.Top, remaining.Right, remaining.Bottom - bottomHeight);
                    break;
                case Dock.Left:
                    var leftWidth = MathF.Min(MathF.Max(0, desired.Width), MathF.Max(0, remaining.Width));
                    childRect = new SKRect(remaining.Left, remaining.Top, remaining.Left + leftWidth, remaining.Bottom);
                    remaining = new SKRect(remaining.Left + leftWidth, remaining.Top, remaining.Right, remaining.Bottom);
                    break;
                case Dock.Right:
                    var rightWidth = MathF.Min(MathF.Max(0, desired.Width), MathF.Max(0, remaining.Width));
                    childRect = new SKRect(remaining.Right - rightWidth, remaining.Top, remaining.Right, remaining.Bottom);
                    remaining = new SKRect(remaining.Left, remaining.Top, remaining.Right - rightWidth, remaining.Bottom);
                    break;
                case Dock.Fill:
                    childRect = remaining;
                    break;
                default:
                    childRect = remaining;
                    break;
            }
            
            child.Arrange(ClampRect(childRect));
            remaining = ClampRect(remaining);
        }
    }

    private int GetLastDockedVisibleIndex()
    {
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            if (!child.IsVisible)
            {
                continue;
            }

            var dock = _dockMap.GetValueOrDefault(child, Dock.Left);
            if (dock != Dock.None)
            {
                return i;
            }
        }

        return -1;
    }

    private static SKRect ClampRect(SKRect rect)
    {
        float left = MathF.Min(rect.Left, rect.Right);
        float right = MathF.Max(rect.Left, rect.Right);
        float top = MathF.Min(rect.Top, rect.Bottom);
        float bottom = MathF.Max(rect.Top, rect.Bottom);
        return new SKRect(left, top, right, bottom);
    }
    
    public override void Paint(SKCanvas canvas)
    {
        // Container doesn't paint
    }
}
