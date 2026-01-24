using System;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    public static class LayoutPositioningLogic
    {
        public static void ResolvePositionedBox(LayoutBox box, LayoutBox containingBlock, BoxModel containerGeometry)
        {
            if (box.ComputedStyle == null) return;
            
            var style = box.ComputedStyle;
            var cb = containerGeometry.PaddingBox; // Positioning is typically relative to padding box of container (for absolute)
            
            // Fixed is relative to viewport - handled by passing Viewport as containerGeometry?
            // Actually, if fixed, containing block is viewport. Caller must handle this.
            
            float left = 0;
            float top = 0;
            float right = 0;
            float bottom = 0;
            
            bool hasLeft = style.Left.HasValue;
            bool hasRight = style.Right.HasValue;
            bool hasTop = style.Top.HasValue;
            bool hasBottom = style.Bottom.HasValue;
            
            // Width/Height resolution
            float width = 0;
            float height = 0;
            
            // Simplistic Width Resolution
            if (style.Width.HasValue)
            {
                 width = (float)style.Width.Value;
            }
            else
            {
                // Auto width for absolute: shrink to fit?
                // Or if left+right are set, stretch.
                if (hasLeft && hasRight)
                {
                    width = cb.Width - (float)style.Left.Value - (float)style.Right.Value;
                }
                else
                {
                    // Shrink to fit / Intrinsic
                     // Use existing logic? Need to measure.
                     // For MVP, if auto, assume 0 or dummy measure.
                     width = 100; // Placeholder for measure
                }
            }
            
            // Height
             if (style.Height.HasValue)
            {
                 height = (float)style.Height.Value;
            }
            else
            {
                 if (hasTop && hasBottom)
                {
                    height = cb.Height - (float)style.Top.Value - (float)style.Bottom.Value;
                }
                else
                {
                    height = 50; // Placeholder
                }
            }
            
            // Horizontal Position
            if (hasLeft)
            {
                left = cb.Left + (float)style.Left.Value;
            }
            else if (hasRight)
            {
                left = cb.Right - (float)style.Right.Value - width;
            }
            else
            {
                // Static position (where it would have been).
                // Requires tracking the static position. 
                // For MVP, default to 0 (top-left of container).
                left = cb.Left;
            }
            
            // Vertical Position
            if (hasTop)
            {
                top = cb.Top + (float)style.Top.Value;
            }
            else if (hasBottom)
            {
                top = cb.Bottom - (float)style.Bottom.Value - height;
            }
            else
            {
                top = cb.Top;
            }
            
            // Update Geometry
            box.Geometry.ContentBox = new SKRect(left, top, left + width, top + height);
            
            // Sync
            box.Geometry.Padding = style.Padding;
            box.Geometry.Border = style.BorderThickness;
            box.Geometry.Margin = style.Margin;
            
            SyncBoxes(box.Geometry);
        }
        
        private static void SyncBoxes(BoxModel geometry)
        {
             var cb = geometry.ContentBox;
            var p = geometry.Padding;
            var b = geometry.Border;
            var m = geometry.Margin;
            
            geometry.PaddingBox = new SKRect(
                cb.Left - (float)p.Left,
                cb.Top - (float)p.Top,
                cb.Right + (float)p.Right,
                cb.Bottom + (float)p.Bottom);
                
            geometry.BorderBox = new SKRect(
                geometry.PaddingBox.Left - (float)b.Left,
                geometry.PaddingBox.Top - (float)b.Top,
                geometry.PaddingBox.Right + (float)b.Right,
                geometry.PaddingBox.Bottom + (float)b.Bottom);
                
            geometry.MarginBox = new SKRect(
                geometry.BorderBox.Left - (float)m.Left,
                geometry.BorderBox.Top - (float)m.Top,
                geometry.BorderBox.Right + (float)m.Right,
                geometry.BorderBox.Bottom + (float)m.Bottom);
        }
    }
}
