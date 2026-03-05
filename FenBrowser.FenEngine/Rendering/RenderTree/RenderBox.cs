using FenBrowser.Core.Css;
using System;
using SkiaSharp;
using FenBrowser.Core;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a visual rectangle (div, p, img, etc.).
    /// </summary>
    public class RenderBox : RenderObject
    {
        public override void Layout(SKSize availableSize)
        {
            if (Style == null) Style = new CssComputed(); // Ensure Style is never null

            // TEMPORARILY DISABLED FOR DEBUGGING - Show everything to ensure content is visible
            /*
            // Smart Display None:
            // Only respect display:none if it's NOT a critical structural element.
            // This prevents "white screen" on sites that hide body/main initially,
            // while still hiding popups/overlays/clutter.
            if (Style.Display == "none")
            {
                var tag = Node?.TagName?.ToUpperInvariant();
                // Always show these structural tags even if hidden
                bool forceShow = tag == "BODY" || tag == "MAIN" || tag == "ARTICLE" || tag == "SECTION" || tag == "HEADER" || tag == "FOOTER";
                
                // Also show DIVs that are direct children of BODY (often main wrappers)
                if (!forceShow && tag == "DIV" && Node?.Parent?.TagName?.ToUpperInvariant() == "BODY")
                {
                    forceShow = true;
                }

                if (!forceShow)
                {
                    Bounds = SKRect.Empty;
                    return;
                }
            }
            */

            // 1. Calculate Box Model properties
            var margin = Style.Margin;
            var padding = Style.Padding;
            var border = Style.BorderThickness;



            // 2. Determine Width
            double targetWidth = 0;
            
            string bs = null;
            if (Style.Map != null) Style.Map.TryGetValue("box-sizing", out bs);
            bool isBorderBox = (bs ?? "").Trim().Equals("border-box", StringComparison.OrdinalIgnoreCase);

            if (Style.Width.HasValue) 
            {
                targetWidth = Style.Width.Value;
                if (!isBorderBox) targetWidth += padding.Left + padding.Right + border.Left + border.Right;
            }
            else if (Style.WidthPercent.HasValue) 
            {
                if (double.IsInfinity(availableSize.Width)) targetWidth = double.PositiveInfinity;
                else targetWidth = availableSize.Width * (Style.WidthPercent.Value / 100.0) - margin.Left - margin.Right;
            }
            else targetWidth = availableSize.Width - margin.Left - margin.Right;
            
            if (Style.Display == null) // Default UA styles
            {
                var tag = Node?.TagName?.ToUpperInvariant();
                if (tag == "INPUT" || tag == "SELECT" || tag == "TEXTAREA" || tag == "BUTTON" || tag == "IMG") 
                    Style.Display = "inline-block";
                else if (tag == "SPAN" || tag == "A" || tag == "LABEL" || tag == "B" || tag == "I" || tag == "STRONG" || tag == "EM")
                    Style.Display = "inline";
                else 
                    Style.Display = "block";
            }
            
            bool isBlock = Style.Display == "block" || Style.Display == "flex" || Style.Display == "grid"; 
            
            if (!Style.Width.HasValue && !Style.WidthPercent.HasValue)
            {
                // Intrinsic widths for replaced elements
                var tag = Node?.TagName?.ToUpperInvariant();
                if (tag == "INPUT" || tag == "BUTTON" || tag == "SELECT")
                {
                     targetWidth = 150; // Default generic width
                }
                else if (tag == "IMG")
                {
                     targetWidth = 300; // Placeholder
                }
                else if (tag == "HR")
                {
                    // HR takes full width by default (stays as initialized)
                }
                else if (Style.Display == "inline" || Style.Display == "inline-block")
                {
                     // Shrink to fit content is handled later via IsInfinity check or LayoutInlineChildren
                     // But here we need to set a base? 
                     // No, if isBlock is false, LayoutInlineChildren logic applies.
                     // But Wait, LayoutInlineChildren returns HEIGHT.
                     // RenderBox Width needs to be known. 
                     // If it's inline-block, it should be shrink-to-fit.
                     // We set targetWidth to Infinity to trigger shrink logic later.
                     targetWidth = double.PositiveInfinity;
                }
            }

            if (targetWidth < 0) targetWidth = 0;

            // Constrain by Min/Max Width
            if (Style.MinWidth.HasValue && targetWidth < Style.MinWidth.Value) targetWidth = Style.MinWidth.Value;
            if (Style.MaxWidth.HasValue && targetWidth > Style.MaxWidth.Value) targetWidth = Style.MaxWidth.Value;

            // 3. Prepare content area
            double contentWidth = targetWidth - padding.Left - padding.Right - border.Left - border.Right;
            if (contentWidth < 0) contentWidth = 0;

            double contentHeight = 0;

            // 4. Layout Children
            // Check if we should do Inline Layout, Block Layout, or Flex Layout
            bool isFlex = Style.Display == "flex" || Style.Display == "inline-flex";

            if (isFlex)
            {
                double knownContentHeight = Style.Height.HasValue
                    ? Style.Height.Value - (Style.Padding.Top + Style.Padding.Bottom + Style.BorderThickness.Top + Style.BorderThickness.Bottom)
                    : double.PositiveInfinity;
                if (knownContentHeight < 0) knownContentHeight = 0;
                contentHeight = LayoutFlexChildren(contentWidth, knownContentHeight);
            }
            else
            {
                // Heuristic: If we have children and the first one is inline, we try inline layout.
                bool isInlineFormattingContext = HasInlineChildren();

                if (isInlineFormattingContext)
                {
                    contentHeight = LayoutInlineChildren(contentWidth);
                }
                else
                {
                    contentHeight = LayoutBlockChildren(contentWidth);
                }
            }

            // 5. Determine Height
            double targetHeight = Style.Height ?? (contentHeight + padding.Top + padding.Bottom + border.Top + border.Bottom);
            
            // Height Percent Support
            if (!Style.Height.HasValue && Style.HeightPercent.HasValue && !double.IsInfinity(availableSize.Height))
            {
                targetHeight = availableSize.Height * (Style.HeightPercent.Value / 100.0) - margin.Top - margin.Bottom;
            }

            // Aspect Ratio Support
            // If aspect-ratio is specified and one dimension is known, calculate the other
            if (Style.AspectRatio.HasValue && Style.AspectRatio.Value > 0)
            {
                bool widthIsSet = Style.Width.HasValue || Style.WidthPercent.HasValue;
                bool heightIsSet = Style.Height.HasValue || Style.HeightPercent.HasValue;
                
                if (widthIsSet && !heightIsSet)
                {
                    // Width is known, calculate height from aspect ratio
                    // aspect-ratio = width / height, so height = width / aspect-ratio
                    targetHeight = (targetWidth - padding.Left - padding.Right - border.Left - border.Right) / Style.AspectRatio.Value + padding.Top + padding.Bottom + border.Top + border.Bottom;
                }
                else if (!widthIsSet && heightIsSet)
                {
                    // Height is known, calculate width from aspect ratio
                    // aspect-ratio = width / height, so width = height * aspect-ratio  
                    double innerHeight = targetHeight - padding.Top - padding.Bottom - border.Top - border.Bottom;
                    targetWidth = innerHeight * Style.AspectRatio.Value + padding.Left + padding.Right + border.Left + border.Right;
                   
                    // Re-apply width constraints
                    if (Style.MinWidth.HasValue && targetWidth < Style.MinWidth.Value) targetWidth = Style.MinWidth.Value;
                    if (Style.MaxWidth.HasValue && targetWidth > Style.MaxWidth.Value) targetWidth = Style.MaxWidth.Value;
                }
            }

            // Intrinsic height for replaced elements if not specified
            if (!Style.Height.HasValue && !Style.HeightPercent.HasValue && contentHeight == 0)
            {
                var tag = Node?.TagName?.ToUpperInvariant();
                if (tag == "INPUT" || tag == "BUTTON" || tag == "SELECT") targetHeight = 30; // Default height
                else if (tag == "IMG") targetHeight = 150; // Placeholder height
            }
            
            // Constrain by Min/Max Height
            if (Style.MinHeight.HasValue && targetHeight < Style.MinHeight.Value) targetHeight = Style.MinHeight.Value;
            if (Style.MaxHeight.HasValue && targetHeight > Style.MaxHeight.Value) targetHeight = Style.MaxHeight.Value;

            // Handle infinite width (shrink to fit)
            if (double.IsInfinity(targetWidth))
            {
                double maxRight = padding.Left + border.Left;
                if (Children.Count > 0)
                {
                    foreach (var child in Children)
                    {
                        if (child.Style != null && (child.Style.Position == "absolute" || child.Style.Position == "fixed")) continue;

                        var childMargin = child.Style?.Margin ?? new Thickness(0);
                        double right = child.Bounds.Left + child.Bounds.Width + childMargin.Right;
                        if (right > maxRight) maxRight = right;
                    }
                }
                
                targetWidth = maxRight + padding.Right + border.Right;
                
                // Fallback for replaced elements with percentage width (which became Infinity) but no children
                if (Children.Count == 0)
                {
                    var tag = Node?.TagName?.ToUpperInvariant();
                    if (tag == "INPUT" || tag == "SELECT" || tag == "BUTTON" || tag == "TEXTAREA") 
                        targetWidth = Math.Max(targetWidth, 150 + padding.Left + padding.Right + border.Left + border.Right);
                    else if (tag == "IMG") 
                        targetWidth = Math.Max(targetWidth, 300 + padding.Left + padding.Right + border.Left + border.Right);
                }
                
                // Re-apply min/max constraints
                if (Style.MaxWidth.HasValue && targetWidth > Style.MaxWidth.Value) targetWidth = Style.MaxWidth.Value;

                // Re-layout children if we resolved a finite width from infinity
                // This is crucial for justify-content: center/end to work correctly after we determine our size
                if (isFlex && !double.IsInfinity(targetWidth) && targetWidth > 0)
                {
                    double resolvedContentWidth = targetWidth - padding.Left - padding.Right - border.Left - border.Right;
                    if (resolvedContentWidth < 0) resolvedContentWidth = 0;
                    double knownContentHeight2 = Style.Height.HasValue
                        ? Style.Height.Value - (Style.Padding.Top + Style.Padding.Bottom + Style.BorderThickness.Top + Style.BorderThickness.Bottom)
                        : double.PositiveInfinity;
                    if (knownContentHeight2 < 0) knownContentHeight2 = 0;
                    LayoutFlexChildren(resolvedContentWidth, knownContentHeight2);
                }
            }

            // 6. Set Final Bounds
            targetWidth = EnsureValid(targetWidth);
            targetHeight = EnsureValid(targetHeight);
            Bounds = SKRect.Create(0, 0, (float)targetWidth, (float)targetHeight);

            // 7. Layout Absolute Children
            LayoutAbsoluteChildren(new SKSize((float)targetWidth, (float)targetHeight));
        }

        private bool HasInlineChildren()
        {
            if (Children.Count == 0) return false;
            // If first child is text or explicitly inline, we treat as inline context
            var first = Children[0];
            if (first is RenderText) return true;
            if (first.Style != null && (first.Style.Display == "inline" || first.Style.Display == "inline-block")) return true;
            return false;
        }

        private double LayoutBlockChildren(double contentWidth)
        {
            double currentY = Style.Padding.Top + Style.BorderThickness.Top;
            foreach (var child in Children)
            {
                if (child.Style != null && (child.Style.Position == "absolute" || child.Style.Position == "fixed")) continue;

                child.Layout(new SKSize((float)contentWidth, float.PositiveInfinity));
                var childMargin = child.Style?.Margin ?? new Thickness(0);
                
                currentY += childMargin.Top;
                
                var childBounds = child.Bounds;
                childBounds.Location = new SKPoint((float)(Style.Padding.Left + Style.BorderThickness.Left + childMargin.Left), (float)currentY);
                child.Bounds = childBounds;

                currentY += childBounds.Height + childMargin.Bottom;
            }
            return currentY - (Style.Padding.Top + Style.BorderThickness.Top); // Return content height
        }

        private double LayoutInlineChildren(double contentWidth)
        {
            double startX = Style.Padding.Left + Style.BorderThickness.Left;
            double startY = Style.Padding.Top + Style.BorderThickness.Top;
            
            double currentX = startX;
            double currentY = startY;
            double currentRowHeight = 0;

            foreach (var child in Children)
            {
                if (child.Style != null && (child.Style.Position == "absolute" || child.Style.Position == "fixed")) continue;

                // Measure child
                child.Layout(new SKSize((float)contentWidth, float.PositiveInfinity));
                
                var childMargin = child.Style?.Margin ?? new Thickness(0);
                double childTotalWidth = child.Bounds.Width + childMargin.Left + childMargin.Right;
                double childTotalHeight = child.Bounds.Height + childMargin.Top + childMargin.Bottom;

                // Check if fits on current line
                if (currentX + childTotalWidth > startX + contentWidth && currentX > startX)
                {
                    // Wrap to next line
                    currentX = startX;
                    currentY += currentRowHeight;
                    currentRowHeight = 0;
                }

                // Position child
                var childBounds = child.Bounds;
                childBounds.Location = new SKPoint((float)(currentX + childMargin.Left), (float)(currentY + childMargin.Top));
                child.Bounds = childBounds;

                // Advance
                currentX += childTotalWidth;
                currentRowHeight = Math.Max(currentRowHeight, childTotalHeight);
            }

            return (currentY + currentRowHeight) - startY;
        }

        private double LayoutFlexChildren(double contentWidth, double contentHeight = double.PositiveInfinity)
        {
            // Basic Flexbox Implementation
            var dir = Style.FlexDirection?.ToLowerInvariant() ?? "row";
            var wrap = Style.FlexWrap?.ToLowerInvariant() ?? "nowrap";
            var justify = Style.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            var align = Style.AlignItems?.ToLowerInvariant() ?? "stretch";

            bool isRow = dir.Contains("row");
            bool isReverse = dir.Contains("reverse");
            bool canWrap = wrap == "wrap";

            double startX = Style.Padding.Left + Style.BorderThickness.Left;
            double startY = Style.Padding.Top + Style.BorderThickness.Top;
            
            double mainAxisCurrent = isRow ? startX : startY;
            double crossAxisCurrent = isRow ? startY : startX;
            double crossAxisMax = 0; // Max size in cross axis for current line

            // 1. Measure all children first
            foreach (var child in Children)
            {
                if (child.Style != null && (child.Style.Position == "absolute" || child.Style.Position == "fixed")) continue;

                // For flex items, we might need to constrain them differently, but for now, let them size naturally
                // If row, height is infinite. If column, width is contentWidth (maybe?)
                // Simplified: Measure with available space
                child.Layout(new SKSize(isRow ? float.PositiveInfinity : (float)contentWidth, float.PositiveInfinity));
            }

            // 2. Position children (Simplified: Single line or simple wrap, no shrinking/growing yet)
            // TODO: Implement FlexGrow/Shrink
            
            var lines = new System.Collections.Generic.List<System.Collections.Generic.List<RenderObject>>();
            var currentLine = new System.Collections.Generic.List<RenderObject>();
            lines.Add(currentLine);

            double currentMainSize = 0;

            foreach (var child in Children)
            {
                if (child.Style != null && (child.Style.Position == "absolute" || child.Style.Position == "fixed")) continue;

                var childMargin = child.Style?.Margin ?? new Thickness(0);
                double childMainSize = isRow 
                    ? child.Bounds.Width + childMargin.Left + childMargin.Right 
                    : child.Bounds.Height + childMargin.Top + childMargin.Bottom;
                
                double childCrossSize = isRow
                    ? child.Bounds.Height + childMargin.Top + childMargin.Bottom
                    : child.Bounds.Width + childMargin.Left + childMargin.Right;

                if (canWrap && currentMainSize + childMainSize > (isRow ? contentWidth : double.PositiveInfinity) && currentMainSize > 0)
                {
                    // Wrap
                    currentLine = new System.Collections.Generic.List<RenderObject>();
                    lines.Add(currentLine);
                    currentMainSize = 0;
                    // Reset cross axis max for new line? No, we need to track line heights.
                }

                currentLine.Add(child);
                currentMainSize += childMainSize;
            }

            // 3. Layout lines
            double totalCrossSize = 0;
            
            foreach (var line in lines)
            {
                double lineCrossSize = 0;
                double lineMainSize = 0;
                double totalGrow = 0;

                // 1. Calculate initial line size and total grow
                foreach (var child in line)
                {
                    var childMargin = child.Style?.Margin ?? new Thickness(0);
                    double childMain = isRow
                        ? child.Bounds.Width + childMargin.Left + childMargin.Right
                        : child.Bounds.Height + childMargin.Top + childMargin.Bottom;
                    lineMainSize += childMain;
                    totalGrow += child.Style?.FlexGrow ?? 0;
                }

                // 2. Apply Flex Grow / Flex Shrink
                double constraint = isRow ? contentWidth : contentHeight;
                double freeSpace = constraint - lineMainSize;

                if (freeSpace > 0 && totalGrow > 0 && !double.IsInfinity(freeSpace))
                {
                    // Flex Grow: distribute extra space proportionally to flex-grow values
                    foreach (var child in line)
                    {
                        double grow = child.Style?.FlexGrow ?? 0;
                        if (grow > 0)
                        {
                            double extra = freeSpace * (grow / totalGrow);
                            if (isRow)
                            {
                                double newWidth = child.Bounds.Width + extra;
                                var oldWidth = child.Style.Width;
                                child.Style.Width = newWidth;
                                child.Layout(new SKSize((float)newWidth, float.PositiveInfinity));
                                child.Style.Width = oldWidth;
                            }
                            else
                            {
                                double newHeight = child.Bounds.Height + extra;
                                var oldHeight = child.Style.Height;
                                child.Style.Height = newHeight;
                                child.Layout(new SKSize((float)contentWidth, (float)newHeight));
                                child.Style.Height = oldHeight;
                            }
                        }
                    }
                    lineMainSize = constraint;
                }
                else if (freeSpace < 0 && !double.IsInfinity(constraint))
                {
                    // Flex Shrink: reduce oversized items proportionally to flex-shrink * base-size
                    double totalWeightedShrink = 0;
                    foreach (var child in line)
                    {
                        double shrink = child.Style?.FlexShrink ?? 1.0;
                        double childMain = isRow ? child.Bounds.Width : child.Bounds.Height;
                        totalWeightedShrink += shrink * childMain;
                    }

                    if (totalWeightedShrink > 0)
                    {
                        foreach (var child in line)
                        {
                            double shrink = child.Style?.FlexShrink ?? 1.0;
                            if (shrink > 0)
                            {
                                double childMain = isRow ? child.Bounds.Width : child.Bounds.Height;
                                double shrinkRatio = (shrink * childMain) / totalWeightedShrink;
                                double reduction = Math.Abs(freeSpace) * shrinkRatio;
                                if (isRow)
                                {
                                    double newWidth = Math.Max(0, child.Bounds.Width - reduction);
                                    var oldWidth = child.Style.Width;
                                    child.Style.Width = newWidth;
                                    child.Layout(new SKSize((float)newWidth, float.PositiveInfinity));
                                    child.Style.Width = oldWidth;
                                }
                                else
                                {
                                    double newHeight = Math.Max(0, child.Bounds.Height - reduction);
                                    var oldHeight = child.Style.Height;
                                    child.Style.Height = newHeight;
                                    child.Layout(new SKSize((float)contentWidth, (float)newHeight));
                                    child.Style.Height = oldHeight;
                                }
                            }
                        }
                    }
                }


                // Recompute line main size after flex grow/shrink adjustments.
                lineMainSize = 0;
                foreach (var child in line)
                {
                    var childMargin = child.Style?.Margin ?? new Thickness(0);
                    lineMainSize += isRow
                        ? child.Bounds.Width + childMargin.Left + childMargin.Right
                        : child.Bounds.Height + childMargin.Top + childMargin.Bottom;
                }

                // 3. Calculate Cross Size (after potential resize)
                foreach (var child in line)
                {
                    var childMargin = child.Style?.Margin ?? new Thickness(0);
                    double childCross = isRow
                        ? child.Bounds.Height + childMargin.Top + childMargin.Bottom
                        : child.Bounds.Width + childMargin.Left + childMargin.Right;
                    
                    lineCrossSize = Math.Max(lineCrossSize, childCross);
                }

                // Distribute main axis space (Justify Content)
                double mainConstraint = isRow ? contentWidth : contentHeight;
                double remainingMain = !double.IsInfinity(mainConstraint) ? mainConstraint - lineMainSize : 0;
                if (remainingMain < 0) remainingMain = 0;

                double startMain = isRow ? startX : startY;
                double gapMain = 0;

                // Only justify if we have a finite constraint on the main axis
                bool performJustify = !double.IsInfinity(mainConstraint);

                if (performJustify) // Justify applies to main axis (row or column)
                {
                    if (justify == "center") startMain += remainingMain / 2;
                    else if (justify == "flex-end") startMain += remainingMain;
                    else if (justify == "space-between" && line.Count > 1) gapMain = remainingMain / (line.Count - 1);
                    else if (justify == "space-around") { gapMain = remainingMain / line.Count; startMain += gapMain / 2; }
                    else if (justify == "space-evenly")
                    {
                        if (line.Count > 0)
                        {
                            gapMain = remainingMain / (line.Count + 1);
                            startMain += gapMain;
                        }
                    }
                }

                var positionedLine = isReverse ? line.AsEnumerable().Reverse() : line;
                double itemMainPos = isReverse ? startMain + lineMainSize : startMain;

                foreach (var child in positionedLine)
                {
                    var childMargin = child.Style?.Margin ?? new Thickness(0);
                    var bounds = child.Bounds;

                    // Align Items (Cross Axis)
                    double itemCrossPos = isRow ? crossAxisCurrent : crossAxisCurrent; // Start of line
                    
                    // Stretch?
                    if (align == "stretch")
                    {
                        if (isRow) bounds = new SKRect(bounds.Left, bounds.Top, bounds.Right, (float)(bounds.Top + Math.Max(bounds.Height, lineCrossSize - childMargin.Top - childMargin.Bottom)));
                        else bounds = new SKRect(bounds.Left, bounds.Top, (float)(bounds.Left + Math.Max(bounds.Width, lineCrossSize - childMargin.Left - childMargin.Right)), bounds.Bottom);
                    }
                    else if (align == "center")
                    {
                        double childCross = isRow ? bounds.Height : bounds.Width;
                        double freeCross = lineCrossSize - childCross - (isRow ? (childMargin.Top + childMargin.Bottom) : (childMargin.Left + childMargin.Right));
                        itemCrossPos += freeCross / 2;
                    }
                    else if (align == "flex-end")
                    {
                        double childCross = isRow ? bounds.Height : bounds.Width;
                        double freeCross = lineCrossSize - childCross - (isRow ? (childMargin.Top + childMargin.Bottom) : (childMargin.Left + childMargin.Right));
                        itemCrossPos += freeCross;
                    }

                    if (isRow)
                    {
                        if (isReverse)
                        {
                            itemMainPos -= childMargin.Right + bounds.Width;
                            bounds.Location = new SKPoint((float)itemMainPos, (float)(itemCrossPos + childMargin.Top));
                            itemMainPos -= childMargin.Left + gapMain;
                        }
                        else
                        {
                            bounds.Location = new SKPoint((float)(itemMainPos + childMargin.Left), (float)(itemCrossPos + childMargin.Top));
                            itemMainPos += childMargin.Left + bounds.Width + childMargin.Right + gapMain;
                        }
                    }
                    else
                    {
                        if (isReverse)
                        {
                            itemMainPos -= childMargin.Bottom + bounds.Height;
                            bounds.Location = new SKPoint((float)(itemCrossPos + childMargin.Left), (float)itemMainPos);
                            itemMainPos -= childMargin.Top + gapMain;
                        }
                        else
                        {
                            bounds.Location = new SKPoint((float)(itemCrossPos + childMargin.Left), (float)(itemMainPos + childMargin.Top));
                            itemMainPos += childMargin.Top + bounds.Height + childMargin.Bottom + gapMain;
                        }
                    }

                    child.Bounds = bounds;
                }

                crossAxisCurrent += lineCrossSize;
                totalCrossSize += lineCrossSize;
            }

            return totalCrossSize;
        }

        private void LayoutAbsoluteChildren(SKSize availableSize)
        {
            foreach (var child in Children)
            {
                if (child.Style != null && (child.Style.Position == "absolute" || child.Style.Position == "fixed"))
                {
                    // Measure
                    // If left/right are both set, width is constrained.
                    // If top/bottom are both set, height is constrained.
                    
                    double x = 0;
                    double y = 0;
                    
                    // Simplified: Just layout with available size
                    child.Layout(availableSize);
                    
                    var bounds = child.Bounds;
                    
                    if (child.Style.Left.HasValue) x = child.Style.Left.Value;
                    else if (child.Style.Right.HasValue) x = availableSize.Width - child.Style.Right.Value - bounds.Width;
                    
                    if (child.Style.Top.HasValue) y = child.Style.Top.Value;
                    else if (child.Style.Bottom.HasValue) y = availableSize.Height - child.Style.Bottom.Value - bounds.Height;

                    bounds.Location = new SKPoint((float)x, (float)y);
                    child.Bounds = bounds;
                }
            }
        }

        private static double EnsureValid(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                return 0;
            return value;
        }
    }
}


