// =============================================================================
// PaintDebugOverlay.cs
// Visual Paint Debugging Overlay
// 
// PURPOSE: Draw colored rectangles to visualize element boundaries
// USAGE: Call RenderOverlay after normal rendering when debug flag is enabled
// OUTPUT: Colored box outlines: content (green), padding (blue), border (red), margin (orange)
// =============================================================================

using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Paint
{
    /// <summary>
    /// Visual overlay for debugging layout boxes.
    /// Shows content box (green), padding box (blue), border box (red), margin box (orange).
    /// </summary>
    public static class PaintDebugOverlay
    {
        // Color scheme for different box edges
        private static readonly SKColor ContentColor = new SKColor(0, 255, 0, 128);      // Green
        private static readonly SKColor PaddingColor = new SKColor(0, 128, 255, 128);    // Blue
        private static readonly SKColor BorderColor = new SKColor(255, 0, 0, 128);       // Red
        private static readonly SKColor MarginColor = new SKColor(255, 165, 0, 128);     // Orange

        private static readonly SKColor LabelBackground = new SKColor(0, 0, 0, 200);
        private static readonly SKColor LabelText = new SKColor(255, 255, 255, 255);

        /// <summary>
        /// Render debug overlay for all elements with computed boxes.
        /// </summary>
        public static void RenderOverlay(
            SKCanvas canvas,
            Element root,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            bool showLabels = true)
        {
            if (canvas == null || root == null || boxes == null)
                return;

            global::FenBrowser.Core.EngineLogCompat.Debug("[PaintDebug] Rendering overlay", LogCategory.Rendering);

            // Traverse tree and draw boxes
            RenderNode(canvas, root, boxes, styles, showLabels);
        }

        /// <summary>
        /// Render debug overlay for a single element.
        /// </summary>
        public static void RenderElement(
            SKCanvas canvas,
            Element element,
            BoxModel box,
            bool showLabels = true)
        {
            if (canvas == null || element == null)
                return;

            DrawBoxModel(canvas, element, box, showLabels);
        }

        // ========================================================================
        // INTERNAL: Rendering Logic
        // ========================================================================

        private static void RenderNode(
            SKCanvas canvas,
            Node node,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            bool showLabels)
        {
            // Draw this node's box if it has layout
            if (node is Element elem && boxes.TryGetValue(node, out var box))
            {
                // Skip display:none elements
                if (styles.TryGetValue(node, out var style) && style.Display == "none")
                {
                    // Don't render, continue to children
                }
                else
                {
                    DrawBoxModel(canvas, elem, box, showLabels);
                }
            }

            // Recurse children
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    RenderNode(canvas, child, boxes, styles, showLabels);
                }
            }
        }

        private static void DrawBoxModel(SKCanvas canvas, Element element, BoxModel box, bool showLabels)
        {
            // Draw each box edge with different color
            // Order: Margin (outermost) → Border → Padding → Content (innermost)

            using var marginPaint = new SKPaint
            {
                Color = MarginColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };

            using var borderPaint = new SKPaint
            {
                Color = BorderColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };

            using var paddingPaint = new SKPaint
            {
                Color = PaddingColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };

            using var contentPaint = new SKPaint
            {
                Color = ContentColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,  // Content box is thicker
                IsAntialias = true
            };

            // Draw rectangles (outermost to innermost)
            canvas.DrawRect(box.MarginBox, marginPaint);
            canvas.DrawRect(box.BorderBox, borderPaint);
            canvas.DrawRect(box.PaddingBox, paddingPaint);
            canvas.DrawRect(box.ContentBox, contentPaint);

            // Draw label if enabled
            if (showLabels)
            {
                DrawLabel(canvas, element, box);
            }
        }

        private static void DrawLabel(SKCanvas canvas, Element element, BoxModel box)
        {
            // Label content: "TAG#id.class WxH"
            string tagName = element.TagName ?? "?";
            string id = element.GetAttribute("id");
            string className = element.GetAttribute("class");

            string label = tagName;
            if (!string.IsNullOrEmpty(id))
                label += $"#{id}";
            if (!string.IsNullOrEmpty(className))
            {
                var firstClass = className.Split(' ')[0];
                label += $".{firstClass}";
            }

            // Add dimensions
            label += $" {box.ContentBox.Width:F0}×{box.ContentBox.Height:F0}";

            // Position label at top-left of content box
            float labelX = box.ContentBox.Left;
            float labelY = box.ContentBox.Top;

            // Skip if box is too small to render label
            if (box.ContentBox.Width < 30 || box.ContentBox.Height < 10)
                return;

            using var textPaint = new SKPaint
            {
                Color = LabelText,
                TextSize = 10,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
            };

            // Measure label
            float textWidth = textPaint.MeasureText(label);
            float textHeight = 10;

            // Background
            using var bgPaint = new SKPaint
            {
                Color = LabelBackground,
                Style = SKPaintStyle.Fill
            };

            var labelRect = new SKRect(
                labelX,
                labelY,
                labelX + textWidth + 4,
                labelY + textHeight + 2
            );

            canvas.DrawRect(labelRect, bgPaint);

            // Text (baseline is at Bottom - Descent)
            canvas.DrawText(label, labelX + 2, labelY + textHeight - 2, textPaint);
        }

        // ========================================================================
        // PUBLIC: Filtering Helpers
        // ========================================================================

        /// <summary>
        /// Render overlay only for elements matching a selector.
        /// </summary>
        public static void RenderOverlayFiltered(
            SKCanvas canvas,
            Element root,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            Func<Element, bool> filter,
            bool showLabels = true)
        {
            if (canvas == null || root == null || boxes == null)
                return;

            RenderNodeFiltered(canvas, root, boxes, styles, filter, showLabels);
        }

        private static void RenderNodeFiltered(
            SKCanvas canvas,
            Node node,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            Func<Element, bool> filter,
            bool showLabels)
        {
            // Draw this node's box if it matches filter
            if (node is Element elem && boxes.TryGetValue(node, out var box))
            {
                if (filter(elem))
                {
                    DrawBoxModel(canvas, elem, box, showLabels);
                }
            }

            // Recurse children
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    RenderNodeFiltered(canvas, child, boxes, styles, filter, showLabels);
                }
            }
        }
    }
}

