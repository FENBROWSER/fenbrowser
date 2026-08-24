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
            "foreignObject", "animation", "animate", "animateTransform", "set"
        };

        private static readonly HashSet<string> FallbackProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "paint-order", "vector-effect"
        };

        private static readonly HashSet<string> AdvancedTextAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "textLength", "lengthAdjust", "rotate", "writing-mode", "direction",
            "unicode-bidi", "glyph-orientation-horizontal", "glyph-orientation-vertical",
            "dominant-baseline", "alignment-baseline", "baseline-shift",
            "word-spacing", "text-decoration", "text-rendering", "font", "font-stretch",
            "font-variant"
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
                if (element.Name == "use" &&
                    (string.Equals(attribute.Key, "href", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(attribute.Key, "xlink:href", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(attribute.Value) &&
                    attribute.Value[0] != '#')
                {
                    report.RejectResource($"SVG {element.Name} external reference rejected");
                    continue;
                }
                if ((element.Name == "text" || element.Name == "tspan") &&
                    AdvancedTextAttributes.Contains(attribute.Key))
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

        public static void InspectHierarchy(SvgElement root, SvgParseReport report)
        {
            var pending = new Stack<SvgElement>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                SvgElement element = pending.Pop();
                if (element.Name.Equals("animateMotion", StringComparison.Ordinal) &&
                    element.Parent?.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
                {
                    report.RequireFallback("SVG animateMotion target requires compatibility fallback");
                }
                for (int i = 0; i < element.Children.Count; i++) pending.Push(element.Children[i]);
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
