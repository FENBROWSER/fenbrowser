using SkiaSharp;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Renders SVG elements to Skia canvas.
    /// Supports: paths, basic shapes (rect, circle, ellipse), viewBox transforms, fills, strokes.
    /// </summary>
    public class SvgRenderer
    {
        /// <summary>
        /// Render an SVG element to the canvas.
        /// </summary>
        public void RenderSvgElement(SKCanvas canvas, Element svgElement, SKRect bounds, CssComputed style)
        {
            if (svgElement == null || canvas == null) return;

            canvas.Save();

            try
            {
                // Apply viewBox transform if present
                var viewBox = svgElement.GetAttribute("viewBox");
                if (!string.IsNullOrEmpty(viewBox))
                {
                    var transform = CalculateViewBoxTransform(viewBox, bounds);
                    canvas.Concat(ref transform);
                }
                else
                {
                    // No viewBox, just translate to bounds position
                    canvas.Translate(bounds.Left, bounds.Top);
                }

                // Render child elements
                RenderSvgChildren(canvas, svgElement);
            }
            finally
            {
                canvas.Restore();
            }
        }

        private void RenderSvgChildren(SKCanvas canvas, Element parent)
        {
            if (parent.Children == null) return;

            foreach (var child in parent.Children)
            {
                if (child is Element elem)
                {
                    RenderSvgShape(canvas, elem);
                }
            }
        }

        private void RenderSvgShape(SKCanvas canvas, Element element)
        {
            var tagName = element.TagName?.ToUpperInvariant();

            switch (tagName)
            {
                case "PATH":
                    RenderPath(canvas, element);
                    break;
                case "RECT":
                    RenderRect(canvas, element);
                    break;
                case "CIRCLE":
                    RenderCircle(canvas, element);
                    break;
                case "ELLIPSE":
                    RenderEllipse(canvas, element);
                    break;
                case "LINE":
                    RenderLine(canvas, element);
                    break;
                case "POLYLINE":
                case "POLYGON":
                    RenderPoly(canvas, element, tagName == "POLYGON");
                    break;
                case "USE":
                    RenderUse(canvas, element);
                    break;
                case "G":
                    // Group - just render children
                    RenderSvgChildren(canvas, element);
                    break;
            }
        }

        private void RenderUse(SKCanvas canvas, Element element)
        {
            var href = element.GetAttribute("href") ?? element.GetAttribute("xlink:href");
            if (string.IsNullOrEmpty(href) || !href.StartsWith("#")) return;

            var targetId = href.Substring(1);
            
            // Walk up to the root to search for the referenced ID
            var root = element;
            while (root.ParentElement != null)
                root = root.ParentElement;

            var target = FindElementById(root, targetId);
            if (target == null) return;

            var x = ParseFloat(element.GetAttribute("x"));
            var y = ParseFloat(element.GetAttribute("y"));

            canvas.Save();
            try
            {
                if (x != 0 || y != 0)
                {
                    canvas.Translate(x, y);
                }
                
                // Render the target (skip if it's the exact same element to avoid trivial recursion loop)
                if (target != element)
                {
                    RenderSvgShape(canvas, target);
                }
            }
            finally
            {
                canvas.Restore();
            }
        }

        private Element FindElementById(Element root, string id)
        {
            if (root.GetAttribute("id") == id) return root;
            if (root.Children == null) return null;
            
            // Simple recursive search to avoid allocating Descendants/LINQ in rendering loop
            foreach (var child in root.Children)
            {
                if (child is Element elem)
                {
                    var found = FindElementById(elem, id);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void RenderPath(SKCanvas canvas, Element element)
        {
            var d = element.GetAttribute("d");
            if (string.IsNullOrEmpty(d)) return;

            var path = ParsePathData(d);
            if (path == null) return;

            using (path)
            {
                PaintShape(canvas, element, path);
            }
        }

        private void RenderRect(SKCanvas canvas, Element element)
        {
            var x = ParseFloat(element.GetAttribute("x"));
            var y = ParseFloat(element.GetAttribute("y"));
            var width = ParseFloat(element.GetAttribute("width"));
            var height = ParseFloat(element.GetAttribute("height"));

            if (width <= 0 || height <= 0) return;

            using (var path = new SKPath())
            {
                path.AddRect(new SKRect(x, y, x + width, y + height));
                PaintShape(canvas, element, path);
            }
        }

        private void RenderCircle(SKCanvas canvas, Element element)
        {
            var cx = ParseFloat(element.GetAttribute("cx"));
            var cy = ParseFloat(element.GetAttribute("cy"));
            var r = ParseFloat(element.GetAttribute("r"));

            if (r <= 0) return;

            using (var path = new SKPath())
            {
                path.AddCircle(cx, cy, r);
                PaintShape(canvas, element, path);
            }
        }

        private void RenderEllipse(SKCanvas canvas, Element element)
        {
            var cx = ParseFloat(element.GetAttribute("cx"));
            var cy = ParseFloat(element.GetAttribute("cy"));
            var rx = ParseFloat(element.GetAttribute("rx"));
            var ry = ParseFloat(element.GetAttribute("ry"));

            if (rx <= 0 || ry <= 0) return;

            using (var path = new SKPath())
            {
                path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
                PaintShape(canvas, element, path);
            }
        }

        private void RenderLine(SKCanvas canvas, Element element)
        {
            var x1 = ParseFloat(element.GetAttribute("x1"));
            var y1 = ParseFloat(element.GetAttribute("y1"));
            var x2 = ParseFloat(element.GetAttribute("x2"));
            var y2 = ParseFloat(element.GetAttribute("y2"));

            using (var path = new SKPath())
            {
                path.MoveTo(x1, y1);
                path.LineTo(x2, y2);
                PaintShape(canvas, element, path);
            }
        }

        private void RenderPoly(SKCanvas canvas, Element element, bool close)
        {
            var points = element.GetAttribute("points");
            if (string.IsNullOrEmpty(points)) return;

            var coords = points.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (coords.Length < 2) return;

            using (var path = new SKPath())
            {
                for (int i = 0; i < coords.Length - 1; i += 2)
                {
                    var x = ParseFloat(coords[i]);
                    var y = ParseFloat(coords[i + 1]);

                    if (i == 0)
                        path.MoveTo(x, y);
                    else
                        path.LineTo(x, y);
                }

                if (close)
                    path.Close();

                PaintShape(canvas, element, path);
            }
        }

        private void PaintShape(SKCanvas canvas, Element element, SKPath path)
        {
            // Fill
            var fill = element.GetAttribute("fill");
            if (fill != "none" && string.IsNullOrEmpty(fill) || (!string.IsNullOrEmpty(fill) && fill != "none"))
            {
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = ParseColor(fill ?? "#000000");

                    var opacity = ParseFloat(element.GetAttribute("opacity"), 1.0f);
                    var fillOpacity = ParseFloat(element.GetAttribute("fill-opacity"), 1.0f);
                    paint.Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * opacity * fillOpacity));

                    canvas.DrawPath(path, paint);
                }
            }

            // Stroke
            var stroke = element.GetAttribute("stroke");
            if (!string.IsNullOrEmpty(stroke) && stroke != "none")
            {
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Stroke;
                    paint.Color = ParseColor(stroke);
                    paint.StrokeWidth = ParseFloat(element.GetAttribute("stroke-width"), 1.0f);

                    var opacity = ParseFloat(element.GetAttribute("opacity"), 1.0f);
                    var strokeOpacity = ParseFloat(element.GetAttribute("stroke-opacity"), 1.0f);
                    paint.Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * opacity * strokeOpacity));

                    canvas.DrawPath(path, paint);
                }
            }
        }

        private SKPath ParsePathData(string d)
        {
            try
            {
                var path = SKPath.ParseSvgPathData(d);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private SKMatrix CalculateViewBoxTransform(string viewBox, SKRect bounds)
        {
            var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return SKMatrix.Identity;

            var minX = ParseFloat(parts[0]);
            var minY = ParseFloat(parts[1]);
            var vbWidth = ParseFloat(parts[2]);
            var vbHeight = ParseFloat(parts[3]);

            if (vbWidth <= 0 || vbHeight <= 0) return SKMatrix.Identity;

            // Calculate scale to fit viewBox into bounds
            var scaleX = bounds.Width / vbWidth;
            var scaleY = bounds.Height / vbHeight;

            // Use same scale for both axes (preserve aspect ratio)
            var scale = Math.Min(scaleX, scaleY);

            // Create transform: translate to bounds, scale, translate by -minX/-minY
            var matrix = SKMatrix.CreateIdentity();
            matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(bounds.Left, bounds.Top));
            matrix = SKMatrix.Concat(matrix, SKMatrix.CreateScale(scale, scale));
            matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(-minX, -minY));

            return matrix;
        }

        private float ParseFloat(string value, float defaultValue = 0f)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;

            value = value.Trim().Replace("px", "").Replace("pt", "");

            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                return result;

            return defaultValue;
        }

        private SKColor ParseColor(string color)
        {
            if (string.IsNullOrEmpty(color) || color == "none")
                return SKColors.Black;

            if (color.StartsWith("#"))
            {
                if (SKColor.TryParse(color, out var skColor))
                    return skColor;
            }

            // Named colors (simplified)
            switch (color.ToLowerInvariant())
            {
                case "black": return SKColors.Black;
                case "white": return SKColors.White;
                case "red": return SKColors.Red;
                case "green": return SKColors.Green;
                case "blue": return SKColors.Blue;
                default: return SKColors.Black;
            }
        }
    }
}

