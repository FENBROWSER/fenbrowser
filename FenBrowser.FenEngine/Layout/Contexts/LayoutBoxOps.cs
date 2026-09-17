using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout.Contexts // Namespace matching usage
{
    public static class LayoutBoxOps
    {
        public static void ComputeBoxModelFromContent(LayoutBox box, float contentW, float contentH)
        {
            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + contentW, top + contentH);
            SyncBoxes(box.Geometry);
        }

        /// <summary>
        /// Recomputes PaddingBox, BorderBox, and MarginBox from the current ContentBox
        /// plus the Padding/Border/Margin thickness values stored on the geometry.
        /// </summary>
        public static void SyncBoxes(BoxModel geometry)
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

        public static void SetPosition(LayoutBox box, float x, float y)
        {
             // Align the outermost resolved box top-left to x,y.
             // Some layout paths populate ContentBox/BorderBox before MarginBox is
             // synchronized; anchoring exclusively on MarginBox then shifts descendants
             // too far to the right/bottom when the outer boxes are still empty.
             var anchor = GetPositionAnchor(box.Geometry);
             float dx = x - anchor.X;
             float dy = y - anchor.Y;
             
             ShiftBoxModel(box.Geometry, dx, dy);
        }

        /// <summary>
        /// Moves <paramref name="box"/> so its content origin sits at (0, 0), carrying
        /// its descendants with it.
        /// </summary>
        /// <remarks>
        /// This used to zero every descendant independently, which did not move the
        /// subtree — it flattened it, collapsing every box onto its own origin. Callers
        /// reset a subtree immediately before laying it out again, and
        /// FormattingContext.Layout may answer that from its cache, whose stated
        /// contract is that "children retain their relative offsets". The flatten broke
        /// exactly that invariant, so a cached re-layout left the subtree collapsed:
        /// a table cell's vertical-align:middle offset was computed correctly and then
        /// wiped, which is why the reCAPTCHA checkbox sat in the top-left corner of its
        /// cell instead of centred.
        /// </remarks>
        public static void ResetSubtreeToOrigin(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            ShiftSubtreeUniform(box, -box.Geometry.ContentBox.Left, -box.Geometry.ContentBox.Top);
        }

        // Same traversal the reset has always used — no visited set, so it stays
        // allocation-free on a hot path — but every box moves by the SAME delta
        // instead of each collapsing onto its own origin.
        private static void ShiftSubtreeUniform(LayoutBox box, float dx, float dy)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            ShiftBoxModel(box.Geometry, dx, dy);

            var children = box.Children;
            for (var index = 0; index < children.Count; index++)
            {
                ShiftSubtreeUniform(children[index], dx, dy);
            }
        }

        [ThreadStatic] private static long s_shiftSubtreeRoots;
        [ThreadStatic] private static long s_shiftSubtreeNodes;

        /// <summary>
        /// ShiftSubtree root calls and total boxes moved since <see cref="ResetShiftCounters"/>.
        /// Nodes far above the box count means subtrees are being re-shifted.
        /// </summary>
        internal static (long Roots, long Nodes) ShiftCounters => (s_shiftSubtreeRoots, s_shiftSubtreeNodes);

        internal static void ResetShiftCounters()
        {
            s_shiftSubtreeRoots = 0;
            s_shiftSubtreeNodes = 0;
        }

        /// <summary>
        /// Subtree depth past which ShiftSubtree starts tracking visited boxes. A box
        /// tree is a tree, so the guard only matters for a malformed one; no real
        /// document nests this deep, and FormattingContext caps layout at 120.
        /// </summary>
        private const int ShiftCycleGuardDepth = 256;

        public static void ShiftSubtree(LayoutBox box, float dx, float dy)
        {
            s_shiftSubtreeRoots++;
            ShiftSubtree(box, dx, dy, depth: 0, visited: null);
        }

        private static void ShiftSubtree(LayoutBox box, float dx, float dy, int depth, HashSet<LayoutBox> visited)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            // Allocating a visited set per shift, and hashing every box into it, cost
            // more than the shift itself: a single layout pass of a large article ran
            // ~986,000 shifts over ~9.5M boxes. Only start paying for the cycle guard
            // at a depth a well-formed box tree cannot reach.
            if (visited == null && depth >= ShiftCycleGuardDepth)
            {
                visited = new HashSet<LayoutBox>();
            }

            if (visited != null && !visited.Add(box))
            {
                return;
            }

            s_shiftSubtreeNodes++;
            ShiftBoxModel(box.Geometry, dx, dy);

            var children = box.Children;
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                if (IsFixedPosition(child?.ComputedStyle))
                {
                    continue;
                }

                ShiftSubtree(child, dx, dy, depth + 1, visited);
            }
        }

        /// <summary>
        /// Whether a box is <c>position: fixed</c>, without the string allocation
        /// GetEffectivePosition pays to normalise every other keyword — this runs once
        /// per box per shift.
        /// </summary>
        private static bool IsFixedPosition(CssComputed style)
        {
            if (style == null)
            {
                return false;
            }

            var position = style.Position;
            if (string.IsNullOrWhiteSpace(position) &&
                (style.Map == null ||
                 !style.Map.TryGetValue("position", out position) ||
                 string.IsNullOrWhiteSpace(position)))
            {
                return false;
            }

            return position.AsSpan().Trim().Equals("fixed", StringComparison.OrdinalIgnoreCase);
        }

        public static void ShiftDescendants(LayoutBox box, float dx, float dy)
        {
            if (box == null)
            {
                return;
            }

            foreach (var child in box.Children)
            {
                if (IsFixedPosition(child?.ComputedStyle))
                {
                    continue;
                }

                // depth 1: the caller's box stands in for the root of the walk, so a
                // child subtree picks up the cycle guard at the same depth it would
                // through ShiftSubtree.
                s_shiftSubtreeRoots++;
                ShiftSubtree(child, dx, dy, depth: 1, visited: null);
            }
        }

        public static void PositionSubtree(LayoutBox box, float x, float y, LayoutState state)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            var relativeOffset = ResolveRelativeOffset(box, box.ComputedStyle, state);
            float targetX = x + relativeOffset.X;
            float targetY = y + relativeOffset.Y;
            var anchor = GetPositionAnchor(box.Geometry);
            float dx = targetX - anchor.X;
            float dy = targetY - anchor.Y;

            ShiftSubtree(box, dx, dy);
        }

        /// <summary>
        /// Translates a subtree by (dx, dy) without resolving relative offsets.
        /// Used by sticky positioning to apply the sticky constraint delta.
        /// </summary>
        public static void TranslateSubtree(LayoutBox box, float dx, float dy)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            ShiftSubtree(box, dx, dy);
        }

        /// <summary>
        /// Resolves the first available text baseline for a layout box relative to its margin-box top.
        /// Returns false when no text-backed baseline can be resolved.
        /// </summary>
        public static bool TryResolveBaselineOffsetFromMarginTop(LayoutBox box, out float baselineOffset)
        {
            baselineOffset = 0f;
            if (box?.Geometry == null)
            {
                return false;
            }

            if (TryResolveLocalBaselineOffset(box.Geometry, out float localOffset))
            {
                baselineOffset = ClampBaselineOffset(localOffset, box.Geometry.MarginBox.Height);
                return baselineOffset > 0f;
            }

            float bestDescendantOffset = float.MaxValue;
            foreach (var child in box.Children)
            {
                if (!TryResolveBaselineOffsetFromMarginTop(child, out float childOffset))
                {
                    continue;
                }

                float relativeOffset = (child.Geometry.MarginBox.Top - box.Geometry.MarginBox.Top) + childOffset;
                if (!float.IsFinite(relativeOffset) || relativeOffset < 0f)
                {
                    continue;
                }

                bestDescendantOffset = Math.Min(bestDescendantOffset, relativeOffset);
            }

            if (bestDescendantOffset < float.MaxValue)
            {
                baselineOffset = ClampBaselineOffset(bestDescendantOffset, box.Geometry.MarginBox.Height);
                return baselineOffset > 0f;
            }

            return false;
        }

        public static void ShiftBoxModel(BoxModel model, float dx, float dy)
        {
            model.ContentBox = OffsetRect(model.ContentBox, dx, dy);
            model.PaddingBox = OffsetRect(model.PaddingBox, dx, dy);
            model.BorderBox = OffsetRect(model.BorderBox, dx, dy);
            model.MarginBox = OffsetRect(model.MarginBox, dx, dy);
        }
        
        private static SKRect OffsetRect(SKRect r, float dx, float dy)
        {
            return new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
        }

        private static SKPoint GetPositionAnchor(BoxModel model)
        {
            if (model == null)
            {
                return SKPoint.Empty;
            }

            if (HasResolvedRect(model.MarginBox))
            {
                return new SKPoint(model.MarginBox.Left, model.MarginBox.Top);
            }

            if (HasResolvedRect(model.BorderBox))
            {
                return new SKPoint(model.BorderBox.Left, model.BorderBox.Top);
            }

            if (HasResolvedRect(model.PaddingBox))
            {
                return new SKPoint(model.PaddingBox.Left, model.PaddingBox.Top);
            }

            return new SKPoint(model.ContentBox.Left, model.ContentBox.Top);
        }

        private static bool HasResolvedRect(SKRect rect)
        {
            return float.IsFinite(rect.Left) &&
                   float.IsFinite(rect.Top) &&
                   float.IsFinite(rect.Right) &&
                   float.IsFinite(rect.Bottom) &&
                   (Math.Abs(rect.Left) > 0.01f ||
                    Math.Abs(rect.Top) > 0.01f ||
                    Math.Abs(rect.Right) > 0.01f ||
                    Math.Abs(rect.Bottom) > 0.01f);
        }

        private static bool TryResolveLocalBaselineOffset(BoxModel geometry, out float baselineOffset)
        {
            baselineOffset = 0f;
            if (geometry == null)
            {
                return false;
            }

            float marginToContent = geometry.ContentBox.Top - geometry.MarginBox.Top;
            if (!float.IsFinite(marginToContent) || marginToContent < 0f)
            {
                marginToContent = 0f;
            }

            if (geometry.Lines != null && geometry.Lines.Count > 0)
            {
                var firstLine = geometry.Lines[0];
                float lineBaseline = marginToContent + firstLine.Origin.Y + firstLine.Baseline;
                if (float.IsFinite(lineBaseline) && lineBaseline > 0f)
                {
                    baselineOffset = lineBaseline;
                    return true;
                }
            }

            float baseline = geometry.Baseline;
            if (!float.IsFinite(baseline) || baseline <= 0f)
            {
                baseline = geometry.Ascent;
            }

            if (float.IsFinite(baseline) && baseline > 0f)
            {
                baselineOffset = marginToContent + baseline;
                return baselineOffset > 0f;
            }

            return false;
        }

        private static float ClampBaselineOffset(float baselineOffset, float marginHeight)
        {
            if (!float.IsFinite(baselineOffset) || baselineOffset <= 0f)
            {
                return 0f;
            }

            if (float.IsFinite(marginHeight) && marginHeight > 0f)
            {
                return Math.Min(marginHeight, baselineOffset);
            }

            return baselineOffset;
        }

        // What a percentage on a box resolves against: the content width of its
        // containing block, which is the nearest ancestor that is neither an inline
        // box nor an anonymous block - neither of those is a containing block.
        //
        // The layout state was the wrong source for this. It carried the viewport's
        // width rather than the block the box actually sits in, and once a box had
        // been sized the state was carrying *that* width on the next pass, so the
        // error squared: reCAPTCHA's challenge tiles are images at width:300%
        // inside a 100px wrapper and came out 5760px wide, then 17280px, instead of
        // 300px. Every tile then framed a sliver of a hugely magnified image, which
        // is why the challenge rendered as smeared bands rather than photographs.
        /// <summary>
        /// A percentage width resolves against the containing block only while that
        /// block has a definite width. Inside a shrink-to-fit probe neither the
        /// available width nor the containing block is known, so the percentage is
        /// cyclic (CSS Sizing 3 §5.2.1) and the box sizes as auto — which also means
        /// its own children are laid out in a shrink-to-fit pass rather than against
        /// a probe width they would then be measured back from.
        /// </summary>
        // Inline boxes and anonymous blocks are never containing blocks. An atomic inline
        // (inline-block, inline-flex, inline-grid, inline-table) is a block container that
        // sits on a line, and it is one (CSS 2.1 §10.1). It is still an InlineBox in the
        // box tree, and skipping it made bing.com's `li::after { display:block; width:100% }`
        // tab underline resolve against the whole scope bar instead of its tab.
        private static bool IsPercentageContainingBlock(LayoutBox ancestor)
        {
            if (ancestor is AnonymousBlockBox)
            {
                return false;
            }

            if (ancestor is InlineBox)
            {
                string display = ancestor.ComputedStyle?.Display;
                return !string.IsNullOrEmpty(display) &&
                       !string.Equals(display, "inline", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(display, "contents", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        internal static bool IsCyclicPercentageWidth(CssComputed style, LayoutState state)
        {
            if (style == null || !style.WidthPercent.HasValue || style.Width.HasValue)
            {
                return false;
            }

            bool availableUnconstrained = float.IsInfinity(state.AvailableSize.Width) || float.IsNaN(state.AvailableSize.Width);
            bool containingBlockDefinite = float.IsFinite(state.ContainingBlockWidth) && state.ContainingBlockWidth > 0f;
            return availableUnconstrained && !containingBlockDefinite;
        }

        internal static float ResolvePercentageBaseWidth(LayoutBox box, LayoutState state)
        {
            for (var ancestor = box?.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (!IsPercentageContainingBlock(ancestor))
                {
                    continue;
                }

                if (ancestor.ComputedStyle?.Width is double specified && specified > 0d)
                {
                    return (float)specified;
                }

                float width = ancestor.Geometry?.ContentBox.Width ?? 0f;
                return width > 0f
                    ? width
                    : (state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth);
            }

            return state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
        }

        // A percentage height needs a containing block whose own height is definite,
        // so only an ancestor that states one answers for it. Anything else leaves
        // the percentage to behave as auto, which is what the fallback does.
        internal static float ResolvePercentageBaseHeight(LayoutBox box, LayoutState state)
        {
            for (var ancestor = box?.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (!IsPercentageContainingBlock(ancestor))
                {
                    continue;
                }

                // Prefer the height the ancestor states over the one it has been
                // laid out to: inline content is measured before the block it sits
                // in has resolved its own box, so the laid-out value reads 0 here
                // and the percentage would fall through to the viewport.
                if (ancestor.ComputedStyle?.Height is double specified && specified > 0d)
                {
                    return (float)specified;
                }

                float height = ancestor.Geometry?.ContentBox.Height ?? 0f;
                bool definite = ancestor.ComputedStyle != null &&
                                (ancestor.ComputedStyle.HeightPercent.HasValue ||
                                 !string.IsNullOrEmpty(ancestor.ComputedStyle.HeightExpression));
                if (definite && height > 0f)
                {
                    return height;
                }

                break;
            }

            return state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
        }

        /// <summary>
        /// CSS 2.2 §10.5: a percentage height resolves against the containing block's height
        /// only when that height does not depend on content; otherwise it computes to 'auto'.
        /// Returns NaN in that case instead of falling back to the viewport the way
        /// <see cref="ResolvePercentageBaseHeight"/> does. Flex/grid items and out-of-flow boxes
        /// keep the height their formatting context established.
        /// </summary>
        internal static float ResolveDefinitePercentageBaseHeight(LayoutBox box, LayoutState state)
        {
            for (var ancestor = box?.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is InlineBox || ancestor is AnonymousBlockBox)
                {
                    continue;
                }

                var style = ancestor.ComputedStyle;
                bool heightIsAuto = style == null ||
                                    (!style.Height.HasValue &&
                                     !style.HeightPercent.HasValue &&
                                     string.IsNullOrEmpty(style.HeightExpression));
                if (heightIsAuto && !IsFlexOrGridItem(ancestor) &&
                    (!ancestor.IsOutOfFlow || !HasBothVerticalInsets(style)))
                {
                    return float.NaN;
                }

                // The basis is definite, but its used value is only known from the ancestor's
                // length or from the containing-block height its formatting context handed down.
                // The ancestor's laid-out box is not a basis: it may already contain this box,
                // and bing.com's mic icon grew 5.5px on every pass against its own line box.
                // Falling back to the viewport made the same icon 800px tall. With neither
                // known, the percentage behaves as auto (CSS 2.2 §10.5).
                if (style?.Height is double specified && specified > 0d)
                {
                    return (float)specified;
                }

                return state.ContainingBlockHeight > 0f ? state.ContainingBlockHeight : float.NaN;
            }

            // No block ancestor: the initial containing block, which is the viewport.
            return state.ViewportHeight > 0f ? state.ViewportHeight : float.NaN;
        }

        // An absolutely positioned box with auto height has a definite height only when both
        // top and bottom are set: CSS 2.2 §10.6.4 then solves the height from the insets.
        private static bool HasBothVerticalInsets(CssComputed style) =>
            style != null &&
            (style.Top.HasValue || style.TopPercent.HasValue) &&
            (style.Bottom.HasValue || style.BottomPercent.HasValue);

        private static bool IsFlexOrGridItem(LayoutBox box)
        {
            string display = box.Parent?.ComputedStyle?.Display;
            return display != null &&
                   (display.Contains("flex", StringComparison.OrdinalIgnoreCase) ||
                    display.Contains("grid", StringComparison.OrdinalIgnoreCase));
        }

        // CSS 2.1 9.4.3: percentage left/right resolve against the containing
        // block's width and top/bottom against its height - not against the box's
        // own size, and not against the viewport. reCAPTCHA nudges each challenge
        // tile into place with left/top:-100%, so getting this wrong moved every
        // tile by its own width instead of the wrapper's.
        private static SKPoint ResolveRelativeOffset(LayoutBox box, CssComputed style, LayoutState state)
        {
            if (style == null || !string.Equals(LayoutStyleResolver.GetEffectivePosition(style), "relative", StringComparison.OrdinalIgnoreCase))
            {
                return SKPoint.Empty;
            }

            float cbWidth = ResolvePercentageBaseWidth(box, state);
            if (!float.IsFinite(cbWidth) || cbWidth <= 0f)
            {
                cbWidth = state.AvailableSize.Width;
            }
            if (!float.IsFinite(cbWidth) || cbWidth <= 0f)
            {
                cbWidth = state.ViewportWidth;
            }

            float cbHeight = ResolvePercentageBaseHeight(box, state);
            if (!float.IsFinite(cbHeight) || cbHeight <= 0f)
            {
                cbHeight = state.AvailableSize.Height;
            }
            if (!float.IsFinite(cbHeight) || cbHeight <= 0f)
            {
                cbHeight = state.ViewportHeight;
            }

            float dx = 0f;
            if (TryResolveInsetOffset(style, "left", cbWidth, out float leftOffset))
            {
                dx = leftOffset;
            }
            else if (TryResolveInsetOffset(style, "right", cbWidth, out float rightOffset))
            {
                dx = -rightOffset;
            }

            float dy = 0f;
            if (TryResolveInsetOffset(style, "top", cbHeight, out float topOffset))
            {
                dy = topOffset;
            }
            else if (TryResolveInsetOffset(style, "bottom", cbHeight, out float bottomOffset))
            {
                dy = -bottomOffset;
            }

            return new SKPoint(dx, dy);
        }

        private static bool TryResolveInsetOffset(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            bool mapHasAuthoredInset = MapHasAuthoredInset(style?.Map, side);
            if (!HasExplicitInset(style, side))
            {
                return false;
            }

            if (TryResolveInsetFromPrimaryKey(style, side, containingSize, out offset))
            {
                return true;
            }

            if (TryResolveInsetFromLogicalKey(style, side, containingSize, out offset))
            {
                return true;
            }

            if (TryResolveInsetFromShorthand(style, "inset", side, containingSize, out offset))
            {
                return true;
            }

            string axisShorthand = side == "top" || side == "bottom" ? "inset-block" : "inset-inline";
            if (TryResolveInsetFromShorthand(style, axisShorthand, side, containingSize, out offset))
            {
                return true;
            }

            // If this side was explicitly authored but token parsing did not resolve
            // a concrete length (e.g. em/calc forms), use the computed typed projection.
            if (mapHasAuthoredInset && TryResolveInsetFromTypedProjection(style, side, containingSize, out offset))
            {
                return true;
            }

            // Fallback only for map-less synthetic styles (e.g. tests directly setting
            // CssComputed.Top/Left without authored map keys).
            if (style?.Map == null || style.Map.Count == 0)
            {
                return TryResolveInsetFromTypedProjection(style, side, containingSize, out offset);
            }

            return false;
        }

        private static bool TryResolveInsetFromTypedProjection(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style == null)
            {
                return false;
            }

            switch (side)
            {
                case "top":
                    if (style.Top.HasValue)
                    {
                        offset = (float)style.Top.Value;
                        return true;
                    }
                    break;
                case "right":
                    if (style.Right.HasValue)
                    {
                        offset = (float)style.Right.Value;
                        return true;
                    }
                    break;
                case "bottom":
                    if (style.Bottom.HasValue)
                    {
                        offset = (float)style.Bottom.Value;
                        return true;
                    }
                    break;
                case "left":
                    if (style.Left.HasValue)
                    {
                        offset = (float)style.Left.Value;
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static bool TryResolveInsetFromPrimaryKey(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style?.Map == null || !style.Map.TryGetValue(side, out string raw))
            {
                return false;
            }

            return TryParseInsetValue(raw, containingSize, out offset);
        }

        private static bool TryResolveInsetFromLogicalKey(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style?.Map == null)
            {
                return false;
            }

            string logicalKey = side switch
            {
                "top" => "inset-block-start",
                "bottom" => "inset-block-end",
                "left" => "inset-inline-start",
                "right" => "inset-inline-end",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(logicalKey) || !style.Map.TryGetValue(logicalKey, out string raw))
            {
                return false;
            }

            return TryParseInsetValue(raw, containingSize, out offset);
        }

        private static bool TryResolveInsetFromShorthand(CssComputed style, string shorthandKey, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style?.Map == null || !style.Map.TryGetValue(shorthandKey, out string raw) || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string[] tokens = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return false;
            }

            string selectedToken;
            if (string.Equals(shorthandKey, "inset-block", StringComparison.OrdinalIgnoreCase))
            {
                selectedToken = side switch
                {
                    "top" => tokens[0],
                    "bottom" => tokens.Length > 1 ? tokens[1] : tokens[0],
                    _ => string.Empty
                };
            }
            else if (string.Equals(shorthandKey, "inset-inline", StringComparison.OrdinalIgnoreCase))
            {
                selectedToken = side switch
                {
                    "left" => tokens[0],
                    "right" => tokens.Length > 1 ? tokens[1] : tokens[0],
                    _ => string.Empty
                };
            }
            else
            {
                selectedToken = side switch
                {
                    "top" => tokens[0],
                    "right" => tokens.Length == 1 ? tokens[0] : tokens.Length == 2 ? tokens[1] : tokens[1],
                    "bottom" => tokens.Length == 1 ? tokens[0] : tokens.Length == 2 ? tokens[0] : tokens.Length == 3 ? tokens[2] : tokens[2],
                    "left" => tokens.Length == 1 ? tokens[0] : tokens.Length == 2 ? tokens[1] : tokens.Length == 3 ? tokens[1] : tokens[3],
                    _ => string.Empty
                };
            }

            if (string.IsNullOrWhiteSpace(selectedToken))
            {
                return false;
            }

            return TryParseInsetValue(selectedToken, containingSize, out offset);
        }

        private static bool TryParseInsetValue(string raw, float containingSize, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string token = raw.Trim();
            if (token.Length == 0 ||
                token.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("revert", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                string px = token.Substring(0, token.Length - 2).Trim();
                if (float.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedPx))
                {
                    value = parsedPx;
                    return true;
                }
            }

            if (token.EndsWith("%", StringComparison.OrdinalIgnoreCase))
            {
                string percent = token.Substring(0, token.Length - 1).Trim();
                if (containingSize > 0f &&
                    float.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedPercent))
                {
                    value = containingSize * (parsedPercent / 100f);
                    return true;
                }
            }

            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float rawNumber))
            {
                value = rawNumber;
                return true;
            }

            return false;
        }

        private static bool HasExplicitInset(CssComputed style, string side)
        {
            if (style == null || string.IsNullOrWhiteSpace(side))
            {
                return false;
            }

            bool mapHasAuthoredInset = MapHasAuthoredInset(style.Map, side);
            if (mapHasAuthoredInset)
            {
                return true;
            }

            // If author map exists but doesn't contain this inset, typed fields can be stale
            // from previous style states and must be ignored.
            if (style.Map != null && style.Map.Count > 0)
            {
                return false;
            }

            return side switch
            {
                "top" => style.Top.HasValue || style.TopPercent.HasValue,
                "right" => style.Right.HasValue || style.RightPercent.HasValue,
                "bottom" => style.Bottom.HasValue || style.BottomPercent.HasValue,
                "left" => style.Left.HasValue || style.LeftPercent.HasValue,
                _ => false
            };
        }

        private static bool MapHasAuthoredInset(System.Collections.Generic.IReadOnlyDictionary<string, string> map, string side)
        {
            if (map == null || map.Count == 0 || string.IsNullOrWhiteSpace(side))
            {
                return false;
            }

            if (map.ContainsKey(side) || map.ContainsKey("inset") || map.ContainsKey($"inset-{side}"))
            {
                return true;
            }

            return side switch
            {
                "top" => map.ContainsKey("inset-block-start"),
                "bottom" => map.ContainsKey("inset-block-end"),
                "left" => map.ContainsKey("inset-inline-start"),
                "right" => map.ContainsKey("inset-inline-end"),
                _ => false
            };
        }
    }
}
