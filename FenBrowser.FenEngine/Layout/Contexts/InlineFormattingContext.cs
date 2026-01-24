using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using FenBrowser.FenEngine.Layout; 

namespace FenBrowser.FenEngine.Layout.Contexts
{
    public class InlineFormattingContext : FormattingContext
    {
        private static InlineFormattingContext _instance;
        public static InlineFormattingContext Instance => _instance ??= new InlineFormattingContext();

        // Line Box Structure
        private class LineBox
        {
            public float Width = 0;
            public float Height = 0;
            public float Baseline = 0;
            public List<LayoutBox> Items = new List<LayoutBox>();
        }

        public override void Layout(LayoutBox box, LayoutState state)
        {
            // Inline Context lays out the children of 'box' into line boxes.
            // 'box' is typically a BlockBox that contains inline-level children.
            
            float maxWidth = state.AvailableSize.Width;
            float currentX = 0;
            float currentY = 0;

            // Initialize Geometry
            // Resolve width similar to Block (fill available)
            ResolveContextWidth(box, state);
            
            // Content start
            currentX = (float)box.Geometry.Padding.Left + (float)box.Geometry.Border.Left;
            currentY = (float)box.Geometry.Padding.Top + (float)box.Geometry.Border.Top;
            float startX = currentX;
            float contentLimit = box.Geometry.ContentBox.Width;

            float maxLineHeight = 0;
            var outOfFlow = new List<LayoutBox>();

            foreach (var child in box.Children)
            {
                // Reliability Gate: Check intra-frame deadline
                state.Deadline?.Check();

                if (child.IsOutOfFlow)
                {
                    outOfFlow.Add(child);
                    continue;
                }

                // Measure child (Intrinsic)
                // Text needs correct font measurement.
                SKSize childSize = MeasureInlineChild(child);
                
                // Check for Wrap
                if (currentX + childSize.Width > startX + contentLimit && currentX > startX)
                {
                    // New Line
                    currentX = startX;
                    currentY += maxLineHeight;
                    maxLineHeight = 0;
                }

                // Place child
                // Assuming child geometry is (0,0) based, shift it
                if (child.Geometry == null) child.Geometry = new BoxModel();
                
                // Create simple box model for the inline item
                child.Geometry.ContentBox = new SKRect(currentX, currentY, currentX + childSize.Width, currentY + childSize.Height);
                // Sync others
                child.Geometry.PaddingBox = child.Geometry.ContentBox;
                child.Geometry.BorderBox = child.Geometry.ContentBox;
                child.Geometry.MarginBox = child.Geometry.ContentBox;

                currentX += childSize.Width;
                maxLineHeight = Math.Max(maxLineHeight, childSize.Height);
            }

            // Final Height
            currentY += maxLineHeight;
            float finalH = currentY + (float)box.Geometry.Padding.Bottom + (float)box.Geometry.Border.Bottom;
            
            // Check explicit height
            float finalContentHeight = Math.Max(0f, finalH - ((float)box.Geometry.Padding.Top + (float)box.Geometry.Border.Top + (float)box.Geometry.Padding.Bottom + (float)box.Geometry.Border.Bottom));
            
            if (box.ComputedStyle != null && box.ComputedStyle.Height.HasValue)
            {
                 finalContentHeight = (float)box.ComputedStyle.Height.Value;
            }

            // Update box height
             box.Geometry.ContentBox = new SKRect(
                 box.Geometry.ContentBox.Left,
                 box.Geometry.ContentBox.Top,
                 box.Geometry.ContentBox.Right,
                 box.Geometry.ContentBox.Top + finalContentHeight
             );
            
            SyncBoxes(box.Geometry);
            
            // Handle out-of-flow
             foreach (var oof in outOfFlow)
            {
                 LayoutPositioningLogic.ResolvePositionedBox(oof, box, box.Geometry);
                 var context = FormattingContext.Resolve(oof);
                 var childState = new LayoutState(
                    new SKSize(oof.Geometry.ContentBox.Width, oof.Geometry.ContentBox.Height),
                    oof.Geometry.ContentBox.Width,
                    oof.Geometry.ContentBox.Height,
                    state.ViewportWidth,
                    state.ViewportHeight
                );
                context.Layout(oof, childState);
            }
        }

        private SKSize MeasureInlineChild(LayoutBox child)
        {
            if (child is TextLayoutBox textBox)
            {
                // Simple estimation
                // In real engine, use FontManager/Skia Paint
                float charWidth = 8f; 
                float lineHeight = 16f;
                // Parse font size?
                if (textBox.ComputedStyle != null && textBox.ComputedStyle.FontSize.HasValue)
                {
                    lineHeight = (float)textBox.ComputedStyle.FontSize.Value * 1.2f;
                    charWidth = lineHeight * 0.5f; 
                }
                
                return new SKSize(textBox.TextContent.Length * charWidth, lineHeight);
            }
            else if (child is InlineBox inlineBox)
            {
                 // Recurse? Or Atomic?
                 // If it's a <span>, it might contain text.
                 // Ideally we flatten or recurse. 
                 // For MVP, assuming flattened or atomic.
                 return new SKSize(50f, 20f);
            }
            
            return new SKSize(0f,0f);
        }

        private void ResolveContextWidth(LayoutBox box, LayoutState state)
        {
            float available = state.AvailableSize.Width;
            
            // Check explicit width
            float finalW = available; 
            
            var p = box.ComputedStyle?.Padding ?? new Thickness();
            var b = box.ComputedStyle?.BorderThickness ?? new Thickness();
            var m = box.ComputedStyle?.Margin ?? new Thickness();

            // Default auto logic
            float used = (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
            finalW = Math.Max(0f, available - used);

            if (box.ComputedStyle != null && box.ComputedStyle.Width.HasValue)
            {
                finalW = (float)box.ComputedStyle.Width.Value;
            }

            // Set content box
            // Preserve Left/Top (for positioning)
            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + finalW, top);
            
            // Set props
            box.Geometry.Padding = p;
            box.Geometry.Border = b;
            box.Geometry.Margin = m;
            
            SyncBoxes(box.Geometry);
        }
        
        private void SyncBoxes(BoxModel geometry)
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
    }
}
