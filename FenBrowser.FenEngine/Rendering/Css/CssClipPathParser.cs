using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS clip-path parser - converts CSS clip-path values to SKPath for clipping.
    /// Supports: circle(), ellipse(), inset(), polygon()
    /// </summary>
    public static class CssClipPathParser
    {
        private static bool TryParseOuterFunction(string input, out string name, out string arguments)
        {
            name = null;
            arguments = null;
            if (string.IsNullOrWhiteSpace(input)) return false;

            int i = 0;
            while (i < input.Length && IsCssWhitespace(input[i])) i++;
            int nameStart = i;
            while (i < input.Length &&
                   ((input[i] >= 'a' && input[i] <= 'z') ||
                    (input[i] >= 'A' && input[i] <= 'Z') ||
                    input[i] == '-'))
            {
                i++;
            }
            if (i == nameStart) return false;

            name = input.Substring(nameStart, i - nameStart).ToLowerInvariant();
            while (i < input.Length && IsCssWhitespace(input[i])) i++;
            if (i >= input.Length || input[i] != '(') return false;

            int argumentStart = ++i;
            int depth = 1;
            char quote = '\0';
            bool escaped = false;
            while (i < input.Length && depth > 0)
            {
                char c = input[i];
                if (quote != '\0')
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote) quote = '\0';
                    i++;
                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                    i++;
                    continue;
                }
                if (c == '\\')
                {
                    i += Math.Min(2, input.Length - i);
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') depth--;

                if (depth == 0)
                {
                    arguments = input.Substring(argumentStart, i - argumentStart).Trim();
                    i++;
                    break;
                }
                i++;
            }

            if (depth != 0 || quote != '\0') return false;
            while (i < input.Length && IsCssWhitespace(input[i])) i++;
            return i == input.Length;
        }

        private static bool IsCssWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

        /// <summary>
        /// Parse a CSS clip-path value and return an SKPath for clipping
        /// </summary>
        public static SKPath Parse(string clipPath, SKRect bounds)
        {
            if (string.IsNullOrWhiteSpace(clipPath) ||
                string.Equals(clipPath.Trim(), "none", StringComparison.OrdinalIgnoreCase) ||
                !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
                !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom) ||
                bounds.Width < 0 || bounds.Height < 0)
            {
                return null;
            }

            if (!TryParseOuterFunction(clipPath, out var funcName, out var argsStr))
                return null;

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
                        radius = Math.Max(0f, ParseLength(radiusStr, bounds.Width, radius));
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

            return PathBuilderHelper.Build(path => path.AddCircle(cx, cy, radius));
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
                        rx = Math.Max(0f, ParseLength(radii[0], bounds.Width, rx));
                    if (radii.Length >= 2)
                        ry = Math.Max(0f, ParseLength(radii[1], bounds.Height, ry));
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

            var rect = new SKRect(cx - rx, cy - ry, cx + rx, cy + ry);
            return PathBuilderHelper.Build(path => path.AddOval(rect));
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
                    borderRadius = Math.Max(0f, ParseLength(roundParts[1].Trim(), bounds.Width, 0));
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

            if (borderRadius > 0)
                return PathBuilderHelper.Build(path => path.AddRoundRect(rect, borderRadius, borderRadius));
            else
                return PathBuilderHelper.Build(path => path.AddRect(rect));
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

            return PathBuilderHelper.Build(path =>
            {
                path.MoveTo(points[0]);
                for (int i = 1; i < points.Count; i++)
                {
                    path.LineTo(points[i]);
                }
                path.Close();
            });
        }

        private static float ParseLength(string s, float reference, float defaultValue)
        {
            if (string.IsNullOrWhiteSpace(s) || !float.IsFinite(reference)) return defaultValue;
            s = s.Trim();

            if (s.EndsWith("%", StringComparison.Ordinal))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 1), out var percent)
                    ? reference * percent / 100f
                    : defaultValue;
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 2), out var pixels) ? pixels : defaultValue;
            return TryParseFiniteFloat(s.AsSpan(), out var number) ? number : defaultValue;
        }

        private static float ParsePosition(string s, float origin, float size, float defaultValue)
        {
            if (string.IsNullOrWhiteSpace(s) || !float.IsFinite(origin) || !float.IsFinite(size))
                return defaultValue;
            s = s.Trim();

            if (string.Equals(s, "left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "top", StringComparison.OrdinalIgnoreCase)) return origin;
            if (string.Equals(s, "center", StringComparison.OrdinalIgnoreCase)) return origin + size / 2f;
            if (string.Equals(s, "right", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "bottom", StringComparison.OrdinalIgnoreCase)) return origin + size;

            if (s.EndsWith("%", StringComparison.Ordinal))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 1), out var percent)
                    ? origin + size * percent / 100f
                    : defaultValue;
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                return TryParseFiniteFloat(s.AsSpan(0, s.Length - 2), out var pixels)
                    ? origin + pixels
                    : defaultValue;
            return TryParseFiniteFloat(s.AsSpan(), out var number) ? origin + number : defaultValue;
        }

        private static bool TryParseFiniteFloat(ReadOnlySpan<char> text, out float value)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                float.IsFinite(value))
            {
                return true;
            }

            value = 0f;
            return false;
        }
    }
}
