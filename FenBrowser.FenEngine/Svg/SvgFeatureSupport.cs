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
        internal const string ScriptElementReason =
            "SVG document declares a script element; the single-pass renderer never executes it " +
            "and refuses to paint the pre-script state";

        private static readonly HashSet<string> JavaScriptTypeEssences = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/ecmascript", "application/javascript", "application/x-ecmascript",
            "application/x-javascript", "text/ecmascript", "text/javascript",
            "text/javascript1.0", "text/javascript1.1", "text/javascript1.2",
            "text/javascript1.3", "text/javascript1.4", "text/javascript1.5",
            "text/jscript", "text/livescript", "text/x-ecmascript", "text/x-javascript"
        };

        private static readonly HashSet<string> FallbackElements = new(StringComparer.Ordinal)
        {
            "foreignObject", "animation"
        };

        private static readonly HashSet<string> TimingElements = new(StringComparer.Ordinal)
        {
            "animate", "animateColor", "animateMotion", "animateTransform", "set"
        };

        private static readonly HashSet<string> TimingHandlerAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "onbegin", "onend", "onrepeat", "onload", "onclick", "onmouseover",
            "onmouseout", "onmousedown", "onmouseup", "onactivate", "onfocusin",
            "onfocusout", "onzoom", "onpan", "onscroll"
        };

        private static readonly HashSet<string> AdvancedTextAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "textLength", "lengthAdjust", "rotate", "writing-mode",
            "unicode-bidi", "glyph-orientation-horizontal", "glyph-orientation-vertical",
            "dominant-baseline", "alignment-baseline", "baseline-shift",
            "word-spacing", "text-decoration", "text-rendering", "font", "font-stretch",
            "font-variant"
        };

        private static readonly HashSet<string> InertDominantBaselines = new(StringComparer.OrdinalIgnoreCase)
        {
            "auto", "alphabetic", "baseline", "central", "middle",
            "text-before-edge", "before-edge", "text-top", "hanging",
            "text-after-edge", "after-edge", "text-bottom", "ideographic"
        };

        public static void Inspect(SvgElement element, SvgParseReport report)
        {
            if (FallbackElements.Contains(element.Name))
            {
                report.RequireFallback($"SVG feature '{element.Name}' requires compatibility fallback");
            }

            InspectScriptedElement(element, report);

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
                    !SvgValues.TryParseLocalReference(attribute.Value, out _) &&
                    !ResolvesToNoElement(attribute.Value))
                {
                    report.RejectResource($"SVG {element.Name} external reference rejected");
                    continue;
                }
                if ((element.Name == "text" || element.Name == "tspan") &&
                    AdvancedTextAttributes.Contains(attribute.Key) &&
                    RequiresParseTimeTextFallback(attribute.Key, attribute.Value))
                {
                    report.RequireFallback(
                        $"SVG text attribute '{attribute.Key}' requires compatibility fallback");
                    continue;
                }
                if (attribute.Key.Equals("vector-effect", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(attribute.Value) &&
                    !attribute.Value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    !SupportsNonScalingStroke(element, attribute.Value))
                {
                    report.RequireFallback($"SVG property '{attribute.Key}' requires compatibility fallback");
                }
            }
        }

        /// <summary>
        /// Document-level form of the script admission rule, used by the draw walk
        /// so the root actually being painted is re-checked with the same rule the
        /// bounded parse applied per element. Both paths share one diagnostic, so a
        /// document admitted by the parse reports the identical reason again here
        /// instead of a second, paint-stage invention.
        /// </summary>
        public static bool InspectScriptAdmission(SvgElement root, SvgParseReport report)
        {
            if (root == null)
            {
                return false;
            }

            bool rejected = false;
            var pending = new Stack<SvgElement>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                SvgElement current = pending.Pop();
                rejected |= InspectScriptedElement(current, report);
                var children = current.Children;
                for (int i = 0; i < children.Count; i++) pending.Push(children[i]);
            }
            return rejected;
        }

        private static bool InspectScriptedElement(SvgElement element, SvgParseReport report)
        {
            var attributes = element.Attributes;
            bool rejected = false;
            if (IsScriptElementName(element.Name) && DeclaresExecutableScript(attributes))
            {
                report.RequireFallback(ScriptElementReason);
                rejected = true;
            }

            if (attributes == null || !TimingElements.Contains(element.Name))
            {
                return rejected;
            }

            foreach (var attribute in attributes)
            {
                if (!TimingHandlerAttributes.Contains(attribute.Key) ||
                    string.IsNullOrWhiteSpace(attribute.Value))
                {
                    continue;
                }
                report.RequireFallback(
                    $"SVG animate event handler attribute '{attribute.Key}' requires compatibility fallback");
                rejected = true;
            }
            return rejected;
        }

        private static bool DeclaresExecutableScript(KeyValuePair<string, string>[] attributes)
        {
            if (attributes == null) return true;
            string type = null;
            for (int i = 0; i < attributes.Length; i++)
            {
                if (attributes[i].Key.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    type = attributes[i].Value;
                    break;
                }
            }
            if (type == null) return true;
            var essence = type.AsSpan().Trim();
            int parameters = essence.IndexOf(';');
            if (parameters >= 0) essence = essence[..parameters].Trim();
            return essence.Equals("module", StringComparison.OrdinalIgnoreCase) ||
                   JavaScriptTypeEssences.Contains(essence.ToString());
        }

        private static bool IsScriptElementName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int start = 0;
            for (int i = name.Length - 1; i >= 0; i--)
            {
                char c = name[i];
                if (c == ':' || c == '{' || c == '}')
                {
                    start = i + 1;
                    break;
                }
            }
            return name.Length - start == 6 &&
                   name.AsSpan(start).Equals("script".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parse-time admission for the advanced text properties. Values the
        /// bounded parse can categorically reject are routed here; values whose
        /// effect depends on the resolved cascade are validated by the text
        /// layout pass, which reports the fallback with the specific property
        /// instead of condemning the document at attribute-sight.
        /// </summary>
        internal static bool RequiresParseTimeTextFallback(string key, string value)
        {
            if (key.Equals("writing-mode", StringComparison.OrdinalIgnoreCase))
                return !IsInertWritingMode(value);
            if (key.Equals("unicode-bidi", StringComparison.OrdinalIgnoreCase))
                return IsUnimplementedUnicodeBidi(value);
            if (key.Equals("text-decoration", StringComparison.OrdinalIgnoreCase))
                return !IsSupportedTextDecoration(value);
            if (key.Equals("text-rendering", StringComparison.OrdinalIgnoreCase))
                return !IsInertTextRendering(value);
            if (key.Equals("font-stretch", StringComparison.OrdinalIgnoreCase))
                return !IsInertFontStretch(value);
            if (key.Equals("font-variant", StringComparison.OrdinalIgnoreCase))
                return !IsInertFontVariant(value);
            if (key.Equals("font", StringComparison.OrdinalIgnoreCase))
                return true;
            if (key.Equals("glyph-orientation-horizontal", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("glyph-orientation-vertical", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        internal static bool IsSupportedDominantBaseline(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var keyword = value.AsSpan().Trim();
            if (IsInheritedKeyword(keyword)) return true;
            return InertDominantBaselines.Contains(keyword.ToString());
        }

        internal static bool IsSupportedLengthAdjust(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var keyword = value.AsSpan().Trim();
            return keyword.Equals("spacing", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("spacingAndGlyphs", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsInertWritingMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var keyword = value.AsSpan().Trim();
            return IsInheritedKeyword(keyword) ||
                   keyword.Equals("horizontal-tb", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("lr", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("lr-tb", StringComparison.OrdinalIgnoreCase);
        }

        private const int MaxUnicodeBidiTokens = 2;

        internal static bool IsUnimplementedUnicodeBidi(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var keyword = value.AsSpan().Trim();
            if (IsIsolateKeyword(keyword)) return true;

            int inspected = 0;
            bool plaintext = false;
            var tokenizer = SvgValues.CreateTokenizer(keyword);
            while (tokenizer.Next(out var token))
            {
                if (++inspected > MaxUnicodeBidiTokens) return true;
                if (IsIsolateKeyword(token)) return true;
                if (token.Equals("plaintext", StringComparison.OrdinalIgnoreCase)) plaintext = true;
            }
            if (inspected < 2) return false;
            return plaintext;
        }

        private static bool IsIsolateKeyword(ReadOnlySpan<char> keyword) =>
            keyword.Equals("isolate", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("isolate-override", StringComparison.OrdinalIgnoreCase);

        internal static bool CarriesIsolateKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var keyword = value.AsSpan().Trim();
            if (IsIsolateKeyword(keyword)) return true;
            var tokenizer = SvgValues.CreateTokenizer(keyword);
            while (tokenizer.Next(out var token))
            {
                if (IsIsolateKeyword(token)) return true;
            }
            return false;
        }

        internal static bool IsSupportedTextDecoration(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var keyword = value.AsSpan().Trim();
            if (IsInheritedKeyword(keyword)) return true;
            int inspected = 0;
            var tokenizer = SvgValues.CreateTokenizer(keyword);
            while (tokenizer.Next(out var token))
            {
                if (++inspected > 4) return false;
                if (token.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("underline", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("overline", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("line-through", StringComparison.OrdinalIgnoreCase))
                    continue;
                return false;
            }
            return inspected > 0;
        }

        internal static bool IsInertTextRendering(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var keyword = value.AsSpan().Trim();
            return IsInheritedKeyword(keyword) ||
                   keyword.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("optimizeSpeed", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("optimizeLegibility", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("geometricPrecision", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsInertFontStretch(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var keyword = value.AsSpan().Trim();
            return IsInheritedKeyword(keyword) ||
                   keyword.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
                   keyword.Equals("100%", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsInertFontVariant(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return value.AsSpan().Trim().Equals("normal", StringComparison.OrdinalIgnoreCase);
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
                string target = value.Substring(start + 4, close - start - 4)
                    .Trim().Trim('\'', '"').Trim();
                if (target.Length > 0 && target[0] != '#') return true;
                searchStart = close + 1;
            }
            return false;
        }

        internal static bool ResolvesToNoElement(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase);

        internal static bool SupportsNonScalingStroke(SvgElement element, string value)
        {
            if (element == null || string.IsNullOrWhiteSpace(value) ||
                !value.Trim().Equals("non-scaling-stroke", StringComparison.OrdinalIgnoreCase))
                return false;
            return element.Name is "path" or "rect" or "circle" or "ellipse" or
                "line" or "polyline" or "polygon";
        }

        private static bool IsInheritedKeyword(ReadOnlySpan<char> keyword) =>
            keyword.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("revert", StringComparison.OrdinalIgnoreCase);
    }
}
