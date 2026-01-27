using System;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.Core;

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

        public override void Layout(LayoutBox box, LayoutState state)
        {
            if (!(box is BlockBox blockBox)) return;
            
            // DEBUG: Entry log
            string dbgTag = (blockBox.SourceNode as FenBrowser.Core.Dom.Element)?.TagName ?? "?";
            FenLogger.Info($"[BFC-ENTRY] <{dbgTag}> AvailH={state.AvailableSize.Height} ViewH={state.ViewportHeight}", LogCategory.Layout);

            // 1. Resolve Width
            ResolveWidth(blockBox, state);

            // 2. Prepare for Child Layout
            float yOffset = 0;
            // Add padding top + border top to start content cursor
            yOffset += (float)blockBox.Geometry.Padding.Top + (float)blockBox.Geometry.Border.Top;
            
            float xOffset = (float)blockBox.Geometry.Padding.Left + (float)blockBox.Geometry.Border.Left;
            float contentWidth = blockBox.Geometry.ContentBox.Width;

            // 3. Iterate Children
            var outOfFlow = new List<LayoutBox>();
            var floatManager = new FloatManager();
            
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
                    // Float Layout (Simplified - ignores margins for simplicity in this context)
                    var childState = CreateChildState(contentWidth, state);
                    FormattingContext.Resolve(child).Layout(child, childState);
                    
                    float floatWidth = child.Geometry.MarginBox.Width;
                    float fx = (floatStyle == "left") ? xOffset : (xOffset + contentWidth - floatWidth);
                    
                    LayoutBoxOps.SetPosition(child, fx, yOffset + currentY);
                    floatManager.AddFloat(child, floatStyle == "left");
                    
                    // [Compliance] Ensure float contributes to container height if it establishes a BFC
                    // For now, we always include it if height is auto
                    maxBottom = Math.Max(maxBottom, currentY + child.Geometry.MarginBox.Height);
                }
                else
                {
                    // Normal Flow Block
                    var childState = CreateChildState(contentWidth, state);
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
                    
                    float childX = xOffset + (float)child.Geometry.Margin.Left;
                    float childY = yOffset + currentY; // Absolute to container BorderBox top
                    
                    LayoutBoxOps.SetPosition(child, childX, childY);
                    
                    // Advance cursor by CONTENT (BorderBox) height
                    currentY += child.Geometry.BorderBox.Height;
                    
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
                string t = (blockBox.SourceNode as FenBrowser.Core.Dom.Element)?.TagName?.ToUpperInvariant();
                if (t == "IMG" || t == "SVG" || t == "INPUT" || t == "BUTTON")
                {
                    // Basic intrinsic height resolution if not explicit
                    float h = (float)(blockBox.ComputedStyle?.Height ?? 0);
                    if (h <= 0) h = 24f; // Default for icons/inputs
                    maxBottom = Math.Max(maxBottom, h);
                }
            }

            yOffset += maxBottom; // Total height from Border Top to last content/margin edge

            // SHRINK-TO-FIT: If width was auto and available was infinity, we shrink to widest child
            if (blockBox.ComputedStyle != null && !blockBox.ComputedStyle.Width.HasValue && float.IsInfinity(state.AvailableSize.Width))
            {
                float maxWidth = 0;
                foreach (var child in blockBox.Children)
                {
                    maxWidth = Math.Max(maxWidth, child.Geometry.MarginBox.Width);
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
            // Auto height = content bottom + padding bottom + border bottom
            float autoHeight = yOffset + (float)blockBox.Geometry.Padding.Bottom + (float)blockBox.Geometry.Border.Bottom;
            
            // ICB OVERRIDE: HTML and BODY must be at least viewport height
            // This is the Initial Containing Block invariant - required for CSS height chain
            string tag = (blockBox.SourceNode as FenBrowser.Core.Dom.Element)?.TagName?.ToUpperInvariant() ?? "";
            bool isRootElement = (tag == "HTML" || tag == "BODY");
            
            if (isRootElement && autoHeight < state.ViewportHeight)
            {
                autoHeight = state.ViewportHeight;
                System.Console.WriteLine($"[BFC-ICB] <{tag}> auto-height forced to viewport: {state.ViewportHeight}px");
            }
            
            // Check explicit height
            // (Simplified)
             if (blockBox.ComputedStyle != null && blockBox.ComputedStyle.Height.HasValue)
            {
                // Explicit height wins
                float explicitH = (float)blockBox.ComputedStyle.Height.Value;
                blockBox.Geometry.ContentBox = new SKRect(
                    blockBox.Geometry.ContentBox.Left,
                    blockBox.Geometry.ContentBox.Top,
                    blockBox.Geometry.ContentBox.Right,
                    blockBox.Geometry.ContentBox.Top + explicitH
                );
                
                // Reset outer boxes to match new content box + explicit padding/border default?
                // Usually we sync.
                SyncBoxes(blockBox.Geometry);
                // Console.WriteLine($"[BlockBFC] Set Explicit Height: {explicitH}...");
                // Recalculate boxes... needed?
            }
            else
            {
                // Console.WriteLine($"[BlockBFC] No Explicit Height. Auto height: {autoHeight}");
                // Use auto height
                // We need to set the height of the box model buffers
                float h = autoHeight - ((float)blockBox.Geometry.Padding.Top + (float)blockBox.Geometry.Border.Top + (float)blockBox.Geometry.Padding.Bottom + (float)blockBox.Geometry.Border.Bottom);
                blockBox.Geometry.ContentBox = new SKRect(
                    blockBox.Geometry.ContentBox.Left, 
                    blockBox.Geometry.ContentBox.Top, 
                    blockBox.Geometry.ContentBox.Left + blockBox.Geometry.ContentBox.Width, 
                    blockBox.Geometry.ContentBox.Top + Math.Max(0, h));
                
                // Re-sync outer boxes
                // (Geometry logic usually centralized)
                SyncBoxes(blockBox.Geometry);
            }

            // Layout Out of Flow
            foreach (var oof in outOfFlow)
            {
                 LayoutPositioningLogic.ResolvePositionedBox(oof, blockBox, blockBox.Geometry);
                 var context = FormattingContext.Resolve(oof);
                 var childState = new LayoutState(
                    new SKSize(blockBox.Geometry.ContentBox.Width, blockBox.Geometry.ContentBox.Height),
                    blockBox.Geometry.ContentBox.Width,
                    (float)blockBox.Geometry.ContentBox.Height,
                    state.ViewportWidth,
                    state.ViewportHeight
                );
                context.Layout(oof, childState);
            }
        }

        private void ResolveWidth(BlockBox box, LayoutState state)
        {
            float available = state.AvailableSize.Width;
            if (float.IsInfinity(available) || float.IsNaN(available)) available = state.ViewportWidth;
            if (float.IsInfinity(available) || float.IsNaN(available)) available = 1920f; // Fallback if Viewport is invalid
            
            FenLogger.Info($"[BFC-RESOLVE-START] Avail={state.AvailableSize.Width} Sanitized={available} VP={state.ViewportWidth}", LogCategory.Layout);

            // 1. Initial values from computed style
            var style = box.ComputedStyle;
            Thickness padding = style?.Padding ?? new Thickness();
            Thickness border = style?.BorderThickness ?? new Thickness();
            Thickness margin = style?.Margin ?? new Thickness();

            string tag = (box.SourceNode as FenBrowser.Core.Dom.Element)?.TagName ?? "?";
            string id = (box.SourceNode as FenBrowser.Core.Dom.Element)?.GetAttribute("id") ?? "";

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

            if (width.HasValue)
            {
                resolvedContentWidth = width.Value;
            }
            else
            {
                // Width auto fills available space minus margins
                float marginExtras = (float)(margin.Left + margin.Right);
                resolvedContentWidth = Math.Max(0, available - horizontalExtras - marginExtras);
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
