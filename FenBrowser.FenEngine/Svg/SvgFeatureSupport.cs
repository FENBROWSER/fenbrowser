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
            "style", "tspan", "textPath",
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
                else if (string.Equals(attribute.Key, "style", StringComparison.OrdinalIgnoreCase))
                {
                    InspectInlineStyle(attribute.Value, report);
                }
            }
        }

        private static void InspectInlineStyle(string style, SvgParseReport report)
        {
            var remaining = style.AsSpan();
            while (!remaining.IsEmpty)
            {
                int semicolon = remaining.IndexOf(';');
                var declaration = semicolon < 0 ? remaining : remaining.Slice(0, semicolon);
                remaining = semicolon < 0 ? default : remaining.Slice(semicolon + 1);
                int colon = declaration.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var property = declaration.Slice(0, colon).Trim();
                var value = declaration.Slice(colon + 1).Trim();
                foreach (string unsupported in FallbackProperties)
                {
                    if (property.Equals(unsupported.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                        !value.Equals("none".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        report.RequireFallback($"SVG property '{unsupported}' requires compatibility fallback");
                        break;
                    }
                }
                foreach (string unsupported in AdvancedTextAttributes)
                {
                    if (property.Equals(unsupported.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        report.RequireFallback($"SVG text property '{unsupported}' requires compatibility fallback");
                        break;
                    }
                }
            }
        }
    }
}
