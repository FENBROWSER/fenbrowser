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
                    // Doesn't subtract from remaining yet in Measure pass
                    break;
            }
        }
        
        return new SKSize(Math.Max(maxWidth, usedWidth), Math.Max(maxHeight, usedHeight));
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        SKRect remaining = finalRect;
        int count = Children.Count;
        
        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            if (!child.IsVisible) continue;
            
            var dock = _dockMap.GetValueOrDefault(child, Dock.Left);
            if (dock == Dock.None) continue; // Skip floating/manual widgets
            
            var desired = child.DesiredSize;
            
            if (LastChildFill && i == count - 1)
            {
                child.Arrange(remaining);
                break;
            }
            
            SKRect childRect;
            switch (dock)
            {
                case Dock.Top:
                    childRect = new SKRect(remaining.Left, remaining.Top, remaining.Right, remaining.Top + desired.Height);
                    remaining = new SKRect(remaining.Left, remaining.Top + desired.Height, remaining.Right, remaining.Bottom);
                    break;
                case Dock.Bottom:
                    childRect = new SKRect(remaining.Left, remaining.Bottom - desired.Height, remaining.Right, remaining.Bottom);
                    remaining = new SKRect(remaining.Left, remaining.Top, remaining.Right, remaining.Bottom - desired.Height);
                    break;
                case Dock.Left:
                    childRect = new SKRect(remaining.Left, remaining.Top, remaining.Left + desired.Width, remaining.Bottom);
                    remaining = new SKRect(remaining.Left + desired.Width, remaining.Top, remaining.Right, remaining.Bottom);
                    break;
                case Dock.Right:
                    childRect = new SKRect(remaining.Right - desired.Width, remaining.Top, remaining.Right, remaining.Bottom);
                    remaining = new SKRect(remaining.Left, remaining.Top, remaining.Right - desired.Width, remaining.Bottom);
                    break;
                case Dock.Fill:
                    childRect = remaining;
                    break;
                default:
                    childRect = remaining;
                    break;
            }
            
            child.Arrange(childRect);
        }
    }
    
    public override void Paint(SKCanvas canvas)
    {
        // Container doesn't paint
    }
}
