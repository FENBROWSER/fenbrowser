using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering.Core;

namespace FenBrowser.FenEngine.Rendering.Interaction
{
    /// <summary>
    /// Hit testing for click detection.
    /// Determines which element is at a given (x, y) coordinate.
    /// </summary>
    public static class HitTester
    {
        /// <summary>
        /// Find the element at the given coordinates.
        /// Returns the deepest (smallest area) element containing the point.
        /// </summary>
        /// <param name="ctx">Render context with box data</param>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        /// <returns>The element at the point, or null if none</returns>
        public static LiteElement HitTest(RenderContext ctx, float x, float y)
        {
            if (ctx?.Boxes == null) return null;
            
            LiteElement bestMatch = null;
            float minArea = float.MaxValue;

            // Take a snapshot for thread safety
            var boxSnapshot = ctx.Boxes.ToArray();

            foreach (var kvp in boxSnapshot)
            {
                var element = kvp.Key;
                var box = kvp.Value;

                // Skip invisible elements
                if (box.MarginBox.Width <= 0 || box.MarginBox.Height <= 0) continue;

                // Check if point is inside border box
                if (box.BorderBox.Contains(x, y))
                {
                    float area = box.BorderBox.Width * box.BorderBox.Height;

                    // Prefer smaller area (child over parent)
                    if (area <= minArea)
                    {
                        minArea = area;
                        bestMatch = element;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Find all elements at the given coordinates, ordered from deepest to shallowest.
        /// </summary>
        /// <param name="ctx">Render context with box data</param>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        /// <returns>List of elements containing the point, smallest area first</returns>
        public static List<LiteElement> HitTestAll(RenderContext ctx, float x, float y)
        {
            if (ctx?.Boxes == null) return new List<LiteElement>();

            var hits = new List<(LiteElement element, float area)>();
            var boxSnapshot = ctx.Boxes.ToArray();

            foreach (var kvp in boxSnapshot)
            {
                var element = kvp.Key;
                var box = kvp.Value;

                if (box.MarginBox.Width <= 0 || box.MarginBox.Height <= 0) continue;

                if (box.BorderBox.Contains(x, y))
                {
                    float area = box.BorderBox.Width * box.BorderBox.Height;
                    hits.Add((element, area));
                }
            }

            // Sort by area ascending (smallest/deepest first)
            return hits.OrderBy(h => h.area).Select(h => h.element).ToList();
        }

        /// <summary>
        /// Find the nearest clickable element (A, BUTTON, INPUT, etc.) at coordinates.
        /// </summary>
        /// <param name="ctx">Render context with box data</param>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        /// <returns>The nearest clickable element, or null if none</returns>
        public static LiteElement HitTestClickable(RenderContext ctx, float x, float y)
        {
            var hits = HitTestAll(ctx, x, y);

            foreach (var element in hits)
            {
                string tag = element.Tag?.ToUpperInvariant();
                if (tag == "A" || tag == "BUTTON" || tag == "INPUT" || 
                    tag == "SELECT" || tag == "TEXTAREA" || tag == "LABEL")
                {
                    return element;
                }

                // Check for role="button" or onclick attribute
                if (element.Attr != null)
                {
                    if (element.Attr.ContainsKey("onclick") || 
                        (element.Attr.TryGetValue("role", out var role) && role == "button"))
                    {
                        return element;
                    }
                }
            }

            // Return the deepest element if no clickable found
            return hits.FirstOrDefault();
        }
    }
}
