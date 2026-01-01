using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using SkiaSharp;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// Implements the CSS Flexbox Layout Algorithm.
    /// Handles main/cross axis sizing, wrapping, alignment, and gap support.
    /// </summary>
    public static class CssFlexLayout
    {
        public class FlexLine
        {
            public List<Node> Items = new List<Node>();
            public float MainSize { get; set; }
            public float CrossSize { get; set; }
            public float CrossStart { get; set; } // Relative to content box
        }

        public static LayoutMetrics Measure(
            Element container, 
            SKSize availableSize, 
            Func<Node, SKSize, int, LayoutMetrics> measureChild,
            Func<Node, CssComputed> getStyle,
            Func<Node, CssComputed, bool> shouldHide,
            int depth)
        {
            var style = getStyle(container);
            bool isRow = !(style?.FlexDirection?.ToLowerInvariant().Contains("column") ?? false);
            bool isWrap = (style?.FlexWrap?.ToLowerInvariant() ?? "nowrap") != "nowrap";
            float gap = (float)(style?.Gap ?? 0);

            // Fix: Include text nodes (unless pure whitespace) as they are valid flex items
            var children = container.Children.Where(c => 
            {
                if (c is Text t && !string.IsNullOrWhiteSpace(t.Data)) return true;
                return !shouldHide(c, getStyle(c));
            }).ToList();
            
            /* [PERF-REMOVED] */
            
            if (children.Count == 0) return new LayoutMetrics();

            float mainAvailable = isRow ? availableSize.Width : availableSize.Height;
            if (float.IsInfinity(mainAvailable)) mainAvailable = float.MaxValue;

            var lines = new List<FlexLine>();
            var currentLine = new FlexLine();
            lines.Add(currentLine);

            float currentMainUsed = 0;
            float maxChildWidth = 0; // For returning metrics
            float totalContentHeight = 0; // For returning metrics

            // 1. Measure items and determine line breaking
            foreach (var child in children)
            {
                // Measure child (we might need to remeasure later if we support flex-basis properly)
                // For now, measure with available space constraint
                var childMetrics = measureChild(child, availableSize, depth + 1);
                var childStyle = getStyle(child);
                
                var margin = childStyle?.Margin ?? new Thickness(0);
                float mMain = isRow ? (float)(margin.Left + margin.Right) : (float)(margin.Top + margin.Bottom);
                float mCross = isRow ? (float)(margin.Top + margin.Bottom) : (float)(margin.Left + margin.Right);

                float childMainSize = isRow ? childMetrics.MaxChildWidth : childMetrics.ContentHeight;
                float childCrossSize = isRow ? childMetrics.ContentHeight : childMetrics.MaxChildWidth;
                
                float totalItemMain = childMainSize + mMain;

                // Check for wrap
                // If usage > available AND not the first item in line
                if (isWrap && (currentMainUsed + totalItemMain > mainAvailable) && currentLine.Items.Count > 0)
                {
                    // New Line
                    currentLine.MainSize = currentMainUsed - gap; // Remove trailing gap
                    lines.Add(currentLine = new FlexLine());
                    currentMainUsed = 0;
                }

                currentLine.Items.Add(child);
                currentLine.CrossSize = Math.Max(currentLine.CrossSize, childCrossSize + mCross);
                
                currentMainUsed += totalItemMain + gap;
            }
            currentLine.MainSize = currentMainUsed - gap; // Final line size

            // Calculate return metrics
            float containerMainSize = 0;
            float containerCrossSize = 0;

            foreach (var line in lines)
            {
                // If row: container width is max line width, height is sum of line heights
                if (isRow)
                {
                    containerMainSize = Math.Max(containerMainSize, line.MainSize);
                    containerCrossSize += line.CrossSize;
                }
                else
                {
                    containerMainSize = Math.Max(containerMainSize, line.MainSize); // Height in column
                    containerCrossSize += line.CrossSize; // Width in column
                }
            }

            // Add gaps between lines (if we supported gap for multi-line flex)
            // Spec says 'gap' applies to both main and cross axis (row-gap, col-gap)
            // Simplified: adding gap between lines
            if (lines.Count > 1)
            {
                 containerCrossSize += gap * (lines.Count - 1);
            }

            return new LayoutMetrics
            {
                MaxChildWidth = isRow ? containerMainSize : containerCrossSize,
                ContentHeight = isRow ? containerCrossSize : containerMainSize
            };
        }

        public static void Arrange(
            Element container, 
            SKRect contentBox, 
            Action<Node, SKRect, int> arrangeChild,
            Func<Node, CssComputed> getStyle,
             Func<Node, SKSize> getDesiredSize, // Helper to get measured sizes
            Func<Node, CssComputed, bool> shouldHide,
            int depth)
        {
            var style = getStyle(container);
            bool isRow = !(style?.FlexDirection?.ToLowerInvariant().Contains("column") ?? false);
            bool isWrap = (style?.FlexWrap?.ToLowerInvariant() ?? "nowrap") != "nowrap";
            float gap = (float)(style?.Gap ?? 0);
            
            string justify = style?.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            string alignContent = style?.AlignContent?.ToLowerInvariant() ?? "stretch";
            string alignItems = style?.AlignItems?.ToLowerInvariant() ?? "stretch";

            // Fix: Include text nodes in Arrange too
            var children = container.Children.Where(c => 
            {
                if (c is Text t && !string.IsNullOrWhiteSpace(t.Data)) return true;
                return !shouldHide(c, getStyle(c));
            }).ToList();
            if (children.Count == 0) return;

            // Re-calculate lines (Arrange needs to know lines to position them)
            // Ideal world: Measure returns a state object. For now, re-simulate logic or rely on simplified single-line if no wrap.
            
            // To properly handle this without re-measure being expensive, we assume we can just look at sizes.
            
            float mainAvailable = isRow ? contentBox.Width : contentBox.Height;
            
            var lines = new List<FlexLine>();
            var currentLine = new FlexLine();
            lines.Add(currentLine);
            float currentMainUsed = 0;

            foreach (var child in children)
            {
                var s = getDesiredSize(child);
                var cStyle = getStyle(child);
                var margin = cStyle?.Margin ?? new Thickness(0);
                
                float mMain = isRow ? (float)(margin.Left + margin.Right) : (float)(margin.Top + margin.Bottom);
                float mCross = isRow ? (float)(margin.Top + margin.Bottom) : (float)(margin.Left + margin.Right);

                float childMainSize = isRow ? s.Width : s.Height;
                float childCrossSize = isRow ? s.Height : s.Width;
                
                float totalItemMain = childMainSize + mMain;
                
                if (isWrap && (currentMainUsed + totalItemMain > mainAvailable) && currentLine.Items.Count > 0)
                {
                    currentLine.MainSize = currentMainUsed - gap;
                    lines.Add(currentLine = new FlexLine());
                    currentMainUsed = 0;
                }
                
                currentLine.Items.Add(child);
                currentLine.CrossSize = Math.Max(currentLine.CrossSize, childCrossSize + mCross);
                currentMainUsed += totalItemMain + gap;
            }
            currentLine.MainSize = currentMainUsed - gap;

            // 2. Arrange Lines in Cross Axis
            float totalCrossSize = lines.Sum(l => l.CrossSize) + (gap * (lines.Count - 1));
            float crossFreeSpace = (isRow ? contentBox.Height : contentBox.Width) - totalCrossSize;
            float crossPos = isRow ? contentBox.Top : contentBox.Left;

            // Align Content (distribute lines) - Simplified to just 'stretch' behavior (stacking) usually or 'flex-start'
            // If we have extra space on cross axis
            
            // 3. Arrange Items in Main Axis per Line
            foreach (var line in lines)
            {
                float lineCrossSize = line.CrossSize;
                
                // Justify Content (Main Axis)
                float freeMain = mainAvailable - line.MainSize;
                float startOffset = 0;
                float itemGap = gap;

                if (freeMain > 0)
                {
                    switch (justify)
                    {
                        case "center": startOffset = freeMain / 2; break;
                        case "flex-end": startOffset = freeMain; break;
                        case "space-between": 
                            if (line.Items.Count > 1) itemGap += freeMain / (line.Items.Count - 1); 
                            break;
                        case "space-around":
                             if (line.Items.Count > 0) {
                                 float p = freeMain / line.Items.Count;
                                 startOffset = p / 2;
                                 itemGap += p;
                             }
                             break;
                    }
                }

                // Flex Grow logic: Distribute free space if items want to grow
                // NOTE: This overrides justify-content logic if any item grows
                float totalFlexGrow = 0;
                int autoMarginCount = 0;

                foreach(var item in line.Items)
                {
                    var s = getStyle(item);
                    totalFlexGrow += (float)(s?.FlexGrow ?? 0);
                    
                    if (isRow)
                    {
                        if (s?.MarginLeftAuto == true) autoMarginCount++;
                        if (s?.MarginRightAuto == true) autoMarginCount++;
                    }
                    else
                    {
                        if (s?.MarginTopAuto == true) autoMarginCount++;
                        if (s?.MarginBottomAuto == true) autoMarginCount++;
                    }
                }

                bool hasFlexGrow = totalFlexGrow > 0 && freeMain > 0;
                bool hasAutoMargins = autoMarginCount > 0 && freeMain > 0 && !hasFlexGrow; // FlexGrow takes precedence
                
                float growUnit = hasFlexGrow ? freeMain / totalFlexGrow : 0;
                float autoMarginUnit = hasAutoMargins ? freeMain / autoMarginCount : 0;
                
                // If we are growing OR using auto-margins, startOffset resets to 0
                if (hasFlexGrow || hasAutoMargins) 
                {
                    startOffset = 0;
                    itemGap = gap; // Reset gap expansion from justify
                }

                float currentMainPos = (isRow ? contentBox.Left : contentBox.Top) + startOffset;

                foreach (var child in line.Items)
                {
                    var s = getDesiredSize(child);
                    var cStyle = getStyle(child);
                    var m = cStyle?.Margin ?? new Thickness(0);

                    // Cross Alignment (Align Items)
                    // Calculate available cross space in the line
                    // The line height is lineCrossSize.
                    // The child takes childCrossSize + margins.
                    
                    float mt = (float)m.Top, mb = (float)m.Bottom, ml = (float)m.Left, mr = (float)m.Right;
                    float childCrossMargins = isRow ? (mt + mb) : (ml + mr);
                    float childCrossSize = isRow ? s.Height : s.Width;
                    
                    // Handle Stretch (if height/width not explicit, simplistically check if size is default or 0)
                    // Ideally we check style.Height.IsAuto. For now, if stretch and child size < lineCrossSize (and not fixed), stretch it.
                    // A better check would be: does the child have a specific cross size?
                    // We assume if 'Stretch' is active, we force the cross size to fill the line (minus margins)
                    if (alignItems == "stretch")
                    {
                         // Basic heuristic: if child is smaller than line, stretch it.
                         // Limitation: doesn't respect 'max-height'.
                         float targetSize = lineCrossSize - childCrossMargins;
                         if (targetSize > 0) childCrossSize = targetSize;
                    }

                    // Calculate Alignment Offset
                    float usedCrossSpace = childCrossSize + childCrossMargins;
                    float itemCrossFreeSpace = lineCrossSize - usedCrossSpace;
                    float alignOffset = 0;

                    if (itemCrossFreeSpace > 0)
                    {
                        if (alignItems == "center") alignOffset = itemCrossFreeSpace / 2;
                        else if (alignItems == "flex-end") alignOffset = itemCrossFreeSpace;
                    }
                    
                    float childCrossStart = crossPos + alignOffset;

                    // Calculate Flex Grow expansion
                    float flexGrow = (float)(cStyle?.FlexGrow ?? 0);
                    float expansion = (hasFlexGrow && flexGrow > 0) ? growUnit * flexGrow : 0;
                    
                    // Auto Margins (Main Axis)
                    float extraLeft = 0, extraRight = 0, extraTop = 0, extraBottom = 0;
                    if (hasAutoMargins)
                    {
                        if (isRow)
                        {
                            if (cStyle?.MarginLeftAuto == true) extraLeft = autoMarginUnit;
                            if (cStyle?.MarginRightAuto == true) extraRight = autoMarginUnit;
                        }
                        else
                        {
                            if (cStyle?.MarginTopAuto == true) extraTop = autoMarginUnit;
                            if (cStyle?.MarginBottomAuto == true) extraBottom = autoMarginUnit;
                        }
                    }

                    // Final Rect
                    float x, y, w, h;
                    if (isRow)
                    {
                        childCrossStart += mt; // Add margin top
                        currentMainPos += extraLeft; // Apply auto-margin-left
                        
                        x = currentMainPos + ml;
                        y = childCrossStart;
                        w = s.Width + expansion; // Apply Grow
                        h = childCrossSize; // Use potentially stretched size
                        
                        // Advance specifically for this item's right auto margin
                        // We do it by adding to the 'itemSize' equivalent tracking, but we must update currentMainPos after
                    }
                    else
                    {
                        childCrossStart += ml; // Add margin left
                        currentMainPos += extraTop; // Apply auto-margin-top
                        
                        x = childCrossStart;
                        y = currentMainPos + mt;
                        w = childCrossSize; // Use potentially stretched size
                        h = s.Height + expansion; // Apply Grow
                    }


                    arrangeChild(child, new SKRect(x, y, x + w, y + h), depth + 1);

                    // Advance Main Pos
                    float itemSize = isRow ? (w + ml + mr + extraRight ) : (h + mt + mb + extraBottom);
                    currentMainPos += itemSize + itemGap;
                }

                // Advance Cross Pos for next line
                crossPos += lineCrossSize + gap;
            }
        }
    }
}
