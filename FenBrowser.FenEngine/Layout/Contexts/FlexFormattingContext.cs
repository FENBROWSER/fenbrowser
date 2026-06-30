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
            // EngineLogCompat.Debug($"[FLEX-CTX] Layout called for {box.SourceNode?.NodeName} ({(box.SourceNode as Element)?.TagName})");
            var container = box;

            // Flex items are frequently relaid out during probing/stretching. Reset subtree
            // coordinates before each pass so descendants don't retain stale absolute offsets.
            LayoutBoxOps.ResetSubtreeToOrigin(container);

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
            bool isReverse = direction.Contains("reverse");
            bool isWrap = IsWrapEnabled(style);
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
            bool heightPercentHasDefiniteBasis =
                style?.HeightPercent.HasValue == true &&
                HasDefinitePercentageHeightBasis(container, state);
            bool rawHeightIsPercentage =
                style?.Map != null &&
                style.Map.TryGetValue("height", out var rawHeight) &&
                IsPercentageHeight(rawHeight);
            bool hasHeightMapExplicit =
                style?.Map != null &&
                style.Map.ContainsKey("height") &&
                !rawHeightIsPercentage &&
                (style.HeightPercent.HasValue != true || heightPercentHasDefiniteBasis);
            bool hasExplicitHeightForFlexSizing =
                style?.Height.HasValue == true ||
                heightPercentHasDefiniteBasis ||
                !string.IsNullOrEmpty(style?.HeightExpression) ||
                hasHeightMapExplicit;
            bool mainAxisUnconstrained = isRow
                ? (float.IsInfinity(state.AvailableSize.Width) || float.IsNaN(state.AvailableSize.Width))
                : (float.IsInfinity(state.AvailableSize.Height) || float.IsNaN(state.AvailableSize.Height));
            bool hasExplicitMainSize = isRow
                ? (style?.Width.HasValue == true || style?.WidthPercent.HasValue == true || !string.IsNullOrEmpty(style?.WidthExpression))
                : hasExplicitHeightForFlexSizing;
            bool hasExplicitCrossSize = isRow
                ? hasExplicitHeightForFlexSizing
                : (style?.Width.HasValue == true ||
                   style?.WidthPercent.HasValue == true ||
                   !string.IsNullOrEmpty(style?.WidthExpression) ||
                   (style?.Map != null && style.Map.ContainsKey("width")));
            bool shrinkToContentMainAxis = !hasExplicitMainSize && (!isRow || mainAxisUnconstrained);

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

            // Sort items by CSS order property (default 0). LINQ OrderBy is stable,
            // preserving DOM order for items with the same order value.
            if (items.Any(it => (it.ComputedStyle?.Order ?? 0) != 0))
            {
                items = items.OrderBy(it => it.ComputedStyle?.Order ?? 0).ToList();
            }

            // 3. Main/Cross Axis setup
            float containerMainSize = isRow ? container.Geometry.ContentBox.Width : container.Geometry.ContentBox.Height;
            float containerCrossSize = isRow ? container.Geometry.ContentBox.Height : container.Geometry.ContentBox.Width;
            
            // Robustness: Handle unconstrained sizes
            if (float.IsInfinity(containerMainSize)) containerMainSize = isRow ? state.ViewportWidth : 1000f;
            if (float.IsInfinity(containerCrossSize)) containerCrossSize = isRow ? 1000f : state.ViewportWidth;
            if (float.IsNaN(containerMainSize)) containerMainSize = 0;
            if (float.IsNaN(containerCrossSize)) containerCrossSize = 0;

            // Clamp auto/unspecified widths to the viewport to avoid runaway inline sizes
            // that push flex-end content off-screen in dense action clusters.
            bool hasExplicitMain =
                isRow
                    ? (style?.Width.HasValue == true || style?.WidthPercent.HasValue == true || !string.IsNullOrEmpty(style?.WidthExpression))
                    : hasExplicitHeightForFlexSizing;
            
            if (!hasExplicitMain)
            {
                // Apply min-width / min-height if present in Map for auto sizing
                if (style?.Map != null)
                {
                    string minKey = isRow ? "min-width" : "min-height";
                    if (style.Map.TryGetValue(minKey, out var rawMin) && !string.IsNullOrWhiteSpace(rawMin))
                    {
                        var raw = rawMin.Trim();
                        float parentSize = isRow ? state.ViewportWidth : state.ViewportHeight;
                        
                        if (raw.EndsWith("%", StringComparison.Ordinal) && float.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        {
                            containerMainSize = Math.Max(containerMainSize, (pct / 100f) * parentSize);
                        }
                        else if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase) && float.TryParse(raw.Substring(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                        {
                            containerMainSize = Math.Max(containerMainSize, px);
                        }
                        else if (raw.EndsWith("vh", StringComparison.OrdinalIgnoreCase) && float.TryParse(raw.Substring(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var vh))
                        {
                            containerMainSize = Math.Max(containerMainSize, (vh / 100f) * state.ViewportHeight);
                        }
                        else if (raw.EndsWith("vw", StringComparison.OrdinalIgnoreCase) && float.TryParse(raw.Substring(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var vw))
                        {
                            containerMainSize = Math.Max(containerMainSize, (vw / 100f) * state.ViewportWidth);
                        }
                    }
                }
            }

            if (isRow && !hasExplicitMain && state.ViewportWidth > 0)
            {
                containerMainSize = Math.Min(containerMainSize, state.ViewportWidth);
            }
            
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
                bool isFlexBasisAuto = !hasFlexBasisExplicit && (itemStyle?.FlexBasis == null);
                // Flex items with auto main-size should measure from intrinsic content width,
                // not from the full container width, regardless of inline/block display.
                bool preferIntrinsicWidth = isRow && !hasExplicitWidth && (isFlexBasisAuto || !hasFlexGrow);
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
                // When the flex container cross-size is auto/indefinite, do not fall back to
                // viewport height. Doing so makes percent/auto heights inside controls (e.g.
                // Google search textarea wrapper) expand to viewport scale.
                childState.ContainingBlockHeight = float.IsFinite(container.Geometry.ContentBox.Height) && container.Geometry.ContentBox.Height > 0
                    ? container.Geometry.ContentBox.Height
                    : 0f;

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
                        LayoutWithForcedWidth(item, reState, targetWidth);
                    }
                }
            }

            // 5. Resolve flex-basis and compute flex factors
            // Compute gap early so it's included in remaining-space calculation.
            float fontSizeEarly = (float)(style.FontSize ?? 16);
            float rootFontSizeEarly = 16f; // Standard default

            float ParseGap(string gapVal)
            {
                if (string.IsNullOrWhiteSpace(gapVal) || gapVal.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 0;
                gapVal = gapVal.Trim().ToLowerInvariant();
                if (gapVal.EndsWith("px") && float.TryParse(gapVal.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) return px;
                if (gapVal.EndsWith("rem") && float.TryParse(gapVal.Replace("rem", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var rem)) return rem * rootFontSizeEarly;
                if (gapVal.EndsWith("em") && float.TryParse(gapVal.Replace("em", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var em)) return em * fontSizeEarly;
                if (float.TryParse(gapVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) return raw;
                return 0f;
            }

            float gapShorthandEarly = (float)(style.Gap ?? 0);
            if (gapShorthandEarly <= 0 && style.Map != null && style.Map.TryGetValue("gap", out var rawGapEarly))
                gapShorthandEarly = ParseGap(rawGapEarly);

            float colGapEarly = (float)(style.ColumnGap ?? 0);
            if (colGapEarly <= 0 && style.Map != null && style.Map.TryGetValue("column-gap", out var rawCgE))
                colGapEarly = ParseGap(rawCgE);
            if (colGapEarly <= 0) colGapEarly = gapShorthandEarly;

            float rowGapEarly = (float)(style.RowGap ?? 0);
            if (rowGapEarly <= 0 && style.Map != null && style.Map.TryGetValue("row-gap", out var rawRgE))
                rowGapEarly = ParseGap(rawRgE);
            if (rowGapEarly <= 0) rowGapEarly = gapShorthandEarly;

            float gapForFlex = isRow ? colGapEarly : rowGapEarly;

            // Compute flex base sizes for each item.
            // Per CSS Flexbox spec: flex-basis determines the initial main size before grow/shrink.
            // flex-basis: auto → use CSS width/height or measured intrinsic size
            // flex-basis: 0 → base size is 0 (common with flex:1)
            // flex-basis: <length> → use that length
            float totalMainSize = 0;
            float totalFlexGrow = 0;
            float totalWeightedShrink = 0;
            var flexContentBases = new Dictionary<LayoutBox, float>();

            foreach (var item in items)
            {
                var itemStyle = item.ComputedStyle;
                float measuredContent = isRow ? item.Geometry.ContentBox.Width : item.Geometry.ContentBox.Height;
                float measuredMargin = isRow ? item.Geometry.MarginBox.Width : GetColumnMainSize(item);
                float overhead = Math.Max(0, measuredMargin - measuredContent);

                // Resolve flex-basis: definite value → use it; NaN/null → auto (use measured)
                double? rawBasis = itemStyle?.FlexBasis;
                float contentBasis;
                if (rawBasis.HasValue && !double.IsNaN(rawBasis.Value) && rawBasis.Value >= 0)
                    contentBasis = (float)rawBasis.Value;
                else
                    contentBasis = measuredContent; // auto: use measured content size

                flexContentBases[item] = contentBasis;
                totalMainSize += contentBasis + overhead;

                var flexGrow = ResolveFlexGrow(itemStyle);
                if (flexGrow.HasValue)
                    totalFlexGrow += (float)flexGrow.Value;

                float shrink = (float)(ResolveFlexShrink(itemStyle) ?? 1);
                if (shrink > 0)
                    totalWeightedShrink += shrink * Math.Max(1f, contentBasis);
            }

            // Include gaps in total main size
            totalMainSize += gapForFlex * Math.Max(0, items.Count - 1);

            // If a flex item still has 0 width, no flex-basis, and declares flex, recover.
            if (isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    if (item.Geometry.ContentBox.Width > 0) continue;
                    var itemStyle = item.ComputedStyle;
                    // Skip items with explicit flex-basis: 0 — they should grow, not be recovered
                    if (itemStyle?.FlexBasis.HasValue == true && !double.IsNaN(itemStyle.FlexBasis.Value))
                        continue;
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
                    LayoutWithForcedWidth(item, reState, remainingForItem);

                    totalMainSize = totalMainSize - currentItemSize + item.Geometry.MarginBox.Width;
                }
            }

            bool mainSizeWasAuto = isRow
                ? container.Geometry.ContentBox.Width <= 0
                : container.Geometry.ContentBox.Height <= 0;

            if (shrinkToContentMainAxis)
            {
                if (containerMainSize <= 0 || float.IsNaN(containerMainSize) || float.IsInfinity(containerMainSize) || (!hasExplicitMain && containerMainSize < totalMainSize))
                {
                    containerMainSize = totalMainSize;
                }

                if (mainSizeWasAuto && containerMainSize > 0 && float.IsFinite(containerMainSize))
                {
                    if (isRow)
                        LayoutBoxOps.ComputeBoxModelFromContent(container, containerMainSize, container.Geometry.ContentBox.Height);
                    else
                        LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, containerMainSize);
                }
            }

            float remainingSpace = containerMainSize - totalMainSize;

            // Handle Flex-Shrink (when items overflow the container)
            // Per CSS Flexbox §9.7: in multi-line containers (flex-wrap != nowrap)
            // overflowing items wrap to a new line instead of shrinking. Skip shrink here
            // so the wrap pass below can break them into lines.
            if (remainingSpace < 0 && totalWeightedShrink > 0 && !isWrap)
            {
                float overflow = -remainingSpace;
                foreach (var item in items)
                {
                    float shrink = (float)(ResolveFlexShrink(item.ComputedStyle) ?? 1);
                    float contentBasis = flexContentBases.TryGetValue(item, out var cb) ? cb : 0f;
                    float weight = shrink * Math.Max(1f, contentBasis);

                    if (LayoutValidator.IsSafeDivision(totalWeightedShrink) && weight > 0)
                    {
                        float ratio = LayoutValidator.SafeDivide(weight, totalWeightedShrink, 0f, "FlexShrink ratio");
                        float shrinkAmount = overflow * ratio;

                        if (isRow)
                        {
                            // Shrink from basis, not from measured size
                            float targetWidth = Math.Max(0, contentBasis - shrinkAmount);
                            LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, item.Geometry.ContentBox.Height);

                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                            reState.ContainingBlockWidth = targetWidth;
                            reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                            LayoutWithForcedWidth(item, reState, targetWidth);
                        }
                        else
                        {
                            float targetHeight = Math.Max(0, contentBasis - shrinkAmount);
                            LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, targetHeight);

                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, targetHeight);
                            reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                            reState.ContainingBlockHeight = targetHeight;
                            LayoutWithForcedHeight(item, reState, targetHeight);
                        }
                    }
                }
                totalMainSize = containerMainSize;
            }
            else if (remainingSpace > 0 && totalFlexGrow > 0)
            {
                foreach (var item in items)
                {
                    float grow = (float)(ResolveFlexGrow(item.ComputedStyle) ?? 0);
                    if (grow > 0)
                    {
                        float share = remainingSpace * LayoutValidator.SafeDivide(grow, totalFlexGrow, 0f, "FlexGrow ratio");
                        float contentBasis = flexContentBases.TryGetValue(item, out var cb) ? cb : 0f;

                        if (isRow)
                        {
                             // Target = basis + growth share
                             float targetWidth = contentBasis + share;
                             LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, item.Geometry.ContentBox.Height);

                             var reState = state.Clone();
                             reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                             reState.ContainingBlockWidth = targetWidth;
                             reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                             LayoutWithForcedWidth(item, reState, targetWidth);
                        }
                        else
                        {
                             float targetHeight = contentBasis + share;
                             LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, targetHeight);

                             var reState = state.Clone();
                             reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, targetHeight);
                             reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                             reState.ContainingBlockHeight = targetHeight;
                             LayoutWithForcedHeight(item, reState, targetHeight);
                        }
                    }
                }
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
                        LayoutWithForcedWidth(item, reState, newWidth);
                    }
                }
            }
            else if (!isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    var flexGrow = ResolveFlexGrow(item.ComputedStyle);
                    if (!flexGrow.HasValue || flexGrow.Value <= 0 || item.Geometry.ContentBox.Height > 0)
                    {
                        continue;
                    }

                    float currentItemSize = GetColumnMainSize(item);
                    float newHeight = Math.Max(0, containerMainSize - (totalMainSize - currentItemSize));
                    if (newHeight <= 0)
                    {
                        continue;
                    }

                    float targetWidth = item.Geometry.ContentBox.Width > 0
                        ? item.Geometry.ContentBox.Width
                        : container.Geometry.ContentBox.Width;

                    LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, newHeight);

                    var reState = state.Clone();
                    reState.AvailableSize = new SKSize(targetWidth, newHeight);
                    reState.ContainingBlockWidth = targetWidth;
                    reState.ContainingBlockHeight = newHeight;
                    LayoutWithForcedHeight(item, reState, newHeight);

                    if (item.Geometry.ContentBox.Height <= 0)
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, newHeight);
                    }
                }
            }

            // Final safety: if any row flex item collapsed to near-zero width, recover using
            // descendant content bounds but never exceed the container's main size.
            if (isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    if (item.Geometry.ContentBox.Width > 1f) continue;

                    ApplyCollapsedFlexItemFallback(item);
                    if (item.Geometry.ContentBox.Width > 1f) continue;

                    float descendantWidth = 0f;
                    if (TryGetDescendantExtent(item, out var requiredWidth, out _))
                    {
                        descendantWidth = Math.Max(0f, requiredWidth);
                    }

                    float targetWidth = descendantWidth > 1f
                        ? Math.Min(containerMainSize, descendantWidth)
                        : Math.Max(0, containerMainSize - (totalMainSize - item.Geometry.MarginBox.Width));

                    if (targetWidth <= 1f) continue;

                    LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, item.Geometry.ContentBox.Height);

                    var reState = state.Clone();
                    reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                    reState.ContainingBlockWidth = targetWidth;
                    reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                    LayoutWithForcedWidth(item, reState, targetWidth);
                }
            }
            else if (!isRow && containerMainSize > 0)
            {
                foreach (var item in items)
                {
                    if (item.Geometry.ContentBox.Height > 1f) continue;
                    if (HasOnlyOutOfFlowOrIgnorableDescendants(item))
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, 0f);
                        continue;
                    }

                    ApplyCollapsedFlexItemFallback(item);
                    if (item.Geometry.ContentBox.Height > 1f) continue;

                    float descendantHeight = 0f;
                    if (TryGetDescendantExtent(item, out _, out var requiredHeight))
                    {
                        descendantHeight = Math.Max(0f, requiredHeight);
                    }

                    bool canUseRemainingSpace = ResolveFlexGrow(item.ComputedStyle).GetValueOrDefault() > 0;
                    float currentItemSize = GetColumnMainSize(item);
                    float targetHeight = descendantHeight > 1f
                        ? Math.Min(containerMainSize, descendantHeight)
                        : canUseRemainingSpace
                            ? Math.Max(0, containerMainSize - (totalMainSize - currentItemSize))
                            : 0f;

                    if (targetHeight <= 1f) continue;

                    float targetWidth = item.Geometry.ContentBox.Width > 0
                        ? item.Geometry.ContentBox.Width
                        : container.Geometry.ContentBox.Width;

                    LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, targetHeight);

                    var reState = state.Clone();
                    reState.AvailableSize = new SKSize(targetWidth, targetHeight);
                    reState.ContainingBlockWidth = targetWidth;
                    reState.ContainingBlockHeight = targetHeight;
                    LayoutWithForcedHeight(item, reState, targetHeight);

                    if (item.Geometry.ContentBox.Height <= 1f)
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(item, targetWidth, targetHeight);
                    }
                }
            }

            // Clamp flex items to their min/max constraints on the main axis.
            // Per CSS Flexbox §9.8, min/max constraints are applied AFTER flex-grow/shrink
            // but BEFORE final placement. This prevents items from overflowing or collapsing
            // beyond their specified constraints.
            ClampFlexItemMainSizes(items, isRow, containerMainSize, state);

            // 6. Placement (Main Axis - Justify Content) with wrap/wrap-reverse
            // Reuse gap values computed before flex calculations
            float gap = gapForFlex;
            float columnGapVal = colGapEarly;
            float rowGapVal = rowGapEarly;

            // If shrink-to-content and no definite main size, use measured total main size
            if (shrinkToContentMainAxis)
            {
                if (containerMainSize <= 0 || float.IsNaN(containerMainSize) || float.IsInfinity(containerMainSize) || (!hasExplicitMain && containerMainSize < totalMainSize))
                {
                    containerMainSize = totalMainSize;
                    if (isRow)
                        LayoutBoxOps.ComputeBoxModelFromContent(container, containerMainSize, container.Geometry.ContentBox.Height);
                    else
                        LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, containerMainSize);
                }
            }

            // Line builder for wrap
            List<List<LayoutBox>> lines = new List<List<LayoutBox>>();
            List<float> lineCrossSizes = new List<float>();

            float lineMainSize = 0;
            float lineCrossSize = 0;
            var currentLine = new List<LayoutBox>();

            void CommitLine()
            {
                if (lineMainSize < 0) lineMainSize = 0;
                lines.Add(new List<LayoutBox>(currentLine));
                lineCrossSizes.Add(lineCrossSize);
                currentLine.Clear();
                lineMainSize = 0;
                lineCrossSize = 0;
            }

            float mainLimit = containerMainSize;
            if (float.IsNaN(mainLimit) || float.IsInfinity(mainLimit) || mainLimit <= 0)
                mainLimit = isRow ? state.ViewportWidth : state.ViewportHeight;
            if (mainLimit <= 0) mainLimit = 1920f;

            if (!isWrap)
            {
                lines.Add(items);

                // Single-line flex: cross-size = container's inner cross dimension
                // (width for column, height for row). Fall back to max item cross-size
                // when the container cross dimension is auto/0.
                float singleLineCross = isRow
                    ? container.Geometry.ContentBox.Height
                    : container.Geometry.ContentBox.Width;
                float maxItemCross = 0f;
                foreach (var item in items)
                {
                    float itemCross = isRow
                        ? item.Geometry.MarginBox.Height
                        : item.Geometry.MarginBox.Width;
                    maxItemCross = Math.Max(maxItemCross, itemCross);
                }

                if (!hasExplicitCrossSize)
                {
                    singleLineCross = Math.Max(singleLineCross, maxItemCross);
                }
                else if (singleLineCross <= 0)
                {
                    singleLineCross = maxItemCross;
                }

                lineCrossSizes.Add(singleLineCross > 0
                    ? singleLineCross
                    : ComputeFallbackCrossSize(items, isRow, container));
            }
            else
            {
                foreach (var item in items)
                {
                    float itemMain = isRow ? item.Geometry.MarginBox.Width : GetColumnMainSize(item);
                    float itemCross = isRow ? item.Geometry.MarginBox.Height : item.Geometry.MarginBox.Width;

                    float projected = lineMainSize + (currentLine.Count > 0 ? gap : 0) + itemMain;
                    if (currentLine.Count > 0 && projected > mainLimit)
                    {
                        CommitLine();
                    }

                    currentLine.Add(item);
                    lineMainSize = (currentLine.Count == 1) ? itemMain : lineMainSize + gap + itemMain;
                    lineCrossSize = Math.Max(lineCrossSize, itemCross);
                }

                if (currentLine.Count > 0) CommitLine();
            }

            // Recompute container cross size for wrapped lines
            float lineGap = isRow ? rowGapVal : columnGapVal; // cross-axis gap between flex lines
            float totalCrossSize = 0f;
            for (int i = 0; i < lineCrossSizes.Count; i++)
            {
                totalCrossSize += lineCrossSizes[i];
                if (i < lineCrossSizes.Count - 1) totalCrossSize += lineGap;
            }

            // Resize auto-height container to fit all lines
            if (isRow)
            {
                bool shouldGrowAutoCross = !hasExplicitCrossSize && totalCrossSize > 0;
                if (shouldGrowAutoCross || container.Geometry.ContentBox.Height <= 0)
                {
                    float resolvedCross = shouldGrowAutoCross
                        ? Math.Max(container.Geometry.ContentBox.Height, totalCrossSize)
                        : totalCrossSize;

                    container.Geometry.ContentBox = new SKRect(0, 0, container.Geometry.ContentBox.Width, resolvedCross);
                    LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, resolvedCross);
                    containerCrossSize = resolvedCross;
                }
            }
            else
            {
                bool shouldGrowAutoCross = !hasExplicitCrossSize && totalCrossSize > 0;
                if (shouldGrowAutoCross || container.Geometry.ContentBox.Width <= 0)
                {
                    float resolvedCross = shouldGrowAutoCross
                        ? Math.Max(container.Geometry.ContentBox.Width, totalCrossSize)
                        : totalCrossSize;

                    container.Geometry.ContentBox = new SKRect(0, 0, resolvedCross, container.Geometry.ContentBox.Height);
                    LayoutBoxOps.ComputeBoxModelFromContent(container, resolvedCross, container.Geometry.ContentBox.Height);
                    containerCrossSize = resolvedCross;
                }
            }

            // Resolve align-content for multi-line flex containers.
            // Controls how flex lines are distributed in the cross axis.
            string alignContent = style?.AlignContent?.ToLowerInvariant();
            if (string.IsNullOrEmpty(alignContent) && style?.Map != null &&
                style.Map.TryGetValue("align-content", out var rawAlignContent))
            {
                alignContent = rawAlignContent?.Trim().ToLowerInvariant();
            }
            alignContent ??= "stretch";

            // Position lines
            bool isWrapReverse = style.FlexWrap != null && style.FlexWrap.IndexOf("reverse", StringComparison.OrdinalIgnoreCase) >= 0;

            // Compute free cross space for align-content distribution
            float crossFreeSpace = containerCrossSize - totalCrossSize;
            float alignContentStartOffset = 0f;
            float alignContentLineGapExtra = 0f;

            // align-content only applies to multi-line containers (wrap).
            // For single-line, it has no effect per spec.
            if (lines.Count > 1 || (isWrap && lines.Count == 1))
            {
                if (alignContent == "center")
                {
                    alignContentStartOffset = crossFreeSpace / 2;
                }
                else if (alignContent == "flex-end" || alignContent == "end")
                {
                    alignContentStartOffset = crossFreeSpace;
                }
                else if (alignContent == "space-between" && lines.Count > 1 && crossFreeSpace > 0)
                {
                    alignContentLineGapExtra = crossFreeSpace / (lines.Count - 1);
                }
                else if (alignContent == "space-around" && lines.Count > 0 && crossFreeSpace > 0)
                {
                    alignContentLineGapExtra = crossFreeSpace / lines.Count;
                    alignContentStartOffset = alignContentLineGapExtra / 2;
                }
                else if (alignContent == "space-evenly" && lines.Count > 0 && crossFreeSpace > 0)
                {
                    alignContentLineGapExtra = crossFreeSpace / (lines.Count + 1);
                    alignContentStartOffset = alignContentLineGapExtra;
                }
                else if (alignContent == "stretch" && crossFreeSpace > 0 && lines.Count > 0)
                {
                    // Distribute extra cross space equally to each line's cross-size
                    float extraPerLine = crossFreeSpace / lines.Count;
                    for (int i = 0; i < lineCrossSizes.Count; i++)
                    {
                        lineCrossSizes[i] += extraPerLine;
                    }
                }
                // flex-start / start / normal → no offset (default)
            }

            float crossPos = isWrapReverse
                ? (containerCrossSize > 0 ? containerCrossSize : totalCrossSize)
                : alignContentStartOffset;

            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.Count == 0) continue;

                // Calculate total main size for this line
                float lineMainTotal = line.Sum(it => isRow ? it.Geometry.MarginBox.Width : GetColumnMainSize(it));
                lineMainTotal += gap * Math.Max(0, line.Count - 1);

                float lineCross = lineCrossSizes[lineIndex];

                // align-items: stretch per line
                if (alignItems == "stretch" && lineCross > 0)
                {
                    foreach (var item in line)
                    {
                        // align-self overrides stretch
                        string itemAlign = ResolveItemAlignment(item.ComputedStyle, alignItems);
                        if (itemAlign != "stretch") continue;

                        float itemCross = isRow ? item.Geometry.MarginBox.Height : item.Geometry.MarginBox.Width;
                        if (itemCross >= lineCross) continue;

                        // Per spec: stretched size = line cross size minus margin, padding, border
                        var iMargin = item.ComputedStyle?.Margin ?? new FenBrowser.Core.Thickness();
                        var iPad = item.ComputedStyle?.Padding ?? new FenBrowser.Core.Thickness();
                        var iBrd = item.ComputedStyle?.BorderThickness ?? new FenBrowser.Core.Thickness();
                        float marginCross = isRow
                            ? (float)(iMargin.Top + iMargin.Bottom)
                            : (float)(iMargin.Left + iMargin.Right);
                        float padBrdCross = isRow
                            ? (float)(iPad.Top + iPad.Bottom + iBrd.Top + iBrd.Bottom)
                            : (float)(iPad.Left + iPad.Right + iBrd.Left + iBrd.Right);
                        float newContentCross = Math.Max(0, lineCross - marginCross - padBrdCross);

                        if (isRow)
                        {
                            float preservedWidth = item.Geometry.ContentBox.Width;
                            LayoutBoxOps.ComputeBoxModelFromContent(item, preservedWidth, newContentCross);
                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, newContentCross);
                            reState.ContainingBlockWidth = preservedWidth;
                            reState.ContainingBlockHeight = newContentCross;
                            LayoutWithForcedHeight(item, reState, newContentCross);
                            // Re-layout above may shrink the box back to its intrinsic content
                            // height when the item has no explicit height. Re-apply the
                            // stretched cross-axis size so align-items:stretch is honored.
                            if (item.Geometry.ContentBox.Height < newContentCross - 0.5f)
                            {
                                LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, newContentCross);
                            }
                        }
                        else
                        {
                            float preservedHeight = item.Geometry.ContentBox.Height;
                            LayoutBoxOps.ComputeBoxModelFromContent(item, newContentCross, preservedHeight);
                            var reState = state.Clone();
                            reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, preservedHeight);
                            reState.ContainingBlockWidth = newContentCross;
                            reState.ContainingBlockHeight = preservedHeight;
                            LayoutWithForcedWidth(item, reState, newContentCross);
                            if (item.Geometry.ContentBox.Width < newContentCross - 0.5f)
                            {
                                LayoutBoxOps.ComputeBoxModelFromContent(item, newContentCross, item.Geometry.ContentBox.Height);
                            }
                        }
                    }
                }

                float remaining = containerMainSize - lineMainTotal;
                float startOffset = 0f;
                float itemStepExtra = 0f;

                int autoMarginCount = 0;
                var autoMarginBefore = new Dictionary<LayoutBox, bool>();
                var autoMarginAfter = new Dictionary<LayoutBox, bool>();
                LayoutBox previousItem = null;
                foreach (var item in line)
                {
                    var itemStyle = item.ComputedStyle;
                    bool beforeAuto = isRow ? IsMarginAuto(itemStyle, "left") : IsMarginAuto(itemStyle, "top");
                    bool afterAuto = isRow ? IsMarginAuto(itemStyle, "right") : IsMarginAuto(itemStyle, "bottom");
                    autoMarginBefore[item] = beforeAuto;
                    autoMarginAfter[item] = afterAuto;
                    if (beforeAuto) autoMarginCount++;
                    if (afterAuto) autoMarginCount++;
                }

                float lineBaselineOffset = 0f;
                bool lineHasBaselineAlignment = false;
                if (isRow)
                {
                    foreach (var item in line)
                    {
                        string itemAlign = ResolveItemAlignment(item.ComputedStyle, alignItems);
                        if (!IsBaselineAlignment(itemAlign))
                        {
                            continue;
                        }

                        // Cross-axis auto margins take precedence over alignment.
                        bool crossAutoStart = IsMarginAuto(item.ComputedStyle, "top");
                        bool crossAutoEnd = IsMarginAuto(item.ComputedStyle, "bottom");
                        if (crossAutoStart || crossAutoEnd)
                        {
                            continue;
                        }

                        lineHasBaselineAlignment = true;
                        lineBaselineOffset = Math.Max(
                            lineBaselineOffset,
                            ResolveBaselineOffsetFromMarginTop(item));
                    }
                }

                // justify-content: center and flex-end work even when remaining < 0
                // (items overflow symmetrically for center, or from the start for flex-end)
                if (justifyContent == "center")
                    startOffset = remaining / 2;
                else if (justifyContent == "flex-end" || justifyContent == "end")
                    startOffset = remaining;
                else if (remaining > 0)
                {
                    if (justifyContent == "space-between" && line.Count > 1)
                        itemStepExtra = remaining / (line.Count - 1);
                    else if (justifyContent == "space-around" && line.Count > 0)
                    {
                        itemStepExtra = remaining / line.Count;
                        startOffset = itemStepExtra / 2;
                    }
                    else if (justifyContent == "space-evenly" && line.Count > 0)
                    {
                        itemStepExtra = remaining / (line.Count + 1);
                        startOffset = itemStepExtra;
                    }
                }

                if (autoMarginCount > 0 && remaining > 0)
                {
                    startOffset = 0;
                    itemStepExtra = 0;
                }

                float lineMainPos = startOffset;

                float crossStart = isWrapReverse ? crossPos - lineCross : crossPos;

                foreach (var item in line)
                {
                    float autoBefore = 0f;
                    float autoAfter = 0f;
                    if (autoMarginCount > 0 && remaining > 0)
                    {
                        float share = remaining / autoMarginCount;
                        if (autoMarginBefore[item]) autoBefore = share;
                        if (autoMarginAfter[item]) autoAfter = share;
                    }

                    // Per-item alignment (align-self overrides align-items)
                    string itemAlign = ResolveItemAlignment(item.ComputedStyle, alignItems);

                    // Cross-axis auto margins (take precedence over alignment)
                    bool crossAutoStart = isRow
                        ? IsMarginAuto(item.ComputedStyle, "top")
                        : IsMarginAuto(item.ComputedStyle, "left");
                    bool crossAutoEnd = isRow
                        ? IsMarginAuto(item.ComputedStyle, "bottom")
                        : IsMarginAuto(item.ComputedStyle, "right");

                    float x = 0, y = 0;
                    if (isRow)
                    {
                        lineMainPos += autoBefore;
                        x = lineMainPos;

                        float crossFree = lineCross - item.Geometry.MarginBox.Height;

                        // Cross-axis auto margins absorb free space
                        if (crossAutoStart && crossAutoEnd)
                            y = crossStart + crossFree / 2;
                        else if (crossAutoStart)
                            y = crossStart + crossFree;
                        else if (crossAutoEnd)
                            y = crossStart;
                        else if (itemAlign == "center")
                            y = crossStart + crossFree / 2;
                        else if (itemAlign == "flex-end" || itemAlign == "end")
                            y = crossStart + crossFree;
                        else if (IsBaselineAlignment(itemAlign) && lineHasBaselineAlignment)
                        {
                            float itemBaselineOffset = ResolveBaselineOffsetFromMarginTop(item);
                            y = crossStart + lineBaselineOffset - itemBaselineOffset;
                        }
                        else if (IsBaselineAlignment(itemAlign))
                        {
                            y = crossStart;
                        }
                        else
                            y = crossStart; // flex-start / stretch

                        lineMainPos += item.Geometry.MarginBox.Width + gap + itemStepExtra + autoAfter;
                    }
                    else
                    {
                        var colMain = GetColumnMainSize(item);
                        lineMainPos += autoBefore;
                        y = lineMainPos;

                        float crossFree = lineCross - item.Geometry.MarginBox.Width;

                        // Cross-axis auto margins absorb free space
                        if (crossAutoStart && crossAutoEnd)
                            x = crossStart + crossFree / 2;
                        else if (crossAutoStart)
                            x = crossStart + crossFree;
                        else if (crossAutoEnd)
                            x = crossStart;
                        else if (itemAlign == "center")
                            x = crossStart + crossFree / 2;
                        else if (itemAlign == "flex-end" || itemAlign == "end")
                            x = crossStart + crossFree;
                        else
                            x = crossStart;

                        lineMainPos += colMain + gap + itemStepExtra + autoAfter;
                    }

                    float placementX = container.Geometry.ContentBox.Left + x;
                    float placementY = container.Geometry.ContentBox.Top + y;

                    if (isRow &&
                        previousItem != null &&
                        !HasNegativeInlineMargins(previousItem) &&
                        !HasNegativeInlineMargins(item) &&
                        !IsRelativelyPositioned(previousItem) &&
                        !IsRelativelyPositioned(item))
                    {
                        // Guard against geometry drift where a measured item advances the main-axis
                        // cursor less than its painted margin-box width, causing visible overlap.
                        float minLeft = previousItem.Geometry.MarginBox.Right;
                        if (IsGoogleTopRightAppsToSignInPair(container, previousItem, item))
                        {
                            // Keep a small visual gap so the Apps affordance does not intrude
                            // into the Sign in pill even when icon paint slightly overhangs.
                            minLeft += 8f;
                        }
                        if (placementX < minLeft - 0.5f)
                        {
                            placementX = minLeft;
                        }
                    }

                    LayoutBoxOps.PositionSubtree(item, placementX, placementY, state);
                    if (IsRelativelyPositioned(item))
                    {
                        ClampRelativeDescendantsToItemStart(item);
                    }

                    // Final anti-overlap guard for Google top-right controls:
                    // ensure Sign in starts to the right of previously placed siblings.
                    if (isRow && IsGoogleTopRightSignInItem(container, item))
                    {
                        float requiredLeft = float.MinValue;
                        foreach (var placed in line)
                        {
                            if (ReferenceEquals(placed, item))
                            {
                                break;
                            }

                            requiredLeft = Math.Max(requiredLeft, placed.Geometry.MarginBox.Right + 8f);
                        }

                        if (requiredLeft > float.MinValue && item.Geometry.MarginBox.Left < requiredLeft - 0.5f)
                        {
                            LayoutBoxOps.PositionSubtree(item, requiredLeft, item.Geometry.MarginBox.Top, state);
                        }

                        if (state.ViewportWidth > 0f)
                        {
                            const float rightViewportGutter = 16f;
                            float maxRight = Math.Max(0f, state.ViewportWidth - rightViewportGutter);
                            float overflowRight = item.Geometry.MarginBox.Right - maxRight;
                            if (overflowRight > 0.5f)
                            {
                                float clampedLeft = Math.Max(0f, item.Geometry.MarginBox.Left - overflowRight);
                                LayoutBoxOps.PositionSubtree(item, clampedLeft, item.Geometry.MarginBox.Top, state);
                            }
                        }
                    }

                    TryLogGoogleTopRightGeometry(container, item);
                    previousItem = item;
                }

                crossPos = isWrapReverse
                    ? crossStart - lineGap - alignContentLineGapExtra
                    : crossStart + lineCross + lineGap + alignContentLineGapExtra;
            }

            // Apply reverse direction: mirror main-axis positions
            if (isReverse)
            {
                float mainSize = isRow ? container.Geometry.ContentBox.Width : container.Geometry.ContentBox.Height;
                float mainOrigin = isRow ? container.Geometry.ContentBox.Left : container.Geometry.ContentBox.Top;
                foreach (var lineItems in lines)
                {
                    foreach (var item in lineItems)
                    {
                        if (isRow)
                        {
                            float oldX = item.Geometry.MarginBox.Left;
                            float oldLocalX = oldX - mainOrigin;
                            float newX = mainOrigin + mainSize - oldLocalX - item.Geometry.MarginBox.Width;
                            LayoutBoxOps.PositionSubtree(item, newX, item.Geometry.MarginBox.Top, state);
                        }
                        else
                        {
                            float oldY = item.Geometry.MarginBox.Top;
                            float oldLocalY = oldY - mainOrigin;
                            float newY = mainOrigin + mainSize - oldLocalY - GetColumnMainSize(item);
                            LayoutBoxOps.PositionSubtree(item, item.Geometry.MarginBox.Left, newY, state);
                        }
                    }
                }
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

                LayoutPositioningLogic.ResolvePositionedBox(oof, container, container.Geometry, state);

                float resolvedWidth = Math.Max(0f, oof.Geometry.ContentBox.Width);
                float resolvedHeight = Math.Max(0f, oof.Geometry.ContentBox.Height);
                var childState = state.Clone();
                childState.AvailableSize = new SKSize(resolvedWidth, resolvedHeight);
                childState.ContainingBlockWidth = resolvedWidth;
                childState.ContainingBlockHeight = resolvedHeight;
                context.Layout(oof, childState);

                LayoutPositioningLogic.ResolvePositionedBox(oof, container, container.Geometry, state);
            }
        }

        private void ResolveContainerDimensions(LayoutBox box, LayoutState state)
        {
            // Similar to ResolveWidth/Height in BlockContext
            var widthResolution = LayoutConstraintResolver.ResolveWidth(state, "Flex.ResolveContainerDimensions");
            float rawAvailable = widthResolution.RawAvailable;
            bool widthUnconstrained = widthResolution.IsUnconstrained;
            float available = widthResolution.ResolvedAvailable;
            
            var padding = box.ComputedStyle?.Padding ?? new FenBrowser.Core.Thickness();
            var border = box.ComputedStyle?.BorderThickness ?? new FenBrowser.Core.Thickness();
            var margin = box.ComputedStyle?.Margin ?? new FenBrowser.Core.Thickness();
            float horizontalChrome = (float)(padding.Left + padding.Right + border.Left + border.Right);
            float verticalChrome = (float)(padding.Top + padding.Bottom + border.Top + border.Bottom);
            bool isBorderBox = string.Equals(box.ComputedStyle?.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase);

            // Width
            float width = 0;
            bool hasExplicitWidthFromMap = box.ComputedStyle?.Map != null &&
                                           box.ComputedStyle.Map.TryGetValue("width", out var rawWidthFromMap) &&
                                           !string.IsNullOrWhiteSpace(rawWidthFromMap) &&
                                           !string.Equals(rawWidthFromMap.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            bool hasExplicitWidth = box.ComputedStyle?.Width.HasValue == true ||
                                    box.ComputedStyle?.WidthPercent.HasValue == true ||
                                    !string.IsNullOrEmpty(box.ComputedStyle?.WidthExpression) ||
                                    hasExplicitWidthFromMap;
            bool resolvedDefiniteWidth = false;
            if (box.ComputedStyle?.Width.HasValue == true)
            {
                width = (float)box.ComputedStyle.Width.Value;
                resolvedDefiniteWidth = true;
            }
            else if (box.ComputedStyle?.WidthPercent.HasValue == true)
            {
                float parentWidth = state.AvailableSize.Width;
                if (float.IsInfinity(parentWidth) || parentWidth <= 0)
                    parentWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                if (!float.IsInfinity(parentWidth) && parentWidth > 0)
                {
                    width = (float)(box.ComputedStyle.WidthPercent.Value / 100.0 * parentWidth);
                    resolvedDefiniteWidth = true;
                }
            }
            if (resolvedDefiniteWidth)
            {
                // Already resolved from an explicit width/width-percent. Just apply
                // border-box adjustment if applicable; do NOT overwrite with the
                // available-space fallback below.
                if (isBorderBox)
                {
                    width = Math.Max(0f, width - horizontalChrome);
                }
            }
            else if (widthUnconstrained)
            {
                // In intrinsic/probe passes (available width = infinity), auto-width flex items
                // must not eagerly fill the containing block; that produces oversized bases and
                // breaks shrink calculations for control clusters.
                width = hasExplicitWidth
                    ? Math.Max(0, available - (float)margin.Horizontal - horizontalChrome)
                    : 0f;
            }
            else width = Math.Max(0, rawAvailable - (float)margin.Horizontal - horizontalChrome);

            // Height
            float height = 0;
            if (box.ComputedStyle?.Height.HasValue == true) 
            {
                height = (float)box.ComputedStyle.Height.Value;
                if (isBorderBox)
                {
                    height = Math.Max(0f, height - verticalChrome);
                }
            }
            else
            {
                bool resolvedPercentHeight = false;
                if (box.ComputedStyle?.HeightPercent.HasValue == true)
                {
                    float parentHeight = ResolveDefinitePercentageHeightBasis(box, state);
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                    {
                        height = (float)(box.ComputedStyle.HeightPercent.Value / 100.0 * parentHeight);
                        resolvedPercentHeight = true;
                    }
                }

                if (!resolvedPercentHeight)
                {
                    bool hasInFlowChildren = box.Children.Any(child =>
                        child != null &&
                        !child.IsOutOfFlow &&
                        child.ComputedStyle?.Display?.Contains("none", StringComparison.OrdinalIgnoreCase) != true &&
                        !IsIgnorableFlexItem(child));
                    if (!hasInFlowChildren &&
                        LayoutHelper.TryResolveLineHeight(box.ComputedStyle, out var resolvedLineHeight) &&
                        resolvedLineHeight > 0f)
                    {
                        // Empty/leaf flex wrappers can still rely on line-height when height is auto.
                        height = resolvedLineHeight;
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
                }
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
                if (isBorderBox)
                {
                    minW = Math.Max(0f, minW - horizontalChrome);
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
                if (isBorderBox && float.IsFinite(maxW))
                {
                    maxW = Math.Max(0f, maxW - horizontalChrome);
                }

                if (box.ComputedStyle.MinHeight.HasValue) minH = (float)box.ComputedStyle.MinHeight.Value;
                else if (box.ComputedStyle.MinHeightPercent.HasValue == true)
                {
                    float parentHeight = ResolveDefinitePercentageHeightBasis(box, state);
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
                    else if (raw.EndsWith("vh", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(raw.Substring(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var vh))
                        {
                            minH = (vh / 100f) * state.ViewportHeight;
                        }
                    }
                    else if (raw.EndsWith("vw", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(raw.Substring(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var vw))
                        {
                            minH = (vw / 100f) * state.ViewportWidth;
                        }
                    }
                    else
                    {
                        minH = LayoutHelper.EvaluateCssExpression(raw, parentHeight, state.ViewportWidth, state.ViewportHeight);
                    }
                }
                if (isBorderBox)
                {
                    minH = Math.Max(0f, minH - verticalChrome);
                }

                if (box.ComputedStyle.MaxHeight.HasValue) maxH = (float)box.ComputedStyle.MaxHeight.Value;
                else if (box.ComputedStyle.MaxHeightPercent.HasValue == true)
                {
                    float parentHeight = ResolveDefinitePercentageHeightBasis(box, state);
                    if (parentHeight > 0) maxH = (float)(box.ComputedStyle.MaxHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(box.ComputedStyle.MaxHeightExpression))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0) parentHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                    maxH = LayoutHelper.EvaluateCssExpression(box.ComputedStyle.MaxHeightExpression, parentHeight, state.ViewportWidth, state.ViewportHeight);
                }
                if (isBorderBox && float.IsFinite(maxH))
                {
                    maxH = Math.Max(0f, maxH - verticalChrome);
                }
            }

            if (width < minW) width = minW;
            if (width > maxW && float.IsFinite(maxW)) width = maxW;
            if (height < minH) height = minH;
            if (height > maxH && float.IsFinite(maxH)) height = maxH;

            float marginLeft = (float)margin.Left;
            float marginRight = (float)margin.Right;
            bool leftAuto = box.ComputedStyle?.MarginLeftAuto ?? false;
            bool rightAuto = box.ComputedStyle?.MarginRightAuto ?? false;

            if (!widthUnconstrained && (leftAuto || rightAuto))
            {
                float nonMarginWidth = width + horizontalChrome;
                float remainingSpace = available - nonMarginWidth;

                if (leftAuto && rightAuto)
                {
                    float share = Math.Max(0f, remainingSpace / 2f);
                    marginLeft = share;
                    marginRight = share;
                }
                else if (leftAuto)
                {
                    marginLeft = Math.Max(0f, remainingSpace - marginRight);
                }
                else if (rightAuto)
                {
                    marginRight = Math.Max(0f, remainingSpace - marginLeft);
                }
            }

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
                        if (type == "checkbox" || type == "radio")
                        {
                            width = ReplacedElementSizing.NativeCheckboxRadioSize;
                        }
                        else if (type == "submit" || type == "button" || type == "reset")
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
                        string label = LayoutHelper.GetRenderableTextContentTrimmed(el);
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
                }

                if (height <= 0)
                {
                    if (tag == "INPUT")
                    {
                        // Use a modern default that matches common design systems.
                        // Facebook login inputs are 52px; browsers typically default
                        // text inputs to ~20-24px but sites override via CSS.  When
                        // the external CSS cascade fails to match (e.g. StyleX
                        // @property-dependent rules), this fallback keeps inputs
                        // usable instead of collapsing to 0.
                        string type = (el.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
                        height = (type == "checkbox" || type == "radio") ? ReplacedElementSizing.NativeCheckboxRadioSize : 40f;
                    }
                    else if (tag == "BUTTON")
                    {
                        height = 44f;
                    }
                    else if (tag == "SELECT")
                    {
                        height = 40f;
                    }
                    else if (tag == "TEXTAREA")
                    {
                        height = 60f;
                    }
                }

                if ((width <= 0 || height <= 0) && ReplacedElementSizing.IsReplacedElementTag(tag))
                {
                    float attrW = 0f;
                    float attrH = 0f;
                    ReplacedElementSizing.TryGetLengthAttribute(el, "width", out attrW);
                    ReplacedElementSizing.TryGetLengthAttribute(el, "height", out attrH);
                    float intrinsicW = 0f;
                    float intrinsicH = 0f;
                    ReplacedElementSizing.TryResolveIntrinsicSizeFromElement(tag, el, out intrinsicW, out intrinsicH);

                    var resolved = ReplacedElementSizing.ResolveReplacedSize(
                        tag,
                        box.ComputedStyle,
                        new SKSize(box.Geometry.ContentBox.Width, box.Geometry.ContentBox.Height),
                        intrinsicW,
                        intrinsicH,
                        attrW,
                        attrH,
                        constrainAutoToAvailableWidth: false);

                    if (width <= 0) width = resolved.Width;
                    if (height <= 0) height = resolved.Height;
                }
            }

            // If a flex wrapper has a single replaced child and unresolved size,
            // use a small non-zero fallback to prevent collapse.
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
            
            float contentLeft = marginLeft + (float)border.Left + (float)padding.Left;
            box.Geometry.ContentBox = new SKRect(contentLeft, 0, contentLeft + width, height);
            box.Geometry.Padding = padding;
            box.Geometry.Border = border;
            box.Geometry.Margin = new FenBrowser.Core.Thickness(marginLeft, margin.Top, marginRight, margin.Bottom);

            // Sync boxes (Content -> Padding -> Border -> Margin)
            LayoutBoxOps.ComputeBoxModelFromContent(box, width, height);
        }

        private static bool HasNegativeInlineMargins(LayoutBox item)
        {
            if (item?.ComputedStyle == null)
            {
                return false;
            }

            return item.ComputedStyle.Margin.Left < 0 || item.ComputedStyle.Margin.Right < 0;
        }

        private static bool IsRelativelyPositioned(LayoutBox item)
        {
            return string.Equals(
                LayoutStyleResolver.GetEffectivePosition(item?.ComputedStyle),
                "relative",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ClampRelativeDescendantsToItemStart(LayoutBox item)
        {
            if (item?.Geometry == null)
            {
                return;
            }

            foreach (var child in item.Children)
            {
                ClampRelativeDescendantToItemStart(item, child);
            }
        }

        private static void ClampRelativeDescendantToItemStart(LayoutBox item, LayoutBox child)
        {
            if (child?.Geometry == null)
            {
                return;
            }

            var position = LayoutStyleResolver.GetEffectivePosition(child.ComputedStyle);
            if (string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            float dx = child.Geometry.MarginBox.Left < item.Geometry.MarginBox.Left
                ? item.Geometry.MarginBox.Left - child.Geometry.MarginBox.Left
                : 0f;
            float dy = child.Geometry.MarginBox.Top < item.Geometry.MarginBox.Top
                ? item.Geometry.MarginBox.Top - child.Geometry.MarginBox.Top
                : 0f;

            if (dx != 0f || dy != 0f)
            {
                LayoutBoxOps.ShiftSubtree(child, dx, dy);
            }

            foreach (var grandchild in child.Children)
            {
                ClampRelativeDescendantToItemStart(item, grandchild);
            }
        }

        private static bool IsGoogleTopRightAppsToSignInPair(LayoutBox container, LayoutBox previousItem, LayoutBox currentItem)
        {
            if (container?.SourceNode is not Element containerElement)
            {
                return false;
            }

            string containerClass = containerElement.ClassName ?? string.Empty;
            if (!containerClass.Contains("gb_y", StringComparison.Ordinal))
            {
                return false;
            }

            if (previousItem?.SourceNode is not Element previousElement || currentItem?.SourceNode is not Element currentElement)
            {
                return false;
            }

            string prevAria = previousElement.GetAttribute("aria-label") ?? string.Empty;
            string currAria = currentElement.GetAttribute("aria-label") ?? string.Empty;
            string prevClass = previousElement.ClassName ?? string.Empty;
            string currClass = currentElement.ClassName ?? string.Empty;

            bool previousIsApps =
                prevAria.Equals("Google apps", StringComparison.OrdinalIgnoreCase) ||
                prevClass.Contains("gb_D", StringComparison.Ordinal) ||
                prevClass.Contains("gb_C", StringComparison.Ordinal);
            bool currentIsSignIn =
                currAria.Equals("Sign in", StringComparison.OrdinalIgnoreCase) ||
                currClass.Contains("gb_z", StringComparison.Ordinal);

            return previousIsApps && currentIsSignIn;
        }

        private static bool IsGoogleTopRightSignInItem(LayoutBox container, LayoutBox item)
        {
            if (container?.SourceNode is not Element containerElement || item?.SourceNode is not Element itemElement)
            {
                return false;
            }

            string containerClass = containerElement.ClassName ?? string.Empty;
            if (!containerClass.Contains("gb_y", StringComparison.Ordinal))
            {
                return false;
            }

            string aria = itemElement.GetAttribute("aria-label") ?? string.Empty;
            string cls = itemElement.ClassName ?? string.Empty;
            return aria.Equals("Sign in", StringComparison.OrdinalIgnoreCase) ||
                   cls.Contains("gb_z", StringComparison.Ordinal);
        }

        private static void TryLogGoogleTopRightGeometry(LayoutBox container, LayoutBox item)
        {
            if (container?.SourceNode is not Element containerElement || item?.SourceNode is not Element itemElement)
            {
                return;
            }

            var containerClass = containerElement.ClassName ?? string.Empty;
            if (!containerClass.Contains("gb_y", StringComparison.Ordinal))
            {
                return;
            }

            var itemClass = itemElement.ClassName ?? string.Empty;
            var aria = itemElement.GetAttribute("aria-label") ?? string.Empty;
            bool isTarget = itemClass.Contains("gb_D", StringComparison.Ordinal) ||
                            itemClass.Contains("gb_z", StringComparison.Ordinal) ||
                            itemClass.Contains("gb_A", StringComparison.Ordinal) ||
                            itemClass.Contains("gb_C", StringComparison.Ordinal) ||
                            aria.Equals("Google apps", StringComparison.OrdinalIgnoreCase) ||
                            aria.Equals("Sign in", StringComparison.OrdinalIgnoreCase);
            if (!isTarget)
            {
                return;
            }

            var r = item.Geometry.MarginBox;
            FenBrowser.Core.EngineLogCompat.Info(
                $"[GOOGLE-TOPRIGHT-LAYOUT] cls={itemClass} aria={aria} rect=({r.Left:F1},{r.Top:F1},{r.Width:F1}x{r.Height:F1})",
                LogCategory.Layout);
        }

        private static float ResolveDefinitePercentageHeightBasis(LayoutBox box, LayoutState state)
        {
            if (box?.Parent == null)
            {
                float basis = state.ContainingBlockHeight;
                if (!float.IsFinite(basis) || basis <= 0f)
                {
                    basis = state.AvailableSize.Height;
                }

                if (!float.IsFinite(basis) || basis <= 0f)
                {
                    basis = state.ViewportHeight;
                }

                return basis;
            }

            if (TryResolveOutOfFlowContainingBlockHeight(box.Parent, state, out float resolvedOutOfFlowHeight))
            {
                return resolvedOutOfFlowHeight;
            }

            if (!HasDefiniteContainingBlockHeight(box.Parent))
            {
                return float.NaN;
            }

            float parentHeight = state.ContainingBlockHeight;
            if (!float.IsFinite(parentHeight) || parentHeight <= 0f)
            {
                parentHeight = state.AvailableSize.Height;
            }

            return parentHeight;
        }

        private static bool HasDefinitePercentageHeightBasis(LayoutBox box, LayoutState state)
        {
            float basis = ResolveDefinitePercentageHeightBasis(box, state);
            return float.IsFinite(basis) && basis > 0f;
        }

        private static bool IsPercentageHeight(string rawHeight)
        {
            return !string.IsNullOrWhiteSpace(rawHeight) &&
                   rawHeight.Trim().EndsWith("%", StringComparison.Ordinal);
        }

        private static bool HasDefiniteContainingBlockHeight(LayoutBox box)
        {
            if (box == null)
            {
                return true;
            }

            if (IsResolvedOutOfFlowHeightDefinite(box))
            {
                return true;
            }

            var style = box.ComputedStyle;
            if (style == null)
            {
                return false;
            }

            if (style.Height.HasValue || !string.IsNullOrWhiteSpace(style.HeightExpression))
            {
                return true;
            }

            if (style.HeightPercent.HasValue)
            {
                return HasDefiniteContainingBlockHeight(box.Parent);
            }

            if (HasDefinitePositionedHeight(style))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveOutOfFlowContainingBlockHeight(LayoutBox parent, LayoutState state, out float resolvedHeight)
        {
            resolvedHeight = float.NaN;
            if (!IsResolvedOutOfFlowHeightDefinite(parent))
            {
                return false;
            }

            resolvedHeight = state.ContainingBlockHeight;
            if (!float.IsFinite(resolvedHeight) || resolvedHeight <= 0f)
            {
                resolvedHeight = state.AvailableSize.Height;
            }

            if (!float.IsFinite(resolvedHeight) || resolvedHeight <= 0f)
            {
                resolvedHeight = parent.Geometry?.ContentBox.Height ?? float.NaN;
            }

            return float.IsFinite(resolvedHeight) && resolvedHeight > 0f;
        }

        private static bool IsResolvedOutOfFlowHeightDefinite(LayoutBox box)
        {
            if (box == null || !box.IsOutOfFlow || box.Geometry == null)
            {
                return false;
            }

            var style = box.ComputedStyle;
            if (style == null)
            {
                return false;
            }

            if (style.Height.HasValue || !string.IsNullOrWhiteSpace(style.HeightExpression))
            {
                return true;
            }

            if (style.HeightPercent.HasValue)
            {
                return HasDefiniteContainingBlockHeight(box.Parent);
            }

            return HasDefinitePositionedHeight(style);
        }

        private static bool HasDefinitePositionedHeight(CssComputed style)
        {
            if (style == null)
            {
                return false;
            }

            var position = LayoutStyleResolver.GetEffectivePosition(style);
            if (!string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return HasVerticalInset(style, isTop: true) && HasVerticalInset(style, isTop: false);
        }

        private static bool HasVerticalInset(CssComputed style, bool isTop)
        {
            if (style == null)
            {
                return false;
            }

            if (isTop)
            {
                return style.Top.HasValue || style.TopPercent.HasValue;
            }

            return style.Bottom.HasValue || style.BottomPercent.HasValue;
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
            if (currentW > 1f && currentH > 0f) return;

            float fallbackW = currentW;
            float fallbackH = currentH;
            var style = item.ComputedStyle;

            if (fallbackW <= 0f && style?.Width.HasValue == true) fallbackW = (float)style.Width.Value;
            if (fallbackH <= 0f && style?.Height.HasValue == true) fallbackH = (float)style.Height.Value;
            if (fallbackH <= 0f &&
                LayoutHelper.TryResolveLineHeight(style, out var resolvedLineHeight) &&
                resolvedLineHeight > 0f)
            {
                fallbackH = resolvedLineHeight;
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

            // Recover from narrow container widths when descendants clearly need more
            // (e.g., icon/control clusters measured in intrinsic probe passes).
            if (item.Children.Count > 0 && fallbackW <= 1f)
            {
                if (TryGetDescendantExtent(item, out var descendantWidth, out var descendantHeight))
                {
                    if (descendantWidth > 0f)
                    {
                        fallbackW = Math.Max(fallbackW, descendantWidth);
                    }

                    if (fallbackH <= 0f && descendantHeight > 0f)
                    {
                        fallbackH = descendantHeight;
                    }
                }
            }

            if (fallbackW <= 0f) fallbackW = 1f;
            if (fallbackH <= 0f) fallbackH = 1f;

            LayoutBoxOps.ComputeBoxModelFromContent(item, fallbackW, fallbackH);
        }

        private static bool TryGetDescendantExtent(LayoutBox item, out float width, out float height)
        {
            width = 0f;
            height = 0f;

            if (item == null || item.Children.Count == 0 || item.Geometry == null)
            {
                return false;
            }

            float originLeft = item.Geometry.MarginBox.Left;
            float originTop = item.Geometry.MarginBox.Top;
            float minLeft = float.PositiveInfinity;
            float maxRight = float.NegativeInfinity;
            float minTop = float.PositiveInfinity;
            float maxBottom = float.NegativeInfinity;

            foreach (var child in item.Children)
            {
                AccumulateDescendantExtent(
                    child,
                    originLeft,
                    originTop,
                    ref minLeft,
                    ref maxRight,
                    ref minTop,
                    ref maxBottom);
            }

            bool hasHorizontal =
                float.IsFinite(minLeft) &&
                float.IsFinite(maxRight) &&
                maxRight > minLeft;
            bool hasVertical =
                float.IsFinite(minTop) &&
                float.IsFinite(maxBottom) &&
                maxBottom > minTop;

            if (hasHorizontal)
            {
                width = maxRight - minLeft;
            }

            if (hasVertical)
            {
                height = maxBottom - minTop;
            }

            return hasHorizontal || hasVertical;
        }

        private static void AccumulateDescendantExtent(
            LayoutBox node,
            float originLeft,
            float originTop,
            ref float minLeft,
            ref float maxRight,
            ref float minTop,
            ref float maxBottom)
        {
            if (node?.Geometry == null)
            {
                return;
            }

            if (node.IsOutOfFlow)
            {
                return;
            }

            float left = node.Geometry.MarginBox.Left - originLeft;
            float right = node.Geometry.MarginBox.Right - originLeft;
            float top = node.Geometry.MarginBox.Top - originTop;
            float bottom = node.Geometry.MarginBox.Bottom - originTop;

            if (float.IsFinite(left) && float.IsFinite(right))
            {
                minLeft = Math.Min(minLeft, left);
                maxRight = Math.Max(maxRight, right);
            }

            if (float.IsFinite(top) && float.IsFinite(bottom))
            {
                minTop = Math.Min(minTop, top);
                maxBottom = Math.Max(maxBottom, bottom);
            }

            foreach (var child in node.Children)
            {
                AccumulateDescendantExtent(
                    child,
                    originLeft,
                    originTop,
                    ref minLeft,
                    ref maxRight,
                    ref minTop,
                    ref maxBottom);
            }
        }

        private static void LayoutWithForcedWidth(LayoutBox item, LayoutState state, float forcedWidth)
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

            var oldWidth = style.Width;
            var oldWidthPercent = style.WidthPercent;
            var oldWidthExpression = style.WidthExpression;

            style.Width = Math.Max(0, forcedWidth);
            style.WidthPercent = null;
            style.WidthExpression = null;

            FormattingContext.Resolve(item).Layout(item, state);

            style.Width = oldWidth;
            style.WidthPercent = oldWidthPercent;
            style.WidthExpression = oldWidthExpression;
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
                !string.IsNullOrWhiteSpace(shorthand))
            {
                var parts = shorthand.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                // margin: T R B L  or  T H B  or  V H  or  ALL
                string target = null;
                switch (side)
                {
                    case "top":    target = parts.Length >= 1 ? parts[0] : null; break;
                    case "right":  target = parts.Length >= 2 ? parts[1] : (parts.Length >= 1 ? parts[0] : null); break;
                    case "bottom": target = parts.Length >= 3 ? parts[2] : (parts.Length >= 1 ? parts[0] : null); break;
                    case "left":   target = parts.Length >= 4 ? parts[3] : (parts.Length >= 2 ? parts[1] : (parts.Length >= 1 ? parts[0] : null)); break;
                }
                if (string.Equals(target?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves the effective cross-axis alignment for a flex item.
        /// align-self on the item overrides align-items on the container.
        /// </summary>
        private static string ResolveItemAlignment(CssComputed itemStyle, string containerAlignItems)
        {
            var containerAlign = string.IsNullOrWhiteSpace(containerAlignItems)
                ? "stretch"
                : containerAlignItems.Trim().ToLowerInvariant();

            if (itemStyle == null)
            {
                return IsBaselineAlignment(containerAlign) ? "baseline" : containerAlign;
            }

            string alignSelf = itemStyle.AlignSelf?.ToLowerInvariant();
            if (string.IsNullOrEmpty(alignSelf) && itemStyle.Map != null)
            {
                itemStyle.Map.TryGetValue("align-self", out var raw);
                alignSelf = raw?.Trim().ToLowerInvariant();
            }

            if (string.IsNullOrEmpty(alignSelf) || alignSelf == "auto")
                return IsBaselineAlignment(containerAlign) ? "baseline" : containerAlign;

            return IsBaselineAlignment(alignSelf) ? "baseline" : alignSelf;
        }

        private static bool IsBaselineAlignment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized == "baseline" ||
                   normalized == "first baseline" ||
                   normalized == "first-baseline" ||
                   normalized == "last baseline" ||
                   normalized == "last-baseline";
        }

        private static float ResolveBaselineOffsetFromMarginTop(LayoutBox item)
        {
            if (item?.Geometry == null)
            {
                return 0f;
            }

            if (LayoutBoxOps.TryResolveBaselineOffsetFromMarginTop(item, out float propagatedBaseline))
            {
                return propagatedBaseline;
            }

            float marginToContent = item.Geometry.ContentBox.Top - item.Geometry.MarginBox.Top;
            if (!float.IsFinite(marginToContent) || marginToContent < 0f)
            {
                marginToContent = 0f;
            }

            // For non-text flex items, synthesize baseline from the lower border edge.
            float fallback = item.Geometry.BorderBox.Bottom - item.Geometry.MarginBox.Top;
            if (float.IsFinite(fallback) && fallback > 0f)
            {
                return fallback;
            }

            fallback = item.Geometry.ContentBox.Bottom - item.Geometry.MarginBox.Top;
            if (float.IsFinite(fallback) && fallback > 0f)
            {
                return fallback;
            }

            fallback = marginToContent + Math.Max(0f, item.Geometry.ContentBox.Height);
            return float.IsFinite(fallback) && fallback > 0f ? fallback : 0f;
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

        private static bool HasOnlyOutOfFlowOrIgnorableDescendants(LayoutBox box)
        {
            if (box == null || box.Children.Count == 0) return false;
            return box.Children.All(IsOutOfFlowOrIgnorableDescendant);
        }

        private static bool IsOutOfFlowOrIgnorableDescendant(LayoutBox box)
        {
            if (box == null) return true;
            if (box.IsOutOfFlow) return true;
            if (box is TextLayoutBox textBox)
            {
                return string.IsNullOrWhiteSpace(textBox.TextContent);
            }
            if (box.SourceNode is Text textNode)
            {
                return string.IsNullOrWhiteSpace(textNode.Data);
            }
            if (box.Children.Count == 0)
            {
                return false;
            }
            return box.Children.All(IsOutOfFlowOrIgnorableDescendant);
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

        private static bool IsWrapEnabled(CssComputed style)
        {
            string wrap = style?.FlexWrap;
            if (string.IsNullOrWhiteSpace(wrap) &&
                style?.Map != null &&
                style.Map.TryGetValue("flex-wrap", out var rawWrap))
            {
                wrap = rawWrap;
            }

            if (string.IsNullOrWhiteSpace(wrap))
            {
                return false;
            }

            wrap = wrap.Trim();
            return wrap.Equals("wrap", StringComparison.OrdinalIgnoreCase) ||
                   wrap.Equals("wrap-reverse", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Clamp each flex item's main-axis size to its min/max constraints.
        /// Per CSS Flexbox §9.8, min/max are applied AFTER flex-grow/shrink
        /// but BEFORE final placement.
        /// </summary>
        private static void ClampFlexItemMainSizes(List<LayoutBox> items, bool isRow,
            float containerMainSize, LayoutState state)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var style = item.ComputedStyle;
                if (style == null) continue;

                float currentMainSize = isRow
                    ? item.Geometry.ContentBox.Width
                    : item.Geometry.ContentBox.Height;
                if (currentMainSize <= 0f) continue;

                // Resolve min constraint for main axis
                float minMain = 0f;
                if (isRow)
                {
                    if (style.MinWidth.HasValue)
                        minMain = (float)style.MinWidth.Value;
                    else if (style.MinWidthPercent.HasValue == true && containerMainSize > 0)
                        minMain = (float)(style.MinWidthPercent.Value / 100.0 * containerMainSize);
                }
                else
                {
                    if (style.MinHeight.HasValue)
                        minMain = (float)style.MinHeight.Value;
                    else if (style.MinHeightPercent.HasValue == true && containerMainSize > 0)
                        minMain = (float)(style.MinHeightPercent.Value / 100.0 * containerMainSize);
                }

                if (minMain > 0f && string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase))
                {
                    var chrome = isRow
                        ? item.Geometry.Padding.Left + item.Geometry.Padding.Right + item.Geometry.Border.Left + item.Geometry.Border.Right
                        : item.Geometry.Padding.Top + item.Geometry.Padding.Bottom + item.Geometry.Border.Top + item.Geometry.Border.Bottom;
                    minMain = Math.Max(0f, minMain - (float)chrome);
                }

                // Resolve max constraint for main axis
                float maxMain = float.PositiveInfinity;
                if (isRow)
                {
                    if (style.MaxWidth.HasValue)
                        maxMain = (float)style.MaxWidth.Value;
                    else if (style.MaxWidthPercent.HasValue == true && containerMainSize > 0)
                        maxMain = (float)(style.MaxWidthPercent.Value / 100.0 * containerMainSize);
                }
                else
                {
                    if (style.MaxHeight.HasValue)
                        maxMain = (float)style.MaxHeight.Value;
                    else if (style.MaxHeightPercent.HasValue == true && containerMainSize > 0)
                        maxMain = (float)(style.MaxHeightPercent.Value / 100.0 * containerMainSize);
                }

                if (float.IsFinite(maxMain) && string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase))
                {
                    var chrome = isRow
                        ? item.Geometry.Padding.Left + item.Geometry.Padding.Right + item.Geometry.Border.Left + item.Geometry.Border.Right
                        : item.Geometry.Padding.Top + item.Geometry.Padding.Bottom + item.Geometry.Border.Top + item.Geometry.Border.Bottom;
                    maxMain = Math.Max(0f, maxMain - (float)chrome);
                }

                // Clamp and re-layout if needed
                float clampedSize = currentMainSize;
                bool needsClamp = false;
                if (currentMainSize < minMain && minMain > 0f)
                {
                    clampedSize = minMain;
                    needsClamp = true;
                }
                if (currentMainSize > maxMain && float.IsFinite(maxMain))
                {
                    clampedSize = maxMain;
                    needsClamp = true;
                }

                if (needsClamp)
                {
                    if (isRow)
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(item, clampedSize, item.Geometry.ContentBox.Height);
                        var reState = state.Clone();
                        reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, item.Geometry.ContentBox.Height);
                        reState.ContainingBlockWidth = clampedSize;
                        reState.ContainingBlockHeight = item.Geometry.ContentBox.Height;
                        LayoutWithForcedWidth(item, reState, clampedSize);
                    }
                    else
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(item, item.Geometry.ContentBox.Width, clampedSize);
                        var reState = state.Clone();
                        reState.AvailableSize = new SKSize(item.Geometry.MarginBox.Width, clampedSize);
                        reState.ContainingBlockWidth = item.Geometry.ContentBox.Width;
                        reState.ContainingBlockHeight = clampedSize;
                        LayoutWithForcedHeight(item, reState, clampedSize);
                    }
                }
            }
        }
    }
    
    // Helper to avoid code duplication with BlockContext for box sizing
    // LayoutBoxOps moved to FenBrowser.FenEngine.Layout.Contexts.LayoutBoxOps.cs
}
