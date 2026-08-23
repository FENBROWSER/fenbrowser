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

        public readonly List<string> Warnings = new List<string>();
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
            if (Warnings.Count < MaxWarnings && !Warnings.Contains(message))
            {
                Warnings.Add(message);
            }
        }

        public void RequireFallback(string reason)
        {
            UnsupportedFeatureIgnored = true;
            Warn(reason);
        }

        public void RejectResource(string reason)
        {
            ResourceRejected = true;
            Warn(reason);
        }
    }
}
