using SkiaSharp;
using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.DevTools;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Debug overlay renderer.
    /// Draws visual debugging information on top of the rendered page.
    /// 
    /// Phase 4: Visibility - Visual debugging tools for layout inspection.
    /// 
    /// This is a SEPARATE render pass that runs AFTER normal painting.
    /// It only activates when DebugConfig flags are enabled (zero overhead when off).
    /// </summary>
    public static class DebugOverlay
    {
        // Color palette for box visualization
        private static readonly SKColor MarginColor = new SKColor(255, 165, 0, 100);    // Orange
        private static readonly SKColor BorderColor = new SKColor(0, 0, 255, 100);      // Blue
        private static readonly SKColor PaddingColor = new SKColor(0, 255, 0, 100);     // Green
        private static readonly SKColor ContentColor = new SKColor(255, 0, 255, 100);   // Magenta

        // Dirty region colors
        private static readonly SKColor StyleDirtyColor = new SKColor(255, 0, 0, 150);  // Red
        private static readonly SKColor LayoutDirtyColor = new SKColor(255, 255, 0, 150); // Yellow
        private static readonly SKColor PaintDirtyColor = new SKColor(0, 0, 255, 150);  // Blue

        // HitTest highlight
        private static readonly SKColor HitTestColor = new SKColor(0, 255, 255, 100);   // Cyan

        // Overflow clip indicator
        private static readonly SKColor OverflowColor = new SKColor(255, 0, 0, 200);    // Red dashed

        /// <summary>
        /// Renders debug overlays on the canvas.
        /// Call this AFTER normal page rendering.
        /// </summary>
        /// <param name="canvas">Skia canvas to draw on.</param>
        /// <param name="boxes">All layout boxes from the layout engine.</param>
        /// <param name="root">Root DOM node for dirty flag traversal.</param>
        public static void Render(SKCanvas canvas, IEnumerable<KeyValuePair<Node, BoxModel>> boxes, Node root = null)
        {
            var config = DebugConfig.Instance;
            
            // Early exit if nothing enabled (zero overhead)
            if (!config.AnyOverlayEnabled)
                return;

            canvas.Save();
            
            try
            {
                // Draw box outlines
                if (config.ShowLayoutBoxes || config.ShowMarginBox || config.ShowPaddingBox || config.ShowContentBox)
                {
                    DrawBoxOutlines(canvas, boxes, config);
                }

                // Draw dirty regions
                if (config.ShowDirtyRegions && root != null)
                {
                    DrawDirtyRegions(canvas, boxes, root);
                }

                // Draw HitTest target
                if (config.ShowHitTestTarget && config.CurrentHitTarget != null)
                {
                    DrawHitTestTarget(canvas, boxes, config.CurrentHitTarget as Node);
                }
            }
            finally
            {
                canvas.Restore();
            }
        }

        private static void DrawBoxOutlines(SKCanvas canvas, IEnumerable<KeyValuePair<Node, BoxModel>> boxes, DebugConfig config)
        {
            using var marginPaint = CreateOutlinePaint(MarginColor);
            using var borderPaint = CreateOutlinePaint(BorderColor, 2);
            using var paddingPaint = CreateOutlinePaint(PaddingColor);
            using var contentPaint = CreateOutlinePaint(ContentColor);

            foreach (var kvp in boxes)
            {
                var box = kvp.Value;
                if (box == null) continue;

                if (config.ShowLayoutBoxes || config.ShowMarginBox)
                {
                    if (!box.MarginBox.IsEmpty)
                        canvas.DrawRect(box.MarginBox, marginPaint);
                }

                if (config.ShowLayoutBoxes)
                {
                    if (!box.BorderBox.IsEmpty)
                        canvas.DrawRect(box.BorderBox, borderPaint);
                }

                if (config.ShowLayoutBoxes || config.ShowPaddingBox)
                {
                    if (!box.PaddingBox.IsEmpty)
                        canvas.DrawRect(box.PaddingBox, paddingPaint);
                }

                if (config.ShowLayoutBoxes || config.ShowContentBox)
                {
                    if (!box.ContentBox.IsEmpty)
                        canvas.DrawRect(box.ContentBox, contentPaint);
                }
            }
        }

        private static void DrawDirtyRegions(SKCanvas canvas, IEnumerable<KeyValuePair<Node, BoxModel>> boxes, Node root)
        {
            using var stylePaint = CreateFillPaint(StyleDirtyColor);
            using var layoutPaint = CreateFillPaint(LayoutDirtyColor);
            using var paintPaint = CreateFillPaint(PaintDirtyColor);

            var boxDict = new Dictionary<Node, BoxModel>();
            foreach (var kvp in boxes)
            {
                boxDict[kvp.Key] = kvp.Value;
            }

            DrawDirtyRecursive(canvas, root, boxDict, stylePaint, layoutPaint, paintPaint);
        }

        private static void DrawDirtyRecursive(
            SKCanvas canvas, 
            Node node, 
            Dictionary<Node, BoxModel> boxes,
            SKPaint stylePaint,
            SKPaint layoutPaint,
            SKPaint paintPaint)
        {
            if (node == null) return;

            if (boxes.TryGetValue(node, out var box) && box != null && !box.BorderBox.IsEmpty)
            {
                // Draw overlay for each dirty type
                if (node.StyleDirty)
                {
                    canvas.DrawRect(box.BorderBox, stylePaint);
                }
                else if (node.LayoutDirty)
                {
                    canvas.DrawRect(box.BorderBox, layoutPaint);
                }
                else if (node.PaintDirty)
                {
                    canvas.DrawRect(box.BorderBox, paintPaint);
                }
            }

            // Recurse into children
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    DrawDirtyRecursive(canvas, child, boxes, stylePaint, layoutPaint, paintPaint);
                }
            }
        }

        private static void DrawHitTestTarget(SKCanvas canvas, IEnumerable<KeyValuePair<Node, BoxModel>> boxes, Node target)
        {
            if (target == null) return;

            using var paint = CreateOutlinePaint(HitTestColor, 3);
            using var fillPaint = CreateFillPaint(new SKColor(0, 255, 255, 50));

            foreach (var kvp in boxes)
            {
                if (kvp.Key == target && kvp.Value != null)
                {
                    var box = kvp.Value;
                    if (!box.BorderBox.IsEmpty)
                    {
                        canvas.DrawRect(box.BorderBox, fillPaint);
                        canvas.DrawRect(box.BorderBox, paint);
                    }
                    break;
                }
            }
        }

        private static SKPaint CreateOutlinePaint(SKColor color, float strokeWidth = 1)
        {
            return new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                IsAntialias = true
            };
        }

        private static SKPaint CreateFillPaint(SKColor color)
        {
            return new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
        }

        /// <summary>
        /// Draws overflow clip boundaries with dashed lines.
        /// </summary>
        public static void DrawOverflowClip(SKCanvas canvas, SKRect clipRect)
        {
            if (!DebugConfig.Instance.ShowOverflow) return;

            using var paint = new SKPaint
            {
                Color = OverflowColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0)
            };

            canvas.DrawRect(clipRect, paint);
        }
    }
}

