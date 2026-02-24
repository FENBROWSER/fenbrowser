using System;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Implements the Block Formatting Context (BFC).
    /// Lays out block-level boxes vertically.
    /// </summary>
    public class BlockFormattingContext : FormattingContext
    {
        private static BlockFormattingContext _instance;
        public static BlockFormattingContext Instance => _instance ??= new BlockFormattingContext();

        protected override void LayoutCore(LayoutBox box, LayoutState state)
        {
            var blockBox = box;
            
            // DEBUG: Entry log
            string dbgTag = (blockBox.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName ?? "?";
            FenLogger.Info($"[BFC-ENTRY] <{dbgTag}> AvailH={state.AvailableSize.Height} ViewH={state.ViewportHeight}", LogCategory.Layout);

            // 1. Resolve Width
            ResolveWidth(blockBox, state);

            // 2. Prepare for Child Layout
            // In the new pipeline (BoxTreeBuilder -> FormattingContext), ContentBox starts at (0,0)
            // and SyncBoxes extends padding/border outward. Children are placed at (0, currentY)
            // relative to the ContentBox origin. No padding+border offset is needed here because
            // CollectBoxesAbsolute already accounts for the ContentBox position.
            float yOffset = 0;
            float xOffset = 0;
            float contentWidth = blockBox.Geometry.ContentBox.Width;
            bool shrinkToFitPass =
                (blockBox.ComputedStyle == null ||
                 (!blockBox.ComputedStyle.Width.HasValue &&
                  !blockBox.ComputedStyle.WidthPercent.HasValue &&
                  string.IsNullOrEmpty(blockBox.ComputedStyle.WidthExpression)))
                && float.IsInfinity(state.AvailableSize.Width);
            float childFlowWidth = shrinkToFitPass ? float.PositiveInfinity : contentWidth;

            // 3. Iterate Children
            var outOfFlow = new List<LayoutBox>();
            var floatManager = new FloatManager();
            const float floatEpsilon = 0.5f;
            const int maxFloatPlacementIterations = 256;
            
            // Cursor relative to content box top
            float currentY = 0; 
            float maxBottom = 0;
            float lastMarginBottom = 0;
            bool isFirstChild = true;

            // Does parent prevent top margin collapse? (Padding/border/overflow etc)
            bool parentPreventsTopCollapse = (blockBox.Geometry.Padding.Top > 0 || blockBox.Geometry.Border.Top > 0);
            
            foreach (var child in blockBox.Children)
            {
                state.Deadline?.Check();

                if (child.IsOutOfFlow)
                {
                    outOfFlow.Add(child);
                    continue;
                }

                // Whitespace-only text between block-level siblings should not create
                // anonymous blocks with line-height artifacts in normal block flow.
                if (child is TextLayoutBox textChild)
                {
                    string textData = (textChild.SourceNode as Text)?.Data ?? textChild.TextContent ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(textData))
                    {
                        if (textChild.Geometry == null)
                        {
                            textChild.Geometry = new BoxModel();
                        }

                        float tx = textChild.Geometry.ContentBox.Left;
                        float ty = textChild.Geometry.ContentBox.Top;
                        textChild.Geometry.ContentBox = new SKRect(tx, ty, tx, ty);
                        textChild.Geometry.Padding = new Thickness();
                        textChild.Geometry.Border = new Thickness();
                        textChild.Geometry.Margin = new Thickness();
                        textChild.Geometry.Lines = new List<ComputedTextLine>();
                        SyncBoxes(textChild.Geometry);
                        continue;
                    }
                }

                // Floats and Clearance
                string floatStyle = child.ComputedStyle?.Float?.ToLowerInvariant() ?? "none";
                string clearStyle = child.ComputedStyle?.Clear?.ToLowerInvariant() ?? "none";
                
                if (clearStyle != "none")
                {
                    float clearY = floatManager.GetClearanceY(clearStyle, currentY + lastMarginBottom);
                    if (clearY > currentY + lastMarginBottom)
                    {
                        currentY = clearY;
                        lastMarginBottom = 0; // Clearance resets collapsing
                    }
                }

                if (floatStyle == "left" || floatStyle == "right")
                {
                    // Float auto-width should probe with unconstrained inline size (shrink-to-fit).
                    bool floatAutoWidth =
                        child.ComputedStyle == null ||
                        (!child.ComputedStyle.Width.HasValue &&
                         !child.ComputedStyle.WidthPercent.HasValue &&
                         string.IsNullOrEmpty(child.ComputedStyle.WidthExpression));

                    float floatChildConstraint = floatAutoWidth ? float.PositiveInfinity : childFlowWidth;
                    var childState = CreateChildState(floatChildConstraint, state);
                    FormattingContext.Resolve(child).Layout(child, childState);

                    float floatWidth = Math.Max(0f, child.Geometry.MarginBox.Width);
                    float floatHeight = Math.Max(0f, child.Geometry.MarginBox.Height);
                    float containerInline = contentWidth;

                    if (!float.IsFinite(containerInline) || containerInline <= 0f)
                    {
                        // During probe passes content width may be unresolved; use viewport as a safe clamp.
                        containerInline = Math.Max(floatWidth, state.ViewportWidth);
                    }

                    float placementY = currentY + lastMarginBottom;
                    float fx = xOffset;
                    int placementGuard = 0;

                    while (placementGuard++ < maxFloatPlacementIterations)
                    {
                        var space = floatManager.GetAvailableSpace(
                            placementY,
                            Math.Max(1f, floatHeight),
                            containerInline);

                        float availableBand = space.AvailableWidth;
                        if (!float.IsFinite(availableBand))
                        {
                            availableBand = containerInline;
                        }

                        if (floatWidth <= 0f || availableBand + floatEpsilon >= floatWidth)
                        {
                            if (floatStyle == "right")
                            {
                                fx = xOffset + Math.Max(space.LeftOffset, containerInline - space.RightOffset - floatWidth);
                            }
                            else
                            {
                                fx = xOffset + space.LeftOffset;
                            }

                            break;
                        }

                        float nextY = floatManager.GetNextVerticalPosition(placementY);
                        if (nextY <= placementY + floatEpsilon)
                        {
                            placementY += Math.Max(1f, floatHeight);
                            break;
                        }

                        placementY = nextY;
                    }

                    LayoutBoxOps.SetPosition(child, fx, yOffset + placementY);
                    floatManager.AddFloat(child, floatStyle == "left");
                    
                    // [Compliance] Ensure float contributes to container height if it establishes a BFC
                    // For now, we always include it if height is auto
                    maxBottom = Math.Max(maxBottom, placementY + child.Geometry.MarginBox.Height);
                }
                else
                {
                    // Normal Flow Block
                    var childState = CreateChildState(childFlowWidth, state);
                    FormattingContext.Resolve(child).Layout(child, childState);
                    
                    float childMarginTop = (float)child.Geometry.Margin.Top;
                    float childMarginBottom = (float)child.Geometry.Margin.Bottom;

                    // MARGIN COLLAPSING
                    float collapsedMargin = 0;
                    if (isFirstChild)
                    {
                        if (parentPreventsTopCollapse)
                        {
                            collapsedMargin = childMarginTop;
                        }
                        else
                        {
                            // Bubbles up to parent - child sits at 0 relative to content box
                            collapsedMargin = 0;
                            // (We should ideally update parent's bubbled margin, but BFC usually handles it)
                        }
                    }
                    else
                    {
                        collapsedMargin = Math.Max(lastMarginBottom, childMarginTop);
                    }

                    currentY += collapsedMargin;

                    float childY = currentY;
                    float childX = xOffset;

                    if (floatManager.HasFloats && float.IsFinite(contentWidth) && contentWidth > 0f)
                    {
                        float placementHeight = Math.Max(1f, child.Geometry.MarginBox.Height);
                        float requiredInline = Math.Max(0f, child.Geometry.MarginBox.Width);
                        bool explicitInlineSize =
                            child.ComputedStyle != null &&
                            (child.ComputedStyle.Width.HasValue ||
                             child.ComputedStyle.WidthPercent.HasValue ||
                             !string.IsNullOrEmpty(child.ComputedStyle.WidthExpression));

                        int placementGuard = 0;
                        while (placementGuard++ < maxFloatPlacementIterations)
                        {
                            var space = floatManager.GetAvailableSpace(childY, placementHeight, contentWidth);
                            childX = xOffset + space.LeftOffset;

                            if (!explicitInlineSize || requiredInline <= 0f || space.AvailableWidth + floatEpsilon >= requiredInline)
                            {
                                break;
                            }

                            float nextY = floatManager.GetNextVerticalPosition(childY);
                            if (nextY <= childY + floatEpsilon)
                            {
                                childY += Math.Max(1f, placementHeight);
                                break;
                            }

                            childY = nextY;
                        }
                    }

                    LayoutBoxOps.SetPosition(child, childX, yOffset + childY);
                    
                    // Advance cursor by CONTENT (BorderBox) height
                    currentY = childY + child.Geometry.BorderBox.Height;
                    
                    lastMarginBottom = childMarginBottom;
                    isFirstChild = false;
                    
                    maxBottom = Math.Max(maxBottom, currentY);
                }
            }
            
            // Include last margin if it doesn't collapse with parent bottom
            bool parentPreventsBottomCollapse = (blockBox.Geometry.Padding.Bottom > 0 || blockBox.Geometry.Border.Bottom > 0);
            if (parentPreventsBottomCollapse)
            {
                currentY += lastMarginBottom;
            }
            // Handle intrinsic height for empty replaced elements (IMG, SVG, etc.)
            if (blockBox.Children.Count == 0)
            {
                string t = (blockBox.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName?.ToUpperInvariant();
                if (t == "IMG" || t == "SVG" || t == "CANVAS" || t == "VIDEO" || t == "IFRAME" || t == "EMBED" || t == "OBJECT" ||
                    t == "INPUT" || t == "TEXTAREA" || t == "BUTTON" || t == "SELECT")
                {
                    float w = blockBox.Geometry.ContentBox.Width;
                    float h = (float)(blockBox.ComputedStyle?.Height ?? 0);

                    if (ReplacedElementSizing.IsReplacedElementTag(t) &&
                        blockBox.SourceNode is FenBrowser.Core.Dom.V2.Element replacedElement)
                    {
                        float attrW = 0f;
                        float attrH = 0f;
                        ReplacedElementSizing.TryGetLengthAttribute(replacedElement, "width", out attrW);
                        ReplacedElementSizing.TryGetLengthAttribute(replacedElement, "height", out attrH);

                        var resolved = ReplacedElementSizing.ResolveReplacedSize(
                            t,
                            blockBox.ComputedStyle,
                            state.AvailableSize,
                            0f,
                            0f,
                            attrW,
                            attrH,
                            constrainAutoToAvailableWidth: false);

                        if (w <= 0) w = resolved.Width;
                        if (h <= 0) h = resolved.Height;
                    }

                    if (w <= 0)
                    {
                        if (t == "INPUT") w = 150f;
                        else if (t == "TEXTAREA") w = 200f;
                        else if (t == "BUTTON") w = 100f;
                        else if (t == "SELECT") w = 120f;
                        else w = 300f;
                    }
                    blockBox.Geometry.ContentBox = new SKRect(
                        blockBox.Geometry.ContentBox.Left,
                        blockBox.Geometry.ContentBox.Top,
                        blockBox.Geometry.ContentBox.Left + w,
                        blockBox.Geometry.ContentBox.Top + Math.Max(0f, h));

                    if (h <= 0)
                    {
                        if (t == "INPUT" || t == "SELECT") h = 24f;
                        else if (t == "TEXTAREA") h = 48f;
                        else if (t == "BUTTON") h = 28f;
                        else h = 150f;
                    }
                    maxBottom = Math.Max(maxBottom, h);
                    SyncBoxes(blockBox.Geometry);
                }
            }

            yOffset += maxBottom; // Total height from Border Top to last content/margin edge

            // SHRINK-TO-FIT: If width was auto and available was infinity, we shrink to widest child
            if (blockBox.ComputedStyle != null && !blockBox.ComputedStyle.Width.HasValue && float.IsInfinity(state.AvailableSize.Width))
            {
                float maxWidth = 0;
                foreach (var child in blockBox.Children)
                {
                    if (child == null || child.IsOutOfFlow)
                    {
                        continue;
                    }

                    float childWidth = child.Geometry.MarginBox.Width;
                    if (!float.IsFinite(childWidth) || childWidth <= 0f)
                    {
                        continue;
                    }

                    maxWidth = Math.Max(maxWidth, childWidth);
                }
                
                // Update ContentBox width
                blockBox.Geometry.ContentBox = new SKRect(
                    blockBox.Geometry.ContentBox.Left,
                    blockBox.Geometry.ContentBox.Top,
                    blockBox.Geometry.ContentBox.Left + maxWidth,
                    blockBox.Geometry.ContentBox.Bottom
                );
                
                SyncBoxes(blockBox.Geometry);
            }

            // 4. Resolve Height
            // Auto height = padding_top + border_top + content_height + padding_bottom + border_bottom
            // yOffset is now 0 + maxBottom (pure content height), so we add all padding+border
            float autoHeight = (float)blockBox.Geometry.Padding.Top + (float)blockBox.Geometry.Border.Top
                              + yOffset
                              + (float)blockBox.Geometry.Padding.Bottom + (float)blockBox.Geometry.Border.Bottom;
            
            // ICB OVERRIDE: HTML and BODY must be at least viewport height
            // This is the Initial Containing Block invariant - required for CSS height chain
            string tag = (blockBox.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName?.ToUpperInvariant() ?? "";
            bool isRootElement = (tag == "HTML" || tag == "BODY");
            
            if (isRootElement && autoHeight < state.ViewportHeight)
            {
                autoHeight = state.ViewportHeight;
                System.Console.WriteLine($"[BFC-ICB] <{tag}> auto-height forced to viewport: {state.ViewportHeight}px");
            }
            
            // Check explicit height (px / % / calc)
            // (Simplified)
            float? explicitHeight = null;
            if (blockBox.ComputedStyle != null)
            {
                if (blockBox.ComputedStyle.Height.HasValue)
                {
                    explicitHeight = (float)blockBox.ComputedStyle.Height.Value;
                }
                else if (blockBox.ComputedStyle.HeightPercent.HasValue)
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ViewportHeight;
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                        explicitHeight = (float)(blockBox.ComputedStyle.HeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(blockBox.ComputedStyle.HeightExpression))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ViewportHeight;
                    explicitHeight = LayoutHelper.EvaluateCssExpression(
                        blockBox.ComputedStyle.HeightExpression,
                        parentHeight,
                        state.ViewportWidth,
                        state.ViewportHeight);
                }
            }

            float resolvedContentHeight;
            if (explicitHeight.HasValue)
            {
                // Explicit height wins
                resolvedContentHeight = explicitHeight.Value;
            }
            else
            {
                // Console.WriteLine($"[BlockBFC] No Explicit Height. Auto height: {autoHeight}");
                // Use auto height
                // We need to set the height of the box model buffers
                resolvedContentHeight = autoHeight - ((float)blockBox.Geometry.Padding.Top + (float)blockBox.Geometry.Border.Top + (float)blockBox.Geometry.Padding.Bottom + (float)blockBox.Geometry.Border.Bottom);
                if (resolvedContentHeight < 0) resolvedContentHeight = 0;
            }

            // Apply min/max height constraints (px, %, calc)
            if (blockBox.ComputedStyle != null)
            {
                float minH = 0f;
                float maxH = float.PositiveInfinity;

                if (blockBox.ComputedStyle.MinHeight.HasValue)
                {
                    minH = (float)blockBox.ComputedStyle.MinHeight.Value;
                }
                else if (blockBox.ComputedStyle.MinHeightPercent.HasValue)
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ViewportHeight;
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                        minH = (float)(blockBox.ComputedStyle.MinHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(blockBox.ComputedStyle.MinHeightExpression))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ViewportHeight;
                    minH = LayoutHelper.EvaluateCssExpression(
                        blockBox.ComputedStyle.MinHeightExpression,
                        parentHeight,
                        state.ViewportWidth,
                        state.ViewportHeight);
                }

                if (blockBox.ComputedStyle.MaxHeight.HasValue)
                {
                    maxH = (float)blockBox.ComputedStyle.MaxHeight.Value;
                }
                else if (blockBox.ComputedStyle.MaxHeightPercent.HasValue)
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ViewportHeight;
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                        maxH = (float)(blockBox.ComputedStyle.MaxHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(blockBox.ComputedStyle.MaxHeightExpression))
                {
                    float parentHeight = state.AvailableSize.Height;
                    if (float.IsInfinity(parentHeight) || parentHeight <= 0)
                        parentHeight = state.ViewportHeight;
                    maxH = LayoutHelper.EvaluateCssExpression(
                        blockBox.ComputedStyle.MaxHeightExpression,
                        parentHeight,
                        state.ViewportWidth,
                        state.ViewportHeight);
                }

                if (maxH < minH) maxH = minH;
                resolvedContentHeight = Math.Max(minH, Math.Min(resolvedContentHeight, maxH));
            }

            blockBox.Geometry.ContentBox = new SKRect(
                blockBox.Geometry.ContentBox.Left,
                blockBox.Geometry.ContentBox.Top,
                blockBox.Geometry.ContentBox.Left + blockBox.Geometry.ContentBox.Width,
                blockBox.Geometry.ContentBox.Top + resolvedContentHeight);
            
            // Re-sync outer boxes
            // (Geometry logic usually centralized)
            SyncBoxes(blockBox.Geometry);

            // Layout Out of Flow
            foreach (var oof in outOfFlow)
            {
                var context = FormattingContext.Resolve(oof);

                // Pass 1: intrinsic measurement (auto-size shrink-to-fit signal).
                var intrinsicState = state.Clone();
                intrinsicState.AvailableSize = new SKSize(float.PositiveInfinity, float.PositiveInfinity);
                intrinsicState.ContainingBlockWidth = blockBox.Geometry.ContentBox.Width;
                intrinsicState.ContainingBlockHeight = blockBox.Geometry.ContentBox.Height;
                context.Layout(oof, intrinsicState);

                // Solve abs/fixed geometry from intrinsic size and insets.
                LayoutPositioningLogic.ResolvePositionedBox(oof, blockBox, blockBox.Geometry);

                // Pass 2: layout contents using resolved box size.
                var resolvedWidth = Math.Max(0f, oof.Geometry.ContentBox.Width);
                var resolvedHeight = Math.Max(0f, oof.Geometry.ContentBox.Height);
                var resolvedState = new LayoutState(
                    new SKSize(resolvedWidth, resolvedHeight),
                    resolvedWidth,
                    resolvedHeight,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    state.Deadline);
                context.Layout(oof, resolvedState);

                // Re-apply final absolute position after child layout potentially touched geometry.
                LayoutPositioningLogic.ResolvePositionedBox(oof, blockBox, blockBox.Geometry);
            }
        }

        private void ResolveWidth(LayoutBox box, LayoutState state)
        {
            float rawAvailable = state.AvailableSize.Width;
            bool widthUnconstrained = float.IsInfinity(rawAvailable) || float.IsNaN(rawAvailable);
            float available = widthUnconstrained ? state.ViewportWidth : rawAvailable;
            if (float.IsInfinity(available) || float.IsNaN(available) || available <= 0)
                available = 1920f; // Fallback if viewport is invalid.
            
            FenLogger.Info($"[BFC-RESOLVE-START] Avail={state.AvailableSize.Width} Sanitized={available} VP={state.ViewportWidth}", LogCategory.Layout);

            // 1. Initial values from computed style
            var style = box.ComputedStyle;
            Thickness padding = style?.Padding ?? new Thickness();
            Thickness border = style?.BorderThickness ?? new Thickness();
            Thickness margin = style?.Margin ?? new Thickness();

            string tag = (box.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName ?? "?";
            string id = (box.SourceNode as FenBrowser.Core.Dom.V2.Element)?.GetAttribute("id") ?? "";

            // 2. Resolve width vs percentages
            float? width = null;
            if (style != null)
            {
                if (style.Width.HasValue) width = (float)style.Width.Value;
                else if (style.WidthPercent.HasValue) width = (float)(style.WidthPercent.Value / 100.0 * available);
            }

            // 3. Resolve Max/Min constraints
            float? maxWidth = null;
            if (style != null)
            {
                if (style.MaxWidth.HasValue) maxWidth = (float)style.MaxWidth.Value;
                else if (style.MaxWidthPercent.HasValue) maxWidth = (float)(style.MaxWidthPercent.Value / 100.0 * available);
            }

            float minWidth = 0;
            if (style != null)
            {
                if (style.MinWidth.HasValue) minWidth = (float)style.MinWidth.Value;
                else if (style.MinWidthPercent.HasValue) minWidth = (float)(style.MinWidthPercent.Value / 100.0 * available);
            }

            FenLogger.Info($"[BFC-RESOLVE] <{tag}> W={style?.Width} WP={style?.WidthPercent} MW={style?.MaxWidth} MWP={style?.MaxWidthPercent} Avail={available}", LogCategory.Layout);

            // 4. Calculate content width before margins
            float resolvedContentWidth;
            float horizontalExtras = (float)(padding.Left + padding.Right + border.Left + border.Right);
            float marginExtras = (float)(margin.Left + margin.Right);

            if (width.HasValue)
            {
                resolvedContentWidth = width.Value;
            }
            else if (widthUnconstrained)
            {
                // Use a finite probe width so inline/text children can measure naturally;
                // the shrink-to-fit pass after child layout will tighten if needed.
                resolvedContentWidth = Math.Max(0f, available - horizontalExtras - marginExtras);
            }
            else
            {
                // Width auto fills available space minus margins
                resolvedContentWidth = Math.Max(0, rawAvailable - horizontalExtras - marginExtras);
            }

            // Apply constraints
            if (maxWidth.HasValue) resolvedContentWidth = Math.Min(resolvedContentWidth, maxWidth.Value);
            resolvedContentWidth = Math.Max(resolvedContentWidth, minWidth);

            // 5. Handle Margin Auto (Centering)
            float marginLeft = (float)margin.Left;
            float marginRight = (float)margin.Right;
            bool leftAuto = style?.MarginLeftAuto ?? false;
            bool rightAuto = style?.MarginRightAuto ?? false;

            if (leftAuto || rightAuto)
            {
                float remainingSpace = available - (resolvedContentWidth + horizontalExtras);
                
                if (leftAuto && rightAuto)
                {
                    marginLeft = Math.Max(0, remainingSpace / 2f);
                    marginRight = Math.Max(0, remainingSpace / 2f);
                }
                else if (leftAuto)
                {
                    marginLeft = Math.Max(0, remainingSpace - marginRight);
                }
                else if (rightAuto)
                {
                    marginRight = Math.Max(0, remainingSpace - marginLeft);
                }
                
                FenLogger.Info($"[BFC-WIDTH] <{tag}#{id}> Centered: ML={marginLeft} MR={marginRight} Cont={resolvedContentWidth} Avail={available}", LogCategory.Layout);
            }

            if (float.IsNaN(resolvedContentWidth) || float.IsInfinity(resolvedContentWidth))
            {
                 FenLogger.Error($"[BFC-RESOLVE-ERROR] ResolvedWidth is {resolvedContentWidth}. Forcing 0.", LogCategory.Layout);
                 resolvedContentWidth = 0;
            }

            box.Geometry.ContentBox = new SKRect(
                box.Geometry.ContentBox.Left, 
                box.Geometry.ContentBox.Top, 
                box.Geometry.ContentBox.Left + resolvedContentWidth, 
                box.Geometry.ContentBox.Bottom);
            
            box.Geometry.Padding = padding;
            box.Geometry.Border = border;
            box.Geometry.Margin = new Thickness(marginLeft, margin.Top, marginRight, margin.Bottom);

            SyncBoxes(box.Geometry);
        }
        
        private void SyncBoxes(BoxModel geometry)
        {
            // Re-construct layers based on ContentBox and Thickness
            // Assume ContentBox is at local (0,0) or correct offset?
            // Actually, we usually position relative to "Border Box Origin" or "Margin Box Origin".
            // Let's standardise on Border Box Top-Left being (0,0) for local layout?
            // Or Content Box Top-Left?
            
            // Let's say ContentBox is set.
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

        private void ShiftBox(BoxModel geometry, float dx, float dy)
        {
            geometry.ContentBox.Offset(dx, dy);
            geometry.PaddingBox.Offset(dx, dy);
            geometry.BorderBox.Offset(dx, dy);
            geometry.MarginBox.Offset(dx, dy);
        }

        private LayoutState CreateChildState(float contentWidth, LayoutState state)
        {
             float childAvailableHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
             return new LayoutState(
                new SKSize(contentWidth, childAvailableHeight),
                contentWidth,
                childAvailableHeight,
                state.ViewportWidth,
                state.ViewportHeight,
                state.Deadline
            );
        }
    }

    /// <summary>
    /// Placeholder for IFC.
    /// </summary>

}

