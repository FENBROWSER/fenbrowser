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
            var floatManager = new FloatManager();
            float currentY = yOffset; // Track Y relative to content box top

            foreach (var child in blockBox.Children)
            {
                // Reliability Gate: Check intra-frame deadline
                state.Deadline?.Check();

                if (child.IsOutOfFlow)
                {
                    outOfFlow.Add(child);
                    continue;
                }

                // Check Float
                string floatStyle = child.ComputedStyle?.Float?.ToLowerInvariant() ?? "none";
                string clearStyle = child.ComputedStyle?.Clear?.ToLowerInvariant() ?? "none";

                // Handle Clearance (for both floats and blocks)
                if (clearStyle != "none")
                {
                    float clearY = floatManager.GetClearanceY(clearStyle, currentY);
                    if (clearY > currentY)
                    {
                        // Add top margin to satisfy clearance?
                        // Or just move yOffset
                        currentY = clearY;
                    }
                }

                // Create child state
                float childAvailableHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
                var childState = new LayoutState(
                    new SKSize(contentWidth, childAvailableHeight),
                    contentWidth,
                    childAvailableHeight,
                    state.ViewportWidth,
                    state.ViewportHeight
                );

                var context = FormattingContext.Resolve(child);

                if (floatStyle == "left" || floatStyle == "right")
                {
                    // Float Layout
                    context.Layout(child, childState);
                    
                    // Box is sized. Now place it.
                    float floatWidth = child.Geometry.MarginBox.Width;
                    
                    // Simple placement: At current Y, find side
                    // If doesn't fit? (Not implemented: Float drop)
                    
                    float fx = 0;
                    if (floatStyle == "left")
                    {
                        fx = xOffset; // Left edge of content
                        // Check if we need to push down due to existing left floats?
                        // FloatManager will handle complex stacking if we expanded it.
                        // For now, assume it stacks against previous left float or edge.
                        // Simplified: floats stack vertically if width > available?
                        // Correct: They try to go as high as possible.
                    }
                    else
                    {
                        fx = xOffset + contentWidth - floatWidth;
                    }
                    
                    // Add to manager
                    LayoutBoxOps.SetPosition(child, fx, currentY);
                    child.Geometry.MarginBox = new SKRect(fx, currentY, fx + floatWidth, currentY + child.Geometry.MarginBox.Height); // Refix absolute
                    
                    floatManager.AddFloat(child, floatStyle == "left");
                    
                    // Floats don't advance the normal flow Y immediately for subsequent blocks,
                    // BUT they take up space.
                    // Block flow continues at currentY (unless cleared).
                }
                else
                {
                    // Normal Flow Block
                    // Check available space due to floats (Intrusion)
                    // If this block creates a new BFC (e.g. overflow: hidden), it narrows next to floats.
                    // If it is a normal block, it overlaps floats but its *text* (inline) wraps.
                    
                    // Simplified: We implement BFC narrowing for ALL blocks for better out-of-the-box appearance
                    // (simulating overflow: hidden behavior which is common for layouts)
                    
                    var space = floatManager.GetAvailableSpace(currentY, 0, contentWidth);
                    // If tight space, might need to move down?
                    
                    // Layout
                     context.Layout(child, childState);
                     
                     float childX = xOffset + (float)child.Geometry.Margin.Left;
                     float childY = currentY + (float)child.Geometry.Margin.Top; // Collapsing skipped
                     
                     LayoutBoxOps.SetPosition(child, childX, childY);
                     
                     currentY += child.Geometry.MarginBox.Height;
                }
            }
            
            yOffset = currentY; // Final Y

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
