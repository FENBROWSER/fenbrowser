using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using SkiaSharp;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    // PARTIAL: Extends MinimalLayoutComputer to add Multi-column Layout logic
    // Updated to handle parsing manually since CssComputed lacks columns properties
    public partial class MinimalLayoutComputer
    {
        private bool IsMultiColumn(CssComputed style)
        {
            if (style == null) return false;
            return (style.ColumnCountInt > 1 || style.ColumnWidthFloat > 0);
        }

        private LayoutMetrics MeasureMultiColumn(Element elem, SKSize availableSize, int depth)
        {
             var style = GetStyle(elem);
             float gap = (float)(style.ColumnGap ?? 16f);
             
             // Resolve Column Count & Width
             int count = style.ColumnCountInt ?? 1;
             float colWidth = (float)(style.ColumnWidthFloat ?? 0);
             float availableW = availableSize.Width;
             
             if (float.IsInfinity(availableW)) availableW = _viewportWidth; 
             
             // Algorithm from spec for resolving count/width
             if (colWidth > 0)
             {
                 int potential = (int)Math.Max(1, Math.Floor((availableW + gap) / (colWidth + gap)));
                 if (style.ColumnCountInt.HasValue) 
                     count = Math.Min(style.ColumnCountInt.Value, potential);
                 else
                     count = potential;
             }
             
             // Calculate effective column width
             float effectiveColWidth = (availableW - ((count - 1) * gap)) / count;
             if (effectiveColWidth < 1) effectiveColWidth = 1;
             
             // 1. Measure children to get total content height
             var colConstraint = new SKSize(effectiveColWidth, float.PositiveInfinity);
             var m = MeasureBlockInternal(elem, colConstraint, depth, elem);
             
             // 2. Calculate Balanced Height
             // Naive balance + account for largest child
             float totalHeight = m.ContentHeight;
             float maxChildH = 0;
             if (elem.ChildNodes != null) {
                 foreach(var c in elem.ChildNodes) {
                     if (_desiredSizes.TryGetValue(c, out var sz)) maxChildH = Math.Max(maxChildH, sz.Height);
                 }
             }
             
             float balancedHeight = Math.Max(maxChildH, (float)Math.Ceiling(totalHeight / count));
             
             // Container height is balancedHeight unless fixed
             float finalH = balancedHeight;
             if (style.Height.HasValue) finalH = (float)style.Height.Value;
             
             float finalW = availableSize.Width; 
             if (style.Width.HasValue) finalW = (float)style.Width.Value;
             else if (availableSize.Width > 0 && !float.IsInfinity(availableSize.Width)) finalW = availableSize.Width; 
             
             // Report Intrinsic Widths
             float minContent = colWidth > 0 ? colWidth : m.MinContentWidth;
             float maxContent = colWidth > 0 ? (colWidth * count + gap * (count - 1)) : m.MaxContentWidth;

             return new LayoutMetrics { 
                 ContentHeight = finalH, 
                 MaxChildWidth = effectiveColWidth, 
                 MinContentWidth = minContent,
                 MaxContentWidth = maxContent,
                 MarginTop = m.MarginTop, 
                 MarginBottom = m.MarginBottom 
             };
        }

        private void ArrangeMultiColumn(Element elem, SKRect finalRect)
        {
             var style = GetStyle(elem);
             float gap = (float)(style.ColumnGap ?? 16f);
             
             int count = style.ColumnCountInt ?? 1;
             float colWidth = (float)(style.ColumnWidthFloat ?? 0);
             float availableW = finalRect.Width;
             
             if (colWidth > 0)
             {
                 int potential = (int)Math.Max(1, Math.Floor((availableW + gap) / (colWidth + gap)));
                 if (style.ColumnCountInt.HasValue) 
                     count = Math.Min(style.ColumnCountInt.Value, potential);
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
             
             if (elem.ChildNodes != null)
             {
                 foreach(var child in elem.ChildNodes)
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
