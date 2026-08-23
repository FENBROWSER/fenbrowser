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
        public string IdAttribute;
        public bool IsSelfClosing;
        public string TextContent;
        public KeyValuePair<string, string>[] Attributes;
        public readonly List<SvgElement> Children = new List<SvgElement>();
        public readonly List<SvgContentPart> Content = new List<SvgContentPart>();
        public SvgElement Parent;
        public SvgElement PreviousElementSibling;
        public Dictionary<string, string> CascadedDeclarations;

        private Dictionary<string, string> _lookup;

        public string GetAttribute(string name)
        {
            var attrs = Attributes;
            if (attrs == null || name == null)
            {
                return null;
            }

            // Attribute lists are tiny (< 256); linear scan avoids allocating the
            // lookup table for the overwhelming majority of elements.
            for (int i = 0; i < attrs.Length; i++)
            {
                if (string.Equals(attrs[i].Key, name, System.StringComparison.OrdinalIgnoreCase))
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

        public string GetPresentationProperty(string name)
        {
            if (CascadedDeclarations != null &&
                CascadedDeclarations.TryGetValue(name, out var value))
            {
                return value;
            }
            return GetAttribute(name);
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
            if (Has(reason, "script") || Has(reason, "dynamic") || Has(reason, "pseudo-element"))
                return "dynamic-content";
            if (Has(reason, "unknown element") || Has(reason, "unsupported SVG feature") || Has(reason, "SVG feature"))
                return "unsupported-element";
            if (Has(reason, "property") || Has(reason, "attribute")) return "unsupported-property";
            return "unsupported-feature";
        }

        public static string ClassifyResourceRejection(string reason)
        {
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
