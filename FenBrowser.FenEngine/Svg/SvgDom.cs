using System.Collections.Generic;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Minimal SVG element tree produced by <see cref="SvgMarkupParser"/>.
    ///
    /// Security model:
    /// - The tree can only be created through the sandboxed parser, which enforces
    ///   element count, nesting depth, attribute count/length and source budgets
    ///   while building it. There is no public constructor surface to bypass them.
    /// - Duplicate attributes keep the FIRST occurrence (browser-style recovery);
    ///   later duplicates are recorded in <see cref="SvgParseReport"/>.
    /// </summary>
    internal sealed class SvgElement
    {
        public string Name;
        public string NamespaceUri;
        public string IdAttribute;
        public bool IsSelfClosing;
        public bool UnclosedForeignElement;
        public string TextContent;
        public KeyValuePair<string, string>[] Attributes;
        public readonly List<SvgElement> Children = new List<SvgElement>();
        public readonly List<SvgContentPart> Content = new List<SvgContentPart>();
        public SvgElement Parent;
        public SvgElement PreviousElementSibling;
        public Dictionary<string, string> CascadedDeclarations;
        public Dictionary<string, string> CustomProperties;
        public Dictionary<string, string> AnimatedProperties;

        /// <summary>
        /// Set only on the synthetic element that stands for a <c>::first-letter</c>
        /// pseudo-element (<see cref="SvgFirstLetter"/>): the text element it belongs to.
        /// </summary>
        public SvgElement FirstLetterOrigin;

        private Dictionary<string, string> _lookup;

        public string GetAttribute(string name)
        {
            if (AnimatedProperties != null && name != null &&
                AnimatedProperties.TryGetValue(name, out string animated))
            {
                return animated;
            }
            var attrs = Attributes;
            if (attrs == null || name == null)
            {
                return null;
            }

            // Attribute lists are tiny (< 256); linear scan avoids allocating the
            // lookup table for the overwhelming majority of elements. Names match
            // exactly: this is an XML document, where 'viewbox' is not 'viewBox'
            // (XML 1.0 §2.3, names are case-sensitive).
            for (int i = 0; i < attrs.Length; i++)
            {
                if (string.Equals(attrs[i].Key, name, System.StringComparison.Ordinal))
                {
                    return attrs[i].Value;
                }
            }
            return null;
        }

        public string GetLookup(string name)
        {
            var map = _lookup ??= BuildLookup();
            return map.TryGetValue(name, out var value) ? value : null;
        }

        public string GetCascadedPresentationProperty(string name)
        {
            if (name == null) return null;
            if (AnimatedProperties != null &&
                AnimatedProperties.TryGetValue(name, out string animated))
            {
                return animated;
            }
            if (CascadedDeclarations == null ||
                !CascadedDeclarations.TryGetValue(name, out string value))
                return null;
            bool substitutes = value.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) >= 0;
            string resolved = ResolveCssVariables(value, new HashSet<string>(System.StringComparer.Ordinal), 0);
            if (resolved != null && !SvgCssCascade.IsDefinitelyInvalid(name, resolved))
                return resolved;
            // A declaration with var() that fails substitution, or is invalid after it,
            // is invalid at computed-value time (CSS Variables 1 §3.1): it still wins the
            // cascade over presentation attributes and behaves as 'unset'. Without var()
            // an invalid declaration was never valid and the attribute applies.
            return substitutes ? "unset" : null;
        }

        public string GetPresentationProperty(string name)
        {
            string value = GetCascadedPresentationProperty(name);
            if (value != null) return value;
            value = GetAttribute(name);
            return ResolveCssVariables(value, new HashSet<string>(System.StringComparer.Ordinal), 0);
        }

        public bool UsesCssPropertySyntax(string name)
        {
            if (CascadedDeclarations != null && CascadedDeclarations.ContainsKey(name)) return true;
            string attribute = GetAttribute(name);
            if (attribute == null) return false;
            return attribute.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   attribute.IndexOf("calc(", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   attribute.IndexOf("min(", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   attribute.IndexOf("max(", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   attribute.IndexOf("clamp(", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string ResolveCssVariables(string value, HashSet<string> active, int depth)
        {
            if (string.IsNullOrEmpty(value) ||
                value.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) < 0)
                return value;
            if (depth >= 16) return null;

            var output = new System.Text.StringBuilder(value.Length);
            int position = 0;
            int replacements = 0;
            while (position < value.Length)
            {
                int start = value.IndexOf("var(", position, System.StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    output.Append(value, position, value.Length - position);
                    break;
                }
                output.Append(value, position, start - position);
                if (++replacements > 32) return null;
                int contentStart = start + 4;
                int close = FindFunctionClose(value, contentStart);
                if (close < 0) return null;
                int comma = FindTopLevelComma(value, contentStart, close);
                string name = value.Substring(
                    contentStart, (comma < 0 ? close : comma) - contentStart).Trim();
                if (!name.StartsWith("--", System.StringComparison.Ordinal) || !active.Add(name))
                    return null;

                string replacement = FindCustomProperty(name);
                if (replacement == null && comma >= 0)
                    replacement = value.Substring(comma + 1, close - comma - 1).Trim();
                replacement = ResolveCssVariables(replacement, active, depth + 1);
                active.Remove(name);
                if (replacement == null) return null;
                output.Append(replacement);
                if (output.Length > SvgMarkupParser.MaxAttributeValueChars) return null;
                position = close + 1;
            }
            return output.ToString();
        }

        private string FindCustomProperty(string name)
        {
            for (SvgElement current = this; current != null; current = current.Parent)
            {
                if (current.CustomProperties != null &&
                    current.CustomProperties.TryGetValue(name, out string value))
                    return value;
            }
            return null;
        }

        private static int FindFunctionClose(string value, int start)
        {
            int nested = 0;
            char quote = '\0';
            bool escaped = false;
            for (int i = start; i < value.Length; i++)
            {
                char c = value[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '\'' or '"') { quote = c; continue; }
                if (c == '(') nested++;
                else if (c == ')' && nested-- == 0) return i;
            }
            return -1;
        }

        private static int FindTopLevelComma(string value, int start, int end)
        {
            int nested = 0;
            char quote = '\0';
            bool escaped = false;
            for (int i = start; i < end; i++)
            {
                char c = value[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '\'' or '"') { quote = c; continue; }
                if (c == '(') nested++;
                else if (c == ')') nested--;
                else if (c == ',' && nested == 0) return i;
            }
            return -1;
        }

        private Dictionary<string, string> BuildLookup()
        {
            // First occurrence wins, matching GetAttribute semantics exactly
            // (F12): dictionary indexer would silently keep the LAST duplicate.
            var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
            var attrs = Attributes;
            if (attrs != null)
            {
                foreach (var kv in attrs)
                {
                    if (!map.ContainsKey(kv.Key))
                    {
                        map[kv.Key] = kv.Value;
                    }
                }
            }
            return map;
        }
    }

    /// <summary>
    /// Parsed SVG document: root element plus an id -> element index built during
    /// parsing (first occurrence wins; duplicate ids are reported, never fatal).
    /// </summary>
    internal sealed class SvgParsedDocument
    {
        public SvgParsedDocument()
        {
            Report = new SvgParseReport();
        }

        public SvgParsedDocument(SvgParseReport report)
        {
            Report = report;
        }

        public SvgElement Root;
        public Dictionary<string, SvgElement> ElementsById;
        public readonly SvgParseReport Report;
    }

    /// <summary>Bounded, allocation-friendly diagnostics for one parse.</summary>
    internal sealed class SvgParseReport
    {
        public const int MaxWarnings = 32;
        public const int MaxWarningLength = 256;

        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> FallbackReasonCodes = new List<string>();
        public readonly List<string> ResourceRejectionReasonCodes = new List<string>();
        public bool SawDoctype;
        public bool SawDuplicateAttribute;
        public bool SawDuplicateId;
        public bool SawClampedValue;
        public bool TruncatedPathData;
        public bool UnsupportedFeatureIgnored;
        public bool ResourceRejected;
        /// <summary>
        /// Mirrors <see cref="Adapters.SvgRenderLimits.LayOutTextAreasOnOneLine"/> for the
        /// cascade, which receives only the report. Set by the engine before the cascade.
        /// </summary>
        public bool TextAreasOnOneLine;
        public int ElementCount;

        public void Warn(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string bounded = message.Length <= MaxWarningLength
                ? message
                : message.Substring(0, MaxWarningLength);
            if (Warnings.Count < MaxWarnings && !Warnings.Contains(bounded))
            {
                Warnings.Add(bounded);
            }
        }

        public void RequireFallback(string reason)
        {
            UnsupportedFeatureIgnored = true;
            AddReason(FallbackReasonCodes, SvgDiagnosticClassifier.ClassifyFallback(reason));
            Warn(reason);
        }

        public void RejectResource(string reason)
        {
            ResourceRejected = true;
            AddReason(ResourceRejectionReasonCodes, SvgDiagnosticClassifier.ClassifyResourceRejection(reason));
            Warn(reason);
        }

        /// <summary>
        /// Takes in a nested document's findings (an external use document): its
        /// fallback and rejection verdicts count as this render's own.
        /// </summary>
        public void Absorb(SvgParseReport nested)
        {
            if (nested == null || ReferenceEquals(nested, this)) return;
            foreach (string warning in nested.Warnings) Warn(warning);
            foreach (string code in nested.FallbackReasonCodes) AddReason(FallbackReasonCodes, code);
            foreach (string code in nested.ResourceRejectionReasonCodes) AddReason(ResourceRejectionReasonCodes, code);
            UnsupportedFeatureIgnored |= nested.UnsupportedFeatureIgnored;
            ResourceRejected |= nested.ResourceRejected;
        }

        private static void AddReason(List<string> target, string code)
        {
            if (target.Count < MaxWarnings && !target.Contains(code)) target.Add(code);
        }
    }

    internal static class SvgDiagnosticClassifier
    {
        public static string ClassifyFallback(string reason)
        {
            if (Has(reason, "animate") || Has(reason, "SMIL") || Has(reason, "feature 'set'"))
                return "smil-animation";
            if (Has(reason, "textPath") || Has(reason, "textLength") || Has(reason, "lengthAdjust") ||
                Has(reason, "per-glyph") || Has(reason, "vertical text") || Has(reason, "tspan"))
                return "advanced-text-layout";
            if (Has(reason, "filter")) return "filter-effects";
            if (Has(reason, "mask")) return "masking";
            if (Has(reason, "marker")) return "markers";
            if (Has(reason, "pattern") || Has(reason, "paint server")) return "paint-server";
            if (Has(reason, "CSS") || Has(reason, "selector") || Has(reason, "stylesheet") || Has(reason, "media query"))
                return "css-cascade";
            if (Has(reason, "external reference") || Has(reason, "external resource"))
                return "external-resource";
            if (Has(reason, "budget") || Has(reason, "depth") || Has(reason, "limit exceeded"))
                return "admission-budget";
            if (Has(reason, "cycle") || Has(reason, "unresolved") || Has(reason, "reference"))
                return "reference-resolution";
            if (Has(reason, "unknown element") || Has(reason, "unsupported SVG feature") || Has(reason, "SVG feature"))
                return "unsupported-element";
            if (Has(reason, "script") || Has(reason, "dynamic") || Has(reason, "pseudo-element"))
                return "dynamic-content";
            if (Has(reason, "property") || Has(reason, "attribute")) return "unsupported-property";
            return "unsupported-feature";
        }

        public static string ClassifyResourceRejection(string reason)
        {
            if (Has(reason, "no authorized resolver context")) return "resource-context-missing";
            if (Has(reason, "cross-origin")) return "cross-origin-resource";
            if (Has(reason, "mismatched URI")) return "resolver-uri-mismatch";
            if (Has(reason, "[resolver-miss]") || Has(reason, "resolver returned no resource") ||
                Has(reason, "resource not found"))
                return "resolver-miss";
            if (Has(reason, "external")) return "external-resource";
            if (Has(reason, "depth") || Has(reason, "recursive")) return "resource-depth";
            if (Has(reason, "budget") || Has(reason, "limit") || Has(reason, "too large")) return "resource-budget";
            if (Has(reason, "UTF-8") || Has(reason, "malformed") || Has(reason, "invalid")) return "invalid-resource";
            if (Has(reason, "unsupported")) return "unsupported-resource";
            return "resource-rejected";
        }

        private static bool Has(string value, string fragment) =>
            value?.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal readonly struct SvgContentPart
    {
        public SvgContentPart(string text)
        {
            Text = text;
            Element = null;
        }

        public SvgContentPart(SvgElement element)
        {
            Text = null;
            Element = element;
        }

        public string Text { get; }
        public SvgElement Element { get; }
        public bool IsText => Text != null;
    }
}
