using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Applies author CSS to the bounded SVG tree. Syntax, declaration recovery,
    /// selector parsing, specificity, and cascade ranking come from the canonical
    /// FenEngine CSS implementation; only tree access/matching is SVG-specific.
    /// </summary>
    internal static class SvgCssCascade
    {
        private const int MaxStyleElements = 256;
        private const int MaxRulesPerStyleElement = 4096;
        private const int MaxTotalRules = 16384;
        private const int MaxDeclarationsPerBlock = 256;
        private const int MaxSelectorMatchDepth = 64;

        private static readonly HashSet<string> SupportedProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "display", "visibility",
            "fill", "fill-opacity", "fill-rule",
            "stroke", "stroke-opacity", "stroke-width", "stroke-linecap",
            "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset",
            "opacity", "clip-path", "clip-rule",
            "stop-color", "stop-opacity",
            "font-family", "font-size", "font-style", "font-weight", "text-anchor"
        };

        private static readonly HashSet<string> NoneIsNoEffect = new(StringComparer.OrdinalIgnoreCase)
        {
            "filter", "mask", "marker", "marker-start", "marker-mid", "marker-end"
        };

        public static void Apply(
            SvgParsedDocument document,
            float viewportWidth,
            SvgParseReport report,
            Action checkDeadline)
        {
            var rules = new List<RuleEntry>();
            var layers = new LayerRegistry();
            int styleCount = 0;
            int sourceOrder = 0;
            CollectStyles(document.Root, rules, layers, ref styleCount, ref sourceOrder, viewportWidth, report, checkDeadline);
            var ruleIndex = new RuleIndex(rules);

            var stack = new Stack<SvgElement>();
            stack.Push(document.Root);
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                checkDeadline();
                ApplyToElement(element, rules, ruleIndex, report, checkDeadline);
                for (int i = element.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(element.Children[i]);
                }
            }
        }

        private static void CollectStyles(
            SvgElement root,
            List<RuleEntry> rules,
            LayerRegistry layers,
            ref int styleCount,
            ref int sourceOrder,
            float viewportWidth,
            SvgParseReport report,
            Action checkDeadline)
        {
            var stack = new Stack<SvgElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                checkDeadline();
                if (string.Equals(element.Name, "style", StringComparison.Ordinal))
                {
                    styleCount++;
                    if (styleCount > MaxStyleElements)
                    {
                        throw new SvgSandboxViolationException(
                            $"SVG style element count ({styleCount}) exceeds limit ({MaxStyleElements})");
                    }

                    string type = element.GetAttribute("type");
                    string media = element.GetAttribute("media");
                    bool supportedType = string.IsNullOrWhiteSpace(type) ||
                        type.Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase);
                    bool mediaSupported;
                    bool styleMediaApplies = MediaApplies(media, viewportWidth, out mediaSupported);
                    if (!mediaSupported)
                    {
                        report.RequireFallback("SVG style media query requires compatibility fallback");
                    }
                    if (supportedType && styleMediaApplies &&
                        !string.IsNullOrWhiteSpace(element.TextContent))
                    {
                        if (SvgFeatureSupport.HasExternalUrlReference(element.TextContent) ||
                            element.TextContent.Contains("@import", StringComparison.OrdinalIgnoreCase))
                        {
                            report.RejectResource("SVG CSS external resource import rejected");
                        }
                        var parser = new CssSyntaxParser(new CssTokenizer(element.TextContent))
                        {
                            MaxRules = MaxRulesPerStyleElement,
                            MaxDeclarationsPerBlock = MaxDeclarationsPerBlock,
                            MaxRuleNestingDepth = 32
                        };
                        var stylesheet = parser.ParseStylesheet();
                        FlattenRules(stylesheet.Rules, rules, layers, sourceOrder++, 0, viewportWidth, report, checkDeadline);
                        if (rules.Count > MaxTotalRules)
                        {
                            throw new SvgSandboxViolationException(
                                $"SVG CSS rule count ({rules.Count}) exceeds limit ({MaxTotalRules})");
                        }
                    }
                }

                for (int i = element.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(element.Children[i]);
                }
            }
        }

        private static void FlattenRules(
            IReadOnlyList<CssRule> source,
            List<RuleEntry> target,
            LayerRegistry layers,
            int sourceOrder,
            int layerOrder,
            float viewportWidth,
            SvgParseReport report,
            Action checkDeadline)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if ((i & 0x3f) == 0) checkDeadline();
                switch (source[i])
                {
                    case CssStyleRule styleRule when styleRule.Selector != null:
                        target.Add(new RuleEntry(styleRule, sourceOrder, layerOrder));
                        if (styleRule.NestedRules.Count > 0)
                        {
                            FlattenRules(styleRule.NestedRules, target, layers, sourceOrder, layerOrder, viewportWidth, report, checkDeadline);
                        }
                        break;
                    case CssLayerRule layer:
                        if (layer.Rules.Count == 0)
                        {
                            layers.RegisterList(layer.Name);
                        }
                        else
                        {
                            int resolvedLayerOrder = layers.GetOrAdd(layer.Name);
                            FlattenRules(layer.Rules, target, layers, sourceOrder, resolvedLayerOrder, viewportWidth, report, checkDeadline);
                        }
                        break;
                    case CssMediaRule media:
                        bool supported;
                        bool applies = MediaApplies(media.Condition, viewportWidth, out supported);
                        if (!supported)
                        {
                            report.RequireFallback("SVG media query requires compatibility fallback");
                        }
                        else if (applies)
                        {
                            FlattenRules(media.Rules, target, layers, sourceOrder, layerOrder, viewportWidth, report, checkDeadline);
                        }
                        break;
                    case CssScopeRule:
                        report.RequireFallback("SVG @scope stylesheet rule requires compatibility fallback");
                        break;
                }
            }
        }

        private static bool MediaApplies(string condition, float viewportWidth, out bool supported)
        {
            supported = true;
            if (string.IsNullOrWhiteSpace(condition)) return true;
            string value = condition.Trim();
            if (value.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("screen", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A deterministic, instance-local subset for the replaced element's width.
            // More complex media syntax remains non-matching rather than mutating the
            // process-global browser media environment.
            bool min = value.StartsWith("(min-width:", StringComparison.OrdinalIgnoreCase);
            bool max = value.StartsWith("(max-width:", StringComparison.OrdinalIgnoreCase);
            if ((min || max) && value.EndsWith(")", StringComparison.Ordinal))
            {
                int colon = value.IndexOf(':');
                string raw = value.Substring(colon + 1, value.Length - colon - 2).Trim();
                if (SvgValues.TryParseLength(raw.AsSpan(), out float amount, out var unit))
                {
                    float resolved = SvgValues.ResolveUnits(amount, unit, 16f, viewportWidth);
                    return min ? viewportWidth >= resolved : viewportWidth <= resolved;
                }
            }
            supported = false;
            return false;
        }

        private static void ApplyToElement(
            SvgElement element,
            IReadOnlyList<RuleEntry> rules,
            RuleIndex ruleIndex,
            SvgParseReport report,
            Action checkDeadline)
        {
            Dictionary<string, Winner> winners = null;
            var candidates = ruleIndex.GetCandidates(element);
            int candidatePosition = 0;
            foreach (int i in candidates)
            {
                if ((candidatePosition++ & 0x3f) == 0) checkDeadline();
                var entry = rules[i];
                var specificity = GetMatchingSpecificity(element, entry.Rule.Selector, report);
                if (!specificity.HasValue) continue;

                var declarations = entry.Rule.Declarations;
                for (int d = 0; d < declarations.Count; d++)
                {
                    var declaration = declarations[d];
                    Consider(
                        ref winners,
                        declaration,
                        new CascadeKey(
                            CssOrigin.Author,
                            declaration.IsImportant,
                            ClampSpecificity(specificity.Value.A),
                            ClampSpecificity(specificity.Value.B),
                            ClampSpecificity(specificity.Value.C),
                            entry.LayerOrder,
                            0,
                            entry.SourceOrder,
                            entry.Rule.Order,
                            d),
                        report);
                }
            }

            string inline = element.GetAttribute("style");
            if (!string.IsNullOrWhiteSpace(inline))
            {
                var parser = new CssSyntaxParser(new CssTokenizer(inline))
                {
                    MaxRules = 1,
                    MaxDeclarationsPerBlock = MaxDeclarationsPerBlock,
                    MaxRuleNestingDepth = 1
                };
                var declarations = parser.ParseDeclarationList();
                for (int i = 0; i < declarations.Count; i++)
                {
                    var declaration = declarations[i];
                    Consider(
                        ref winners,
                        declaration,
                        new CascadeKey(
                            CssOrigin.Author,
                            declaration.IsImportant,
                            1, 0, 0,
                            0,
                            0,
                            int.MaxValue,
                            int.MaxValue,
                            i),
                        report);
                }
            }

            if (winners == null) return;
            element.CascadedDeclarations = new Dictionary<string, string>(winners.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in winners)
            {
                element.CascadedDeclarations[pair.Key] = pair.Value.Value;
            }
        }

        private static void Consider(
            ref Dictionary<string, Winner> winners,
            CssDeclaration declaration,
            CascadeKey key,
            SvgParseReport report)
        {
            if (declaration == null || string.IsNullOrWhiteSpace(declaration.Property)) return;
            string property = declaration.Property;
            string value = declaration.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            if (!SupportedProperties.Contains(property))
            {
                if (NoneIsNoEffect.Contains(property) &&
                    value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (!property.StartsWith("--", StringComparison.Ordinal))
                {
                    report.RequireFallback($"SVG CSS property '{property}' requires compatibility fallback");
                }
                return;
            }
            if (value.Contains("var(", StringComparison.OrdinalIgnoreCase))
            {
                report.RequireFallback("SVG CSS custom-property resolution requires compatibility fallback");
                return;
            }
            if (SvgFeatureSupport.HasExternalUrlReference(value))
            {
                report.RejectResource("SVG CSS external resource reference rejected");
                return;
            }

            winners ??= new Dictionary<string, Winner>(StringComparer.OrdinalIgnoreCase);
            if (!winners.TryGetValue(property, out var current) || key.CompareTo(current.Key) >= 0)
            {
                winners[property] = new Winner(value, key);
            }
        }

        private static Specificity? GetMatchingSpecificity(
            SvgElement element,
            CssSelector selector,
            SvgParseReport report)
        {
            if (selector?.Chains == null) return null;
            Specificity? best = null;
            for (int i = 0; i < selector.Chains.Count; i++)
            {
                var chain = selector.Chains[i];
                if (!IsSupportedSelector(chain))
                {
                    report.RequireFallback("SVG dynamic or pseudo-element selector requires compatibility fallback");
                    continue;
                }
                if (MatchesChain(element, chain, chain.Segments.Count - 1, 0) &&
                    (!best.HasValue || chain.Specificity.CompareTo(best.Value) > 0))
                {
                    best = chain.Specificity;
                }
            }
            return best;
        }

        private static bool IsSupportedSelector(SelectorChain chain)
        {
            if (chain == null || chain.Segments.Count == 0) return false;
            foreach (var segment in chain.Segments)
            {
                if (segment.PseudoElements.Count > 0) return false;
                foreach (var pseudo in segment.PseudoClasses)
                {
                    switch (pseudo.Name)
                    {
                        case "root":
                        case "empty":
                        case "first-child":
                        case "last-child":
                        case "only-child":
                        case "first-of-type":
                        case "last-of-type":
                        case "only-of-type":
                        case "is":
                        case "where":
                        case "not":
                            break;
                        default:
                            return false;
                    }
                }
            }
            return true;
        }

        private static bool MatchesChain(
            SvgElement element,
            SelectorChain chain,
            int index,
            int depth)
        {
            if (element == null || chain == null || index < 0 || depth > MaxSelectorMatchDepth) return false;
            var segment = chain.Segments[index];
            if (!MatchesSegment(element, segment, depth + 1)) return false;
            if (index == 0) return true;

            switch (segment.Combinator)
            {
                case '>':
                    return MatchesChain(element.Parent, chain, index - 1, depth + 1);
                case '+':
                    return MatchesChain(element.PreviousElementSibling, chain, index - 1, depth + 1);
                case '~':
                    for (var sibling = element.PreviousElementSibling; sibling != null; sibling = sibling.PreviousElementSibling)
                    {
                        if (MatchesChain(sibling, chain, index - 1, depth + 1)) return true;
                    }
                    return false;
                default:
                    for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
                    {
                        if (MatchesChain(ancestor, chain, index - 1, depth + 1)) return true;
                    }
                    return false;
            }
        }

        private static bool MatchesSegment(SvgElement element, SelectorSegment segment, int depth)
        {
            if (!string.IsNullOrEmpty(segment.TagName) && segment.TagName != "*" &&
                !string.Equals(element.Name, segment.TagName, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(segment.Id) &&
                !string.Equals(element.IdAttribute, segment.Id, StringComparison.Ordinal)) return false;

            string classValue = element.GetAttribute("class");
            foreach (string requiredClass in segment.Classes)
            {
                if (!ContainsToken(classValue, requiredClass)) return false;
            }
            foreach (var attribute in segment.Attributes)
            {
                if (!MatchesAttribute(element, attribute)) return false;
            }
            foreach (var pseudo in segment.PseudoClasses)
            {
                if (!MatchesPseudo(element, pseudo, depth + 1)) return false;
            }
            return true;
        }

        private static bool MatchesPseudo(SvgElement element, PseudoSelector pseudo, int depth)
        {
            switch (pseudo.Name)
            {
                case "root": return element.Parent == null;
                case "empty": return element.Children.Count == 0 && string.IsNullOrEmpty(element.TextContent);
                case "first-child": return element.PreviousElementSibling == null;
                case "last-child": return element.Parent == null || ReferenceEquals(element.Parent.Children[^1], element);
                case "only-child": return element.Parent == null || element.Parent.Children.Count == 1;
                case "first-of-type": return !HasPreviousOfType(element);
                case "last-of-type": return !HasNextOfType(element);
                case "only-of-type": return !HasPreviousOfType(element) && !HasNextOfType(element);
                case "is":
                case "where":
                    return MatchesAny(element, pseudo.ParsedArgsOrNull, depth + 1);
                case "not":
                    return !MatchesAny(element, pseudo.ParsedArgsOrNull, depth + 1);
                default:
                    return false;
            }
        }

        private static bool MatchesAny(SvgElement element, IReadOnlyList<SelectorChain> chains, int depth)
        {
            if (chains == null) return false;
            for (int i = 0; i < chains.Count; i++)
            {
                if (MatchesChain(element, chains[i], chains[i].Segments.Count - 1, depth + 1)) return true;
            }
            return false;
        }

        private static bool MatchesAttribute(SvgElement element, AttributeSelector attribute)
        {
            string actual = element.GetAttribute(attribute.Name);
            if (attribute.Operator == null) return actual != null;
            if (actual == null) return false;
            var comparison = attribute.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return attribute.Operator switch
            {
                "=" => string.Equals(actual, attribute.Value, comparison),
                "~=" => ContainsToken(actual, attribute.Value, comparison),
                "|=" => string.Equals(actual, attribute.Value, comparison) || actual.StartsWith(attribute.Value + "-", comparison),
                "^=" => actual.StartsWith(attribute.Value, comparison),
                "$=" => actual.EndsWith(attribute.Value, comparison),
                "*=" => actual.IndexOf(attribute.Value, comparison) >= 0,
                _ => false
            };
        }

        private static bool ContainsToken(
            string value,
            string token,
            StringComparison comparison = StringComparison.Ordinal)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token)) return false;
            int start = 0;
            while (start < value.Length)
            {
                while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
                int end = start;
                while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
                if (end > start && string.Equals(value.Substring(start, end - start), token, comparison)) return true;
                start = end + 1;
            }
            return false;
        }

        private static bool HasPreviousOfType(SvgElement element)
        {
            for (var sibling = element.PreviousElementSibling; sibling != null; sibling = sibling.PreviousElementSibling)
            {
                if (string.Equals(sibling.Name, element.Name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool HasNextOfType(SvgElement element)
        {
            if (element.Parent == null) return false;
            bool found = false;
            foreach (var sibling in element.Parent.Children)
            {
                if (found && string.Equals(sibling.Name, element.Name, StringComparison.Ordinal)) return true;
                if (ReferenceEquals(sibling, element)) found = true;
            }
            return false;
        }

        private static ushort ClampSpecificity(int value) =>
            (ushort)Math.Clamp(value, 0, ushort.MaxValue);

        private readonly record struct RuleEntry(
            CssStyleRule Rule,
            int SourceOrder,
            int LayerOrder);

        private readonly record struct Winner(string Value, CascadeKey Key);

        private sealed class LayerRegistry
        {
            private readonly Dictionary<string, int> _orders = new(StringComparer.Ordinal);
            private int _nextOrder = 1;

            public int GetOrAdd(string name)
            {
                name = name?.Trim() ?? string.Empty;
                if (_orders.TryGetValue(name, out int order)) return order;
                order = _nextOrder++;
                _orders[name] = order;
                return order;
            }

            public void RegisterList(string names)
            {
                if (string.IsNullOrWhiteSpace(names))
                {
                    GetOrAdd(string.Empty);
                    return;
                }
                foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    GetOrAdd(name);
                }
            }
        }

        private sealed class RuleIndex
        {
            private readonly Dictionary<string, List<int>> _byId = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<int>> _byClass = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<int>> _byTag = new(StringComparer.Ordinal);
            private readonly List<int> _universal = new();

            public RuleIndex(IReadOnlyList<RuleEntry> rules)
            {
                for (int ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
                {
                    var selector = rules[ruleIndex].Rule.Selector;
                    if (selector?.Chains == null || selector.Chains.Count == 0)
                    {
                        _universal.Add(ruleIndex);
                        continue;
                    }

                    foreach (var chain in selector.Chains)
                    {
                        if (chain.Segments.Count == 0)
                        {
                            AddUnique(_universal, ruleIndex);
                            continue;
                        }
                        var rightmost = chain.Segments[^1];
                        if (!string.IsNullOrEmpty(rightmost.Id))
                        {
                            Add(_byId, rightmost.Id, ruleIndex);
                        }
                        else if (rightmost.Classes.Count > 0)
                        {
                            Add(_byClass, rightmost.Classes[0], ruleIndex);
                        }
                        else if (!string.IsNullOrEmpty(rightmost.TagName) && rightmost.TagName != "*")
                        {
                            Add(_byTag, rightmost.TagName, ruleIndex);
                        }
                        else
                        {
                            AddUnique(_universal, ruleIndex);
                        }
                    }
                }
            }

            public HashSet<int> GetCandidates(SvgElement element)
            {
                var result = new HashSet<int>(_universal);
                AddMatches(result, _byTag, element.Name);
                AddMatches(result, _byId, element.IdAttribute);

                string classValue = element.GetAttribute("class");
                if (!string.IsNullOrWhiteSpace(classValue))
                {
                    foreach (string className in classValue.Split(
                        (char[])null,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        AddMatches(result, _byClass, className);
                    }
                }
                return result;
            }

            private static void Add(Dictionary<string, List<int>> index, string key, int value)
            {
                if (!index.TryGetValue(key, out var values))
                {
                    values = new List<int>();
                    index[key] = values;
                }
                AddUnique(values, value);
            }

            private static void AddMatches(HashSet<int> target, Dictionary<string, List<int>> index, string key)
            {
                if (string.IsNullOrEmpty(key) || !index.TryGetValue(key, out var values)) return;
                foreach (int value in values) target.Add(value);
            }

            private static void AddUnique(List<int> values, int value)
            {
                if (values.Count == 0 || values[^1] != value) values.Add(value);
            }
        }
    }
}
