using SkiaSharp;

namespace FenBrowser.Host.Widgets;

public enum Orientation { Horizontal, Vertical }

/// <summary>
/// Arranges children in a single line (Horizontal or Vertical).
/// </summary>
public class StackPanel : Widget
{
    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public float Spacing { get; set; } = 0;
    
    protected override SKSize OnMeasure(SKSize availableSpace)
    {
        float width = 0;
        float height = 0;
        
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            
            // For now, give child full remaining space? 
            // In a better StackPanel, we'd give infinite in orientation axis.
            child.Measure(availableSpace);
            
            if (Orientation == Orientation.Vertical)
            {
                height += child.DesiredSize.Height + (height > 0 ? Spacing : 0);
                width = Math.Max(width, child.DesiredSize.Width);
            }
            else
            {
                width += child.DesiredSize.Width + (width > 0 ? Spacing : 0);
                height = Math.Max(height, child.DesiredSize.Height);
            }
        }
        
        return new SKSize(width, height);
    }
    
    protected override void OnArrange(SKRect finalRect)
    {
        float currentX = finalRect.Left;
        float currentY = finalRect.Top;
        
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            
            SKRect childRect;
            if (Orientation == Orientation.Vertical)
            {
                childRect = new SKRect(finalRect.Left, currentY, finalRect.Right, currentY + child.DesiredSize.Height);
                currentY += child.DesiredSize.Height + Spacing;
            }
            else
            {
                childRect = new SKRect(currentX, finalRect.Top, currentX + child.DesiredSize.Width, finalRect.Bottom);
                currentX += child.DesiredSize.Width + Spacing;
            }
            
            child.Arrange(childRect);
        }
    }
    
    public override void Paint(SKCanvas canvas)
    {
        // Container doesn't paint anything by default
    }
}
