using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;

namespace FenBrowser.FenEngine.Rendering.Layout
{
    /// <summary>
    /// Flexbox layout algorithm implementation.
    /// Computes layout for flex containers according to CSS Flexbox specification.
    /// </summary>
    public static class FlexLayout
    {
        /// <summary>
        /// Compute flexbox layout for a container and its children.
        /// </summary>
        /// <param name="engine">Layout engine for recursive layout calls</param>
        /// <param name="node">The flex container element</param>
        /// <param name="contentBox">The content box area available for children</param>
        /// <param name="style">Computed styles for the container</param>
        /// <param name="maxChildWidth">Output: maximum width of children (for shrink-to-content)</param>
        /// <param name="shrinkToContent">If true, container should shrink to fit content</param>
        /// <param name="containerHeight">Explicit container height (for flex-grow distribution)</param>
        /// <returns>Total content height after layout</returns>
        public static float Compute(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            out float maxChildWidth,
            bool shrinkToContent = false,
            float containerHeight = 0)
        {
            var ctx = engine.Context;
            
            // Parse flex properties
            string dir = style?.FlexDirection?.ToLowerInvariant() ?? "row";
            bool isRow = dir == "row" || dir == "row-reverse";
            bool isReverse = dir.Contains("reverse");
            string wrap = style?.FlexWrap?.ToLowerInvariant() ?? "nowrap";
            bool shouldWrap = wrap == "wrap" || wrap == "wrap-reverse";
            string justifyContent = style?.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            string alignItems = style?.AlignItems?.ToLowerInvariant() ?? "stretch";
            string alignContent = style?.AlignContent?.ToLowerInvariant() ?? "stretch";
            
            // Parse gap values
            float gapValue = 0;
            float rowGap = 0;
            if (style?.Gap.HasValue == true)
            {
                gapValue = (float)style.Gap.Value;
                rowGap = gapValue;
            }
            if (style?.ColumnGap.HasValue == true) gapValue = (float)style.ColumnGap.Value;
            if (style?.RowGap.HasValue == true) rowGap = (float)style.RowGap.Value;
            else if (style?.Map != null)
            {
                string gapStr = null;
                if (style.Map.TryGetValue("gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    if (float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
                    {
                        gapValue = g;
                        rowGap = g;
                    }
                }
                if (isRow && style.Map.TryGetValue("column-gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out gapValue);
                }
                if (style.Map.TryGetValue("row-gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rowGap);
                }
            }
            
            maxChildWidth = 0;
            
            float cursorX = contentBox.Left;
            float cursorY = contentBox.Top;
            
            if (node.Children == null) return 0;
            
            // Collect flex items (skip whitespace text nodes)
            List<LiteElement> flexItems = new List<LiteElement>();
            foreach (var c in node.Children)
            {
                if (c.IsText && string.IsNullOrWhiteSpace(c.Text)) continue;
                
                // Skip position:absolute/fixed (they don't participate in flex flow)
                CssComputed childStyle = ctx.GetStyle(c);
                string pos = childStyle?.Position?.ToLowerInvariant();
                if (pos == "absolute" || pos == "fixed") continue;
                
                flexItems.Add(c);
            }
            
            if (flexItems.Count == 0) return 0;
            
            // Dispatch to row or column layout
            if (isRow)
            {
                return ComputeRowLayout(engine, node, contentBox, style, flexItems, 
                    gapValue, rowGap, shouldWrap, isReverse, justifyContent, alignItems, alignContent,
                    shrinkToContent, containerHeight, out maxChildWidth);
            }
            else
            {
                return ComputeColumnLayout(engine, node, contentBox, style, flexItems,
                    gapValue, rowGap, shouldWrap, isReverse, justifyContent, alignItems, alignContent,
                    shrinkToContent, containerHeight, out maxChildWidth);
            }
        }
        
        /// <summary>
        /// Compute row-direction flex layout.
        /// </summary>
        private static float ComputeRowLayout(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            List<LiteElement> flexItems,
            float gapValue,
            float rowGap,
            bool shouldWrap,
            bool isReverse,
            string justifyContent,
            string alignItems,
            string alignContent,
            bool shrinkToContent,
            float containerHeight,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            float cursorX = contentBox.Left;
            float cursorY = contentBox.Top;
            
            if (shouldWrap)
            {
                // Row wrap layout - group items into lines
                return ComputeRowWrapLayout(engine, node, contentBox, style, flexItems,
                    gapValue, rowGap, isReverse, justifyContent, alignItems, alignContent,
                    shrinkToContent, containerHeight, out maxChildWidth);
            }
            else
            {
                // Simple row layout (no wrap) with FLEX-GROW support
                return ComputeRowNoWrapLayout(engine, node, contentBox, style, flexItems,
                    gapValue, rowGap, isReverse, justifyContent, alignItems,
                    shrinkToContent, containerHeight, out maxChildWidth);
            }
        }
        
        /// <summary>
        /// Row layout with wrapping.
        /// </summary>
        private static float ComputeRowWrapLayout(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            List<LiteElement> flexItems,
            float gapValue,
            float rowGap,
            bool isReverse,
            string justifyContent,
            string alignItems,
            string alignContent,
            bool shrinkToContent,
            float containerHeight,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            // Build lines by measuring items
            var lines = new List<List<(LiteElement element, float width, float height)>>();
            var currentLine = new List<(LiteElement, float, float)>();
            float currentLineWidth = 0;
            
            foreach (var child in flexItems)
            {
                // Measure child with shrink-to-content
                engine.ComputeLayout(child, 0, 0, contentBox.Width, shrinkToContent: true);
                
                float childWidth = 0, childHeight = 0;
                var childBox = ctx.GetBox(child);
                if (childBox != null)
                {
                    childWidth = childBox.MarginBox.Width;
                    childHeight = childBox.MarginBox.Height;
                }
                
                // Check if wrapping needed
                float testWidth = currentLineWidth + gapValue + childWidth;
                if (currentLine.Count > 0 && testWidth > contentBox.Width)
                {
                    lines.Add(currentLine);
                    currentLine = new List<(LiteElement, float, float)>();
                    currentLineWidth = 0;
                }
                
                currentLine.Add((child, childWidth, childHeight));
                currentLineWidth += (currentLine.Count > 1 ? gapValue : 0) + childWidth;
            }
            
            if (currentLine.Count > 0)
                lines.Add(currentLine);
            
            // Position each line
            float lineY = contentBox.Top;
            float totalHeight = 0;
            
            foreach (var line in lines)
            {
                if (isReverse) line.Reverse();
                
                float lineHeight = line.Max(item => item.height);
                float lineWidth = line.Sum(item => item.width) + gapValue * (line.Count - 1);
                
                if (lineWidth > maxChildWidth) maxChildWidth = lineWidth;
                
                // Calculate justify-content offset
                float freeSpace = shrinkToContent ? 0 : contentBox.Width - lineWidth;
                float startOffset = 0;
                float extraGap = 0;
                
                switch (justifyContent)
                {
                    case "center":
                        startOffset = freeSpace / 2;
                        break;
                    case "flex-end":
                        startOffset = freeSpace;
                        break;
                    case "space-between":
                        if (line.Count > 1) extraGap = freeSpace / (line.Count - 1);
                        break;
                    case "space-around":
                        extraGap = freeSpace / line.Count;
                        startOffset = extraGap / 2;
                        break;
                    case "space-evenly":
                        extraGap = freeSpace / (line.Count + 1);
                        startOffset = extraGap;
                        break;
                }
                
                // Position items in line
                float itemX = contentBox.Left + startOffset;
                foreach (var item in line)
                {
                    float targetY = lineY;
                    
                    // Apply align-items
                    switch (alignItems)
                    {
                        case "center":
                            targetY = lineY + (lineHeight - item.height) / 2;
                            break;
                        case "flex-end":
                            targetY = lineY + lineHeight - item.height;
                            break;
                        case "baseline":
                            // Simplified baseline alignment
                            targetY = lineY;
                            break;
                        // stretch is default - item already fills height
                    }
                    
                    // Re-layout at final position
                    engine.ComputeLayout(item.element, itemX, targetY, item.width, shrinkToContent: true);
                    
                    itemX += item.width + gapValue + extraGap;
                }
                
                lineY += lineHeight + rowGap;
                totalHeight += lineHeight + rowGap;
            }
            
            return totalHeight > 0 ? totalHeight - rowGap : 0;
        }
        
        /// <summary>
        /// Row layout without wrapping (with flex-grow).
        /// </summary>
        private static float ComputeRowNoWrapLayout(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            List<LiteElement> flexItems,
            float gapValue,
            float rowGap,
            bool isReverse,
            string justifyContent,
            string alignItems,
            bool shrinkToContent,
            float containerHeight,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            // FIRST PASS: Measure all items and calculate flex-grow totals
            float totalChildrenWidth = 0;
            float totalGrow = 0;
            var childMeasurements = new List<(LiteElement child, float width, float height, float grow)>();
            
            foreach (var child in flexItems)
            {
                CssComputed childStyle = ctx.GetStyle(child);
                float grow = (float)(childStyle?.FlexGrow ?? 0);
                
                // Measure child with shrink-to-content
                engine.ComputeLayout(child, 0, 0, contentBox.Width, shrinkToContent: true);
                
                float childWidth = 0, childHeight = 0;
                var childBox = ctx.GetBox(child);
                if (childBox != null)
                {
                    childWidth = childBox.MarginBox.Width;
                    childHeight = childBox.MarginBox.Height;
                }
                
                childMeasurements.Add((child, childWidth, childHeight, grow));
                totalChildrenWidth += childWidth;
                totalGrow += grow;
            }
            
            // Calculate free space
            float effectiveContainerWidth = shrinkToContent ? totalChildrenWidth : contentBox.Width;
            float freeMain = effectiveContainerWidth - totalChildrenWidth - (gapValue * (flexItems.Count - 1));
            
            // Apply justify-content
            float mainOffsetStart = 0;
            float extraGap = 0;
            
            if (totalGrow == 0)
            {
                switch (justifyContent)
                {
                    case "center":
                        mainOffsetStart = freeMain / 2;
                        break;
                    case "flex-end":
                        mainOffsetStart = freeMain;
                        break;
                    case "space-between":
                        if (flexItems.Count > 1) extraGap = freeMain / (flexItems.Count - 1);
                        break;
                    case "space-around":
                        extraGap = freeMain / flexItems.Count;
                        mainOffsetStart = extraGap / 2;
                        break;
                    case "space-evenly":
                        extraGap = freeMain / (flexItems.Count + 1);
                        mainOffsetStart = extraGap;
                        break;
                }
            }
            
            // SECOND PASS: Position items
            if (isReverse) childMeasurements.Reverse();
            
            float itemX = contentBox.Left + (mainOffsetStart > 0 ? mainOffsetStart : 0);
            float maxHeight = 0;
            
            foreach (var (child, childWidth, childHeight, grow) in childMeasurements)
            {
                // Calculate grown width
                float finalWidth = childWidth;
                if (totalGrow > 0 && grow > 0 && freeMain > 0)
                {
                    finalWidth += (grow / totalGrow) * freeMain;
                }
                
                // Apply align-items (cross-axis)
                float targetY = contentBox.Top;
                switch (alignItems)
                {
                    case "center":
                        targetY = contentBox.Top + (contentBox.Height - childHeight) / 2;
                        break;
                    case "flex-end":
                        targetY = contentBox.Top + contentBox.Height - childHeight;
                        break;
                }
                
                // Re-layout at final position with final width
                engine.ComputeLayout(child, itemX, targetY, finalWidth, shrinkToContent: false);
                
                if (childHeight > maxHeight) maxHeight = childHeight;
                maxChildWidth = itemX + finalWidth - contentBox.Left;
                
                itemX += finalWidth + gapValue + extraGap;
            }
            
            return maxHeight;
        }
        
        /// <summary>
        /// Compute column-direction flex layout.
        /// </summary>
        private static float ComputeColumnLayout(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            List<LiteElement> flexItems,
            float gapValue,
            float rowGap,
            bool shouldWrap,
            bool isReverse,
            string justifyContent,
            string alignItems,
            string alignContent,
            bool shrinkToContent,
            float containerHeight,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            if (shouldWrap)
            {
                // Column wrap layout
                return ComputeColumnWrapLayout(engine, node, contentBox, style, flexItems,
                    gapValue, rowGap, isReverse, justifyContent, alignItems,
                    out maxChildWidth);
            }
            else
            {
                // Simple column layout with flex-grow
                return ComputeColumnNoWrapLayout(engine, node, contentBox, style, flexItems,
                    gapValue, rowGap, isReverse, justifyContent, alignItems,
                    shrinkToContent, containerHeight, out maxChildWidth);
            }
        }
        
        /// <summary>
        /// Column layout with wrapping.
        /// </summary>
        private static float ComputeColumnWrapLayout(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            List<LiteElement> flexItems,
            float gapValue,
            float rowGap,
            bool isReverse,
            string justifyContent,
            string alignItems,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            // Build columns
            var columns = new List<List<(LiteElement element, float width, float height)>>();
            var currentColumn = new List<(LiteElement, float, float)>();
            float currentColumnHeight = 0;
            
            foreach (var child in flexItems)
            {
                engine.ComputeLayout(child, 0, 0, contentBox.Width, shrinkToContent: true);
                
                float childWidth = 0, childHeight = 0;
                var childBox = ctx.GetBox(child);
                if (childBox != null)
                {
                    childWidth = childBox.MarginBox.Width;
                    childHeight = childBox.MarginBox.Height;
                }
                
                float testHeight = currentColumnHeight + rowGap + childHeight;
                if (currentColumn.Count > 0 && testHeight > contentBox.Height)
                {
                    columns.Add(currentColumn);
                    currentColumn = new List<(LiteElement, float, float)>();
                    currentColumnHeight = 0;
                }
                
                currentColumn.Add((child, childWidth, childHeight));
                currentColumnHeight += (currentColumn.Count > 1 ? rowGap : 0) + childHeight;
            }
            
            if (currentColumn.Count > 0)
                columns.Add(currentColumn);
            
            // Position columns
            float columnX = contentBox.Left;
            float maxHeight = 0;
            
            foreach (var column in columns)
            {
                if (isReverse) column.Reverse();
                
                float columnWidth = column.Max(item => item.width);
                float itemY = contentBox.Top;
                
                foreach (var item in column)
                {
                    float targetX = columnX;
                    
                    // Apply align-items (cross-axis for column = horizontal)
                    switch (alignItems)
                    {
                        case "center":
                            targetX = columnX + (columnWidth - item.width) / 2;
                            break;
                        case "flex-end":
                            targetX = columnX + columnWidth - item.width;
                            break;
                    }
                    
                    engine.ComputeLayout(item.element, targetX, itemY, item.width, shrinkToContent: false);
                    itemY += item.height + rowGap;
                }
                
                float colHeight = itemY - contentBox.Top - rowGap;
                if (colHeight > maxHeight) maxHeight = colHeight;
                columnX += columnWidth + gapValue;
            }
            
            maxChildWidth = columnX - contentBox.Left - gapValue;
            return maxHeight;
        }
        
        /// <summary>
        /// Column layout without wrapping (with flex-grow).
        /// </summary>
        private static float ComputeColumnNoWrapLayout(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            List<LiteElement> flexItems,
            float gapValue,
            float rowGap,
            bool isReverse,
            string justifyContent,
            string alignItems,
            bool shrinkToContent,
            float containerHeight,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            // FIRST PASS: Measure all items
            float totalChildrenHeight = 0;
            float totalGrow = 0;
            var childMeasurements = new List<(LiteElement child, float width, float height, float grow)>();
            
            foreach (var child in flexItems)
            {
                CssComputed childStyle = ctx.GetStyle(child);
                float grow = (float)(childStyle?.FlexGrow ?? 0);
                
                engine.ComputeLayout(child, contentBox.Left, 0, contentBox.Width, shrinkToContent: true);
                
                float childWidth = 0, childHeight = 0;
                var childBox = ctx.GetBox(child);
                if (childBox != null)
                {
                    childWidth = childBox.MarginBox.Width;
                    childHeight = childBox.MarginBox.Height;
                }
                
                childMeasurements.Add((child, childWidth, childHeight, grow));
                totalChildrenHeight += childHeight;
                totalGrow += grow;
                
                if (childWidth > maxChildWidth) maxChildWidth = childWidth;
            }
            
            // Calculate effective container height
            float effectiveContainerHeight = containerHeight > 0 ? containerHeight : contentBox.Height;
            if (style != null && style.Height.HasValue)
            {
                effectiveContainerHeight = (float)style.Height.Value;
            }
            if (effectiveContainerHeight <= 0 || float.IsInfinity(effectiveContainerHeight))
            {
                effectiveContainerHeight = totalChildrenHeight;
            }
            
            float freeMain = effectiveContainerHeight - totalChildrenHeight - (rowGap * (flexItems.Count - 1));
            
            // Apply justify-content
            float mainOffsetStart = 0;
            float extraGap = 0;
            
            if (totalGrow == 0)
            {
                switch (justifyContent)
                {
                    case "center":
                        mainOffsetStart = freeMain / 2;
                        break;
                    case "flex-end":
                        mainOffsetStart = freeMain;
                        break;
                    case "space-between":
                        if (flexItems.Count > 1) extraGap = freeMain / (flexItems.Count - 1);
                        break;
                    case "space-around":
                        extraGap = freeMain / flexItems.Count;
                        mainOffsetStart = extraGap / 2;
                        break;
                    case "space-evenly":
                        extraGap = freeMain / (flexItems.Count + 1);
                        mainOffsetStart = extraGap;
                        break;
                }
            }
            
            // SECOND PASS: Position items
            if (isReverse) childMeasurements.Reverse();
            
            float itemY = contentBox.Top + (mainOffsetStart > 0 ? mainOffsetStart : 0);
            float totalHeight = 0;
            
            foreach (var (child, childWidth, childHeight, grow) in childMeasurements)
            {
                float finalHeight = childHeight;
                if (totalGrow > 0 && grow > 0 && freeMain > 0)
                {
                    finalHeight += (grow / totalGrow) * freeMain;
                }
                
                // Apply align-items (cross-axis = horizontal for column)
                float targetX = contentBox.Left;
                switch (alignItems)
                {
                    case "center":
                        targetX = contentBox.Left + (contentBox.Width - childWidth) / 2;
                        break;
                    case "flex-end":
                        targetX = contentBox.Left + contentBox.Width - childWidth;
                        break;
                }
                
                engine.ComputeLayout(child, targetX, itemY, childWidth, shrinkToContent: false);
                
                itemY += finalHeight + rowGap + extraGap;
                totalHeight += finalHeight + rowGap + extraGap;
            }
            
            return totalHeight > 0 ? totalHeight - rowGap : 0;
        }
    }
}
