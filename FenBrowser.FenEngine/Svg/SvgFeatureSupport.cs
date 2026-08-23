using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Declares visible SVG features outside the first-party static subset.
    /// Detection happens during the bounded parse so unsupported content can be
    /// routed deliberately instead of silently producing incomplete pixels.
    /// </summary>
    internal static class SvgFeatureSupport
    {
        private static readonly HashSet<string> FallbackElements = new(StringComparer.Ordinal)
        {
            "tspan", "textPath",
            "foreignObject", "animation", "animate", "animateTransform", "animateMotion", "set"
        };

        private static readonly HashSet<string> FallbackProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "filter", "mask", "marker", "marker-start", "marker-mid", "marker-end",
            "paint-order", "vector-effect"
        };

        private static readonly HashSet<string> AdvancedTextAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "textLength", "lengthAdjust", "rotate", "writing-mode", "direction",
            "unicode-bidi", "glyph-orientation-horizontal", "glyph-orientation-vertical",
            "dominant-baseline", "alignment-baseline", "baseline-shift", "letter-spacing",
            "word-spacing", "text-decoration", "text-rendering", "font", "font-stretch",
            "font-variant", "xml:space"
        };

        public static void Inspect(SvgElement element, SvgParseReport report)
        {
            if (FallbackElements.Contains(element.Name))
            {
                report.RequireFallback($"SVG feature '{element.Name}' requires compatibility fallback");
            }

            var attributes = element.Attributes;
            if (attributes == null)
            {
                return;
            }

            foreach (var attribute in attributes)
            {
                if (HasExternalUrlReference(attribute.Value))
                {
                    report.RejectResource("SVG external resource reference rejected");
                    continue;
                }
                if ((element.Name == "use" || element.Name == "image") &&
                    (string.Equals(attribute.Key, "href", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(attribute.Key, "xlink:href", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(attribute.Value) &&
                    attribute.Value[0] != '#' &&
                    !(element.Name == "image" && attribute.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                {
                    report.RejectResource($"SVG {element.Name} external reference rejected");
                    continue;
                }
                if (element.Name == "text" && AdvancedTextAttributes.Contains(attribute.Key))
                {
                    report.RequireFallback(
                        $"SVG text attribute '{attribute.Key}' requires compatibility fallback");
                    continue;
                }
                if (FallbackProperties.Contains(attribute.Key) &&
                    !string.IsNullOrWhiteSpace(attribute.Value) &&
                    !string.Equals(attribute.Value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                {
                    report.RequireFallback($"SVG property '{attribute.Key}' requires compatibility fallback");
                }
            }
        }

        internal static bool HasExternalUrlReference(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            int searchStart = 0;
            while (searchStart < value.Length)
            {
                int start = value.IndexOf("url(", searchStart, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return false;
                int close = value.IndexOf(')', start + 4);
                if (close < 0) return false;
                string target = value.Substring(start + 4, close - start - 4).Trim().Trim('\'', '"');
                if (target.Length > 0 && target[0] != '#') return true;
                searchStart = close + 1;
            }
            return false;
        }
    }
}
