using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS clip-path parser - converts CSS clip-path values to SKPath for clipping.
    /// Supports: circle(), ellipse(), inset(), polygon()
    /// </summary>
    public static class CssClipPathParser
    {
        private static readonly Regex FunctionRegex = new Regex(
            @"(\w+)\s*\(\s*([^)]*)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Parse a CSS clip-path value and return an SKPath for clipping
        /// </summary>
        public static SKPath Parse(string clipPath, SKRect bounds)
        {
            if (string.IsNullOrWhiteSpace(clipPath) || clipPath == "none")
                return null;

            var match = FunctionRegex.Match(clipPath);
            if (!match.Success) return null;

            string funcName = match.Groups[1].Value.ToLowerInvariant();
            string argsStr = match.Groups[2].Value.Trim();

            switch (funcName)
            {
                case "circle": return ParseCircle(argsStr, bounds);
                case "ellipse": return ParseEllipse(argsStr, bounds);
                case "inset": return ParseInset(argsStr, bounds);
                case "polygon": return ParsePolygon(argsStr, bounds);
                default: return null;
            }
        }

        /// <summary>
        /// Parse circle(radius at centerX centerY)
        /// </summary>
        private static SKPath ParseCircle(string args, SKRect bounds)
        {
            // Default: circle at center with closest-side radius
            float cx = bounds.MidX;
            float cy = bounds.MidY;
            float radius = Math.Min(bounds.Width, bounds.Height) / 2;

            if (!string.IsNullOrEmpty(args))
            {
                var parts = args.Split(new[] { " at " }, StringSplitOptions.RemoveEmptyEntries);
                
                // Parse radius
                if (parts.Length > 0)
                {
                    string radiusStr = parts[0].Trim().ToLowerInvariant();
                    if (radiusStr == "closest-side")
                        radius = Math.Min(bounds.Width, bounds.Height) / 2;
                    else if (radiusStr == "farthest-side")
                        radius = Math.Max(bounds.Width, bounds.Height) / 2;
                    else
                        radius = ParseLength(radiusStr, bounds.Width, radius);
                }

                // Parse center position
                if (parts.Length > 1)
                {
                    var center = parts[1].Trim().Split(' ');
                    if (center.Length >= 1)
                        cx = ParsePosition(center[0], bounds.Left, bounds.Width, cx);
                    if (center.Length >= 2)
                        cy = ParsePosition(center[1], bounds.Top, bounds.Height, cy);
                }
            }

            var path = new SKPath();
            path.AddCircle(cx, cy, radius);
            return path;
        }

        /// <summary>
        /// Parse ellipse(radiusX radiusY at centerX centerY)
        /// </summary>
        private static SKPath ParseEllipse(string args, SKRect bounds)
        {
            float cx = bounds.MidX;
            float cy = bounds.MidY;
            float rx = bounds.Width / 2;
            float ry = bounds.Height / 2;

            if (!string.IsNullOrEmpty(args))
            {
                var parts = args.Split(new[] { " at " }, StringSplitOptions.RemoveEmptyEntries);
                
                // Parse radii
                if (parts.Length > 0)
                {
                    var radii = parts[0].Trim().Split(' ');
                    if (radii.Length >= 1)
                        rx = ParseLength(radii[0], bounds.Width, rx);
                    if (radii.Length >= 2)
                        ry = ParseLength(radii[1], bounds.Height, ry);
                }

                // Parse center
                if (parts.Length > 1)
                {
                    var center = parts[1].Trim().Split(' ');
                    if (center.Length >= 1)
                        cx = ParsePosition(center[0], bounds.Left, bounds.Width, cx);
                    if (center.Length >= 2)
                        cy = ParsePosition(center[1], bounds.Top, bounds.Height, cy);
                }
            }

            var path = new SKPath();
            var rect = new SKRect(cx - rx, cy - ry, cx + rx, cy + ry);
            path.AddOval(rect);
            return path;
        }

        /// <summary>
        /// Parse inset(top right bottom left round borderRadius)
        /// </summary>
        private static SKPath ParseInset(string args, SKRect bounds)
        {
            float top = 0, right = 0, bottom = 0, left = 0;
            float borderRadius = 0;

            if (!string.IsNullOrEmpty(args))
            {
                // Check for "round" keyword for border radius
                var roundParts = args.Split(new[] { " round " }, StringSplitOptions.RemoveEmptyEntries);
                if (roundParts.Length > 1)
                {
                    borderRadius = ParseLength(roundParts[1].Trim(), bounds.Width, 0);
                    args = roundParts[0];
                }

                var values = args.Trim().Split(' ');
                if (values.Length >= 1)
                    top = ParseLength(values[0], bounds.Height, 0);
                if (values.Length >= 2)
                    right = ParseLength(values[1], bounds.Width, 0);
                else
                    right = top;
                if (values.Length >= 3)
                    bottom = ParseLength(values[2], bounds.Height, 0);
                else
                    bottom = top;
                if (values.Length >= 4)
                    left = ParseLength(values[3], bounds.Width, 0);
                else
                    left = right;
            }

            var rect = new SKRect(
                bounds.Left + left,
                bounds.Top + top,
                bounds.Right - right,
                bounds.Bottom - bottom
            );

            var path = new SKPath();
            if (borderRadius > 0)
                path.AddRoundRect(rect, borderRadius, borderRadius);
            else
                path.AddRect(rect);
            return path;
        }

        /// <summary>
        /// Parse polygon(x1 y1, x2 y2, x3 y3, ...)
        /// </summary>
        private static SKPath ParsePolygon(string args, SKRect bounds)
        {
            if (string.IsNullOrEmpty(args)) return null;

            var points = new List<SKPoint>();
            var pairs = args.Split(',');

            foreach (var pair in pairs)
            {
                var coords = pair.Trim().Split(' ');
                if (coords.Length >= 2)
                {
                    float x = ParsePosition(coords[0], bounds.Left, bounds.Width, bounds.Left);
                    float y = ParsePosition(coords[1], bounds.Top, bounds.Height, bounds.Top);
                    points.Add(new SKPoint(x, y));
                }
            }

            if (points.Count < 3) return null;

            var path = new SKPath();
            path.MoveTo(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                path.LineTo(points[i]);
            }
            path.Close();
            return path;
        }

        private static float ParseLength(string s, float reference, float defaultValue)
        {
            if (string.IsNullOrEmpty(s)) return defaultValue;
            s = s.Trim().ToLowerInvariant();

            if (s.EndsWith("%"))
            {
                if (float.TryParse(s.Replace("%", ""), out float pct))
                    return reference * pct / 100;
            }
            else if (s.EndsWith("px"))
            {
                if (float.TryParse(s.Replace("px", ""), out float px))
                    return px;
            }
            else if (float.TryParse(s, out float num))
            {
                return num;
            }

            return defaultValue;
        }

        private static float ParsePosition(string s, float origin, float size, float defaultValue)
        {
            if (string.IsNullOrEmpty(s)) return defaultValue;
            s = s.Trim().ToLowerInvariant();

            // Named positions
            if (s == "left" || s == "top") return origin;
            if (s == "center") return origin + size / 2;
            if (s == "right" || s == "bottom") return origin + size;

            // Percentage or length
            if (s.EndsWith("%"))
            {
                if (float.TryParse(s.Replace("%", ""), out float pct))
                    return origin + size * pct / 100;
            }
            else if (s.EndsWith("px"))
            {
                if (float.TryParse(s.Replace("px", ""), out float px))
                    return origin + px;
            }
            else if (float.TryParse(s, out float num))
            {
                return origin + num;
            }

            return defaultValue;
        }
    }
}
