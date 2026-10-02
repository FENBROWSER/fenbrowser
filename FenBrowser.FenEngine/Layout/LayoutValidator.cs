using System;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Utility class for validating and sanitizing layout values to prevent NaN/Infinity propagation.
    /// </summary>
    public static class LayoutValidator
    {
        private const float EPSILON = 0.0001f;

        /// <summary>
        /// Sanitizes a float value, replacing NaN/Infinity with a fallback.
        /// </summary>
        /// <param name="value">The value to sanitize</param>
        /// <param name="fallback">Fallback value if invalid (default: 0)</param>
        /// <param name="maxValue">Maximum allowed value (default: 100000 for reasonable UI bounds)</param>
        /// <param name="description">Description for logging (optional)</param>
        /// <returns>Sanitized value clamped between 0 and maxValue</returns>
        public static float Sanitize(float value, float fallback = 0f, float maxValue = 100000f, string description = null)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                if (!string.IsNullOrEmpty(description))
                {
                    EngineLogCompat.Warn($"[LAYOUT-INVALID] {description}: Invalid value {value}, using fallback {fallback}", LogCategory.Layout);
                }
                return fallback;
            }

            // Clamp to valid range
            return Math.Max(0, Math.Min(value, maxValue));
        }

        /// <summary>
        /// Sanitizes an SKRect, ensuring all coordinates are valid.
        /// </summary>
        public static SKRect SanitizeBounds(SKRect bounds, float viewportWidth = 100000f, float viewportHeight = 100000f, string description = null)
        {
            bool hasInvalid = float.IsNaN(bounds.Left) || float.IsInfinity(bounds.Left) ||
                             float.IsNaN(bounds.Top) || float.IsInfinity(bounds.Top) ||
                             float.IsNaN(bounds.Right) || float.IsInfinity(bounds.Right) ||
                             float.IsNaN(bounds.Bottom) || float.IsInfinity(bounds.Bottom);

            if (hasInvalid)
            {
                if (!string.IsNullOrEmpty(description))
                {
                    EngineLogCompat.Error($"[LAYOUT-CORRUPTION] {description}: Invalid bounds {bounds}", LogCategory.Layout);
                }

                return new SKRect(
                    Sanitize(bounds.Left, 0, viewportWidth),
                    Sanitize(bounds.Top, 0, viewportHeight),
                    Sanitize(bounds.Right, 0, viewportWidth), Sanitize(bounds.Bottom, 0, viewportHeight)
                );
            }

            return bounds;
        }

        /// <summary>
        /// Checks if a division operation would be safe (divisor not zero/near-zero).
        /// </summary>
        public static bool IsSafeDivision(float divisor, float epsilon = EPSILON)
        {
            return Math.Abs(divisor) > epsilon;
        }

        /// <summary>
        /// Performs safe division, returning fallback if divisor is too close to zero.
        /// </summary>
        public static float SafeDivide(float numerator, float divisor, float fallback = 0f, string description = null)
        {
            if (!IsSafeDivision(divisor))
            {
                if (!string.IsNullOrEmpty(description))
                {
                    EngineLogCompat.Warn($"[LAYOUT-SAFE-DIVIDE] {description}: Division by near-zero ({divisor}), using fallback {fallback}", LogCategory.Layout);
                }
                return fallback;
            }

            float result = numerator / divisor;
            return Sanitize(result, fallback, description: description);
        }

        /// <summary>
        /// Validates that a value is a positive number.
        /// </summary>
        public static bool IsPositiveNumber(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0;
        }

        /// <summary>
        /// Validates that a value is a non-negative number.
        /// </summary>
        public static bool IsNonNegativeNumber(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
        }

        /// <summary>
        /// Sanitizes the geometry of a LayoutBox inplace.
        /// </summary>
        public static void SanitizeBox(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, float viewportWidth, float viewportHeight)
        {
            if (box?.Geometry == null) return;
            
            // Use generous bounds to allow for scrolling content, but prevent overflow crashes
            float maxWidth = Math.Max(viewportWidth * 10, 50000f);
            float maxHeight = Math.Max(viewportHeight * 10, 50000f);

            var geometry = box.Geometry;
            if (IsFinite(geometry.ContentBox) && IsFinite(geometry.PaddingBox) &&
                IsFinite(geometry.BorderBox) && IsFinite(geometry.MarginBox))
            {
                // The common case, on every flex pass for every box: nothing to fix and
                // nothing to log, so no description is built.
                return;
            }

            // Name the box in the log. "ContentBox: Width=Infinity" repeated a few
            // hundred times says a probe leaked, but not which subtree leaked it,
            // and that is the only thing the message is read for.
            string who = DescribeBox(box);

            geometry.ContentBox = SanitizeBounds(geometry.ContentBox, maxWidth, maxHeight, who + " ContentBox");
            geometry.PaddingBox = SanitizeBounds(geometry.PaddingBox, maxWidth, maxHeight, who + " PaddingBox");
            geometry.BorderBox = SanitizeBounds(geometry.BorderBox, maxWidth, maxHeight, who + " BorderBox");
            geometry.MarginBox = SanitizeBounds(geometry.MarginBox, maxWidth, maxHeight, who + " MarginBox");
        }

        private static bool IsFinite(SKRect rect) =>
            float.IsFinite(rect.Left) && float.IsFinite(rect.Top) &&
            float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);

        /// <summary>
        /// A short tag#id.class identity for a layout box, for diagnostics only.
        /// </summary>
        private static string DescribeBox(FenBrowser.FenEngine.Layout.Tree.LayoutBox box)
        {
            var element = box.SourceNode as FenBrowser.Core.Dom.V2.Element;
            if (element == null)
            {
                return box.GetType().Name;
            }

            string name = element.TagName ?? "?";
            string id = element.GetAttribute("id");
            string className = element.GetAttribute("class");
            if (!string.IsNullOrEmpty(id))
            {
                name += "#" + id;
            }

            if (!string.IsNullOrEmpty(className))
            {
                int cut = className.IndexOf(' ');
                name += "." + (cut > 0 ? className.Substring(0, cut) : className);
            }

            string display = box.ComputedStyle?.Display;
            return string.IsNullOrEmpty(display) ? name : name + "[" + display + "]";
        }
    }
}
