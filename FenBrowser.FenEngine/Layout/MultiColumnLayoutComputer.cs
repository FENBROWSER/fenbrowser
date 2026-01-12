using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using SkiaSharp;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    // PARTIAL: Extends MinimalLayoutComputer to add Multi-column Layout logic
    // Updated to handle parsing manually since CssComputed lacks columns properties
    public partial class MinimalLayoutComputer
    {
        private int ResolveColumnCount(CssComputed style)
        {
            if (style == null) return 1;
            if (style.Map.TryGetValue("column-count", out string val))
            {
                if (int.TryParse(val, out int count)) return count;
            }
            return 1;
        }

        private float ResolveColumnWidth(CssComputed style)
        {
            if (style == null) return 0;
            if (style.Map.TryGetValue("column-width", out string val))
            {
                if (float.TryParse(val.Replace("px", ""), out float w)) return w;
            }
            return 0;
        }
        
        private float ResolveColumnGap(CssComputed style)
        {
            if (style == null) return 16f;
            if (style.Map.TryGetValue("column-gap", out string val))
            {
                if (float.TryParse(val.Replace("px", ""), out float w)) return w;
            }
            return 16f;
        }

        private bool IsMultiColumn(CssComputed style)
        {
            if (style == null) return false;
            int count = ResolveColumnCount(style);
            float width = ResolveColumnWidth(style);
            return (count > 1 || width > 0);
        }

        private LayoutMetrics MeasureMultiColumn(Element elem, SKSize availableSize, int depth)
        {
             var style = GetStyle(elem);
             float gap = ResolveColumnGap(style);
             
             // Resolve Column Count & Width
             int count = ResolveColumnCount(style);
             float colWidth = ResolveColumnWidth(style);
             float availableW = availableSize.Width;
             
             if (float.IsInfinity(availableW)) availableW = _viewportWidth; 
             
             if (colWidth > 0)
             {
                 int potential = (int)Math.Floor((availableW + gap) / (colWidth + gap));
                 potential = Math.Max(1, potential);
                 if (ResolveColumnCount(style) > 1) 
                     count = Math.Min(count, potential);
                 else
                     count = potential;
             }
             
             // Calculate effective column width
             float effectiveColWidth = (availableW - ((count - 1) * gap)) / count;
             if (effectiveColWidth < 1) effectiveColWidth = 1;
             
             // 1. Measure children as if in a single column of this width
             var colConstraint = new SKSize(effectiveColWidth, float.PositiveInfinity); // Unconstrained height
             
             // We reuse MeasureBlock logic to measure children vertically
             var m = MeasureBlockInternal(elem, colConstraint, depth, elem);
             
             // 2. Calculate Balanced Height
             float totalHeight = m.ContentHeight;
             float balancedHeight = totalHeight / count;
             
             // In balanced mode, the container height is roughly balancedHeight
             float finalH = balancedHeight;
             if (style.Height.HasValue) finalH = (float)style.Height.Value;
             
             float finalW = availableSize.Width; 
             if (style.Width.HasValue) finalW = (float)style.Width.Value;
             else if (availableSize.Width > 0 && !float.IsInfinity(availableSize.Width)) finalW = availableSize.Width; 
             
             return new LayoutMetrics { ContentHeight = finalH, MaxChildWidth = finalW, MarginTop = m.MarginTop, MarginBottom = m.MarginBottom };
        }

        private void ArrangeMultiColumn(Element elem, SKRect finalRect)
        {
             var style = GetStyle(elem);
             float gap = ResolveColumnGap(style);
             
             // Re-resolve columns (stateless)
             int count = ResolveColumnCount(style);
             float colWidth = ResolveColumnWidth(style);
             float availableW = finalRect.Width;
             
             if (colWidth > 0)
             {
                 int potential = (int)Math.Floor((availableW + gap) / (colWidth + gap));
                 potential = Math.Max(1, potential);
                 if (ResolveColumnCount(style) > 1) 
                     count = Math.Min(count, potential);
                 else
                     count = potential;
             }
             
             float effectiveColWidth = (availableW - ((count - 1) * gap)) / count;
             if (effectiveColWidth < 1) effectiveColWidth = 1;

             float colHeight = finalRect.Height;
             
             // Layout children
             float curX = 0;
             float curY = 0;
             int curCol = 0;
             
             if (elem.Children != null)
             {
                 foreach(var child in elem.Children)
                 {
                     if (ShouldHide(child, GetStyle(child))) continue;
                     
                     SKSize childSize = _desiredSizes.ContainsKey(child) ? _desiredSizes[child] : new SKSize(0,0);
                     
                     // If adding child exceeds column height, break
                     if (curY + childSize.Height > colHeight && curY > 0 && curCol < count - 1)
                     {
                         curCol++;
                         curY = 0;
                         curX = curCol * (effectiveColWidth + gap);
                     }
                     
                     // Position Child relative to Container (TopLeft)
                     float absoluteX = finalRect.Left + curX + (child is Element ce ? (float)GetStyle(ce).Margin.Left : 0);
                     float absoluteY = finalRect.Top + curY + (child is Element ce2 ? (float)GetStyle(ce2).Margin.Top : 0);
                     
                     var childRect = new SKRect(absoluteX, absoluteY, absoluteX + childSize.Width, absoluteY + childSize.Height);
                     
                     // Store Box - Overwriting to avoid 'Update' mismatch
                     var newBox = new BoxModel 
                     { 
                         BorderBox = childRect,
                         ContentBox = childRect, 
                         PaddingBox = childRect,
                         MarginBox = childRect
                     };
                     _boxes[child] = newBox;
                     
                     // Recurse
                     ArrangeNode(child, childRect, 0); 
                     
                     // Advance
                     curY += childSize.Height;
                     // Simplistic margin collapse skip
                     if (child is Element ce3) curY += (float)(GetStyle(ce3).Margin.Top + GetStyle(ce3).Margin.Bottom); 
                 }
             }
        }
    }
}
