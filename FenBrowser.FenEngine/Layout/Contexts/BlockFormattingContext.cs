// SpecRef: CSS2.2 Visual Formatting Model and positioned layout sizing
// CapabilityId: LAYOUT-POSITIONING-SIZING-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Globalization;
using System.Linq;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Typography;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Implements the Block Formatting Context (BFC).
    /// Lays out block-level boxes vertically.
    /// </summary>
    public class BlockFormattingContext : FormattingContext
    {
        private static BlockFormattingContext _instance;
        private static readonly SkiaFontService s_fontService = new();
        public static BlockFormattingContext Instance => _instance ??= new BlockFormattingContext();
        
        /// <summary>
        /// Guards against infinite shrink-to-fit relayout recursion.
        /// The shrink-to-fit pass is a single one-shot adjustment: if we are
        /// already inside a shrink-to-fit relayout for this box, suppress further
        /// re-entry to avoid an infinite loop on self-sizing flex/block containers.
        /// </summary>
        [ThreadStatic] private static int _shrinkToFitDepth;
        private const int MaxShrinkToFitDepth = 8;

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
            float definiteContentHeightForChildren = ResolveDefiniteContentHeightForChildren(blockBox, state);

            // 3. Iterate Children
            var outOfFlow = new List<OutOfFlowLayoutCandidate>();
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
            bool fragmentationEnabled = TryResolveFragmentainerHeight(state, out float fragmentHeight);
            
            foreach (var child in blockBox.Children)
            {
                state.Deadline?.Check();

                if (child.IsOutOfFlow)
                {
                    outOfFlow.Add(new OutOfFlowLayoutCandidate(child, new SKPoint(xOffset, yOffset + currentY)));
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
                        LayoutBoxOps.SyncBoxes(textChild.Geometry);
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
                    var childState = CreateChildState(floatChildConstraint, state, definiteContentHeightForChildren);
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
                            var narrowedState = CreateChildState(availableBand, state, definiteContentHeightForChildren);
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
                    // Compute margin before layout so FloatOriginY is available for
                    // child IFCs that need to query float intrusions per line.
                    float childMarginTop = (float)(child.ComputedStyle?.Margin.Top ?? 0.0);
                    float childMarginBottom = (float)(child.ComputedStyle?.Margin.Bottom ?? 0.0);

                    // MARGIN COLLAPSING
                    float collapsedMargin;
                    if (isFirstChild)
                    {
                        collapsedMargin = parentPreventsTopCollapse ? childMarginTop : 0f;
                    }
                    else
                    {
                        collapsedMargin = MarginCollapseComputer.Collapse(lastMarginBottom, childMarginTop);
                    }

                    float estimatedChildY = currentY + collapsedMargin;
                    float floatOriginY = yOffset + estimatedChildY;

                    string breakBefore = NormalizeBreakDirective(child.ComputedStyle?.PageBreakBefore);
                    string breakAfter = NormalizeBreakDirective(child.ComputedStyle?.PageBreakAfter);
                    string breakInside = NormalizeBreakDirective(child.ComputedStyle?.PageBreakInside);
                    if (fragmentationEnabled && IsForcedBreakDirective(breakBefore))
                    {
                        if (MoveFlowCursorToNextFragment(ref currentY, fragmentHeight, ref lastMarginBottom, ref isFirstChild, ref floatManager))
                        {
                            maxBottom = Math.Max(maxBottom, currentY);
                        }
                        // Recompute after fragment push
                        estimatedChildY = currentY + collapsedMargin;
                        floatOriginY = yOffset + estimatedChildY;
                    }

                    var childState = CreateChildState(childFlowWidth, state, definiteContentHeightForChildren,
                        floatManager, xOffset, floatOriginY);
                    FormattingContext.Resolve(child).Layout(child, childState);

                    if (TryResolveCollapsedThroughMargin(child, out float collapsedThroughMargin))
                    {
                        childMarginBottom = collapsedThroughMargin;
                    }

                    // Advance cursor by the collapsed margin
                    currentY += collapsedMargin;

                    float childY = currentY;
                    float childX = xOffset;

                    if (fragmentationEnabled &&
                        IsAvoidBreakDirective(breakInside) &&
                        ShouldMoveBlockToNextFragment(childY, child.Geometry.MarginBox.Height, fragmentHeight))
                    {
                        if (MoveFlowCursorToNextFragment(ref currentY, fragmentHeight, ref lastMarginBottom, ref isFirstChild, ref floatManager))
                        {
                            childY = currentY;
                            childX = xOffset;
                            maxBottom = Math.Max(maxBottom, currentY);
                        }
                    }

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
                                var narrowedState = CreateChildState(availableBand, state, definiteContentHeightForChildren);
                                FormattingContext.Resolve(child).Layout(child, narrowedState);
                                childState = narrowedState;

                                placementHeight = Math.Max(1f, child.Geometry.MarginBox.Height);
                                childX = xOffset + finalSpace.LeftOffset;
                            }
                        }
                    }

                    PositionInFlowBlockChild(child, childX, yOffset + childY, childState);
                    CenterSingleButtonFlowChildIfNeeded(blockBox, child, state);

                    // position:sticky — apply sticky constraint after normal-flow
                    // positioning. The sticky offset shifts the visual position but
                    // NOT the flow position (siblings use the static position).
                    string childPosition = LayoutStyleResolver.GetEffectivePosition(child.ComputedStyle);
                    if (string.Equals(childPosition, "sticky", StringComparison.OrdinalIgnoreCase))
                    {
                        var stickyOffset = LayoutPositioningLogic.ResolveStickyOffset(
                            child,
                            blockBox.Geometry,
                            state.ScrollOffsetY,
                            state.ScrollOffsetX);

                        if (Math.Abs(stickyOffset.X) > 0.01f || Math.Abs(stickyOffset.Y) > 0.01f)
                        {
                            LayoutBoxOps.TranslateSubtree(child, stickyOffset.X, stickyOffset.Y);
                        }
                    }

                    // Advance cursor by CONTENT (BorderBox) height — uses the
                    // static (pre-sticky) position so siblings are unaffected.
                    currentY = childY + child.Geometry.BorderBox.Height;
                    
                    lastMarginBottom = childMarginBottom;
                    isFirstChild = false;
                    
                    maxBottom = Math.Max(maxBottom, currentY);

                    if (fragmentationEnabled && IsForcedBreakDirective(breakAfter))
                    {
                        if (MoveFlowCursorToNextFragment(ref currentY, fragmentHeight, ref lastMarginBottom, ref isFirstChild, ref floatManager))
                        {
                            maxBottom = Math.Max(maxBottom, currentY);
                        }
                    }
                }
            }
            
            // Include last margin if it doesn't collapse with parent bottom
            bool parentPreventsBottomCollapse = (blockBox.Geometry.Padding.Bottom > 0 || blockBox.Geometry.Border.Bottom > 0);
            if (parentPreventsBottomCollapse)
            {
                currentY += lastMarginBottom;
                maxBottom = Math.Max(maxBottom, currentY);
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

                        if (!hasExplicitWidth) w = resolved.Width;
                        if (!hasExplicitHeight) h = resolved.Height;
                    }

                    bool nativeCheckboxOrRadio =
                        blockBox.SourceNode is FenBrowser.Core.Dom.V2.Element inputElement &&
                        ReplacedElementSizing.IsNativeCheckboxOrRadio(inputElement);

                    if (nativeCheckboxOrRadio)
                    {
                        blockBox.Geometry.Padding = new Thickness();
                        blockBox.Geometry.Border = new Thickness();
                    }

                    if (w <= 0 && !hasExplicitWidth)
                    {
                        if (nativeCheckboxOrRadio) w = ReplacedElementSizing.NativeCheckboxRadioSize;
                        else if (t == "INPUT") w = 150f;
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
                        if (nativeCheckboxOrRadio) h = ReplacedElementSizing.NativeCheckboxRadioSize;
                        else if (t == "INPUT" || t == "SELECT") h = 24f;
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
            // Shrink-to-fit: always for infinite width probes and floats;
            // also for inline-block / inline-flex / inline-grid inner contexts
            // whose AvailableSize.Width was set to the probe result (finite) at
            // InlineFormattingContext line ~624, causing the first-pass child
            // layout to stretch rather than shrink-wrap.  Without this the status
            // pill widens to the parent block instead of wrapping text.
            bool isInlineAtomicInner = blockAutoWidth &&
                blockBox is FenBrowser.FenEngine.Layout.Tree.InlineBox;
            if (blockAutoWidth && (float.IsInfinity(state.AvailableSize.Width) || isFloatingBox || isInlineAtomicInner))
            {
                float previousWidth = blockBox.Geometry.ContentBox.Width;
                float previousBottom = blockBox.Geometry.ContentBox.Bottom;
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
                    if (string.Equals(blockBox.ComputedStyle?.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase) &&
                        TryResolveBorderBoxMinWidth(blockBox, state, out float borderBoxMinWidth) &&
                        maxWidth <= borderBoxMinWidth + 0.5f)
                    {
                        float horizontalChrome =
                            (float)blockBox.Geometry.Padding.Left +
                            (float)blockBox.Geometry.Padding.Right +
                            (float)blockBox.Geometry.Border.Left +
                            (float)blockBox.Geometry.Border.Right;
                        maxWidth = Math.Max(0f, borderBoxMinWidth - horizontalChrome);
                    }
                }

                // Measure actual content height from children so inline-block
                // inner contexts don't keep a stale (overly short) bottom from
                // the infinite-width probe pass.
                float maxChildBottom = blockBox.Geometry.ContentBox.Top;
                foreach (var child in blockBox.Children)
                {
                    if (child == null || child.IsOutOfFlow) continue;
                    float bottom = child.Geometry.MarginBox.Bottom;
                    if (!float.IsFinite(bottom)) bottom = child.Geometry.BorderBox.Bottom;
                    if (!float.IsFinite(bottom)) bottom = child.Geometry.ContentBox.Bottom;
                    if (float.IsFinite(bottom)) maxChildBottom = Math.Max(maxChildBottom, bottom);
                }

                // Update ContentBox width AND height.
                blockBox.Geometry.ContentBox = new SKRect(
                    blockBox.Geometry.ContentBox.Left,
                    blockBox.Geometry.ContentBox.Top,
                    blockBox.Geometry.ContentBox.Left + maxWidth,
                    maxChildBottom
                );

                LayoutBoxOps.SyncBoxes(blockBox.Geometry);

                // Trigger relayout when width or height changed significantly,
                // or always for inline-block inner contexts (the first pass used
                // infinite width which can produce wrong height for short labels).
                bool heightChanged = isInlineAtomicInner &&
                    Math.Abs(previousBottom - maxChildBottom) > 0.5f;
                if (maxWidth > 0f &&
                    float.IsFinite(previousWidth) &&
                    (Math.Abs(previousWidth - maxWidth) > 0.5f || heightChanged) &&
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

            // box-sizing: border-box means the declared height includes padding+border,
            // so subtract them to get the content-box height the layout pipeline stores.
            bool heightIsBorderBox = string.Equals(blockBox.ComputedStyle?.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase);
            if (explicitHeight.HasValue && heightIsBorderBox)
            {
                float verticalExtras =
                    (float)blockBox.Geometry.Padding.Top +
                    (float)blockBox.Geometry.Padding.Bottom +
                    (float)blockBox.Geometry.Border.Top +
                    (float)blockBox.Geometry.Border.Bottom;
                explicitHeight = Math.Max(0f, explicitHeight.Value - verticalExtras);
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

            if (!explicitHeight.HasValue && !HasNonEmptyInFlowChild(blockBox))
            {
                resolvedContentHeight = 0f;
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
                        float borderBoxMinHeight = minH;
                        minH = Math.Max(0f, minH - nonContentHeight);
                        if (ShouldClampClippedInlineLabelAutoHeight(blockBox, borderBoxMinHeight, nonContentHeight, resolvedContentHeight))
                        {
                            resolvedContentHeight = minH;
                        }
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
            LayoutBoxOps.SyncBoxes(blockBox.Geometry);

            // Layout Out of Flow
            foreach (var outOfFlowCandidate in outOfFlow)
            {
                var oof = outOfFlowCandidate.Box;
                var context = FormattingContext.Resolve(oof);

                // Pass 1: intrinsic measurement (auto-size shrink-to-fit signal).
                var intrinsicState = state.Clone();
                intrinsicState.AvailableSize = new SKSize(float.PositiveInfinity, float.PositiveInfinity);
                intrinsicState.ContainingBlockWidth = blockBox.Geometry.ContentBox.Width;
                intrinsicState.ContainingBlockHeight = blockBox.Geometry.ContentBox.Height;
                context.Layout(oof, intrinsicState);

                // Solve abs/fixed geometry from intrinsic size and insets.
                LayoutPositioningLogic.ResolvePositionedBox(
                    oof,
                    blockBox,
                    blockBox.Geometry,
                    state,
                    staticPosition: outOfFlowCandidate.StaticPosition);

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
                    collapsePositioningMarginsInFinalGeometry: true,
                    staticPosition: outOfFlowCandidate.StaticPosition);
            }
        }

        private readonly struct OutOfFlowLayoutCandidate
        {
            public OutOfFlowLayoutCandidate(LayoutBox box, SKPoint staticPosition)
            {
                Box = box;
                StaticPosition = staticPosition;
            }

            public LayoutBox Box { get; }
            public SKPoint StaticPosition { get; }
        }

        private static bool ShouldClampClippedInlineLabelAutoHeight(LayoutBox box, float borderBoxMinHeight, float nonContentHeight, float resolvedContentHeight)
        {
            if (box?.ComputedStyle == null ||
                borderBoxMinHeight <= 0f ||
                !string.Equals(box.ComputedStyle.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase) ||
                box.Children.Count(c => c != null && !c.IsOutOfFlow) != 1)
            {
                return false;
            }

            var child = box.Children.FirstOrDefault(c => c != null && !c.IsOutOfFlow);
            var childStyle = child?.ComputedStyle;
            if (child == null ||
                !HasClippedInlineMaxHeight(child))
            {
                return false;
            }

            float currentBorderBoxHeight = resolvedContentHeight + nonContentHeight;
            if (currentBorderBoxHeight <= borderBoxMinHeight + 0.5f ||
                !ContainsOnlyInlineTextContent(child))
            {
                return false;
            }

            float lineHeight = box.ComputedStyle.LineHeight.HasValue && box.ComputedStyle.LineHeight.Value > 0
                ? (float)box.ComputedStyle.LineHeight.Value
                : (float)(box.ComputedStyle.FontSize ?? 16d) * 1.2f;
            float minContentHeight = Math.Max(0f, borderBoxMinHeight - nonContentHeight);
            return lineHeight <= minContentHeight + 0.5f;
        }

        private static bool HasClippedInlineMaxHeight(LayoutBox box)
        {
            if (box == null)
            {
                return false;
            }

            var style = box.ComputedStyle;
            if (style != null &&
                string.Equals(style.Display, "inline", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(style.Overflow, "hidden", StringComparison.OrdinalIgnoreCase) &&
                style.MaxHeight.HasValue)
            {
                return true;
            }

            foreach (var child in box.Children)
            {
                if (HasClippedInlineMaxHeight(child))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsOnlyInlineTextContent(LayoutBox box)
        {
            if (box == null)
            {
                return true;
            }

            foreach (var child in box.Children)
            {
                if (child == null)
                {
                    continue;
                }

                if (child.SourceNode is FenBrowser.Core.Dom.V2.Element &&
                    !string.Equals(child.ComputedStyle?.Display, "inline", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!ContainsOnlyInlineTextContent(child))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryResolveBorderBoxMinWidth(LayoutBox box, LayoutState state, out float minWidth)
        {
            minWidth = 0f;
            var style = box?.ComputedStyle;
            if (style == null)
            {
                return false;
            }

            if (style.MinWidth.HasValue)
            {
                minWidth = (float)style.MinWidth.Value;
                return minWidth > 0f;
            }

            if (style.MinWidthPercent.HasValue)
            {
                float parentWidth = state.AvailableSize.Width;
                if (!float.IsFinite(parentWidth) || parentWidth <= 0f)
                {
                    parentWidth = state.ContainingBlockWidth > 0f ? state.ContainingBlockWidth : state.ViewportWidth;
                }

                if (parentWidth > 0f)
                {
                    minWidth = (float)(style.MinWidthPercent.Value / 100.0 * parentWidth);
                    return minWidth > 0f;
                }
            }

            if (!string.IsNullOrEmpty(style.MinWidthExpression))
            {
                float parentWidth = state.AvailableSize.Width;
                if (!float.IsFinite(parentWidth) || parentWidth <= 0f)
                {
                    parentWidth = state.ContainingBlockWidth > 0f ? state.ContainingBlockWidth : state.ViewportWidth;
                }

                minWidth = LayoutHelper.EvaluateCssExpression(style.MinWidthExpression, parentWidth, state.ViewportWidth, state.ViewportHeight);
                return minWidth > 0f;
            }

            return false;
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

            bool hasIntrinsicLabelWidth = false;
            bool hasIntrinsicDescendantWidth = false;
            if (!hasExplicitWidth && TryMeasureTextLabelShrinkToFitWidth(box, out float labelWidth))
            {
                directWidth = Math.Max(directWidth, labelWidth);
                hasIntrinsicLabelWidth = true;
            }

            if (!hasExplicitWidth &&
                TryMeasureDescendantOverflowShrinkToFitWidth(box, out float overflowWidth) &&
                overflowWidth > directWidth + 0.5f)
            {
                directWidth = overflowWidth;
                hasIntrinsicDescendantWidth = true;
            }

            if (!hasExplicitWidth &&
                TryMeasureFlexRowShrinkToFitWidth(box, out float flexRowWidth) &&
                flexRowWidth > directWidth + 0.5f)
            {
                return flexRowWidth;
            }

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

                    if (!isFloating &&
                        !hasIntrinsicLabelWidth &&
                        !hasIntrinsicDescendantWidth &&
                        descendantWidth < directWidth - 0.5f)
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

        private static bool TryMeasureDescendantOverflowShrinkToFitWidth(LayoutBox box, out float width)
        {
            width = 0f;
            if (box?.Geometry == null || box.Children.Count == 0)
            {
                return false;
            }

            float originLeft = box.Geometry.MarginBox.Left;
            float minLeft = float.PositiveInfinity;
            float maxRight = float.NegativeInfinity;

            foreach (var child in box.Children)
            {
                AccumulateDescendantOverflowShrinkToFitWidth(child, originLeft, ref minLeft, ref maxRight);
            }

            if (!float.IsFinite(minLeft) ||
                !float.IsFinite(maxRight) ||
                maxRight <= minLeft)
            {
                return false;
            }

            width = maxRight - minLeft;
            return width > 0f;
        }

        private static void AccumulateDescendantOverflowShrinkToFitWidth(
            LayoutBox box,
            float originLeft,
            ref float minLeft,
            ref float maxRight)
        {
            if (IsIgnorableShrinkToFitChild(box) || box.Geometry == null)
            {
                return;
            }

            float left = box.Geometry.MarginBox.Left - originLeft;
            float right = box.Geometry.MarginBox.Right - originLeft;
            if (float.IsFinite(left) && float.IsFinite(right) && right > left)
            {
                minLeft = Math.Min(minLeft, left);
                maxRight = Math.Max(maxRight, right);
            }

            foreach (var child in box.Children)
            {
                AccumulateDescendantOverflowShrinkToFitWidth(child, originLeft, ref minLeft, ref maxRight);
            }
        }

        private static bool TryMeasureTextLabelShrinkToFitWidth(LayoutBox box, out float width)
        {
            width = 0f;
            if (box == null)
            {
                return false;
            }

            string text = null;
            if (box is TextLayoutBox textBox)
            {
                text = textBox.TextContent;
            }
            else if (box.SourceNode is Element element)
            {
                string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
                if (tag is not ("A" or "BUTTON" or "SPAN" or "LABEL" or "SUP"))
                {
                    return false;
                }

                text = LayoutHelper.GetRenderableTextContentTrimmed(element);
            }

            text = CollapseShrinkToFitWhitespace(text).Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 120)
            {
                return false;
            }

            var style = box.ComputedStyle;
            float fontSize = (float)(style?.FontSize ?? 16);
            int fontWeight = style?.FontWeight ?? 400;
            string fontFamily = style?.FontFamilyName ?? "sans-serif";
            float textWidth = s_fontService.MeasureTextWidth(text, fontFamily, fontSize, fontWeight);
            if (!float.IsFinite(textWidth) || textWidth <= 0f)
            {
                textWidth = fontSize * Math.Max(1, text.Length) * 0.5f;
            }

            var padding = style?.Padding ?? new Thickness();
            var border = style?.BorderThickness ?? new Thickness();
            var margin = style?.Margin ?? new Thickness();
            float horizontalChrome =
                (float)padding.Left +
                (float)padding.Right +
                (float)border.Left +
                (float)border.Right +
                (float)margin.Left +
                (float)margin.Right;

            width = Math.Max(0f, textWidth + horizontalChrome);
            return width > 0f;
        }

        private static string CollapseShrinkToFitWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char ch in text)
            {
                if (TextWhitespaceClassifier.IsCollapsibleWhitespaceChar(ch))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(ch);
                pendingSpace = false;
            }

            return builder.ToString();
        }

        private static bool TryMeasureFlexRowShrinkToFitWidth(LayoutBox box, out float width)
        {
            width = 0f;
            if (box?.Children == null || box.Children.Count == 0)
            {
                return false;
            }

            var style = box.ComputedStyle;
            string display = style?.Display?.Trim().ToLowerInvariant() ?? string.Empty;
            if (display != "flex" && display != "inline-flex")
            {
                return false;
            }

            string direction = style?.FlexDirection?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(direction) &&
                style?.Map != null &&
                style.Map.TryGetValue("flex-direction", out var rawDirection))
            {
                direction = rawDirection?.Trim().ToLowerInvariant();
            }

            if (!string.IsNullOrEmpty(direction) && !direction.Contains("row", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            float gap = ResolveFlexShrinkToFitColumnGap(style);
            int itemCount = 0;
            foreach (var child in box.Children)
            {
                if (IsIgnorableShrinkToFitChild(child))
                {
                    continue;
                }

                float childWidth = MeasureShrinkToFitWidth(child);
                if (!float.IsFinite(childWidth) || childWidth <= 0f)
                {
                    continue;
                }

                if (itemCount > 0)
                {
                    width += gap;
                }

                width += childWidth;
                itemCount++;
            }

            return itemCount > 0 && width > 0f;
        }

        private static bool IsIgnorableShrinkToFitChild(LayoutBox box)
        {
            if (box == null || box.IsOutOfFlow)
            {
                return true;
            }

            if (box.ComputedStyle?.Display?.Contains("none", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (box is TextLayoutBox textBox)
            {
                return string.IsNullOrWhiteSpace(textBox.TextContent);
            }

            if (box.SourceNode is Text textNode)
            {
                return string.IsNullOrWhiteSpace(textNode.Data);
            }

            return false;
        }

        private static float ResolveFlexShrinkToFitColumnGap(CssComputed style)
        {
            if (style == null)
            {
                return 0f;
            }

            if (style.ColumnGap.HasValue && style.ColumnGap.Value > 0)
            {
                return (float)style.ColumnGap.Value;
            }

            if (style.Gap.HasValue && style.Gap.Value > 0)
            {
                return (float)style.Gap.Value;
            }

            float fontSize = (float)(style.FontSize ?? 16);
            if (style.Map != null)
            {
                if (style.Map.TryGetValue("column-gap", out var rawColumnGap) &&
                    TryParseShrinkToFitLength(rawColumnGap, fontSize, out float columnGap))
                {
                    return columnGap;
                }

                if (style.Map.TryGetValue("gap", out var rawGap) &&
                    TryParseShrinkToFitLength(rawGap, fontSize, out float gap))
                {
                    return gap;
                }
            }

            return 0f;
        }

        private static bool TryParseShrinkToFitLength(string raw, float fontSize, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim().ToLowerInvariant();
            if (raw.Equals("normal", StringComparison.Ordinal))
            {
                return false;
            }

            if (raw.EndsWith("px", StringComparison.Ordinal) &&
                float.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            {
                value = Math.Max(0f, px);
                return true;
            }

            if (raw.EndsWith("rem", StringComparison.Ordinal) &&
                float.TryParse(raw[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rem))
            {
                value = Math.Max(0f, rem * 16f);
                return true;
            }

            if (raw.EndsWith("em", StringComparison.Ordinal) &&
                float.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em))
            {
                value = Math.Max(0f, em * fontSize);
                return true;
            }

            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                value = Math.Max(0f, number);
                return true;
            }

            return false;
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
                else if (!string.IsNullOrEmpty(style.WidthExpression))
                {
                    width = LayoutHelper.EvaluateCssExpression(
                        style.WidthExpression,
                        available,
                        state.ViewportWidth,
                        state.ViewportHeight);
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
                else if (!string.IsNullOrEmpty(style.MaxWidthExpression))
                {
                    maxWidth = LayoutHelper.EvaluateCssExpression(
                        style.MaxWidthExpression,
                        available,
                        state.ViewportWidth,
                        state.ViewportHeight);
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
                else if (!string.IsNullOrEmpty(style.MinWidthExpression))
                {
                    minWidth = LayoutHelper.EvaluateCssExpression(
                        style.MinWidthExpression,
                        available,
                        state.ViewportWidth,
                        state.ViewportHeight);
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

            LayoutBoxOps.SyncBoxes(box.Geometry);
        }

        private void ShiftBox(BoxModel geometry, float dx, float dy)
        {
            geometry.ContentBox.Offset(dx, dy);
            geometry.PaddingBox.Offset(dx, dy);
            geometry.BorderBox.Offset(dx, dy);
            geometry.MarginBox.Offset(dx, dy);
        }

        private static float ResolveDefiniteContentHeightForChildren(LayoutBox box, LayoutState state)
        {
            var style = box?.ComputedStyle;
            if (style == null)
            {
                return float.NaN;
            }

            float? height = null;
            if (style.Height.HasValue)
            {
                height = (float)style.Height.Value;
            }
            else if (style.HeightPercent.HasValue)
            {
                float parentHeight = ResolvePercentageHeightContainingBlock(box, state);
                if (float.IsFinite(parentHeight) && parentHeight > 0f)
                {
                    height = (float)(style.HeightPercent.Value / 100.0 * parentHeight);
                }
            }
            else if (!string.IsNullOrEmpty(style.HeightExpression))
            {
                float parentHeight = ResolveExpressionContainingBlockHeight(box, state);
                height = LayoutHelper.EvaluateCssExpression(
                    style.HeightExpression,
                    parentHeight,
                    state.ViewportWidth,
                    state.ViewportHeight);
            }

            if (!height.HasValue || !float.IsFinite(height.Value) || height.Value <= 0f)
            {
                return float.NaN;
            }

            if (string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase))
            {
                var padding = style.Padding;
                var border = style.BorderThickness;
                height = Math.Max(0f, height.Value -
                    (float)(padding.Top + padding.Bottom + border.Top + border.Bottom));
            }

            return height.Value;
        }

        private static LayoutState CreateChildState(float contentWidth, LayoutState state, float definiteContentHeight = float.NaN,
            FloatManager floatManager = null, float floatOriginX = 0f, float floatOriginY = 0f)
        {
             float childAvailableHeight = float.IsFinite(definiteContentHeight) && definiteContentHeight > 0f
                ? definiteContentHeight
                : 0f;
             return new LayoutState(
                new SKSize(contentWidth, childAvailableHeight),
                contentWidth,
                childAvailableHeight,
                state.ViewportWidth,
                state.ViewportHeight,
                state.Deadline
            )
            {
                FloatManager = floatManager,
                FloatOriginX = floatOriginX,
                FloatOriginY = floatOriginY,
                ScrollOffsetX = state.ScrollOffsetX,
                ScrollOffsetY = state.ScrollOffsetY,
                ScrollContainer = state.ScrollContainer
            };
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

        private static bool HasNonEmptyInFlowChild(LayoutBox box)
        {
            foreach (var child in box.Children)
            {
                if (child == null || child.IsOutOfFlow)
                {
                    continue;
                }

                if (child.SourceNode is Text text &&
                    !TextWhitespaceClassifier.IsCollapsibleWhitespaceOnly(text.Data))
                {
                    return true;
                }

                var style = child.ComputedStyle;
                bool hasExplicitHeight =
                    style?.Height.HasValue == true ||
                    style?.HeightPercent.HasValue == true ||
                    !string.IsNullOrWhiteSpace(style?.HeightExpression);
                bool hasVerticalChrome =
                    (style?.Padding.Top ?? 0) > 0 ||
                    (style?.Padding.Bottom ?? 0) > 0 ||
                    (style?.BorderThickness.Top ?? 0) > 0 ||
                    (style?.BorderThickness.Bottom ?? 0) > 0;

                if (hasExplicitHeight || hasVerticalChrome)
                {
                    return true;
                }

                if (child.SourceNode is Element element &&
                    ReplacedElementSizing.IsReplacedElementTag(element.TagName))
                {
                    return true;
                }

                if (HasNonEmptyInFlowChild(child))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CenterSingleButtonFlowChildIfNeeded(LayoutBox parent, LayoutBox child, LayoutState state)
        {
            if (parent?.Geometry == null ||
                child?.Geometry == null ||
                parent.SourceNode is not Element element ||
                !string.Equals(element.TagName, "BUTTON", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!HasSingleVisibleFlowChild(parent, child))
            {
                return;
            }

            var parentContent = parent.Geometry.ContentBox;
            float childHeight = child.Geometry.MarginBox.Height;
            if (parentContent.Height <= childHeight + 1f || childHeight <= 0f)
            {
                return;
            }

            float targetTop = parentContent.Top + (parentContent.Height - childHeight) * 0.5f;
            if (Math.Abs(child.Geometry.MarginBox.Top - targetTop) <= 0.5f)
            {
                return;
            }

            LayoutBoxOps.PositionSubtree(child, child.Geometry.MarginBox.Left, targetTop, state);
        }

        private static bool HasSingleVisibleFlowChild(LayoutBox parent, LayoutBox expectedChild)
        {
            int count = 0;
            LayoutBox visibleChild = null;
            foreach (var child in parent.Children)
            {
                if (child == null || child.IsOutOfFlow || child.ComputedStyle?.Display == "none")
                {
                    continue;
                }

                if (child.SourceNode is Element element)
                {
                    var tag = element.TagName?.ToUpperInvariant();
                    if (tag == "STYLE" || tag == "SCRIPT")
                    {
                        continue;
                    }
                }

                if (child.Geometry?.MarginBox.Height <= 0.5f && child.Geometry?.MarginBox.Width <= 0.5f)
                {
                    continue;
                }

                count++;
                visibleChild = child;
                if (count > 1)
                {
                    return false;
                }
            }

            return count == 1 && ReferenceEquals(visibleChild, expectedChild);
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

        private static bool TryResolveFragmentainerHeight(LayoutState state, out float fragmentHeight)
        {
            fragmentHeight = state.AvailableSize.Height;
            if (!float.IsFinite(fragmentHeight) || fragmentHeight <= 0f)
            {
                fragmentHeight = state.ContainingBlockHeight;
            }

            if (!float.IsFinite(fragmentHeight) || fragmentHeight <= 0f)
            {
                return false;
            }

            return true;
        }

        private static string NormalizeBreakDirective(string raw)
        {
            return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToLowerInvariant();
        }

        private static bool IsForcedBreakDirective(string directive)
        {
            if (string.IsNullOrWhiteSpace(directive))
            {
                return false;
            }

            return directive == "always" ||
                   directive == "left" ||
                   directive == "right" ||
                   directive == "recto" ||
                   directive == "verso" ||
                   directive == "page" ||
                   directive == "column" ||
                   directive == "region";
        }

        private static bool IsAvoidBreakDirective(string directive)
        {
            if (string.IsNullOrWhiteSpace(directive))
            {
                return false;
            }

            return directive == "avoid" ||
                   directive == "avoid-page" ||
                   directive == "avoid-column" ||
                   directive == "avoid-region";
        }

        private static bool MoveFlowCursorToNextFragment(
            ref float currentY,
            float fragmentHeight,
            ref float lastMarginBottom,
            ref bool isFirstChild,
            ref FloatManager floatManager)
        {
            if (!float.IsFinite(fragmentHeight) || fragmentHeight <= 0f)
            {
                return false;
            }

            float next = MoveToNextFragmentStart(currentY, fragmentHeight);
            if (next <= currentY + 0.5f)
            {
                return false;
            }

            currentY = next;
            lastMarginBottom = 0f;
            isFirstChild = true;
            floatManager = new FloatManager();
            return true;
        }

        private static float MoveToNextFragmentStart(float positionY, float fragmentHeight)
        {
            if (!float.IsFinite(fragmentHeight) || fragmentHeight <= 0f)
            {
                return positionY;
            }

            if (!float.IsFinite(positionY))
            {
                positionY = 0f;
            }

            const float epsilon = 0.5f;
            float normalized = Math.Max(0f, positionY);
            float fragmentIndex = (float)Math.Floor((normalized + epsilon) / fragmentHeight);
            return (fragmentIndex + 1f) * fragmentHeight;
        }

        private static bool ShouldMoveBlockToNextFragment(float blockTop, float blockOuterHeight, float fragmentHeight)
        {
            if (!float.IsFinite(fragmentHeight) || fragmentHeight <= 0f)
            {
                return false;
            }

            if (!float.IsFinite(blockTop) || !float.IsFinite(blockOuterHeight) || blockOuterHeight <= 0f)
            {
                return false;
            }

            // If the box itself exceeds a fragment, keep it in place and allow overflow.
            if (blockOuterHeight >= fragmentHeight - 0.5f)
            {
                return false;
            }

            const float epsilon = 0.5f;
            float normalizedTop = Math.Max(0f, blockTop);
            float fragmentIndex = (float)Math.Floor((normalizedTop + epsilon) / fragmentHeight);
            float fragmentBottom = (fragmentIndex + 1f) * fragmentHeight;
            return normalizedTop + blockOuterHeight > fragmentBottom + epsilon;
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
