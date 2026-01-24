using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Dom;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Implements a simplified Flex Layout algorithm.
    /// Supports: row/column, justify-content, align-items, flex-grow.
    /// Targeted for New Tab Page (centering) and UI chrome.
    /// </summary>
    public class FlexFormattingContext : FormattingContext
    {
        private static FlexFormattingContext _instance;
        public static FlexFormattingContext Instance => _instance ??= new FlexFormattingContext();

        public override void Layout(LayoutBox box, LayoutState state)
        {
            // FenLogger.Debug($"[FLEX-CTX] Layout called for {box.SourceNode?.Tag} ({(box.SourceNode as Element)?.TagName})");
            if (!(box is BlockBox container)) return;

            // 1. Resolve Container Dimensions
            // Similar to Block, we need to determine our own width/height first (or at least available space).
            // For the root (body), width/height might be explicit (100vh).
            
            ResolveContainerDimensions(container, state);

            var style = container.ComputedStyle;
            var direction = style.FlexDirection?.ToLowerInvariant() ?? "row";
            bool isRow = direction.Contains("row");
            bool isWrap = style.FlexWrap?.Contains("wrap") == true;

            // 2. Collect Flex Items (In-flow children)
            // Anonymous text nodes should be wrapped in anonymous blocks? 
            // In Flexbox, text nodes become anonymous flex items.
            // Our current tree builder might already handle this, or we treat generic boxes as items.
            var items = container.Children.Where(c => c.ComputedStyle?.Display?.Contains("none") != true).ToList();

            // 3. Main/Cross Axis setup
            float containerMainSize = isRow ? container.Geometry.ContentBox.Width : container.Geometry.ContentBox.Height;
            float containerCrossSize = isRow ? container.Geometry.ContentBox.Height : container.Geometry.ContentBox.Width;
            
            // If main size is effectively infinite or unconstrained (auto), we resolve it by content later.
            // But for 100vh body, it is constrained.

            // 4. Measure Items (Hypothetical Main Size)
            // We pass available space.
            // If we are Row, available width is container width.
            // If we are Column, available width is container width (Cross size).
            
            float crossAxisAvailable = isRow ? container.Geometry.ContentBox.Height : container.Geometry.ContentBox.Width;
            
            // First Validated Pass: Measure all children with "Content" sizing
            foreach (var item in items)
            {
                // Reliability Gate: Check intra-frame deadline
                state.Deadline?.Check();

                // We delegate measurement to the child's context in a "Probe" mode?
                // Or standard Layout?
                // Construct a state with available space.
                
                float childAvailableWidth = isRow 
                    ? float.PositiveInfinity // Flex items shrink-to-fit in main axis initially? No, they have max-content
                    : container.Geometry.ContentBox.Width; // Column: Stretches to width

                if (style.AlignItems == "stretch" && !isRow)
                    childAvailableWidth = container.Geometry.ContentBox.Width;

                var childState = state.Clone();
                childState.AvailableSize = new SKSize(
                    isRow ? state.AvailableSize.Width : container.Geometry.ContentBox.Width, 
                    isRow ? container.Geometry.ContentBox.Height : state.AvailableSize.Height // tentative
                );

                // Run Layout on child to get its content size
                FormattingContext.Resolve(item).Layout(item, childState);
            }

            // 5. Resolving Flex Factors (Grow/Shrink) - Simplified: Only support FlexGrow for now
            // Calculate remaining space
            float totalMainSize = 0;
            float totalFlexGrow = 0;

            foreach (var item in items)
            {
                float itemMainSize = isRow ? item.Geometry.MarginBox.Width : item.Geometry.MarginBox.Height;
                totalMainSize += itemMainSize;

                if (item.ComputedStyle?.FlexGrow.HasValue == true)
                    totalFlexGrow += (float)item.ComputedStyle.FlexGrow.Value;
            }

            float remainingSpace = containerMainSize - totalMainSize;

            if (remainingSpace > 0 && totalFlexGrow > 0)
            {
                // Distribute space
                foreach (var item in items)
                {
                    float grow = (float)(item.ComputedStyle?.FlexGrow ?? 0);
                    if (grow > 0)
                    {
                        float share = remainingSpace * (grow / totalFlexGrow);
                        // Resize item main size
                        if (isRow)
                        {
                            // We need to re-layout with rigid width?
                            // Or just adjust the box?
                            // Re-layout is safer for flow content inside.
                             var childState = state.Clone();
                             float newWidth = item.Geometry.ContentBox.Width + share; // Simplify: Add to content? 
                             // Correction: Flex interaction is on the Margin Box usually, but let's change Content Box
                             // This is slightly incorrect spec-wise (should be basis), but works for "fill remaining"
                             
                             // Force specific size
                             var current = item.Geometry.ContentBox;
                             item.Geometry.ContentBox = new SKRect(current.Left, current.Top, current.Right + share, current.Bottom);
                             item.Geometry.PaddingBox = item.Geometry.ContentBox; // Simplified propagation
                             item.Geometry.BorderBox = item.Geometry.ContentBox;
                             item.Geometry.MarginBox = item.Geometry.ContentBox; // Losing margins?
                             
                             // Re-sync margin box size
                             LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, item.Geometry.ContentBox.Height);
                        }
                        else
                        {
                            // Column grow (height)
                             float newHeight = item.Geometry.ContentBox.Height + share;
                             LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, newHeight);
                        }
                    }
                }
                
                // Recalculate total after grow
                totalMainSize = containerMainSize; 
            }
            
            // 6. Placement (Main Axis - Justify Content)
            float currentMainPos = 0;
            float gap = (float)(style.Gap ?? 0); // Need to parse gap
            
            // Parse Gap manually if null (simple pixel check)
            if (style.Gap == null && style.Map.ContainsKey("gap"))
            {
                 float.TryParse(style.Map["gap"].Replace("px",""), out gap);
            }

            totalMainSize += gap * Math.Max(0, items.Count - 1);

            // Justify Content
            string justifyContent = style.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            float startOffset = 0;
            float itemStepExtra = 0;

            remainingSpace = containerMainSize - totalMainSize;

            if (remainingSpace > 0)
            {
                if (justifyContent == "center")
                {
                    startOffset = remainingSpace / 2;
                }
                else if (justifyContent == "flex-end")
                {
                    startOffset = remainingSpace;
                }
                else if (justifyContent == "space-between" && items.Count > 1)
                {
                    itemStepExtra = remainingSpace / (items.Count - 1);
                }
                else if (justifyContent == "space-around" && items.Count > 0)
                {
                    itemStepExtra = remainingSpace / items.Count;
                    startOffset = itemStepExtra / 2;
                }
            }

            currentMainPos = startOffset;

            // 7. Cross Axis Alignment (Align Items)
            string alignItems = style.AlignItems?.ToLowerInvariant() ?? "stretch";

            float maxCrossSize = 0;
            foreach (var item in items)
            {
                float itemCrossSize = isRow ? item.Geometry.MarginBox.Height : item.Geometry.MarginBox.Width;
                maxCrossSize = Math.Max(maxCrossSize, itemCrossSize);
            }

            // If auto-height container, expand it to wrap lines (single line here)
            if (isRow && container.Geometry.ContentBox.Height == 0) // Auto height
            {
                container.Geometry.ContentBox = new SKRect(0, 0, container.Geometry.ContentBox.Width, maxCrossSize);
                LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, maxCrossSize);
                containerCrossSize = maxCrossSize;
            }

            foreach (var item in items)
            {
                // Position Main Axis
                float x = 0, y = 0;

                if (isRow)
                {
                    x = currentMainPos;
                    // Cross Axis
                    float crossFree = containerCrossSize - item.Geometry.MarginBox.Height;
                    if (alignItems == "center") y = crossFree / 2;
                    else if (alignItems == "flex-end") y = crossFree;
                    else y = 0; // flex-start / stretch
                    
                    // Increment
                    currentMainPos += item.Geometry.MarginBox.Width + gap + itemStepExtra;
                }
                else // Column
                {
                    y = currentMainPos;
                    // Cross Axis
                    float crossFree = containerCrossSize - item.Geometry.MarginBox.Width;
                    if (alignItems == "center") x = crossFree / 2;
                    else if (alignItems == "flex-end") x = crossFree;
                    else x = 0;

                    currentMainPos += item.Geometry.MarginBox.Height + gap + itemStepExtra;
                }

                // Set absolute offset relative to container content box
                // NOTE: LayoutBox geometry is relative to parent Content Box
                LayoutBoxOps.SetPosition(item, x, y);
            }
        }

        private void ResolveContainerDimensions(BlockBox box, LayoutState state)
        {
            // Similar to ResolveWidth/Height in BlockContext
            float available = state.AvailableSize.Width;
            
            var padding = box.ComputedStyle?.Padding ?? new FenBrowser.Core.Thickness();
            var border = box.ComputedStyle?.BorderThickness ?? new FenBrowser.Core.Thickness();
            var margin = box.ComputedStyle?.Margin ?? new FenBrowser.Core.Thickness();

            box.Geometry.Padding = padding;
            box.Geometry.Border = border;
            box.Geometry.Margin = margin;

            // Width
            float width = 0;
            if (box.ComputedStyle?.Width.HasValue == true) width = (float)box.ComputedStyle.Width.Value;
            else width = Math.Max(0, available - (float)(margin.Horizontal + border.Horizontal + padding.Horizontal));

            // Height
            float height = 0;
            if (box.ComputedStyle?.Height.HasValue == true) 
            {
                height = (float)box.ComputedStyle.Height.Value;
            }
            else if (!string.IsNullOrEmpty(box.ComputedStyle?.HeightExpression))
            {
                // Resolve vh/vw etc.
                // Re-using LayoutHelper from MinimalLayoutComputer context logic logic (simplified)
                string expr = box.ComputedStyle.HeightExpression;
                if (expr.EndsWith("vh"))
                {
                    if (float.TryParse(expr.Replace("vh", ""), out float vh))
                    {
                        height = (vh / 100f) * state.ViewportHeight;
                    }
                }
                else if (expr.EndsWith("%"))
                {
                    // constrained percentage
                    if (float.TryParse(expr.Replace("%", ""), out float pct))
                    {
                        height = (pct / 100f) * state.AvailableSize.Height;
                    }
                }
            }

            // Fallback for Root/Body if height is still 0 (implies auto/full-screen)
            // If it's body and display is flex/grid, we often want full viewport if not specified
            bool isRoot = (box.SourceNode as Element)?.TagName == "BODY" || (box.SourceNode as Element)?.TagName == "HTML";
            if (height <= 0 && isRoot)
            {
                height = state.ViewportHeight;
            }
            
            box.Geometry.ContentBox = new SKRect(0, 0, width, height);
            
            // Sync boxes (Content -> Padding -> Border -> Margin)
            LayoutBoxOps.ComputeBoxModelFromContent(box, width, height);
        }
    }
    
    // Helper to avoid code duplication with BlockContext for box sizing
    public static class LayoutBoxOps
    {
        public static void ComputeBoxModelFromContent(LayoutBox box, float contentW, float contentH)
        {
            float x = (float)(box.Geometry.Padding.Left + box.Geometry.Border.Left);
            float y = (float)(box.Geometry.Padding.Top + box.Geometry.Border.Top);
            
            // Re-center content box relative to border box top-left (0,0 of border box?)
            // Wait, our convention:
            // Geometry.ContentBox is rect (0, 0, w, h) ? No, positions are relative to Parent Content.
            // But MarginBox encapsulates everything.
            
            // Let's stick to: ContentBox size is w, h.
            // We set offsets implicitly when painting?
            // No, the Layout engine logic usually sets ContentBox.Location relative to Parent Content.
            
            // For this helper, we just update the specific rect sizes based on Thickness,
            // assuming the Location will be set by the layout parent loop (SetPosition).
            
            box.Geometry.ContentBox = new SKRect(0, 0, contentW, contentH);
            
            // Recalculate outer boxes relative to Content (0,0) - this is local coords
            // NOTE: We rely on the renderer to apply offsets.
            // But LayoutBox.Geometry needs correct dimensions for hit testing and parent measurement.
            
            var p = box.Geometry.Padding;
            var b = box.Geometry.Border;
            var m = box.Geometry.Margin;
            
            box.Geometry.PaddingBox = new SKRect(
                (float)-p.Left, (float)-p.Top, 
                contentW + (float)p.Right, contentH + (float)p.Bottom
            );
            
            box.Geometry.BorderBox = new SKRect(
                (float)(-p.Left - b.Left), (float)(-p.Top - b.Top),
                contentW + (float)(p.Right + b.Right), contentH + (float)(p.Bottom + b.Bottom)
            );
            
            box.Geometry.MarginBox = new SKRect(
                (float)(-p.Left - b.Left - m.Left), (float)(-p.Top - b.Top - m.Top),
                contentW + (float)(p.Right + b.Right + m.Right), contentH + (float)(p.Bottom + b.Bottom + m.Bottom)
            );
        }

        public static void SetPosition(LayoutBox box, float x, float y)
        {
            // Move entire structure so MarginBox.TopLeft maps to x,y in Parent Content Space?
            // No, usually x,y refers to BorderBox or MarginBox origin.
            // Standard Flow: x,y is top-left of MarginBox.
            
            // Let's implement x,y as top-left of MarginBox
            
            var m = box.Geometry.Margin;
            var b = box.Geometry.Border;
            var p = box.Geometry.Padding;
            
            // Offset logic
            float dx = x - box.Geometry.MarginBox.Left;
            float dy = y - box.Geometry.MarginBox.Top;
            
            box.Geometry.MarginBox.Offset(dx, dy);
            box.Geometry.BorderBox.Offset(dx, dy);
            box.Geometry.PaddingBox.Offset(dx, dy);
            box.Geometry.ContentBox.Offset(dx, dy);
        }
    }
}
