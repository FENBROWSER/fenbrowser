// SpecRef: CSS2.2 Visual Formatting Model and positioned layout sizing
// CapabilityId: LAYOUT-POSITIONING-SIZING-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Linq;
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
        
        /// <summary>
        /// Guards against infinite shrink-to-fit relayout recursion.
        /// The shrink-to-fit pass is a single one-shot adjustment: if we are
        /// already inside a shrink-to-fit relayout for this box, suppress further
        /// re-entry to avoid an infinite loop on self-sizing flex/block containers.
        /// </summary>
        [ThreadStatic] private static int _shrinkToFitDepth;
        private const int MaxShrinkToFitDepth = 2;

        protected override void LayoutCore(LayoutBox box, LayoutState state)
        {
            var blockBox = box;

            // Each layout pass should start from local coordinates.
            // Without this, repeated relayouts can keep descendant positions from older
            // passes and only move the parent box, producing mixed coordinate spaces.
            LayoutBoxOps.ResetSubtreeToOrigin(blockBox);
            
            // DEBUG: Entry log
            string dbgTag = (blockBox.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName ?? "?";
            if (DebugConfig.EnableDeepDebug && DebugConfig.LogLayoutConstraints)
            {
                EngineLogCompat.Info($"[BFC-ENTRY] <{dbgTag}> AvailH={state.AvailableSize.Height} ViewH={state.ViewportHeight}", LogCategory.Layout);
            }

            // 1. Resolve Width
            ResolveWidth(blockBox, state);

            bool blockAutoWidth =
                blockBox.ComputedStyle == null ||
                (!blockBox.ComputedStyle.Width.HasValue &&
                 !blockBox.ComputedStyle.WidthPercent.HasValue &&
                 string.IsNullOrEmpty(blockBox.ComputedStyle.WidthExpression));

            // 2. Prepare for Child Layout
            float yOffset = blockBox.Geometry.ContentBox.Top;
            float xOffset = blockBox.Geometry.ContentBox.Left;
            float contentWidth = blockBox.Geometry.ContentBox.Width;
            bool shrinkToFitPass = blockAutoWidth && float.IsInfinity(state.AvailableSize.Width);
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
                    if (TextWhitespaceClassifier.IsCollapsibleWhitespaceOnly(textData))
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
                    float childMarginTopForClear = (float)(child.ComputedStyle?.Margin.Top ?? 0.0);
                    float collapsedMarginForClear;
                    if (isFirstChild)
                    {
                        collapsedMarginForClear = parentPreventsTopCollapse ? childMarginTopForClear : 0f;
                    }
                    else
                    {
                        collapsedMarginForClear = MarginCollapseComputer.Collapse(lastMarginBottom, childMarginTopForClear);
                    }

                    float marginEdgeY = currentY + collapsedMarginForClear;
                    float clearY = floatManager.GetClearanceY(clearStyle, marginEdgeY);
                    if (Math.Abs(clearY - marginEdgeY) > floatEpsilon)
                    {
                        // Preserve the collapsed margin contribution when clearance moves
                        // the top border edge below preceding floats. This keeps follow-up
                        // sibling margin math in the same coordinate space instead of
                        // discarding negative/positive collapsed margins entirely.
                        // Acid2 relies on the computed clearance delta being allowed to
                        // go negative when preceding floats sit above the current margin edge.
                        currentY = clearY - collapsedMarginForClear;
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

                    float floatWidth = GetFloatOuterWidth(child);
                    float floatHeight = GetFloatOuterHeight(child);
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

                        if (floatAutoWidth &&
                            availableBand > 0f &&
                            availableBand + floatEpsilon < floatWidth)
                        {
                            var narrowedState = CreateChildState(availableBand, state);
                            FormattingContext.Resolve(child).Layout(child, narrowedState);
                            floatWidth = GetFloatOuterWidth(child);
                            floatHeight = GetFloatOuterHeight(child);
                        }

                        // If no prior floats intrude on this line, place the float at the current
                        // band even when it is wider than the available inline size. Moving it down
                        // by its own height in that case incorrectly stacks single floats vertically
                        // (seen on Google header controls) instead of allowing normal overflow.
                        if (floatWidth <= 0f ||
                            availableBand + floatEpsilon >= floatWidth ||
                            !floatManager.HasFloats)
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

                    LayoutBoxOps.PositionSubtree(child, fx, yOffset + placementY, childState);

                    float contentLeftEdge = xOffset;
                    float contentRightEdge = xOffset + containerInline;
                    if (floatStyle == "right" && child.Geometry.BorderBox.Right > contentRightEdge + floatEpsilon)
                    {
                        LayoutBoxOps.ShiftSubtree(child, contentRightEdge - child.Geometry.BorderBox.Right, 0f);
                    }
                    else if (floatStyle == "left" && child.Geometry.BorderBox.Left < contentLeftEdge - floatEpsilon)
                    {
                        LayoutBoxOps.ShiftSubtree(child, contentLeftEdge - child.Geometry.BorderBox.Left, 0f);
                    }

                    // Float exclusion math is local to this formatting context's content box.
                    // Store intrusions in that local coordinate space so follow-up block
                    // placement and clearance do not mix absolute document coordinates with
                    // local flow cursors.
                    var localFloatRect = child.Geometry.MarginBox;
                    localFloatRect.Offset(-xOffset, -yOffset);
                    floatManager.AddFloat(localFloatRect, floatStyle == "left");

                    
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
                    if (TryResolveCollapsedThroughMargin(child, out float collapsedThroughMargin))
                    {
                        childMarginBottom = collapsedThroughMargin;
                    }

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
                        collapsedMargin = MarginCollapseComputer.Collapse(lastMarginBottom, childMarginTop);
                    }

                    currentY += collapsedMargin;

                    float childY = currentY;
                    float childX = xOffset;
                    bool ignoreFloatIntrusionForOutOfFlowAutoWidth = blockBox.IsOutOfFlow && blockAutoWidth;

                    if (!ignoreFloatIntrusionForOutOfFlowAutoWidth &&
                        floatManager.HasFloats &&
                        float.IsFinite(contentWidth) &&
                        contentWidth > 0f)
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

                        if (!explicitInlineSize)
                        {
                            var finalSpace = floatManager.GetAvailableSpace(childY, placementHeight, contentWidth);
                            float availableBand = Math.Max(0f, finalSpace.AvailableWidth);
                            if (availableBand > 0f && availableBand + floatEpsilon < contentWidth)
                            {
                                var narrowedState = CreateChildState(availableBand, state);
                                FormattingContext.Resolve(child).Layout(child, narrowedState);
                                childState = narrowedState;

                                placementHeight = Math.Max(1f, child.Geometry.MarginBox.Height);
                                childX = xOffset + finalSpace.LeftOffset;
                            }
                        }
                    }

                    PositionInFlowBlockChild(child, childX, yOffset + childY, childState);
                    
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
                    bool hasExplicitWidth = blockBox.ComputedStyle?.Width.HasValue == true;
                    bool hasExplicitHeight = blockBox.ComputedStyle?.Height.HasValue == true;

                    if (ReplacedElementSizing.IsReplacedElementTag(t) &&
                        blockBox.SourceNode is FenBrowser.Core.Dom.V2.Element replacedElement &&
                        !(t == "OBJECT" && ReplacedElementSizing.ShouldUseObjectFallbackContent(replacedElement)))
                    {
                        float attrW = 0f;
                        float attrH = 0f;
                        ReplacedElementSizing.TryGetLengthAttribute(replacedElement, "width", out attrW);
                        ReplacedElementSizing.TryGetLengthAttribute(replacedElement, "height", out attrH);
                        float intrinsicW = 0f;
                        float intrinsicH = 0f;
                        ReplacedElementSizing.TryResolveIntrinsicSizeFromElement(t, replacedElement, out intrinsicW, out intrinsicH);

                        var resolved = ReplacedElementSizing.ResolveReplacedSize(
                            t,
                            blockBox.ComputedStyle,
                            state.AvailableSize,
                            intrinsicW,
                            intrinsicH,
                            attrW,
                            attrH,
                            constrainAutoToAvailableWidth: false);

                        if (w <= 0 && !hasExplicitWidth) w = resolved.Width;
                        if (h <= 0 && !hasExplicitHeight) h = resolved.Height;
                    }

                    if (w <= 0 && !hasExplicitWidth)
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

                    if (h <= 0 && !hasExplicitHeight)
                    {
                        if (t == "INPUT" || t == "SELECT") h = 24f;
                        else if (t == "TEXTAREA") h = 48f;
                        else if (t == "BUTTON") h = 28f;
                        else h = 150f;
                    }

                    maxBottom = Math.Max(maxBottom, h);
                }
            }

            float intrinsicContentHeight = maxBottom;

            bool isFloatingBox =
                string.Equals(blockBox.ComputedStyle?.Float, "left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(blockBox.ComputedStyle?.Float, "right", StringComparison.OrdinalIgnoreCase);

            // SHRINK-TO-FIT: auto-width floats need this even under a finite available band
            // so nested preferred widths can re-expand after an overly aggressive probe pass.
            if (blockAutoWidth && (float.IsInfinity(state.AvailableSize.Width) || isFloatingBox))
            {
                float previousWidth = blockBox.Geometry.ContentBox.Width;
                float maxWidth = 0;
                float contentLeft = blockBox.Geometry.ContentBox.Left;
                foreach (var child in blockBox.Children)
                {
                    if (child == null || child.IsOutOfFlow)
                    {
                        continue;
                    }

                    float childWidth = MeasureShrinkToFitWidth(child);
                    if (!float.IsFinite(childWidth) || childWidth <= 0f)
                    {
                        continue;
                    }

                    maxWidth = Math.Max(maxWidth, childWidth);

                }

                // Preserve numerical stability for second-pass shrink-to-fit without
                // injecting a full extra pixel into exact-fit cases.
                if (maxWidth > 0f)
                {
                    maxWidth = MathF.Ceiling(maxWidth - 0.001f);
                }
                 
                // Update ContentBox width
                blockBox.Geometry.ContentBox = new SKRect(
                    blockBox.Geometry.ContentBox.Left,
                    blockBox.Geometry.ContentBox.Top,
                    blockBox.Geometry.ContentBox.Left + maxWidth,
                    blockBox.Geometry.ContentBox.Bottom
                );
                
                SyncBoxes(blockBox.Geometry);

                if (maxWidth > 0f &&
                    float.IsFinite(previousWidth) &&
                    Math.Abs(previousWidth - maxWidth) > 0.5f &&
                    blockBox.Children.Count > 0)
                {
                    // Guard: only allow one shrink-to-fit reflow per call chain.
                    // Multiple re-entries create an N^2 loop on self-sizing containers
                    // (e.g. YouTube's nested flex/block layout) causing 6000ms+ frames.
                    if (_shrinkToFitDepth < MaxShrinkToFitDepth)
                    {
                        // The first shrink-to-fit probe leaves child subtrees positioned in the
                        // original pass. Reset local coordinates before re-entering LayoutCore so
                        // the second pass does not accumulate stale offsets into descendants.
                        LayoutBoxOps.ResetSubtreeToOrigin(blockBox);

                        float relayoutAvailableWidth =
                            maxWidth +
                            (float)blockBox.Geometry.Padding.Left +
                            (float)blockBox.Geometry.Padding.Right +
                            (float)blockBox.Geometry.Border.Left +
                            (float)blockBox.Geometry.Border.Right +
                            (float)blockBox.Geometry.Margin.Left +
                            (float)blockBox.Geometry.Margin.Right;

                        var relayoutState = state.Clone();
                        relayoutState.AvailableSize = new SKSize(relayoutAvailableWidth, state.AvailableSize.Height);
                        relayoutState.ContainingBlockWidth = relayoutAvailableWidth;
                        _shrinkToFitDepth++;
                        try   { LayoutCore(blockBox, relayoutState); }
                        finally { _shrinkToFitDepth--; }
                        return;
                    }
                    else
                    {
                        FenBrowser.Core.EngineLogCompat.Warn(
                            $"[BFC] Shrink-to-fit depth {_shrinkToFitDepth} exceeded for <{(blockBox.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName}>. Skipping relayout to break infinite-loop.",
                            FenBrowser.Core.Logging.LogCategory.Layout);
                    }
                }
            }
            // yOffset is now 0 + maxBottom (pure content height), so we add all padding+border
            float autoHeight = (float)blockBox.Geometry.Padding.Top + (float)blockBox.Geometry.Border.Top
                              + intrinsicContentHeight
                              + (float)blockBox.Geometry.Padding.Bottom + (float)blockBox.Geometry.Border.Bottom;
            
            // ICB OVERRIDE: BODY must be at least viewport height
            // This is the Initial Containing Block invariant - required for CSS height chain
            string tag = (blockBox.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName?.ToUpperInvariant() ?? "";
            bool isRootElement = (tag == "BODY");
            
            float rootViewportFallback = Math.Max(state.ViewportHeight, Math.Max(state.AvailableSize.Height, state.ContainingBlockHeight));
            if (isRootElement && rootViewportFallback > 0 && autoHeight < rootViewportFallback)
            {
                autoHeight = rootViewportFallback;
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
                    float parentHeight = ResolvePercentageHeightContainingBlock(blockBox, state);
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                        explicitHeight = (float)(blockBox.ComputedStyle.HeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(blockBox.ComputedStyle.HeightExpression))
                {
                    float parentHeight = ResolveExpressionContainingBlockHeight(blockBox, state);
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
                    float parentHeight = ResolvePercentageHeightContainingBlock(blockBox, state);
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                        minH = (float)(blockBox.ComputedStyle.MinHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(blockBox.ComputedStyle.MinHeightExpression))
                {
                    float parentHeight = ResolveExpressionContainingBlockHeight(blockBox, state);
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
                    float parentHeight = ResolvePercentageHeightContainingBlock(blockBox, state);
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                        maxH = (float)(blockBox.ComputedStyle.MaxHeightPercent.Value / 100.0 * parentHeight);
                }
                else if (!string.IsNullOrEmpty(blockBox.ComputedStyle.MaxHeightExpression))
                {
                    float parentHeight = ResolveExpressionContainingBlockHeight(blockBox, state);
                    maxH = LayoutHelper.EvaluateCssExpression(
                        blockBox.ComputedStyle.MaxHeightExpression,
                        parentHeight,
                        state.ViewportWidth,
                        state.ViewportHeight);
                }

                // Keep auto-height clamping in the same basis as the auto-height accumulator
                // used by this context (outer size including padding/border), then map back
                // to content height for geometry storage.
                if (!explicitHeight.HasValue)
                {
                    float nonContentHeight =
                        (float)blockBox.Geometry.Padding.Top +
                        (float)blockBox.Geometry.Padding.Bottom +
                        (float)blockBox.Geometry.Border.Top +
                        (float)blockBox.Geometry.Border.Bottom;

                    if (minH > 0f)
                    {
                        minH = Math.Max(0f, minH - nonContentHeight);
                    }

                    if (float.IsFinite(maxH))
                    {
                        maxH = Math.Max(0f, maxH - nonContentHeight);
                    }
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
                LayoutPositioningLogic.ResolvePositionedBox(oof, blockBox, blockBox.Geometry, state);

                // Pass 2: layout contents using resolved box size.
                var resolvedWidth = Math.Max(0f, oof.Geometry.ContentBox.Width);
                var resolvedHeight = Math.Max(0f, oof.Geometry.ContentBox.Height);
                var resolvedOuterWidth = Math.Max(resolvedWidth, oof.Geometry.MarginBox.Width);
                var resolvedOuterHeight = Math.Max(resolvedHeight, oof.Geometry.MarginBox.Height);
                var resolvedState = new LayoutState(
                    new SKSize(resolvedOuterWidth, resolvedOuterHeight),
                    resolvedOuterWidth,
                    resolvedOuterHeight,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    state.Deadline);
                context.Layout(oof, resolvedState);

                // Re-apply final absolute position after child layout potentially touched geometry.
                LayoutPositioningLogic.ResolvePositionedBox(
                    oof,
                    blockBox,
                    blockBox.Geometry,
                    state,
                    collapsePositioningMarginsInFinalGeometry: true);
            }
        }

        private static float MeasureShrinkToFitWidth(LayoutBox box)
        {
            if (box == null)
            {
                return 0f;
            }

            float directWidth = box.Geometry.MarginBox.Width;
            bool hasExplicitWidth =
                box.ComputedStyle != null &&
                (box.ComputedStyle.Width.HasValue ||
                 box.ComputedStyle.WidthPercent.HasValue ||
                 !string.IsNullOrEmpty(box.ComputedStyle.WidthExpression));
            bool isFloating =
                string.Equals(box.ComputedStyle?.Float, "left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(box.ComputedStyle?.Float, "right", StringComparison.OrdinalIgnoreCase);

            // Anonymous block wrappers around inline runs often retain the probe width
            // from an unconstrained first pass. For shrink-to-fit, use the inline run's
            // widest descendant instead of the wrapper's provisional width.
            if (box.Children.Count > 0 && (!hasExplicitWidth || box is AnonymousBlockBox))
            {
                float descendantWidth = 0f;
                foreach (var child in box.Children)
                {
                    descendantWidth = Math.Max(descendantWidth, MeasureShrinkToFitWidth(child));
                }

                if (descendantWidth > 0f)
                {
                    if (!float.IsFinite(directWidth) || directWidth <= 0.5f)
                    {
                        return descendantWidth;
                    }

                    if (!isFloating && descendantWidth < directWidth - 0.5f)
                    {
                        return descendantWidth;
                    }

                    // Floats and other shrink-to-fit auto-width boxes can occasionally
                    // underreport their provisional width before descendant block layout
                    // fully expands. Prefer the descendant width in that case so the
                    // second pass does not clamp visible content into a narrow column.
                    if (!hasExplicitWidth && descendantWidth > directWidth + 0.5f)
                    {
                        return descendantWidth;
                    }
                }
            }

            return directWidth;
        }

        private static float GetFloatOuterWidth(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return 0f;
            }

            float width = Math.Max(0f, box.Geometry.MarginBox.Width);
            width = Math.Max(width, Math.Max(0f, box.Geometry.BorderBox.Width));
            width = Math.Max(width, Math.Max(0f, box.Geometry.PaddingBox.Width));
            return Math.Max(width, Math.Max(0f, box.Geometry.ContentBox.Width));
        }

        private static float GetFloatOuterHeight(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return 0f;
            }

            float height = Math.Max(0f, box.Geometry.MarginBox.Height);
            height = Math.Max(height, Math.Max(0f, box.Geometry.BorderBox.Height));
            height = Math.Max(height, Math.Max(0f, box.Geometry.PaddingBox.Height));
            return Math.Max(height, Math.Max(0f, box.Geometry.ContentBox.Height));
        }

        private void ResolveWidth(LayoutBox box, LayoutState state)
        {
            var widthResolution = LayoutConstraintResolver.ResolveWidth(state, "BFC.ResolveWidth");
            float rawAvailable = widthResolution.RawAvailable;
            bool widthUnconstrained = widthResolution.IsUnconstrained;
            float available = widthResolution.ResolvedAvailable;
            
            if (DebugConfig.EnableDeepDebug && DebugConfig.LogLayoutConstraints)
            {
                EngineLogCompat.Info(
                    $"[BFC-RESOLVE-START] Avail={rawAvailable} Resolved={available} Source={widthResolution.Source} CB={state.ContainingBlockWidth} VP={state.ViewportWidth}",
                    LogCategory.Layout);
            }

            // 1. Initial values from computed style
            var style = box.ComputedStyle;
            Thickness padding = style?.Padding ?? new Thickness();
            Thickness border = style?.BorderThickness ?? new Thickness();
            Thickness margin = style?.Margin ?? new Thickness();
            float horizontalExtras = (float)(padding.Left + padding.Right + border.Left + border.Right);
            bool isBorderBox = string.Equals(style?.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase);

            string tag = (box.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName ?? "?";
            string id = (box.SourceNode as FenBrowser.Core.Dom.V2.Element)?.GetAttribute("id") ?? "";

            // 2. Resolve width vs percentages
            float? width = null;
            if (style != null)
            {
                if (style.Width.HasValue)
                {
                    width = (float)style.Width.Value;
                }
                else if (style.WidthPercent.HasValue)
                {
                    width = (float)(style.WidthPercent.Value / 100.0 * available);
                }

                if (width.HasValue && isBorderBox)
                {
                    width = Math.Max(0f, width.Value - horizontalExtras);
                }
            }

            // 3. Resolve Max/Min constraints
            float? maxWidth = null;
            if (style != null)
            {
                if (style.MaxWidth.HasValue)
                {
                    maxWidth = (float)style.MaxWidth.Value;
                }
                else if (style.MaxWidthPercent.HasValue)
                {
                    maxWidth = (float)(style.MaxWidthPercent.Value / 100.0 * available);
                }

                if (maxWidth.HasValue && isBorderBox)
                {
                    maxWidth = Math.Max(0f, maxWidth.Value - horizontalExtras);
                }
            }

            float minWidth = 0;
            if (style != null)
            {
                if (style.MinWidth.HasValue)
                {
                    minWidth = (float)style.MinWidth.Value;
                }
                else if (style.MinWidthPercent.HasValue)
                {
                    minWidth = (float)(style.MinWidthPercent.Value / 100.0 * available);
                }

                if (isBorderBox)
                {
                    minWidth = Math.Max(0f, minWidth - horizontalExtras);
                }
            }

            if (DebugConfig.EnableDeepDebug && DebugConfig.LogLayoutConstraints)
            {
                EngineLogCompat.Info($"[BFC-RESOLVE] <{tag}> W={style?.Width} WP={style?.WidthPercent} MW={style?.MaxWidth} MWP={style?.MaxWidthPercent} Avail={available}", LogCategory.Layout);
            }

            // 4. Calculate content width before margins
            float resolvedContentWidth;
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
                
                if (DebugConfig.EnableDeepDebug && DebugConfig.LogLayoutConstraints)
                {
                    EngineLogCompat.Info($"[BFC-WIDTH] <{tag}#{id}> Centered: ML={marginLeft} MR={marginRight} Cont={resolvedContentWidth} Avail={available}", LogCategory.Layout);
                }
            }

            if (float.IsNaN(resolvedContentWidth) || float.IsInfinity(resolvedContentWidth))
            {
                 EngineLogCompat.Error($"[BFC-RESOLVE-ERROR] ResolvedWidth is {resolvedContentWidth}. Forcing 0.", LogCategory.Layout);
                 resolvedContentWidth = 0;
            }

            var resolvedContentLeft = (float)(marginLeft + border.Left + padding.Left);
            var resolvedContentTop = (float)(margin.Top + border.Top + padding.Top);
            var existingContentHeight = box.Geometry.ContentBox.Height;
            if (!float.IsFinite(existingContentHeight) || existingContentHeight < 0f)
            {
                existingContentHeight = 0f;
            }

            box.Geometry.ContentBox = new SKRect(
                resolvedContentLeft,
                resolvedContentTop,
                resolvedContentLeft + resolvedContentWidth,
                resolvedContentTop + existingContentHeight);
            
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

        private static void PositionInFlowBlockChild(LayoutBox child, float targetMarginLeft, float targetBorderTop, LayoutState state)
        {
            if (child?.Geometry == null)
            {
                return;
            }

            float borderToMarginTop = child.Geometry.BorderBox.Top - child.Geometry.MarginBox.Top;
            float targetMarginTop = targetBorderTop - borderToMarginTop;
            LayoutBoxOps.PositionSubtree(child, targetMarginLeft, targetMarginTop, state);
        }

        private static float ResolvePercentageHeightContainingBlock(LayoutBox box, LayoutState state)
        {
            if (box?.Parent == null)
            {
                return float.NaN;
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
            if (float.IsInfinity(parentHeight) || parentHeight <= 0)
            {
                parentHeight = state.AvailableSize.Height;
            }

            if (float.IsInfinity(parentHeight) || parentHeight <= 0)
            {
                parentHeight = state.ViewportHeight;
            }

            return parentHeight;
        }

        private static float ResolveExpressionContainingBlockHeight(LayoutBox box, LayoutState state)
        {
            float parentHeight = ResolvePercentageHeightContainingBlock(box, state);
            if (!float.IsFinite(parentHeight) || parentHeight <= 0)
            {
                parentHeight = state.ViewportHeight;
            }

            return parentHeight;
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

            if (string.Equals(LayoutStyleResolver.GetEffectivePosition(style), "fixed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (style.HeightPercent.HasValue)
            {
                return HasDefiniteContainingBlockHeight(box.Parent);
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

            float resolvedHeight = box.Geometry.ContentBox.Height;
            return float.IsFinite(resolvedHeight) && resolvedHeight > 0f;
        }

        private static bool TryResolveCollapsedThroughMargin(LayoutBox box, out float collapsedMargin)
        {
            collapsedMargin = 0f;
            if (box?.ComputedStyle == null)
            {
                return false;
            }

            if (!MarginCollapseComputer.ShouldCollapseThrough(box.ComputedStyle, box.Geometry.ContentBox.Height))
            {
                return false;
            }

            float positive = 0f;
            float negative = 0f;
            CombineCollapsedMargin(ref positive, ref negative, (float)box.Geometry.Margin.Top);
            CombineCollapsedMargin(ref positive, ref negative, (float)box.Geometry.Margin.Bottom);

            var firstInFlow = box.Children.FirstOrDefault(static child => child != null && !child.IsOutOfFlow);
            if (firstInFlow != null)
            {
                if (TryResolveCollapsedThroughMargin(firstInFlow, out float childCollapsed))
                {
                    CombineCollapsedMargin(ref positive, ref negative, childCollapsed);
                }
                else
                {
                    CombineCollapsedMargin(ref positive, ref negative, (float)firstInFlow.Geometry.Margin.Top);
                    CombineCollapsedMargin(ref positive, ref negative, (float)firstInFlow.Geometry.Margin.Bottom);
                }
            }

            collapsedMargin = positive + negative;
            return true;
        }

        private static void CombineCollapsedMargin(ref float positive, ref float negative, float margin)
        {
            if (margin > 0f)
            {
                positive = Math.Max(positive, margin);
            }
            else if (margin < 0f)
            {
                negative = Math.Min(negative, margin);
            }
        }

    }

    /// <summary>
    /// Placeholder for IFC.
    /// </summary>

}
