using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Dom.V2;

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

        protected override void LayoutCore(LayoutBox box, LayoutState state)
        {
            // FenLogger.Debug($"[FLEX-CTX] Layout called for {box.SourceNode?.NodeName} ({(box.SourceNode as Element)?.TagName})");
            var container = box;

            // 1. Resolve Container Dimensions
            // Similar to Block, we need to determine our own width/height first (or at least available space).
            // For the root (body), width/height might be explicit (100vh).
            
            ResolveContainerDimensions(container, state);

            var style = container.ComputedStyle;
            string direction = style?.FlexDirection?.ToLowerInvariant();
            if (string.IsNullOrEmpty(direction) && style?.Map != null && style.Map.TryGetValue("flex-direction", out var rawDirection))
            {
                direction = rawDirection?.ToLowerInvariant();
            }
            direction ??= "row";
            bool isRow = direction.Contains("row");
            bool isWrap = style?.FlexWrap?.Contains("wrap") == true ||
                          (style?.Map != null && style.Map.TryGetValue("flex-wrap", out var rawWrap) &&
                           rawWrap?.IndexOf("wrap", StringComparison.OrdinalIgnoreCase) >= 0);
            string alignItems = style?.AlignItems?.ToLowerInvariant();
            if (string.IsNullOrEmpty(alignItems) && style?.Map != null && style.Map.TryGetValue("align-items", out var rawAlign))
            {
                alignItems = rawAlign?.ToLowerInvariant();
            }
            alignItems ??= "stretch";
            string justifyContent = style?.JustifyContent?.ToLowerInvariant();
            if (string.IsNullOrEmpty(justifyContent) && style?.Map != null && style.Map.TryGetValue("justify-content", out var rawJustify))
            {
                justifyContent = rawJustify?.ToLowerInvariant();
            }
            justifyContent ??= "flex-start";
            bool mainAxisUnconstrained = isRow
                ? (float.IsInfinity(state.AvailableSize.Width) || float.IsNaN(state.AvailableSize.Width))
                : (float.IsInfinity(state.AvailableSize.Height) || float.IsNaN(state.AvailableSize.Height));
            bool hasExplicitMainSize = isRow
                ? (style?.Width.HasValue == true || style?.WidthPercent.HasValue == true || !string.IsNullOrEmpty(style?.WidthExpression))
                : (style?.Height.HasValue == true || style?.HeightPercent.HasValue == true || !string.IsNullOrEmpty(style?.HeightExpression));
            bool shrinkToContentMainAxis = mainAxisUnconstrained && !hasExplicitMainSize;

            // 2. Collect Flex Items (In-flow children)
            // Anonymous text nodes should be wrapped in anonymous blocks? 
            // In Flexbox, text nodes become anonymous flex items.
            // Our current tree builder might already handle this, or we treat generic boxes as items.
            var outOfFlow = container.Children
                .Where(c => c.ComputedStyle?.Display?.Contains("none") != true && c.IsOutOfFlow)
                .ToList();

            var rawItems = container.Children
                .Where(c => c.ComputedStyle?.Display?.Contains("none") != true && !c.IsOutOfFlow)
                .Where(c => !IsIgnorableFlexItem(c))
                .ToList();

            var items = new List<LayoutBox>();
            foreach (var candidate in rawItems)
            {
                // Mixed inline content can be wrapped in anonymous boxes. For flex item
                // collection, prefer concrete child boxes to avoid phantom full-height items.
                if (candidate.IsAnonymous && candidate.SourceNode is not Element && candidate.Children.Count > 0)
                {
                    var concreteChildren = candidate.Children
                        .Where(c => !IsIgnorableFlexItem(c))
                        .ToList();

                    if (concreteChildren.Count > 0)
                    {
                        items.AddRange(concreteChildren);
                        continue;
                    }
                }

                items.Add(candidate);
            }

            // 3. Main/Cross Axis setup
            float containerMainSize = isRow ? container.Geometry.ContentBox.Width : container.Geometry.ContentBox.Height;
            float containerCrossSize = isRow ? container.Geometry.ContentBox.Height : container.Geometry.ContentBox.Width;
            
            // Robustness: Handle unconstrained sizes
            if (float.IsInfinity(containerMainSize)) containerMainSize = isRow ? state.ViewportWidth : 1000f;
            if (float.IsInfinity(containerCrossSize)) containerCrossSize = isRow ? 1000f : state.ViewportWidth;
            if (float.IsNaN(containerMainSize)) containerMainSize = 0;
            if (float.IsNaN(containerCrossSize)) containerCrossSize = 0;
            
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

                var itemStyle = item.ComputedStyle;
                bool hasExplicitWidth = itemStyle?.Width.HasValue == true ||
                                        itemStyle?.WidthPercent.HasValue == true ||
                                        !string.IsNullOrEmpty(itemStyle?.WidthExpression) ||
                                        (itemStyle?.Map != null && itemStyle.Map.ContainsKey("width"));
                bool hasFlexGrow = ResolveFlexGrow(itemStyle).GetValueOrDefault() > 0;
                string display = itemStyle?.Display?.ToLowerInvariant() ?? string.Empty;
                bool inlineLike = display.StartsWith("inline", StringComparison.OrdinalIgnoreCase) || display.Length == 0;
                bool hasFlexBasisExplicit = itemStyle?.Map != null &&
                                           (itemStyle.Map.ContainsKey("flex-basis") ||
                                            (itemStyle.Map.TryGetValue("flex", out var flexShorthand) &&
                                             !string.IsNullOrWhiteSpace(flexShorthand) &&
                                             flexShorthand.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3));
                bool preferIntrinsicWidth = isRow && !hasFlexGrow && !hasExplicitWidth && !hasFlexBasisExplicit;
                bool preferIntrinsicCrossInColumn = !isRow && alignItems != "stretch" && !hasExplicitWidth;

                // Use intrinsic probing for inline-like items without flex-grow/width,
                // otherwise constrain to container width to avoid collapse.
                float childAvailWidth = isRow
                    ? (preferIntrinsicWidth
                        ? float.PositiveInfinity
                        : (container.Geometry.ContentBox.Width > 0 ? container.Geometry.ContentBox.Width : float.PositiveInfinity))
                    : (preferIntrinsicCrossInColumn
                        ? float.PositiveInfinity
                        : (container.Geometry.ContentBox.Width > 0 ? container.Geometry.ContentBox.Width : float.PositiveInfinity));
                float childAvailHeight = isRow
                    ? (container.Geometry.ContentBox.Height > 0 ? container.Geometry.ContentBox.Height : float.PositiveInfinity)
                    : float.PositiveInfinity;
                
                // If container height is 0/auto, passing 0 constraint might be wrong for height?
                // But normally we let it grow. If fixed height, we constrain.
                // Keeping original logic for height.

                // Respect alignment constraints
                if (!isRow && alignItems == "stretch") childAvailWidth = container.Geometry.ContentBox.Width;
                if (isRow && alignItems == "stretch" && container.Geometry.ContentBox.Height > 0) childAvailHeight = container.Geometry.ContentBox.Height;

                var childState = state.Clone();
                childState.AvailableSize = new SKSize(childAvailWidth, childAvailHeight);
                childState.ContainingBlockWidth = float.IsFinite(container.Geometry.ContentBox.Width) && container.Geometry.ContentBox.Width > 0
                    ? container.Geometry.ContentBox.Width
                    : state.ViewportWidth;
                childState.ContainingBlockHeight = float.IsFinite(container.Geometry.ContentBox.Height) && container.Geometry.ContentBox.Height > 0
                    ? container.Geometry.ContentBox.Height
                    : state.ViewportHeight;

                // Run Layout on child (will trigger shrink-to-fit if width is auto & avail is infinity)
                FormattingContext.Resolve(item).Layout(item, childState);
                
                // CRITICAL DEFENSE: Sanitize the resulting box geometry immediately.
                // If child layout failed to handle Infinity and produced NaN, fix it here.
                LayoutValidator.SanitizeBox(item, state.ViewportWidth, state.ViewportHeight);

                // Keep flex rows from collapsing to 0px when auto-sized icon/control items
                // are re-measured through nested contexts.
                ApplyCollapsedFlexItemFallback(item);

            }

            // If a flex-grow item probed to an oversized width (common with % widths),
            // clamp it to the container width before shrink calculations to avoid
            // overflow-driven collapse to 0.
            if (isRow && container.Geometry.ContentBox.Width > 0)
            {
                foreach (var item in items)
                {
                    var flexGrow = ResolveFlexGrow(item.ComputedStyle);
                    if (flexGrow.HasValue && flexGrow.Value > 0 &&
                        item.Geometry.MarginBox.Width > container.Geometry.ContentBox.Width)
                    {
                        float targetWidth = container.Geometry.ContentBox.Width;
                        LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, item.Geometry.ContentBox.Height);

                        var reState = state.Clone();
                        reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                        reState.ContainingBlockWidth = targetWidth;
                        reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                        FormattingContext.Resolve(item).Layout(item, reState);
                    }
                }
            }

            // 5. Resolving Flex Factors (Grow/Shrink) - Simplified: Only support FlexGrow for now
            // Calculate remaining space
            float totalMainSize = 0;
            float totalFlexGrow = 0;
            float totalWeightedShrink = 0; // For flex-shrink calculation

            foreach (var item in items)
            {
                float itemMainSize = isRow ? item.Geometry.MarginBox.Width : GetColumnMainSize(item);
                totalMainSize += itemMainSize;

                var flexGrow = ResolveFlexGrow(item.ComputedStyle);
                if (flexGrow.HasValue)
                    totalFlexGrow += (float)flexGrow.Value;
                
                float shrink = (float)(ResolveFlexShrink(item.ComputedStyle) ?? 1);
                if (shrink > 0)
                {
                    float basis = isRow ? item.Geometry.MarginBox.Width : GetColumnMainSize(item);
                    totalWeightedShrink += shrink * basis;
                }
            }

            // If a flex item still has 0 width but declares flex, assign it the remaining space.
            if (isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    if (item.Geometry.ContentBox.Width > 0) continue;
                    var itemStyle = item.ComputedStyle;
                    bool hasFlex = ResolveFlexGrow(itemStyle).GetValueOrDefault() > 0 ||
                                   (itemStyle?.Map != null && (itemStyle.Map.ContainsKey("flex") || itemStyle.Map.ContainsKey("flex-grow")));
                    if (!hasFlex) continue;

                    float currentItemSize = item.Geometry.MarginBox.Width;
                    float remainingForItem = containerMainSize - (totalMainSize - currentItemSize);
                    if (remainingForItem <= 0) continue;

                    LayoutBoxOps.ComputeBoxModelFromContent(item, remainingForItem, item.Geometry.ContentBox.Height);
                    var reState = state.Clone();
                    reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                    reState.ContainingBlockWidth = remainingForItem;
                    reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                    FormattingContext.Resolve(item).Layout(item, reState);

                    // Update totals to reflect the assigned space
                    totalMainSize = totalMainSize - currentItemSize + item.Geometry.MarginBox.Width;
                }
            }

            if (shrinkToContentMainAxis)
            {
                // Only expand to content size if the container has no usable resolved width.
                // When ResolveContainerDimensions already resolved a finite width (e.g. from
                // percentage or explicit CSS), we must respect it so flex-shrink can operate.
                if (containerMainSize <= 0 || float.IsNaN(containerMainSize) || float.IsInfinity(containerMainSize))
                {
                    containerMainSize = totalMainSize;
                }
            }

            float remainingSpace = containerMainSize - totalMainSize;

            // Handle Flex-Shrink (when items overflow the container)
            if (remainingSpace < 0 && totalWeightedShrink > 0)
            {
                float overflow = -remainingSpace; // Positive value representing how much to shrink
                foreach (var item in items)
                {
                    float shrink = (float)(item.ComputedStyle?.FlexShrink ?? 1);
                    float basis = isRow ? item.Geometry.MarginBox.Width : GetColumnMainSize(item);
                    float weight = shrink * basis;
                    
                    // DEFENSIVE: Use IsSafeDivision and SafeDivide to prevent NaN/Infinity
                    if (LayoutValidator.IsSafeDivision(totalWeightedShrink) && weight > 0)
                    {
                        float ratio = LayoutValidator.SafeDivide(weight, totalWeightedShrink, 0f, "FlexShrink ratio");
                        float shrinkAmount = overflow * ratio;

                        if (isRow)
                        {
                            float newWidth = Math.Max(0, item.Geometry.ContentBox.Width - shrinkAmount);
                            LayoutBoxOps.ComputeBoxModelFromContent(item, newWidth, item.Geometry.ContentBox.Height);

                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                            reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                            reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                            FormattingContext.Resolve(item).Layout(item, reState);
                        }
                        else // Column
                        {
                            float newHeight = Math.Max(0, item.Geometry.ContentBox.Height - shrinkAmount);
                            LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, newHeight);

                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, newHeight);
                            reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                            reState.ContainingBlockHeight = newHeight;
                            LayoutWithForcedHeight(item, reState, newHeight);
                        }
                    }
                }
                // Recalculate total after shrink
                totalMainSize = containerMainSize; 
            }
            else if (remainingSpace > 0 && totalFlexGrow > 0)
            {
                // Distribute space
                foreach (var item in items)
                {
                    float grow = (float)(ResolveFlexGrow(item.ComputedStyle) ?? 0);
                    if (grow > 0)
                    {
                        // DEFENSIVE: Use Safe Divide to prevent NaN/Infinity
                        float share = remainingSpace * LayoutValidator.SafeDivide(grow, totalFlexGrow, 0f, "FlexGrow ratio");
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

                             // CRITICAL: Re-layout child so its internal content (IFC/BFC) knows about the new width
                             var reState = state.Clone();
                              reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                             reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                             reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                             FormattingContext.Resolve(item).Layout(item, reState);
                        }
                        else
                        {
                            // Column grow (height)
                             float newHeight = item.Geometry.ContentBox.Height + share;
                             LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, newHeight);

                             // Re-layout child
                             var reState = state.Clone();
                              reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, newHeight);
                             reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                             reState.ContainingBlockHeight = newHeight;
                             LayoutWithForcedHeight(item, reState, newHeight);
                        }
                    }
                }
                
                // Recalculate total after grow
                totalMainSize = containerMainSize; 
            }
            
            // Guard: flex-grow items should not collapse to 0 on the main axis.
            if (isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    var flexGrow = ResolveFlexGrow(item.ComputedStyle);
                    if (flexGrow.HasValue && flexGrow.Value > 0 && item.Geometry.ContentBox.Width <= 0)
                    {
                        float newWidth = Math.Max(0, containerMainSize - (totalMainSize - item.Geometry.MarginBox.Width));
                        LayoutBoxOps.ComputeBoxModelFromContent(item, newWidth, item.Geometry.ContentBox.Height);

                        var reState = state.Clone();
                        reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                        reState.ContainingBlockWidth = newWidth;
                        reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                        FormattingContext.Resolve(item).Layout(item, reState);
                    }
                }
            }

            // Final safety: if any row flex item collapsed to 0 width, recover using child content bounds
            // but never exceed the container's main size.
            if (isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    if (item.Geometry.ContentBox.Width > 0) continue;

                    float childMax = 0f;
                    foreach (var child in item.Children)
                    {
                        childMax = Math.Max(childMax, child.Geometry.MarginBox.Width);
                    }

                    float targetWidth = childMax > 0
                        ? Math.Min(containerMainSize, childMax)
                        : Math.Max(0, containerMainSize - (totalMainSize - item.Geometry.MarginBox.Width));

                    if (targetWidth <= 0) continue;

                    LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, item.Geometry.ContentBox.Height);

                    var reState = state.Clone();
                    reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                    reState.ContainingBlockWidth = targetWidth;
                    reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                    FormattingContext.Resolve(item).Layout(item, reState);
                }
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
            float startOffset = 0;
            float itemStepExtra = 0;

            if (shrinkToContentMainAxis)
            {
                // Only shrink-wrap to content if no finite size was resolved.
                if (containerMainSize <= 0 || float.IsNaN(containerMainSize) || float.IsInfinity(containerMainSize))
                {
                    containerMainSize = totalMainSize;
                    if (isRow)
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(container, containerMainSize, container.Geometry.ContentBox.Height);
                    }
                    else
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, containerMainSize);
                    }
                }
            }

            remainingSpace = containerMainSize - totalMainSize;

            // Auto margins on the main axis absorb remaining space (e.g., footer push-down).
            int autoMarginCount = 0;
            var autoMarginBefore = new Dictionary<LayoutBox, bool>();
            var autoMarginAfter = new Dictionary<LayoutBox, bool>();
            foreach (var item in items)
            {
                var itemStyle = item.ComputedStyle;
                bool beforeAuto = isRow
                    ? IsMarginAuto(itemStyle, "left")
                    : IsMarginAuto(itemStyle, "top");
                bool afterAuto = isRow
                    ? IsMarginAuto(itemStyle, "right")
                    : IsMarginAuto(itemStyle, "bottom");

                autoMarginBefore[item] = beforeAuto;
                autoMarginAfter[item] = afterAuto;

                if (beforeAuto) autoMarginCount++;
                if (afterAuto) autoMarginCount++;
            }

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

            if (autoMarginCount > 0 && remainingSpace > 0)
            {
                // Auto margins take precedence over justify-content spacing.
                startOffset = 0;
                itemStepExtra = 0;
            }

            currentMainPos = startOffset;

            // 7. Cross Axis Alignment (Align Items)
            float maxCrossSize = 0;
            foreach (var item in items)
            {
                float itemCrossSize = isRow ? item.Geometry.MarginBox.Height : item.Geometry.MarginBox.Width;
                maxCrossSize = Math.Max(maxCrossSize, itemCrossSize);
            }
            
            // DEFENSIVE: If maxCrossSize is zero, apply content-based fallback
            if (maxCrossSize == 0 && items.Count > 0)
            {
                maxCrossSize = ComputeFallbackCrossSize(items, isRow, container);
            }

            float totalGap = gap * Math.Max(0, items.Count - 1);

            // If auto-height container, expand it to wrap lines (single line here)
            if (isRow && container.Geometry.ContentBox.Height <= 0) // Auto height or zeroed
            {
                // Correct sum for vertical stacking in row? No, in row height is max child height.
                container.Geometry.ContentBox = new SKRect(0, 0, container.Geometry.ContentBox.Width, maxCrossSize);
                LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, maxCrossSize);
                containerCrossSize = maxCrossSize;
            }
            else if (!isRow && container.Geometry.ContentBox.Height <= 0) // Auto height Column
            {
                float totalColumnHeight = items.Sum(i => GetColumnMainSize(i)) + totalGap;
                container.Geometry.ContentBox = new SKRect(0, 0, container.Geometry.ContentBox.Width, totalColumnHeight);
                LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, totalColumnHeight);
                containerMainSize = totalColumnHeight;
            }

            // FIX: Apply align-items: stretch - expand flex items to fill cross axis
            // This ensures that text content inside stretched items has the correct parent box height
            // NOTE: Use maxCrossSize (calculated from actual item heights) since containerCrossSize
            // is captured before auto-height adjustment and may be 0 for auto-height containers.
            
            // DEBUG: Log stretch pre-conditions
            FenBrowser.Core.FenLogger.Info($"[FLEX-STRETCH-CHECK] alignItems='{alignItems}' maxCrossSize={maxCrossSize:F1} itemCount={items.Count} isRow={isRow}", FenBrowser.Core.Logging.LogCategory.Layout);
            
            if (alignItems == "stretch" && maxCrossSize > 0)
            {
                foreach (var item in items)
                {
                    float itemCrossSize = isRow ? item.Geometry.MarginBox.Height : item.Geometry.MarginBox.Width;
                    float targetCrossSize = maxCrossSize; // Use maxCrossSize, not containerCrossSize
                    
                    // Only stretch if item is smaller than the tallest item
                    if (itemCrossSize < targetCrossSize)
                    {
                        // Calculate new content box dimensions (subtract margins)
                        var margin = item.ComputedStyle?.Margin ?? new FenBrowser.Core.Thickness();
                        float marginCross = isRow ? (float)(margin.Top + margin.Bottom) : (float)(margin.Left + margin.Right);
                        float newContentCross = Math.Max(0, targetCrossSize - marginCross);
                        
                        if (isRow)
                        {
                            // DEBUG: Log stretch attempt
                            FenBrowser.Core.FenLogger.Info($"[FLEX-STRETCH] Row item stretch: currentH={item.Geometry.ContentBox.Height:F1} targetH={newContentCross:F1} maxCrossSize={maxCrossSize:F1}", FenBrowser.Core.Logging.LogCategory.Layout);
                            
                            // Stretch height
                            LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, newContentCross);
                            
                            // Re-layout children with new height so text knows about the stretched box
                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, newContentCross);
                            reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                            reState.ContainingBlockHeight = newContentCross;
                            FormattingContext.Resolve(item).Layout(item, reState);
                        }
                        else
                        {
                            // Stretch width
                            LayoutBoxOps.ComputeBoxModelFromContent(item, newContentCross, item.Geometry.ContentBox.Height);
                            
                            // Re-layout children with new width
                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                            reState.ContainingBlockWidth = newContentCross;
                            reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                            FormattingContext.Resolve(item).Layout(item, reState);
                        }
                    }
                }
            }

            // Ensure containerCrossSize reflects actual item dimensions after all adjustments.
            // After grow/shrink/stretch, items may have changed size but containerCrossSize
            // could still be 0 for auto-height containers, breaking cross-axis alignment.
            if (containerCrossSize <= 0)
            {
                float recalcMax = 0;
                foreach (var item in items)
                {
                    float itemCross = isRow ? item.Geometry.MarginBox.Height : item.Geometry.MarginBox.Width;
                    recalcMax = Math.Max(recalcMax, itemCross);
                }
                containerCrossSize = recalcMax;
            }

            foreach (var item in items)
            {
                // Position Main Axis
                float x = 0, y = 0;
                float autoBefore = 0f;
                float autoAfter = 0f;
                if (autoMarginCount > 0 && remainingSpace > 0)
                {
                    float share = remainingSpace / autoMarginCount;
                    if (autoMarginBefore.TryGetValue(item, out var beforeAuto) && beforeAuto)
                    {
                        autoBefore = share;
                    }
                    if (autoMarginAfter.TryGetValue(item, out var afterAuto) && afterAuto)
                    {
                        autoAfter = share;
                    }
                }

                if (isRow)
                {
                    currentMainPos += autoBefore;
                    x = currentMainPos;
                    // Cross Axis
                    float crossFree = containerCrossSize - item.Geometry.MarginBox.Height;
                    if (alignItems == "center") y = crossFree / 2;
                    else if (alignItems == "flex-end") y = crossFree;
                    else y = 0; // flex-start / stretch
                    
                    // Increment
                    currentMainPos += item.Geometry.MarginBox.Width + gap + itemStepExtra + autoAfter;
                }
                else // Column
                {
                    var colMain = GetColumnMainSize(item);
                    currentMainPos += autoBefore;
                    y = currentMainPos;
                    // Cross Axis
                    float crossFree = containerCrossSize - item.Geometry.MarginBox.Width;
                    if (alignItems == "center") x = crossFree / 2;
                    else if (alignItems == "flex-end") x = crossFree;
                    else x = 0;

                    currentMainPos += colMain + gap + itemStepExtra + autoAfter;
                }

                // Set absolute offset relative to container content box
                // NOTE: LayoutBox geometry is relative to parent Content Box
                LayoutBoxOps.SetPosition(item, x, y);
            }

            // Position absolutely/fixed positioned descendants relative to this flex container.
            foreach (var oof in outOfFlow)
            {
                var context = FormattingContext.Resolve(oof);

                var intrinsicState = state.Clone();
                intrinsicState.AvailableSize = new SKSize(float.PositiveInfinity, float.PositiveInfinity);
                intrinsicState.ContainingBlockWidth = container.Geometry.ContentBox.Width;
                intrinsicState.ContainingBlockHeight = container.Geometry.ContentBox.Height;
                context.Layout(oof, intrinsicState);

                LayoutPositioningLogic.ResolvePositionedBox(oof, container, container.Geometry);

                float resolvedWidth = Math.Max(0f, oof.Geometry.ContentBox.Width);
                float resolvedHeight = Math.Max(0f, oof.Geometry.ContentBox.Height);
                var childState = state.Clone();
                childState.AvailableSize = new SKSize(resolvedWidth, resolvedHeight);
                childState.ContainingBlockWidth = resolvedWidth;
                childState.ContainingBlockHeight = resolvedHeight;
                context.Layout(oof, childState);

                LayoutPositioningLogic.ResolvePositionedBox(oof, container, container.Geometry);
            }
        }

        private void ResolveContainerDimensions(LayoutBox box, LayoutState state)
        {
            // Similar to ResolveWidth/Height in BlockContext
            float rawAvailable = state.AvailableSize.Width;
            bool widthUnconstrained = float.IsInfinity(rawAvailable) || float.IsNaN(rawAvailable);
            float available = widthUnconstrained
                ? (state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth)
                : rawAvailable;
            if (float.IsInfinity(available) || float.IsNaN(available) || available <= 0)
                available = 1920f;
            
            var padding = box.ComputedStyle?.Padding ?? new FenBrowser.Core.Thickness();
            var border = box.ComputedStyle?.BorderThickness ?? new FenBrowser.Core.Thickness();
            var margin = box.ComputedStyle?.Margin ?? new FenBrowser.Core.Thickness();

            box.Geometry.Padding = padding;
            box.Geometry.Border = border;
            box.Geometry.Margin = margin;

            // Width
            float width = 0;
            if (box.ComputedStyle?.Width.HasValue == true) width = (float)box.ComputedStyle.Width.Value;
            else if (box.ComputedStyle?.WidthPercent.HasValue == true)
            {
                float parentWidth = state.AvailableSize.Width;
                if (float.IsInfinity(parentWidth) || parentWidth <= 0)
                    parentWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                if (!float.IsInfinity(parentWidth) && parentWidth > 0)
                    width = (float)(box.ComputedStyle.WidthPercent.Value / 100.0 * parentWidth);
            }
            else if (widthUnconstrained)
            {
                // Even in unconstrained contexts, flex items should honor their containing block width
                // when available (e.g., flex:1 items inside a fixed-width flex row).
                width = Math.Max(0, available - (float)(margin.Horizontal + border.Horizontal + padding.Horizontal));
            }
            else width = Math.Max(0, rawAvailable - (float)(margin.Horizontal + border.Horizontal + padding.Horizontal));

            // Height
            float height = 0;
            if (box.ComputedStyle?.Height.HasValue == true) 
            {
                height = (float)box.ComputedStyle.Height.Value;
            }
            else if (box.ComputedStyle?.HeightPercent.HasValue == true)
            {
                float parentHeight = state.AvailableSize.Height;
                if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                    parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                    height = (float)(box.ComputedStyle.HeightPercent.Value / 100.0 * parentHeight);
            }
            else if (box.ComputedStyle?.LineHeight.HasValue == true && box.ComputedStyle.LineHeight.Value > 0)
            {
                // Many icon wrappers rely on line-height when height is auto.
                height = (float)box.ComputedStyle.LineHeight.Value;
            }
            else if (!string.IsNullOrEmpty(box.ComputedStyle?.HeightExpression))
            {
                float parentHeight = state.AvailableSize.Height;
                if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                    parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                height = LayoutHelper.EvaluateCssExpression(
                    box.ComputedStyle.HeightExpression,
                    parentHeight,
                    state.ViewportWidth,
                    state.ViewportHeight);
            }

            // Apply min/max constraints (including % and expression forms)
            float minW = 0f;
            float maxW = float.PositiveInfinity;
            float minH = 0f;
            float maxH = float.PositiveInfinity;
            if (box.ComputedStyle != null)
            {
                if (box.ComputedStyle.MinWidth.HasValue) minW = (float)box.ComputedStyle.MinWidth.Value;
                else if (box.ComputedStyle.MinWidthPercent.HasValue == true)
                {
                    float parentWidth = state.AvailableSize.Width;
                    if (float.IsInfinity(parentWidth) || parentWidth <= 0) parentWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                    if (parentWidth > 0) minW = (float)(box.ComputedStyle.MinWidthPercent.Value / 100.0 * parentWidth);
                }
                else if (!string.IsNullOrEmpty(box.ComputedStyle.MinWidthExpression))
                {
                    float parentWidth = state.AvailableSize.Width;
                    if (float.IsInfinity(parentWidth) || parentWidth <= 0) parentWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                    minW = LayoutHelper.EvaluateCssExpression(box.ComputedStyle.MinWidthExpression, parentWidth, state.ViewportWidth, state.ViewportHeight);
                }

                if (box.ComputedStyle.MaxWidth.HasValue) maxW = (float)box.ComputedStyle.MaxWidth.Value;
                else if (box.ComputedStyle.MaxWidthPercent.HasValue == true)
                {
                    float parentWidth = state.AvailableSize.Width;
                    if (float.IsInfinity(parentWidth) || parentWidth <= 0) parentWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                    if (parentWidth > 0) maxW = (float)(box.ComputedStyle.MaxWidthPercent.Value / 100.0 * parentWidth);
                }
                else if (!string.IsNullOrEmpty(box.ComputedStyle.MaxWidthExpression))
                {
                    float parentWidth = state.AvailableSize.Width;
                    if (float.IsInfinity(parentWidth) || parentWidth <= 0) parentWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                    maxW = LayoutHelper.EvaluateCssExpression(box.ComputedStyle.MaxWidthExpression, parentWidth, state.ViewportWidth, state.ViewportHeight);
                }

                if (box.ComputedStyle.MinHeight.HasValue) minH = (float)box.ComputedStyle.MinHeight.Value;
                else if (box.ComputedStyle.MinHeightPercent.HasValue == true)
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0) parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                    if (parentHeight > 0) minH = (float)(box.ComputedStyle.MinHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(box.ComputedStyle.MinHeightExpression))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0) parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                    minH = LayoutHelper.EvaluateCssExpression(box.ComputedStyle.MinHeightExpression, parentHeight, state.ViewportWidth, state.ViewportHeight);
                }
                else if (box.ComputedStyle.Map != null &&
                         box.ComputedStyle.Map.TryGetValue("min-height", out var rawMinHeight) &&
                         !string.IsNullOrWhiteSpace(rawMinHeight))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;

                    var raw = rawMinHeight.Trim();
                    if (raw.EndsWith("%", StringComparison.Ordinal))
                    {
                        if (float.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) &&
                            parentHeight > 0)
                        {
                            minH = (pct / 100f) * parentHeight;
                        }
                    }
                    else if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(raw.Substring(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                        {
                            minH = px;
                        }
                    }
                    else
                    {
                        minH = LayoutHelper.EvaluateCssExpression(raw, parentHeight, state.ViewportWidth, state.ViewportHeight);
                    }
                }

                if (box.ComputedStyle.MaxHeight.HasValue) maxH = (float)box.ComputedStyle.MaxHeight.Value;
                else if (box.ComputedStyle.MaxHeightPercent.HasValue == true)
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0) parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                    if (parentHeight > 0) maxH = (float)(box.ComputedStyle.MaxHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(box.ComputedStyle.MaxHeightExpression))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0) parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                    maxH = LayoutHelper.EvaluateCssExpression(box.ComputedStyle.MaxHeightExpression, parentHeight, state.ViewportWidth, state.ViewportHeight);
                }
            }

            if (width < minW) width = minW;
            if (width > maxW && float.IsFinite(maxW)) width = maxW;
            if (height < minH) height = minH;
            if (height > maxH && float.IsFinite(maxH)) height = maxH;

            // Fallback for Root/Body if height is still 0 (implies auto/full-screen)
            // If it's body and display is flex/grid, we often want full viewport if not specified
            bool isRoot = (box.SourceNode as Element)?.TagName == "BODY" || (box.SourceNode as Element)?.TagName == "HTML";
            if (height <= 0 && isRoot)
            {
                height = state.ViewportHeight;
            }

            // Intrinsic fallback for controls/replaced boxes in flex layout.
            if (box.SourceNode is Element el)
            {
                string tag = el.TagName?.ToUpperInvariant() ?? "";

                if (width <= 0)
                {
                    if (tag == "INPUT")
                    {
                        string type = (el.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
                        if (type == "submit" || type == "button" || type == "reset")
                        {
                            string label = el.GetAttribute("value");
                            if (string.IsNullOrWhiteSpace(label)) label = "Button";
                            width = Math.Max(54f, 16f + (label.Length * 7f));
                        }
                        else
                        {
                            width = 150f;
                        }
                    }
                    else if (tag == "BUTTON")
                    {
                        string label = el.TextContent?.Trim();
                        if (string.IsNullOrWhiteSpace(label)) label = "Button";
                        width = Math.Max(54f, 16f + (label.Length * 7f));
                    }
                    else if (tag == "SELECT")
                    {
                        width = 120f;
                    }
                    else if (tag == "TEXTAREA")
                    {
                        width = 200f;
                    }
                    else if (tag == "SVG" || tag == "CANVAS")
                    {
                        if (!TryGetLengthAttribute(el, "width", out width) || width <= 0) width = 24f;
                    }
                    else if (tag == "IMG")
                    {
                        if (!TryGetLengthAttribute(el, "width", out width) || width <= 0) width = 300f;
                    }
                }

                if (height <= 0)
                {
                    if (tag == "INPUT")
                    {
                        height = 24f;
                    }
                    else if (tag == "BUTTON")
                    {
                        height = 36f;
                    }
                    else if (tag == "SELECT")
                    {
                        height = 24f;
                    }
                    else if (tag == "TEXTAREA")
                    {
                        height = 48f;
                    }
                    else if (tag == "SVG" || tag == "CANVAS")
                    {
                        if (!TryGetLengthAttribute(el, "height", out height) || height <= 0) height = 24f;
                    }
                    else if (tag == "IMG")
                    {
                        if (!TryGetLengthAttribute(el, "height", out height) || height <= 0) height = 150f;
                    }
                }
            }

            // Icon wrappers in Google-like UIs are often flex boxes with one replaced child.
            // If CSS parsing misses class-applied heights, avoid collapsing these to 0x0.
            if ((width <= 0 || height <= 0) &&
                string.Equals(box.ComputedStyle?.Display, "flex", StringComparison.OrdinalIgnoreCase) &&
                box.Children.Count == 1 &&
                box.Children[0].SourceNode is Element iconChild)
            {
                string childTag = iconChild.TagName?.ToUpperInvariant() ?? string.Empty;
                if (childTag == "SVG" || childTag == "IMG" || childTag == "CANVAS")
                {
                    if (width <= 0) width = 24f;
                    if (height <= 0) height = 24f;
                }
            }
            
            box.Geometry.ContentBox = new SKRect(0, 0, width, height);
            
            // Sync boxes (Content -> Padding -> Border -> Margin)
            LayoutBoxOps.ComputeBoxModelFromContent(box, width, height);
        }

        private static float GetColumnMainSize(LayoutBox item)
        {
            if (item == null) return 0f;

            float contentHeight = Math.Max(0f, item.Geometry.ContentBox.Height);
            var margin = item.ComputedStyle?.Margin ?? new FenBrowser.Core.Thickness();
            float marginTop = float.IsFinite((float)margin.Top) ? (float)margin.Top : 0f;
            float marginBottom = float.IsFinite((float)margin.Bottom) ? (float)margin.Bottom : 0f;
            float expected = Math.Max(0f, contentHeight + marginTop + marginBottom);

            float marginBoxHeight = item.Geometry.MarginBox.Height;
            if (!float.IsFinite(marginBoxHeight) || marginBoxHeight <= 0)
            {
                return expected;
            }

            // Guard against stale/over-inflated margin-box heights in column placement.
            if (expected > 0 && marginBoxHeight > expected * 2.5f)
            {
                return expected;
            }

            return marginBoxHeight;
        }

        private static bool TryGetLengthAttribute(Element element, string attributeName, out float value)
        {
            value = 0f;
            string raw = element.GetAttribute(attributeName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();
            int numericChars = 0;
            while (numericChars < raw.Length)
            {
                char ch = raw[numericChars];
                if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-')
                {
                    numericChars++;
                    continue;
                }

                break;
            }

            if (numericChars == 0)
            {
                return false;
            }

            string numeric = raw.Substring(0, numericChars);
            if (!float.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            value = Math.Max(0f, value);
            return true;
        }
        
        private float ComputeFallbackCrossSize(List<LayoutBox> items, bool isRow, LayoutBox container)
        {
            float fallback = 0f;
            
            foreach (var item in items)
            {
                if (item.SourceNode is Element elem)
                {
                    // Check for text content
                    string text = elem.TextContent?.Trim() ?? "";
                    if (text.Length > 0)
                    {
                        // Estimate from line-height (default 21px for typical browser default)
                        string[] lines = text.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
                        int lineCount = Math.Max(1, lines.Length);
                        float estimatedHeight = 21.0f * lineCount;
                        fallback = Math.Max(fallback, estimatedHeight);
                    }
                    // Check for child elements (even if zero-height now)
                    else if (elem.Children.Length > 0)
                    {
                        fallback = Math.Max(fallback, 1.0f); // Minimum to prevent total collapse
                    }
                }
            }
            
            return fallback;
        }

        private static void ApplyCollapsedFlexItemFallback(LayoutBox item)
        {
            if (item?.Geometry == null) return;

            float currentW = item.Geometry.ContentBox.Width;
            float currentH = item.Geometry.ContentBox.Height;
            if (currentW > 0f && currentH > 0f) return;

            float fallbackW = currentW;
            float fallbackH = currentH;
            var style = item.ComputedStyle;

            if (fallbackW <= 0f && style?.Width.HasValue == true) fallbackW = (float)style.Width.Value;
            if (fallbackH <= 0f && style?.Height.HasValue == true) fallbackH = (float)style.Height.Value;
            if (fallbackH <= 0f && style?.LineHeight.HasValue == true && style.LineHeight.Value > 0)
            {
                fallbackH = (float)style.LineHeight.Value;
            }

            if (item.SourceNode is Element el)
            {
                string tag = el.TagName?.ToUpperInvariant() ?? string.Empty;
                if (fallbackW <= 0f)
                {
                    if (tag == "SVG" || tag == "CANVAS") fallbackW = 24f;
                    else if (tag == "IMG") fallbackW = 300f;
                    else if (tag == "INPUT") fallbackW = 150f;
                    else if (tag == "BUTTON") fallbackW = 60f;
                }

                if (fallbackH <= 0f)
                {
                    if (tag == "SVG" || tag == "CANVAS") fallbackH = 24f;
                    else if (tag == "IMG") fallbackH = 150f;
                    else if (tag == "INPUT" || tag == "SELECT") fallbackH = 24f;
                    else if (tag == "BUTTON") fallbackH = 36f;
                }
            }

            if (item.Children.Count == 1 && item.Children[0].SourceNode is Element childEl)
            {
                string childTag = childEl.TagName?.ToUpperInvariant() ?? string.Empty;
                if (childTag == "SVG" || childTag == "IMG" || childTag == "CANVAS")
                {
                    if (fallbackW <= 0f) fallbackW = 24f;
                    if (fallbackH <= 0f) fallbackH = 24f;
                }
            }

            if (fallbackW <= 0f) fallbackW = 1f;
            if (fallbackH <= 0f) fallbackH = 1f;

            LayoutBoxOps.ComputeBoxModelFromContent(item, fallbackW, fallbackH);
        }

        private static void LayoutWithForcedHeight(LayoutBox item, LayoutState state, float forcedHeight)
        {
            if (item == null)
            {
                return;
            }

            var style = item.ComputedStyle;
            if (style == null)
            {
                FormattingContext.Resolve(item).Layout(item, state);
                return;
            }

            bool hasExplicitHeight = style.Height.HasValue ||
                                     style.HeightPercent.HasValue ||
                                     !string.IsNullOrEmpty(style.HeightExpression);

            if (hasExplicitHeight)
            {
                FormattingContext.Resolve(item).Layout(item, state);
                return;
            }

            var oldHeight = style.Height;
            var oldHeightPercent = style.HeightPercent;
            var oldHeightExpression = style.HeightExpression;

            style.Height = Math.Max(0, forcedHeight);
            style.HeightPercent = null;
            style.HeightExpression = null;

            FormattingContext.Resolve(item).Layout(item, state);

            style.Height = oldHeight;
            style.HeightPercent = oldHeightPercent;
            style.HeightExpression = oldHeightExpression;
        }

        private static bool IsMarginAuto(CssComputed style, string side)
        {
            if (style?.Map == null) return false;

            if (style.Map.TryGetValue($"margin-{side}", out var value) &&
                string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (style.Map.TryGetValue("margin", out var shorthand) &&
                !string.IsNullOrWhiteSpace(shorthand) &&
                shorthand.IndexOf("auto", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static bool IsIgnorableFlexItem(LayoutBox box)
        {
            if (box == null) return true;
            if (box is TextLayoutBox textBox)
            {
                return string.IsNullOrWhiteSpace(textBox.TextContent);
            }
            if (box.SourceNode is Text textNode)
            {
                return string.IsNullOrWhiteSpace(textNode.Data);
            }

            // Mixed block/inline containers can create anonymous wrappers around
            // whitespace-only text runs. These must not become flex items.
            if (box.IsAnonymous && box.Children.Count > 0)
            {
                return box.Children.All(IsIgnorableFlexDescendant);
            }

            return false;
        }

        private static bool IsIgnorableFlexDescendant(LayoutBox box)
        {
            if (box == null) return true;
            if (box is TextLayoutBox textBox)
            {
                return string.IsNullOrWhiteSpace(textBox.TextContent);
            }
            if (box.SourceNode is Text textNode)
            {
                return string.IsNullOrWhiteSpace(textNode.Data);
            }
            if (box.SourceNode is Element)
            {
                return false;
            }
            if (box.Children.Count == 0)
            {
                return true;
            }
            return box.Children.All(IsIgnorableFlexDescendant);
        }

        private static double? ResolveFlexGrow(CssComputed style)
        {
            if (style == null) return null;
            if (style.FlexGrow.HasValue) return style.FlexGrow.Value;
            if (style.Map != null)
            {
                if (style.Map.TryGetValue("flex-grow", out var fg) && double.TryParse(fg, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return v;
                if (style.Map.TryGetValue("flex", out var flex) && TryParseFlexShorthandFirst(flex, out var flexGrow))
                    return flexGrow;
            }
            return null;
        }

        private static double? ResolveFlexShrink(CssComputed style)
        {
            if (style == null) return null;
            if (style.FlexShrink.HasValue) return style.FlexShrink.Value;
            if (style.Map != null)
            {
                if (style.Map.TryGetValue("flex-shrink", out var fs) && double.TryParse(fs, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return v;
                if (style.Map.TryGetValue("flex", out var flex) && TryParseFlexShorthandSecond(flex, out var flexShrink))
                    return flexShrink;
            }
            return null;
        }

        private static bool TryParseFlexShorthandFirst(string flex, out double flexGrow)
        {
            flexGrow = 0;
            if (string.IsNullOrWhiteSpace(flex)) return false;
            var parts = flex.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out flexGrow);
        }

        private static bool TryParseFlexShorthandSecond(string flex, out double flexShrink)
        {
            flexShrink = 0;
            if (string.IsNullOrWhiteSpace(flex)) return false;
            var parts = flex.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out flexShrink);
        }
    }
    
    // Helper to avoid code duplication with BlockContext for box sizing
    // LayoutBoxOps moved to FenBrowser.FenEngine.Layout.Contexts.LayoutBoxOps.cs
}


