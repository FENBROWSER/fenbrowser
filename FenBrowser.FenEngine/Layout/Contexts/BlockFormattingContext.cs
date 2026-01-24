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
            System.Console.WriteLine($"[BFC-ENTRY] <{dbgTag}> AvailH={state.AvailableSize.Height} ViewH={state.ViewportHeight}");

            // 1. Resolve Width
            ResolveWidth(blockBox, state);

            // 2. Prepare for Child Layout
            float yOffset = 0;
            // Add padding top + border top to start content cursor
            yOffset += (float)blockBox.Geometry.Padding.Top + (float)blockBox.Geometry.Border.Top;
            
            float xOffset = (float)blockBox.Geometry.Padding.Left + (float)blockBox.Geometry.Border.Left;

            // Content width for children
            float contentWidth = blockBox.Geometry.ContentBox.Width;

            // 3. Iterate Children
            var outOfFlow = new List<LayoutBox>();
            foreach (var child in blockBox.Children)
            {
                // Reliability Gate: Check intra-frame deadline
                state.Deadline?.Check();

                if (child.IsOutOfFlow)
                {
                    outOfFlow.Add(child);
                    continue;
                }

                // Create child state
                // Note: Block children usually shrink-to-fit if float, otherwise fill available.
                // The available width is our content width.
                // CRITICAL FIX: Pass actual containing block height (not infinity) for % height resolution
                float childAvailableHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;

                var childState = new LayoutState(
                    new SKSize(contentWidth, childAvailableHeight),
                    contentWidth,
                    childAvailableHeight, // Propagate CB height for percentage resolution
                    state.ViewportWidth,
                    state.ViewportHeight
                );

                // Determine context for child
                var context = FormattingContext.Resolve(child);
                context.Layout(child, childState);

                // Post-Layout: positioning
                // Retrieve child geometry
                var childOuter = child.Geometry.MarginBox;
                
                // Margin Collapsing would happen here (adjusting yOffset or child margins)
                // For MVP: Simple stacking
                yOffset += (float)child.Geometry.Margin.Top;

                // Place child
                // Coordinate system: Relative to Parent's Content Box? 
                // Let's stick to Parent's Origin (0,0) being top-left of parent border box? 
                // Or absolute? 
                // Usually convenient to store relative offsets.
                
                // Let's store geometry relative to Parent Content Box or Border Box?
                // MinimalLayoutComputer used relative.
                
                float childX = xOffset + (float)child.Geometry.Margin.Left;
                float childY = yOffset;
                
                // Update child "Location" (BoxModel usually stores rects, we need to shift them)
                // Assuming child.Geometry was set to (0,0,w,h) during its layout?
                // We shift it now.
                ShiftBox(child.Geometry, childX, childY);

                yOffset += childOuter.Height;
                // Bottom margin handles next gap
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
            float used = 0;

            // Get properties
            Thickness padding = box.ComputedStyle?.Padding ?? new Thickness();
            Thickness border = box.ComputedStyle?.BorderThickness ?? new Thickness();
            Thickness margin = box.ComputedStyle?.Margin ?? new Thickness();

            // Store in geometry
            box.Geometry.Padding = padding;
            box.Geometry.Border = border;
            box.Geometry.Margin = margin;

            // Calculate used width
            // For width: auto, we subtract margins, borders, padding from available.
            // But for content-box, width is just the content.
            
            // Logic:
            // 1. If explicit width exists -> use it.
            // 2. If auto -> available - (margin + border + padding)
            
            float explicitWidth = -1;
            if (box.ComputedStyle != null && box.ComputedStyle.Width.HasValue)
            {
                explicitWidth = (float)box.ComputedStyle.Width.Value;
                // Console.WriteLine($"[BlockBFC] Explicit Width found: {explicitWidth} ...
            }
            else 
            {
                 // Console.WriteLine($"[BlockBFC] No explicit width. Available: {available} ...
            }

            if (explicitWidth >= 0)
            {
                box.Geometry.ContentBox = new SKRect(box.Geometry.ContentBox.Left, box.Geometry.ContentBox.Top, box.Geometry.ContentBox.Left + explicitWidth, box.Geometry.ContentBox.Top);
            }
            else
            {
                // Auto
                float extras = (float)(padding.Left + padding.Right + border.Left + border.Right + margin.Left + margin.Right);
                float contentWidth = Math.Max(0, available - extras);
                box.Geometry.ContentBox = new SKRect(box.Geometry.ContentBox.Left, box.Geometry.ContentBox.Top, box.Geometry.ContentBox.Left + contentWidth, box.Geometry.ContentBox.Top);
            }

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
    }

    /// <summary>
    /// Placeholder for IFC.
    /// </summary>

}
