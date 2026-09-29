using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
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
        private const int MaxUseShadowRoots = 64;
        private const int MaxUseShadowElements = 32768;
        private const int MaxFontShorthandTokens = 16;
        private const int MaxFontFamilyEntries = 8;

        private static readonly HashSet<string> SupportedProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "display", "visibility",
            "fill", "fill-opacity", "fill-rule",
            "stroke", "stroke-opacity", "stroke-width", "stroke-linecap",
            "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset",
            "opacity", "clip-path", "clip-rule",
            "filter", "mask", "mask-type", "marker", "marker-start", "marker-mid", "marker-end",
            "isolation", "mix-blend-mode",
            "flood-color", "flood-opacity",
            "stop-color", "stop-opacity",
            "font-family", "font-size", "font-style", "font-weight", "letter-spacing", "text-anchor",
            "text-decoration", "direction", "unicode-bidi", "font-size-adjust",
            "transform", "transform-origin", "transform-box", "zoom",
            "x", "y", "width", "height", "cx", "cy", "r", "rx", "ry", "d", "path-length",
            "paint-order"
        };

        private static readonly HashSet<string> NoneIsNoEffect = new(StringComparer.OrdinalIgnoreCase)
        {
            "vector-effect"
        };

        private static readonly HashSet<string> NoEffectProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color-interpolation-filters", "enable-background", "text-rendering"
        };

        private static readonly HashSet<string> InertBoxProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "content", "background", "background-color", "background-image",
            "background-position", "background-position-x", "background-position-y",
            "background-size", "background-repeat", "background-repeat-x",
            "background-repeat-y", "background-attachment", "background-origin",
            "background-clip", "background-blend-mode"
        };

        private static readonly HashSet<string> BaseStatePseudoClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "active", "any-link", "autofill", "blank", "checked", "closed", "current",
            "default", "disabled", "dragging", "enabled", "focus", "focus-visible",
            "focus-within", "fullscreen", "future", "host", "host-context", "hover",
            "in-range", "indeterminate", "invalid", "link", "local-link", "modal",
            "muted", "open", "optional", "out-of-range", "past", "paused", "picture-in-picture",
            "placeholder-shown", "playing", "popover-open", "read-only", "read-write",
            "required", "target", "target-within", "user-invalid", "user-valid", "visited"
        };

        private static readonly HashSet<string> PaintedPseudoElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "first-letter", "first-line"
        };

        private static readonly HashSet<string> UndeterminedMediaFeatures = new(StringComparer.OrdinalIgnoreCase)
        {
            "any-hover", "any-pointer", "aspect-ratio", "color-gamut", "color-index",
            "device-aspect-ratio", "device-height", "dynamic-range", "forced-colors",
            "grid", "height", "hover", "inverted-colors", "orientation",
            "overflow-block", "overflow-inline", "pointer", "prefers-color-scheme",
            "prefers-contrast", "prefers-reduced-data", "prefers-reduced-motion",
            "prefers-reduced-transparency", "scripting", "update", "video-dynamic-range"
        };

        private static readonly HashSet<string> BooleanUndeterminedMediaFeatures = new(StringComparer.OrdinalIgnoreCase)
        {
            "any-hover", "any-pointer", "grid", "hover", "inverted-colors",
            "pointer", "scripting", "update"
        };

        private static readonly HashSet<string> UndeterminedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "aural", "braille", "embossed", "handheld", "print", "projection",
            "speech", "tty", "tv"
        };

        // CSS box/input properties captured on inline SVG markup but not consumed
        // by the isolated SVG picture. They are applied by the embedding/layout
        // layer and must not force a pixel-renderer fallback.
        private static readonly HashSet<string> EmbeddingOnlyProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "vertical-align", "border", "border-top", "border-right", "border-bottom", "border-left",
            "border-color", "border-style", "border-width", "outline", "cursor", "pointer-events",
            "user-select", "touch-action",
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "padding", "padding-top", "padding-right", "padding-bottom", "padding-left"
        };

        // What each rejected property would need from the engine, so an operator can
        // tell a missing text-layout subsystem apart from a missing paint detail
        // instead of reading one indistinguishable "requires compatibility fallback"
        // for every unknown name. Each entry names a capability this file cannot
        // grant on its own: a value here is only honest while the stated consumer
        // still does not exist.
        private static readonly Dictionary<string, string> UnimplementedCssCapabilities =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["inline-size"] =
                "establishes a block box the run wraps in; the text model has one line box per text element and no block-size resolution",
            ["line-spacing"] =
                "is not a CSS property; the cascade has no property-name registry that could tell an unknown name from an unimplemented one",
            ["shape-inside"] =
                "wraps the run in an exclusion shape; the text model has no line breaking and no shape boundary",
            ["shape-margin"] =
                "grows a shape exclusion boundary; the text model has no shape boundary",
            ["shape-padding"] =
                "grows a shape exclusion boundary inward; the text model has no shape boundary",
            ["shape-subtract"] =
                "removes a shape from the run's exclusion area; the text model has no shape boundary",
            ["text-align"] =
                "aligns lines inside an established inline-size; the text model has no block-size to align against",
            ["text-decoration-color"] =
                "paints the decoration from a per-run colour; the text model resolves decoration line styles only",
            ["text-orientation"] =
                "rotates glyphs for a vertical block flow; the text model has one horizontal line box per text element",
            ["white-space"] =
                "selects newline preservation and line wrapping; the text normaliser maps every newline to a space and never breaks a line",
            ["writing-mode"] =
                "selects a vertical block flow; the text model has one horizontal line box per text element",
            ["z-index"] =
                "reorders painting into a stacking context; the draw walk paints strictly in document order"
        };

        /// <summary>
        /// Text-in-an-area properties that have no effect when the text lays out on one
        /// line, which is how Chromium paints SVG text that uses them
        /// (<see cref="Adapters.SvgRenderLimits.LayOutTextAreasOnOneLine"/>).
        /// </summary>
        private static readonly HashSet<string> SingleLineAreaProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "inline-size", "shape-inside", "shape-subtract", "shape-margin", "shape-padding",
            "text-align", "line-spacing"
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

            var useTargets = new List<SvgElement>();
            var seenUseTargets = new HashSet<SvgElement>();
            bool stylesFirstLetter = StylesFirstLetter(rules);
            List<SvgElement> firstLetterOrigins = null;
            var stack = new Stack<SvgElement>();
            stack.Push(document.Root);
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                checkDeadline();
                ApplyToElement(element, rules, ruleIndex, report, checkDeadline);
                if (stylesFirstLetter && string.Equals(element.Name, "text", StringComparison.Ordinal))
                {
                    (firstLetterOrigins ??= new List<SvgElement>()).Add(element);
                }
                if (string.Equals(element.Name, "use", StringComparison.Ordinal))
                {
                    CollectUseTarget(document, element, useTargets, seenUseTargets);
                }
                for (int i = element.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(element.Children[i]);
                }
            }

            BuildUseShadowCascades(useTargets, rules, ruleIndex, report, checkDeadline);

            if (firstLetterOrigins != null)
            {
                foreach (var origin in firstLetterOrigins)
                {
                    checkDeadline();
                    ApplyFirstLetter(origin, rules, ruleIndex, report, checkDeadline);
                }
            }
        }

        /// <summary>True for a chain whose only pseudo-element is a trailing ::first-letter.</summary>
        private static bool IsFirstLetterChain(SelectorChain chain)
        {
            if (chain?.Segments == null || chain.Segments.Count == 0) return false;
            for (int i = 0; i < chain.Segments.Count - 1; i++)
            {
                if (chain.Segments[i].PseudoElements.Count > 0) return false;
            }
            var last = chain.Segments[^1].PseudoElements;
            return last.Count == 1 &&
                   string.Equals(last[0].Name, "first-letter", StringComparison.OrdinalIgnoreCase);
        }

        private static bool StylesFirstLetter(List<RuleEntry> rules)
        {
            foreach (var entry in rules)
            {
                var chains = entry.Rule.Selector?.Chains;
                if (chains == null) continue;
                foreach (var chain in chains)
                {
                    if (IsFirstLetterChain(chain)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Styles the ::first-letter of one text element (CSS Pseudo-Elements 4 §2.4):
        /// the rules whose selector is a ::first-letter chain matching the text element
        /// cascade onto a synthetic element around its first letter, which inherits
        /// from the element that holds the letter. Nothing is inserted when no such
        /// rule matches or the text has no letter.
        /// </summary>
        private static void ApplyFirstLetter(
            SvgElement origin,
            IReadOnlyList<RuleEntry> rules,
            RuleIndex ruleIndex,
            SvgParseReport report,
            Action checkDeadline)
        {
            Dictionary<string, Winner> winners = null;
            Dictionary<string, Winner> customWinners = null;
            bool matched = false;
            int candidatePosition = 0;
            foreach (int i in ruleIndex.GetCandidates(origin))
            {
                if ((candidatePosition++ & 0x3f) == 0) checkDeadline();
                var entry = rules[i];
                var chains = entry.Rule.Selector?.Chains;
                if (chains == null) continue;
                Specificity? best = null;
                foreach (var chain in chains)
                {
                    if (!IsFirstLetterChain(chain)) continue;
                    var support = ClassifyChain(origin, chain, 0, firstLetter: true);
                    if (support == SelectorSupport.Unsupported)
                    {
                        report.RequireFallback("SVG unsupported selector requires compatibility fallback");
                        continue;
                    }
                    if (support == SelectorSupport.NotApplicable) continue;
                    if (MatchesChain(origin, chain, chain.Segments.Count - 1, 0, null) &&
                        (!best.HasValue || chain.Specificity.CompareTo(best.Value) > 0))
                    {
                        best = chain.Specificity;
                    }
                }
                if (!best.HasValue) continue;
                matched = true;

                var declarations = entry.Rule.Declarations;
                for (int d = 0; d < declarations.Count; d++)
                {
                    var declaration = declarations[d];
                    if (!SvgFirstLetter.AppliesTo(declaration?.Property)) continue;
                    Consider(
                        origin,
                        ref winners,
                        ref customWinners,
                        declaration,
                        new CascadeKey(
                            CssOrigin.Author,
                            declaration.IsImportant,
                            ClampSpecificity(best.Value.A),
                            ClampSpecificity(best.Value.B),
                            ClampSpecificity(best.Value.C),
                            entry.LayerOrder,
                            0,
                            entry.SourceOrder,
                            entry.Rule.Order,
                            d),
                        report);
                }
            }
            if (!matched) return;

            var pseudo = SvgFirstLetter.Insert(origin);
            if (pseudo == null) return;
            if (winners != null)
            {
                pseudo.CascadedDeclarations = new Dictionary<string, string>(
                    winners.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in winners) pseudo.CascadedDeclarations[pair.Key] = pair.Value.Value;
            }
            if (customWinners != null)
            {
                pseudo.CustomProperties = new Dictionary<string, string>(
                    customWinners.Count, StringComparer.Ordinal);
                foreach (var pair in customWinners) pseudo.CustomProperties[pair.Key] = pair.Value.Value;
            }
        }

        private static void CollectUseTarget(
            SvgParsedDocument document,
            SvgElement use,
            List<SvgElement> targets,
            HashSet<SvgElement> seen)
        {
            string href = use.GetAttribute("href") ?? use.GetLookup("xlink:href");
            if (!SvgValues.TryParseLocalReference(href, out string rawId)) return;
            if (document.ElementsById == null) return;
            if (document.ElementsById.TryGetValue(
                    SvgRenderEngine.DecodeFragmentEscapes(rawId), out var target) &&
                target != null && seen.Add(target))
            {
                targets.Add(target);
            }
        }

        private static void BuildUseShadowCascades(
            List<SvgElement> useTargets,
            IReadOnlyList<RuleEntry> rules,
            RuleIndex ruleIndex,
            SvgParseReport report,
            Action checkDeadline)
        {
            if (useTargets == null || useTargets.Count == 0) return;
            if (rules.Count == 0) return;

            int budget = MaxUseShadowElements;
            int roots = 0;
            foreach (var target in useTargets)
            {
                if (target == null) continue;
                if (UseShadowCascades.TryGetValue(target, out _)) continue;
                if (++roots > MaxUseShadowRoots)
                {
                    report.RequireFallback(
                        $"SVG use shadow cascade root budget ({MaxUseShadowRoots}) exceeded; " +
                        "use-site cascade is incomplete");
                    return;
                }
                if (budget <= 0)
                {
                    report.RequireFallback(
                        $"SVG use shadow cascade element budget ({MaxUseShadowElements}) exceeded; " +
                        "use-site cascade is incomplete");
                    return;
                }

                var order = new List<SvgElement>();
                if (!CollectSubtree(target, order, budget, checkDeadline))
                {
                    report.RequireFallback(
                        $"SVG use shadow cascade element budget ({MaxUseShadowElements}) exceeded; " +
                        "use-site cascade is incomplete");
                    return;
                }
                budget -= order.Count;

                int count = order.Count;
                var cascaded = new Dictionary<string, string>[count];
                var custom = new Dictionary<string, string>[count];
                for (int i = 0; i < count; i++)
                {
                    checkDeadline();
                    ComputeDeclarations(
                        order[i], rules, ruleIndex, report, checkDeadline, target,
                        out cascaded[i], out custom[i]);
                }

                if (MatchesLightDeclarations(order, cascaded, custom)) continue;

                UseShadowCascades.Add(target, new UseShadowCascade
                {
                    Order = order.ToArray(),
                    Cascaded = cascaded,
                    Custom = custom
                });
            }
        }

        private static bool CollectSubtree(
            SvgElement root,
            List<SvgElement> order,
            int budget,
            Action checkDeadline)
        {
            order.Add(root);
            for (int i = 0; i < order.Count; i++)
            {
                if ((i & 0x3f) == 0) checkDeadline();
                var children = order[i].Children;
                for (int c = 0; c < children.Count; c++)
                {
                    if (order.Count >= budget) return false;
                    order.Add(children[c]);
                }
            }
            return order.Count <= budget;
        }

        private static bool MatchesLightDeclarations(
            List<SvgElement> order,
            Dictionary<string, string>[] cascaded,
            Dictionary<string, string>[] custom)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (!DeclarationsEqual(order[i].CascadedDeclarations, cascaded[i], false) ||
                    !DeclarationsEqual(order[i].CustomProperties, custom[i], true))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DeclarationsEqual(
            Dictionary<string, string> left,
            Dictionary<string, string> right,
            bool ordinal)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            var comparison = ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out string value) ||
                    !string.Equals(pair.Value, value, comparison))
                {
                    return false;
                }
            }
            return true;
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
                    bool supportedType = string.IsNullOrWhiteSpace(type) ||
                        type.Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase);
                    bool styleMediaApplies = MediaApplies(
                        element.GetAttribute("media"), viewportWidth, report);
                    if (supportedType && styleMediaApplies &&
                        !string.IsNullOrWhiteSpace(element.TextContent))
                    {
                        if (element.TextContent.Contains("@import", StringComparison.OrdinalIgnoreCase))
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
                        // An empty media query list is invalid CSS and every engine
                        // drops the block; the cascade must not apply it either.
                        if (!string.IsNullOrWhiteSpace(media.Condition) &&
                            MediaApplies(media.Condition, viewportWidth, report))
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

        private static bool MediaApplies(string condition, float viewportWidth, SvgParseReport report)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true;
            int start = 0;
            MediaOutcome outcome = MediaOutcome.NoMatch;
            while (true)
            {
                int separator = condition.IndexOf(',', start);
                int end = separator < 0 ? condition.Length : separator;
                MediaOutcome query = MediaQueryApplies(condition, start, end, viewportWidth);
                outcome = MediaOutcome.Or(in outcome, in query);
                if (outcome.Truth == MediaTruth.Match) return true;
                if (separator < 0) break;
                start = separator + 1;
            }
            if (outcome.Truth == MediaTruth.Undetermined)
            {
                report.RequireFallback(outcome.SubjectIsType
                    ? $"SVG media query type '{outcome.Subject}' requires compatibility fallback"
                    : $"SVG media query feature '{outcome.Subject}' requires compatibility fallback");
            }
            return outcome.Truth == MediaTruth.Match;
        }

        private static MediaOutcome MediaQueryApplies(string condition, int start, int end, float viewportWidth)
        {
            int position = SkipMediaWhitespace(condition, start, end);
            if (position >= end) return MediaOutcome.NoMatch;
            bool negated = TryConsumeMediaKeyword(condition, ref position, end, "not");
            if (!negated) TryConsumeMediaKeyword(condition, ref position, end, "only");
            position = SkipMediaWhitespace(condition, position, end);
            if (position >= end) return MediaOutcome.NoMatch;

            MediaOutcome applies;
            if (condition[position] == '(')
            {
                if (negated)
                {
                    int close = FindMediaCloseParen(condition, position, end);
                    if (close < 0 || ContainsMediaLogicalOperator(condition, position + 1, close))
                    {
                        return MediaOutcome.NoMatch;
                    }
                }
                applies = ReadMediaCondition(condition, ref position, end, viewportWidth, true);
            }
            else
            {
                applies = MediaTypeApplies(ReadMediaIdentifier(condition, ref position, end));
                int mark = SkipMediaWhitespace(condition, position, end);
                if (mark < end && TryConsumeMediaKeyword(condition, ref mark, end, "and"))
                {
                    mark = SkipMediaWhitespace(condition, mark, end);
                    MediaOutcome combined = ReadMediaCondition(
                        condition, ref mark, end, viewportWidth, false);
                    if (applies.Truth != MediaTruth.Undetermined)
                    {
                        applies = combined;
                    }
                }
            }
            return negated ? MediaOutcome.Not(in applies) : applies;
        }

        private static MediaOutcome ReadMediaCondition(
            string condition, ref int position, int end, float viewportWidth, bool allowOr)
        {
            int cursor = position;
            MediaOutcome group = ReadMediaAndGroup(condition, ref cursor, end, viewportWidth, out bool valid);
            if (!valid)
            {
                position = cursor;
                return MediaOutcome.NoMatch;
            }
            if (allowOr)
            {
                while (true)
                {
                    int mark = SkipMediaWhitespace(condition, cursor, end);
                    if (mark >= end || !TryConsumeMediaKeyword(condition, ref mark, end, "or"))
                    {
                        break;
                    }
                    MediaOutcome operand = ReadMediaAndGroup(
                        condition, ref mark, end, viewportWidth, out valid);
                    if (!valid)
                    {
                        position = mark;
                        return MediaOutcome.NoMatch;
                    }
                    cursor = mark;
                    group = MediaOutcome.Or(in group, in operand);
                }
            }
            position = cursor;
            return group;
        }

        private static MediaOutcome ReadMediaAndGroup(
            string condition, ref int position, int end, float viewportWidth, out bool valid)
        {
            int cursor = position;
            MediaOutcome result = MediaOutcome.Match;
            valid = false;
            while (true)
            {
                cursor = SkipMediaWhitespace(condition, cursor, end);
                bool negated = TryConsumeMediaKeyword(condition, ref cursor, end, "not");
                cursor = SkipMediaWhitespace(condition, cursor, end);
                if (cursor >= end || condition[cursor] != '(') break;
                int close = FindMediaCloseParen(condition, cursor, end);
                if (close < 0)
                {
                    position = end;
                    return MediaOutcome.NoMatch;
                }
                if (negated && ContainsMediaLogicalOperator(condition, cursor + 1, close))
                {
                    position = end;
                    return MediaOutcome.NoMatch;
                }
                MediaOutcome term = MediaFeatureApplies(condition, cursor + 1, close, viewportWidth);
                cursor = close + 1;
                if (negated) term = MediaOutcome.Not(in term);
                result = valid ? MediaOutcome.And(in result, in term) : term;
                valid = true;
                int mark = SkipMediaWhitespace(condition, cursor, end);
                if (mark >= end || !TryConsumeMediaKeyword(condition, ref mark, end, "and")) break;
                cursor = mark;
            }
            position = cursor;
            return result;
        }

        private static int SkipMediaWhitespace(string condition, int position, int end)
        {
            while (position < end && char.IsWhiteSpace(condition[position])) position++;
            return position;
        }

        private static bool TryConsumeMediaKeyword(string condition, ref int position, int end, string keyword)
        {
            if (position + keyword.Length > end) return false;
            for (int i = 0; i < keyword.Length; i++)
            {
                if (char.ToLowerInvariant(condition[position + i]) != keyword[i]) return false;
            }
            int after = position + keyword.Length;
            if (after < end && (char.IsLetterOrDigit(condition[after]) || condition[after] == '-'))
            {
                return false;
            }
            position = after;
            return true;
        }

        private static string ReadMediaIdentifier(string condition, ref int position, int end)
        {
            int start = position;
            while (position < end &&
                (char.IsLetterOrDigit(condition[position]) || condition[position] == '-' ||
                 condition[position] == '_'))
            {
                position++;
            }
            return position > start ? condition.Substring(start, position - start) : string.Empty;
        }

        private static int FindMediaCloseParen(string condition, int open, int end)
        {
            int depth = 0;
            for (int i = open; i < end; i++)
            {
                char c = condition[i];
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static bool ContainsMediaLogicalOperator(string condition, int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                char c = condition[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (depth == 0 && char.IsWhiteSpace(c))
                {
                    int mark = SkipMediaWhitespace(condition, i, end);
                    if (TryConsumeMediaKeyword(condition, ref mark, end, "and") ||
                        TryConsumeMediaKeyword(condition, ref mark, end, "or"))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static MediaOutcome MediaTypeApplies(string type)
        {
            if (type.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("only", StringComparison.OrdinalIgnoreCase))
            {
                return MediaOutcome.Match;
            }
            if (type.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("screen", StringComparison.OrdinalIgnoreCase))
            {
                return MediaOutcome.Match;
            }
            if (UndeterminedMediaTypes.Contains(type))
            {
                return MediaOutcome.Undetermined(type, true);
            }
            return MediaOutcome.NoMatch;
        }

        private const float PictureDotsPerPixel = 1f;
        private const int PictureColorBits = 8;
        private const int PictureMonochromeBits = 0;

        private static MediaOutcome MediaFeatureApplies(string condition, int start, int end, float viewportWidth)
        {
            int colon = condition.IndexOf(':', start, end - start);
            string name = (colon < 0
                ? condition.Substring(start, end - start)
                : condition.Substring(start, colon - start)).Trim();
            if (name.Length == 0) return MediaOutcome.NoMatch;
            if (colon < 0) return BooleanMediaFeatureApplies(name);
            return RangeMediaFeatureApplies(
                name, condition.Substring(colon + 1, end - colon - 1).Trim(), viewportWidth);
        }

        private static MediaOutcome BooleanMediaFeatureApplies(string name)
        {
            if (name.Equals("color", StringComparison.OrdinalIgnoreCase))
                return PictureColorBits > 0 ? MediaOutcome.Match : MediaOutcome.NoMatch;
            if (name.Equals("monochrome", StringComparison.OrdinalIgnoreCase))
                return PictureMonochromeBits > 0 ? MediaOutcome.Match : MediaOutcome.NoMatch;
            if (BooleanUndeterminedMediaFeatures.Contains(name))
                return MediaOutcome.Undetermined(name, false);
            return MediaOutcome.NoMatch;
        }

        private static MediaOutcome RangeMediaFeatureApplies(string name, string raw, float viewportWidth)
        {
            if (raw.Length == 0) return MediaOutcome.NoMatch;
            bool min = name.StartsWith("min-", StringComparison.OrdinalIgnoreCase);
            bool max = !min && name.StartsWith("max-", StringComparison.OrdinalIgnoreCase);
            string feature = min || max ? name.Substring(4) : name;

            if (feature.Equals("width", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("device-width", StringComparison.OrdinalIgnoreCase))
            {
                // A media feature length must carry a length unit. Percentages and
                // unitless numbers are invalid in a media query, so they leave the
                // whole condition false instead of resolving against the viewport.
                if (!SvgValues.TryParseLength(raw.AsSpan(), out float amount, out var unit) ||
                    !SvgValues.IsFinite(amount) ||
                    unit == SvgValues.SvgUnit.User || unit == SvgValues.SvgUnit.Percent)
                {
                    return MediaOutcome.NoMatch;
                }
                float resolved = SvgValues.ResolveUnits(amount, unit, 16f, viewportWidth);
                if (!SvgValues.IsFinite(resolved)) return MediaOutcome.NoMatch;
                if (min) return viewportWidth >= resolved ? MediaOutcome.Match : MediaOutcome.NoMatch;
                if (max) return viewportWidth <= resolved ? MediaOutcome.Match : MediaOutcome.NoMatch;
                return Math.Abs(viewportWidth - resolved) < 0.01f
                    ? MediaOutcome.Match
                    : MediaOutcome.NoMatch;
            }

            if (feature.Equals("resolution", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("device-pixel-ratio", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseMediaResolution(raw, out float dppx)) return MediaOutcome.NoMatch;
                if (min) return PictureDotsPerPixel >= dppx ? MediaOutcome.Match : MediaOutcome.NoMatch;
                if (max) return PictureDotsPerPixel <= dppx ? MediaOutcome.Match : MediaOutcome.NoMatch;
                return Math.Abs(PictureDotsPerPixel - dppx) < 0.001f
                    ? MediaOutcome.Match
                    : MediaOutcome.NoMatch;
            }

            if (feature.Equals("color", StringComparison.OrdinalIgnoreCase))
            {
                if (!SvgValues.TryParseNumber(raw.AsSpan(), out float colorBits) ||
                    !SvgValues.IsFinite(colorBits))
                {
                    return MediaOutcome.NoMatch;
                }
                if (min) return PictureColorBits >= colorBits ? MediaOutcome.Match : MediaOutcome.NoMatch;
                if (max) return PictureColorBits <= colorBits ? MediaOutcome.Match : MediaOutcome.NoMatch;
                return PictureColorBits == colorBits ? MediaOutcome.Match : MediaOutcome.NoMatch;
            }

            if (feature.Equals("monochrome", StringComparison.OrdinalIgnoreCase))
            {
                if (!SvgValues.TryParseNumber(raw.AsSpan(), out float monoBits) ||
                    !SvgValues.IsFinite(monoBits))
                {
                    return MediaOutcome.NoMatch;
                }
                if (min) return PictureMonochromeBits >= monoBits ? MediaOutcome.Match : MediaOutcome.NoMatch;
                if (max) return PictureMonochromeBits <= monoBits ? MediaOutcome.Match : MediaOutcome.NoMatch;
                return PictureMonochromeBits == monoBits ? MediaOutcome.Match : MediaOutcome.NoMatch;
            }

            if (UndeterminedMediaFeatures.Contains(feature))
                return MediaOutcome.Undetermined(name, false);

            return MediaOutcome.NoMatch;
        }

        private static bool TryParseMediaResolution(string raw, out float dppx)
        {
            dppx = 0f;
            int split = 0;
            while (split < raw.Length && !char.IsLetter(raw[split])) split++;
            if (split == 0 ||
                !SvgValues.TryParseNumber(raw.AsSpan(0, split), out float amount) ||
                !SvgValues.IsFinite(amount))
            {
                return false;
            }
            string unit = raw.Substring(split).ToLowerInvariant();
            if (unit.Length == 0 || unit == "dppx" || unit == "x") dppx = amount;
            else if (unit == "dpi") dppx = amount / 96f;
            else if (unit == "dpcm") dppx = amount * 96f / 2.54f;
            else return false;
            return SvgValues.IsFinite(dppx);
        }

        private static void ApplyToElement(
            SvgElement element,
            IReadOnlyList<RuleEntry> rules,
            RuleIndex ruleIndex,
            SvgParseReport report,
            Action checkDeadline)
        {
            ComputeDeclarations(
                element, rules, ruleIndex, report, checkDeadline, null,
                out Dictionary<string, string> cascaded,
                out Dictionary<string, string> custom);
            if (cascaded != null) element.CascadedDeclarations = cascaded;
            if (custom != null) element.CustomProperties = custom;
        }

        private static void ComputeDeclarations(
            SvgElement element,
            IReadOnlyList<RuleEntry> rules,
            RuleIndex ruleIndex,
            SvgParseReport report,
            Action checkDeadline,
            SvgElement shadowRoot,
            out Dictionary<string, string> cascaded,
            out Dictionary<string, string> custom)
        {
            Dictionary<string, Winner> winners = null;
            Dictionary<string, Winner> customWinners = null;
            var candidates = ruleIndex.GetCandidates(element);
            int candidatePosition = 0;
            foreach (int i in candidates)
            {
                if ((candidatePosition++ & 0x3f) == 0) checkDeadline();
                var entry = rules[i];
                var specificity = GetMatchingSpecificity(
                    element, entry.Rule.Selector, report, shadowRoot);
                if (!specificity.HasValue) continue;

                var declarations = entry.Rule.Declarations;
                for (int d = 0; d < declarations.Count; d++)
                {
                    var declaration = declarations[d];
                    Consider(
                        element,
                        ref winners,
                        ref customWinners,
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
                        element,
                        ref winners,
                        ref customWinners,
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

            if (winners != null)
            {
                cascaded = new Dictionary<string, string>(
                    winners.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in winners)
                    cascaded[pair.Key] = pair.Value.Value;
            }
            else
            {
                cascaded = null;
            }
            if (customWinners != null)
            {
                custom = new Dictionary<string, string>(
                    customWinners.Count, StringComparer.Ordinal);
                foreach (var pair in customWinners)
                    custom[pair.Key] = pair.Value.Value;
            }
            else
            {
                custom = null;
            }
        }

        private static void Consider(
            SvgElement element,
            ref Dictionary<string, Winner> winners,
            ref Dictionary<string, Winner> customWinners,
            CssDeclaration declaration,
            CascadeKey key,
            SvgParseReport report)
        {
            if (declaration == null || string.IsNullOrWhiteSpace(declaration.Property)) return;
            Consider(
                element, ref winners, ref customWinners,
                declaration.Property, declaration.Value, key, report);
        }

        private static void Consider(
            SvgElement element,
            ref Dictionary<string, Winner> winners,
            ref Dictionary<string, Winner> customWinners,
            string rawProperty,
            string rawValue,
            CascadeKey key,
            SvgParseReport report)
        {
            if (string.IsNullOrWhiteSpace(rawProperty)) return;
            string property = rawProperty;
            string value = rawValue?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            bool isCustomProperty = property.StartsWith("--", StringComparison.Ordinal);
            bool isOverflow = property.Equals("overflow", StringComparison.OrdinalIgnoreCase);
            bool supportedViewportOverflow =
                isOverflow && EstablishesViewport(element) &&
                (value.Equals("visible", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("clip", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("auto", StringComparison.OrdinalIgnoreCase));
            bool inertOverflow = isOverflow && !EstablishesViewport(element);
            bool supportedNonScalingStroke =
                property.Equals("vector-effect", StringComparison.OrdinalIgnoreCase) &&
                SvgFeatureSupport.SupportsNonScalingStroke(element, value);
            bool rootCanvasBackground = IsRootCanvasBackground(element, property, value);
            if (!isCustomProperty && IsInertBoxProperty(element, property))
            {
                if (SvgFeatureSupport.HasExternalUrlReference(value))
                {
                    report.RejectResource("SVG CSS external resource reference rejected");
                }
                return;
            }
            bool isFontShorthand = !isCustomProperty &&
                property.Equals("font", StringComparison.OrdinalIgnoreCase);
            // Single-line text layout honours white-space as space preservation in the
            // text style, so the declaration is kept like any supported property.
            bool singleLineWhiteSpace = report.TextAreasOnOneLine &&
                property.Equals("white-space", StringComparison.OrdinalIgnoreCase);
            if (!isCustomProperty && !isFontShorthand && !SupportedProperties.Contains(property) &&
                !supportedViewportOverflow && !inertOverflow && !supportedNonScalingStroke &&
                !singleLineWhiteSpace && !rootCanvasBackground && !IsNoEffectProperty(property, value))
            {
                if (EmbeddingOnlyProperties.Contains(property)) return;
                // Single-line text layout drops the area properties: they have no effect.
                if (report.TextAreasOnOneLine && SingleLineAreaProperties.Contains(property)) return;
                if (NoneIsNoEffect.Contains(property) &&
                    value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                report.RequireFallback(UnsupportedPropertyReason(property));
                return;
            }
            if (isFontShorthand)
            {
                var status = TryExpandFontShorthand(value, out var fontLonghands);
                if (status == FontShorthandStatus.Unsupported)
                {
                    report.RequireFallback(
                        "SVG CSS property 'font' shorthand value does not expand to the font longhands " +
                        "the text model resolves; requires compatibility fallback");
                }
                else if (status == FontShorthandStatus.Expanded)
                {
                    for (int i = 0; i < fontLonghands.Length; i++)
                    {
                        Consider(
                            element, ref winners, ref customWinners,
                            fontLonghands[i].Property, fontLonghands[i].Value, key, report);
                    }
                }
                return;
            }
            if (SvgFeatureSupport.HasExternalUrlReference(value))
            {
                report.RejectResource("SVG CSS external resource reference rejected");
                return;
            }
            if (!isCustomProperty && IsUnrenderableTextValue(property, value))
            {
                report.RequireFallback($"SVG CSS property '{property}' requires compatibility fallback");
                return;
            }
            if (!isCustomProperty &&
                property.Equals("unicode-bidi", StringComparison.OrdinalIgnoreCase) &&
                SvgFeatureSupport.IsUnimplementedUnicodeBidi(value))
            {
                report.RequireFallback(
                    $"SVG CSS property '{property}' cannot be honoured for value '{value}' because " +
                    (SvgFeatureSupport.CarriesIsolateKeyword(value)
                        ? "it isolates the run sequence from the surrounding paragraph and the text " +
                          "model computes no isolating run sequence"
                        : "the text model resolves no frame or paragraph level for a value that " +
                          "combines keywords") +
                    "; requires compatibility fallback");
                return;
            }
            if (!isCustomProperty && IsDefinitelyInvalid(property, value))
                return;

            if (isCustomProperty)
            {
                customWinners ??= new Dictionary<string, Winner>(StringComparer.Ordinal);
                if (!customWinners.TryGetValue(property, out var customCurrent) ||
                    key.CompareTo(customCurrent.Key) >= 0)
                    customWinners[property] = new Winner(value, key);
                return;
            }

            winners ??= new Dictionary<string, Winner>(StringComparer.OrdinalIgnoreCase);
            if (!winners.TryGetValue(property, out var current) || key.CompareTo(current.Key) >= 0)
            {
                winners[property] = new Winner(value, key);
            }
        }

        private static string UnsupportedPropertyReason(string property) =>
            UnimplementedCssCapabilities.TryGetValue(property, out string missingCapability)
                ? $"SVG CSS property '{property}' cannot be honoured because it {missingCapability}; " +
                    "requires compatibility fallback"
                : $"SVG CSS property '{property}' requires compatibility fallback";

        private static bool EstablishesViewport(SvgElement element) =>
            element.Name is "svg" or "marker";

        /// <summary>
        /// The root svg element's background paints the document canvas (CSS Backgrounds 3
        /// §2.11.2). A plain colour is honoured, from background-color or a background
        /// shorthand that is only a colour; images and other layers are not modelled.
        /// </summary>
        private static bool IsRootCanvasBackground(SvgElement element, string property, string value) =>
            element.Parent == null &&
            string.Equals(element.Name, "svg", StringComparison.Ordinal) &&
            (property.Equals("background-color", StringComparison.OrdinalIgnoreCase) ||
             property.Equals("background", StringComparison.OrdinalIgnoreCase)) &&
            SvgValues.TryParseColor(value.AsSpan(), out _);

        private static bool IsInertBoxProperty(SvgElement element, string property) =>
            InertBoxProperties.Contains(property) &&
            (property.Equals("content", StringComparison.OrdinalIgnoreCase) || !EstablishesViewport(element));

        private static bool IsNoEffectProperty(string property, string value)
        {
            if (property.Equals("shape-rendering", StringComparison.OrdinalIgnoreCase))
            {
                return value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("optimizeSpeed", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("geometricPrecision", StringComparison.OrdinalIgnoreCase);
            }
            if (property.Equals("image-rendering", StringComparison.OrdinalIgnoreCase))
            {
                return value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("smooth", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("high-quality", StringComparison.OrdinalIgnoreCase);
            }
            if (property.Equals("font-variant", StringComparison.OrdinalIgnoreCase))
            {
                return IsCssWideValue(value) || SvgFeatureSupport.IsInertFontVariant(value);
            }
            if (property.Equals("font-stretch", StringComparison.OrdinalIgnoreCase))
            {
                return IsCssWideValue(value) || SvgFeatureSupport.IsInertFontStretch(value);
            }
            if (property.Equals("font-size-adjust", StringComparison.OrdinalIgnoreCase))
            {
                return IsCssWideValue(value) ||
                       value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("from-font", StringComparison.OrdinalIgnoreCase);
            }
            if (property.Equals("line-height", StringComparison.OrdinalIgnoreCase))
            {
                return IsCssWideValue(value) || IsNoEffectLineHeight(value);
            }
            return NoEffectProperties.Contains(property);
        }

        private static bool IsNoEffectLineHeight(string value) =>
            value.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
            SvgValues.TryParseNumber(value.AsSpan(), out float number) && SvgValues.IsFinite(number) ||
            SvgCssLengthEvaluator.TryEvaluate(value, 1f, 16f, 16f, 1f, 1f, out _);

        private static bool IsCssWideValue(string value) =>
            value.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("revert", StringComparison.OrdinalIgnoreCase);

        private static bool IsUnrenderableTextValue(string property, string value)
        {
            if (property.Equals("font-style", StringComparison.OrdinalIgnoreCase))
            {
                return IsObliqueWithAngle(value);
            }
            if (property.Equals("font-size", StringComparison.OrdinalIgnoreCase) ||
                property.Equals("letter-spacing", StringComparison.OrdinalIgnoreCase))
            {
                return SvgCssLengthEvaluator.RequiresUnsupportedUnitSupport(value);
            }
            return false;
        }

        private static bool IsObliqueWithAngle(string value)
        {
            ReadOnlySpan<char> span = value.AsSpan().Trim();
            if (!span.StartsWith("oblique".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            span = span.Slice("oblique".Length).TrimStart();
            if (span.IsEmpty) return false;
            return span.Equals("from-font".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   IsAngle(span);
        }

        private static bool IsAngle(ReadOnlySpan<char> value)
        {
            int cut = 0;
            while (cut < value.Length &&
                   (char.IsDigit(value[cut]) || value[cut] is '.' or '+' or '-' or 'e' or 'E'))
            {
                cut++;
            }
            if (cut == 0) return false;
            ReadOnlySpan<char> unit = value.Slice(cut);
            return unit.Equals("deg".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("grad".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("rad".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                   unit.Equals("turn".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDefinitelyInvalid(string property, string value)
        {
            if (string.IsNullOrWhiteSpace(property) || string.IsNullOrWhiteSpace(value))
                return true;

            string v = value.Trim();
            if (v.Length == 0) return true;
            if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
                v.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            switch (property.ToLowerInvariant())
            {
                case "color":
                    return !v.Equals("currentColor", StringComparison.OrdinalIgnoreCase) &&
                        !SvgValues.TryParseColor(v.AsSpan(), out _);
                case "fill":
                case "stroke":
                    return !IsValidPaintValue(v);
                case "opacity":
                case "fill-opacity":
                case "stroke-opacity":
                case "flood-opacity":
                case "stop-opacity":
                    return !TryEvaluateNumericValue(v);
                case "stroke-width":
                    return !TryEvaluateLengthValue(v) && !TryEvaluateMathValue(v);
                case "stroke-miterlimit":
                    return !TryEvaluateNumericValue(v) && !TryEvaluateMathValue(v);
                case "stroke-dasharray":
                    return !IsValidDashValue(v);
                case "stroke-linecap":
                    return !v.Equals("butt", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("round", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("square", StringComparison.OrdinalIgnoreCase);
                case "stroke-linejoin":
                    return !v.Equals("miter", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("round", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("bevel", StringComparison.OrdinalIgnoreCase);
                case "clip-rule":
                case "fill-rule":
                    return !v.Equals("nonzero", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("evenodd", StringComparison.OrdinalIgnoreCase);
                case "visibility":
                    return !v.Equals("visible", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("hidden", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("collapse", StringComparison.OrdinalIgnoreCase);
                case "clip-path":
                case "mask":
                case "filter":
                case "marker":
                case "marker-start":
                case "marker-mid":
                case "marker-end":
                    return v.StartsWith("url(", StringComparison.OrdinalIgnoreCase) &&
                        !IsReferenceOrNoneValue(v);
                case "x":
                case "y":
                case "width":
                case "height":
                case "cx":
                case "cy":
                case "r":
                case "rx":
                case "ry":
                case "x1":
                case "x2":
                case "y1":
                case "y2":
                    return IsInvalidGeometryValue(property, v);
                case "transform":
                    return IsInvalidTransformValue(v);
                case "d":
                    return v.Equals("none", StringComparison.OrdinalIgnoreCase)
                        ? false
                        : v.StartsWith("path(", StringComparison.OrdinalIgnoreCase) &&
                          !HasBalancedParentheses(v);
                case "font-size":
                    return !TryEvaluateLengthValue(v) && !TryEvaluateMathValue(v) &&
                        !IsFontSizeKeyword(v);
                case "font-weight":
                    return !IsFontWeightValue(v.AsSpan());
                case "font-style":
                    return !v.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("italic", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("oblique", StringComparison.OrdinalIgnoreCase);
                case "text-anchor":
                    return !v.Equals("start", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("middle", StringComparison.OrdinalIgnoreCase) &&
                        !v.Equals("end", StringComparison.OrdinalIgnoreCase);
                case "paint-order":
                    return !IsValidPaintOrder(v);
                case "stroke-dashoffset":
                    return !TryEvaluateLengthValue(v) && !TryEvaluateMathValue(v);
                case "letter-spacing":
                    return !v.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                        !TryEvaluateLengthValue(v) && !TryEvaluateMathValue(v);
                default:
                    return false;
            }
        }

        private static bool IsValidPaintValue(string value)
        {
            if (value.Equals("context-fill", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("context-stroke", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!SvgValues.TryParsePaint(
                    value.AsSpan(), out var kind, out _, out _, out string fallback))
                return false;
            if (kind != SvgValues.PaintKind.ServerRef || string.IsNullOrWhiteSpace(fallback))
                return true;
            return fallback.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                fallback.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ||
                SvgValues.TryParseColor(fallback.AsSpan(), out _);
        }

        private static bool TryEvaluateNumericValue(string value)
        {
            return SvgValues.TryParseNumber(value.AsSpan(), out float number) &&
                SvgValues.IsFinite(number);
        }

        private static bool TryEvaluateLengthValue(string value)
        {
            return SvgValues.TryParseLength(value.AsSpan(), out float length, out _) &&
                SvgValues.IsFinite(length);
        }

        private static bool TryEvaluateMathValue(string value) =>
            SvgCssLengthEvaluator.TryEvaluate(value, 1f, 16f, 16f, 1f, 1f, out _);

        private static bool IsFontSizeKeyword(ReadOnlySpan<char> value) =>
            value.Equals("xx-small".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("x-small".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("small".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("medium".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("large".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("x-large".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("xx-large".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("smaller".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("larger".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsFontWeightKeyword(string value) =>
            value.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("bold", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("bolder", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("lighter", StringComparison.OrdinalIgnoreCase);

        private static bool IsFontStyleKeyword(ReadOnlySpan<char> value) =>
            value.Equals("normal".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("italic".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("oblique".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsFontSizeValue(ReadOnlySpan<char> value) =>
            !value.IsEmpty && (IsFontSizeKeyword(value) ||
                SvgValues.TryParseLength(value, out float length, out _) && SvgValues.IsFinite(length) ||
                SvgCssLengthEvaluator.TryEvaluate(value.ToString(), 1f, 16f, 16f, 1f, 1f, out _));

        private static bool IsFontWeightValue(ReadOnlySpan<char> value)
        {
            if (IsFontWeightKeyword(value.ToString())) return true;
            return SvgValues.TryParseNumber(value, out float weight) &&
                   SvgValues.IsFinite(weight) && weight >= 1f && weight <= 1000f;
        }

        private static bool IsFontStretchValue(ReadOnlySpan<char> value) =>
            value.Equals("normal".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("100%".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsSystemFontKeyword(ReadOnlySpan<char> value) =>
            value.Equals("caption".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("icon".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("menu".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("message-box".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("small-caption".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("status-bar".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsFontFamilyName(ReadOnlySpan<char> entry)
        {
            entry = entry.Trim();
            if (entry.Length < 2) return false;
            if (entry[0] is '"' or '\'')
            {
                if (entry[entry.Length - 1] != entry[0]) return false;
                for (int i = 1; i < entry.Length - 1; i++)
                {
                    if (entry[i] == entry[0] || entry[i] == '\\') return false;
                }
                return true;
            }
            if (char.IsAsciiDigit(entry[0]) ||
                (entry[0] == '-' && entry.Length > 1 && char.IsAsciiDigit(entry[1])))
            {
                return false;
            }
            foreach (char c in entry)
            {
                if (c is '-' or '_' or '.') continue;
                if (char.IsLetterOrDigit(c) || c > 0x7f) continue;
                return false;
            }
            return true;
        }

        private static bool IsFontFamilyList(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty) return false;
            int start = 0;
            int entries = 0;
            while (start <= value.Length)
            {
                ReadOnlySpan<char> rest = value.Slice(start);
                int comma = rest.IndexOf(',');
                ReadOnlySpan<char> entry = comma < 0 ? rest : rest.Slice(0, comma);
                if (!IsFontFamilyName(entry)) return false;
                if (++entries > MaxFontFamilyEntries) return false;
                if (comma < 0) return true;
                start += comma + 1;
            }
            return false;
        }

        private enum FontShorthandStatus : byte
        {
            Expanded,
            Invalid,
            Unsupported
        }

        private static FontShorthandStatus TryExpandFontShorthand(
            string value,
            out FontLonghand[] longhands)
        {
            longhands = null;
            ReadOnlySpan<char> span = value.AsSpan().Trim();
            if (span.IsEmpty) return FontShorthandStatus.Invalid;
            if (IsCssWideValue(value.Trim()) || IsSystemFontKeyword(span))
            {
                return FontShorthandStatus.Unsupported;
            }
            if (span.IndexOfAny('(', ')') >= 0)
            {
                return FontShorthandStatus.Unsupported;
            }

            string style = null;
            string variant = null;
            string weight = null;
            string stretch = null;
            int position = 0;
            int tokens = 0;

            while (true)
            {
                int mark = position;
                ReadOnlySpan<char> token = NextFontToken(span, ref position, ref tokens);
                if (token.IsEmpty)
                {
                    position = mark;
                    break;
                }
                if (token.Equals("normal".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (style == null) style = "normal";
                    else if (variant == null) variant = "normal";
                    else if (weight == null) weight = "normal";
                    else if (stretch == null) stretch = "normal";
                    else return FontShorthandStatus.Invalid;
                    continue;
                }
                if (IsFontStyleKeyword(token))
                {
                    if (style != null) return FontShorthandStatus.Invalid;
                    style = token.ToString();
                    if (style.Equals("oblique", StringComparison.OrdinalIgnoreCase))
                    {
                        int afterStyle = position;
                        ReadOnlySpan<char> next = NextFontToken(span, ref position, ref tokens);
                        if (next.Equals("from-font".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                            IsAngle(next))
                        {
                            return FontShorthandStatus.Unsupported;
                        }
                        position = afterStyle;
                    }
                }
                else if (token.Equals("small-caps".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (variant != null) return FontShorthandStatus.Invalid;
                    variant = token.ToString();
                }
                else if (IsFontWeightValue(token))
                {
                    if (weight != null) return FontShorthandStatus.Invalid;
                    weight = token.ToString();
                }
                else if (IsFontStretchKeyword(token))
                {
                    if (stretch != null) return FontShorthandStatus.Invalid;
                    stretch = token.ToString();
                }
                else if (token.Equals("none".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                         token.Equals("from-font".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                         SvgValues.TryParseNumber(token, out _))
                {
                    return FontShorthandStatus.Unsupported;
                }
                else
                {
                    position = mark;
                    break;
                }
            }

            ReadOnlySpan<char> sizeToken = NextFontToken(span, ref position, ref tokens);
            if (sizeToken.IsEmpty) return FontShorthandStatus.Invalid;
            string lineHeight = null;
            int slash = sizeToken.IndexOf('/');
            if (slash >= 0)
            {
                lineHeight = sizeToken.Slice(slash + 1).ToString();
                sizeToken = sizeToken.Slice(0, slash);
                if (!IsNoEffectLineHeight(lineHeight)) return FontShorthandStatus.Invalid;
            }
            if (!IsFontSizeValue(sizeToken)) return FontShorthandStatus.Invalid;

            SkipFontWhitespace(span, ref position);
            if (position >= span.Length)
            {
                // <'font-family'># is not optional in the font shorthand grammar, so a
                // value that stops after the size does not parse at all. The canonical
                // CSS cascade drops it for the same reason, and every engine drops it,
                // so the declaration is discarded rather than routed to a fallback: the
                // document paints at the inherited size, which is what an engine that
                // dropped the declaration paints.
                return FontShorthandStatus.Invalid;
            }
            ReadOnlySpan<char> family = span.Slice(position);
            if (!IsFontFamilyList(family)) return FontShorthandStatus.Invalid;
            if (variant != null && !variant.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                return FontShorthandStatus.Unsupported;
            }
            if (stretch != null && !IsFontStretchValue(stretch.AsSpan()))
            {
                return FontShorthandStatus.Unsupported;
            }
            if (tokens > MaxFontShorthandTokens) return FontShorthandStatus.Unsupported;

            var expanded = new FontLonghand[7];
            int count = 0;
            expanded[count++] = new FontLonghand("font-style", style ?? "normal");
            if (variant != null) expanded[count++] = new FontLonghand("font-variant", variant);
            expanded[count++] = new FontLonghand("font-weight", weight ?? "normal");
            if (stretch != null) expanded[count++] = new FontLonghand("font-stretch", stretch);
            expanded[count++] = new FontLonghand("font-size", sizeToken.ToString());
            if (lineHeight != null) expanded[count++] = new FontLonghand("line-height", lineHeight);
            expanded[count++] = new FontLonghand("font-family", family.ToString());
            if (count != expanded.Length) Array.Resize(ref expanded, count);
            longhands = expanded;
            return FontShorthandStatus.Expanded;
        }

        private static bool IsFontStretchKeyword(ReadOnlySpan<char> value) =>
            IsFontStretchValue(value) ||
            value.Equals("ultra-condensed".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("extra-condensed".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("condensed".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("semi-condensed".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("semi-expanded".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("expanded".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("extra-expanded".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ultra-expanded".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            SvgValues.TryParseLength(value, out float percent, out var percentUnit) &&
                percentUnit == SvgValues.SvgUnit.Percent;

        private static void SkipFontWhitespace(ReadOnlySpan<char> value, ref int position)
        {
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
        }

        private static ReadOnlySpan<char> NextFontToken(
            ReadOnlySpan<char> value,
            ref int position,
            ref int tokens)
        {
            SkipFontWhitespace(value, ref position);
            if (position >= value.Length) return ReadOnlySpan<char>.Empty;
            int start = position;
            while (position < value.Length && !char.IsWhiteSpace(value[position])) position++;
            tokens++;
            return value.Slice(start, position - start);
        }

        private static bool IsReferenceOrNoneValue(string value)
        {
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return false;
            int close = value.LastIndexOf(')');
            return close > 4;
        }

        private static bool IsValidPaintOrder(string value)
        {
            if (value.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("initial", StringComparison.OrdinalIgnoreCase))
                return true;
            var tokenizer = SvgValues.CreateTokenizer(value.AsSpan());
            bool fill = false;
            bool stroke = false;
            bool markers = false;
            while (tokenizer.Next(out var token))
            {
                if (token.Equals("fill".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (fill) return false;
                    fill = true;
                }
                else if (token.Equals("stroke".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (stroke) return false;
                    stroke = true;
                }
                else if (token.Equals("markers".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    if (markers) return false;
                    markers = true;
                }
                else
                {
                    return false;
                }
            }
            return fill || stroke || markers;
        }

        private static bool IsValidDashValue(string value)
        {
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            var tokenizer = SvgValues.CreateTokenizer(value.AsSpan());
            bool any = false;
            while (tokenizer.Next(out var token))
            {
                if (!SvgValues.TryParseLength(token, out float dash, out _) ||
                    dash < 0f || !SvgValues.IsFinite(dash))
                    return false;
                any = true;
            }
            return any;
        }

        private static bool IsInvalidGeometryValue(string property, string value)
        {
            if (SvgCssLengthEvaluator.IsNestedSvgSizingKeyword(value) ||
                value.StartsWith("calc-size(", StringComparison.OrdinalIgnoreCase))
                return false;
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return true;
            if (SvgValues.TryParseLength(value.AsSpan(), out float length, out var unit))
            {
                if (!SvgValues.IsFinite(length)) return true;
                if (unit == SvgValues.SvgUnit.User && length != 0f) return true;
                return false;
            }
            if (SvgCssLengthEvaluator.RequiresUnsupportedUnitSupport(value)) return false;
            if (SvgCssLengthEvaluator.TryEvaluate(value, 1f, 16f, 16f, 1f, 1f, out _))
                return false;
            return true;
        }

        private static bool IsInvalidTransformValue(string value)
        {
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            bool function = false;
            int depth = 0;
            foreach (char c in value)
            {
                if (c == '(')
                {
                    function = true;
                    depth++;
                    if (depth > 16) return true;
                }
                else if (c == ')')
                {
                    if (depth == 0) return true;
                    depth--;
                }
            }
            return !function || depth != 0;
        }

        private static bool HasBalancedParentheses(string value)
        {
            int depth = 0;
            foreach (char c in value)
            {
                if (c == '(') depth++;
                else if (c == ')' && --depth < 0) return false;
            }
            return depth == 0;
        }

        private static Specificity? GetMatchingSpecificity(
            SvgElement element,
            CssSelector selector,
            SvgParseReport report,
            SvgElement shadowRoot = null)
        {
            if (selector?.Chains == null) return null;
            Specificity? best = null;
            for (int i = 0; i < selector.Chains.Count; i++)
            {
                var chain = selector.Chains[i];
                SelectorSupport support = ClassifyChain(element, chain, 0);
                if (support == SelectorSupport.Unsupported)
                {
                    report.RequireFallback(UnsupportedSelectorReason(element, chain));
                    continue;
                }
                if (support == SelectorSupport.NotApplicable) continue;
                if (MatchesChain(element, chain, chain.Segments.Count - 1, 0, shadowRoot) &&
                    (!best.HasValue || chain.Specificity.CompareTo(best.Value) > 0))
                {
                    best = chain.Specificity;
                }
            }
            return best;
        }

        private static SvgElement MatchedParent(SvgElement element, SvgElement shadowRoot) =>
            ReferenceEquals(element, shadowRoot) ? null : element.Parent;

        private static SvgElement MatchedPreviousSibling(SvgElement element, SvgElement shadowRoot) =>
            ReferenceEquals(element, shadowRoot) ? null : element.PreviousElementSibling;

        private static SelectorSupport ClassifyChain(
            SvgElement element, SelectorChain chain, int depth, bool firstLetter = false)
        {
            if (chain == null || chain.Segments.Count == 0 || depth > MaxSelectorMatchDepth)
            {
                return SelectorSupport.Unsupported;
            }
            bool firstLetterChain = IsFirstLetterChain(chain);
            foreach (var segment in chain.Segments)
            {
                if (segment.PseudoElements.Count > 0 && !(firstLetter && firstLetterChain))
                {
                    // ::first-letter styles a synthetic element (ApplyFirstLetter), never
                    // the element itself; ::first-line is not modelled.
                    if (firstLetterChain) return SelectorSupport.NotApplicable;
                    return HasPaintedPseudoElement(segment) && HostsStyledText(element)
                        ? SelectorSupport.Unsupported
                        : SelectorSupport.NotApplicable;
                }
                foreach (var pseudo in segment.PseudoClasses)
                {
                    switch (pseudo.Name)
                    {
                        case "root":
                        case "scope":
                        case "empty":
                        case "first-child":
                        case "last-child":
                        case "only-child":
                        case "first-of-type":
                        case "last-of-type":
                        case "only-of-type":
                        case "lang":
                            break;
                        case "nth-child":
                        case "nth-last-child":
                        case "nth-of-type":
                        case "nth-last-of-type":
                            if (ClassifyNthArguments(element, pseudo, depth) == SelectorSupport.Unsupported)
                            {
                                return SelectorSupport.Unsupported;
                            }
                            break;
                        case "is":
                        case "where":
                        case "not":
                            if (ClassifyArguments(element, pseudo, depth + 1) == SelectorSupport.Unsupported)
                            {
                                return SelectorSupport.Unsupported;
                            }
                            break;
                        default:
                            if (BaseStatePseudoClasses.Contains(pseudo.Name))
                            {
                                return SelectorSupport.NotApplicable;
                            }
                            return SelectorSupport.Unsupported;
                    }
                }
            }
            return SelectorSupport.Matched;
        }

        private static bool HasPaintedPseudoElement(SelectorSegment segment)
        {
            foreach (var pseudo in segment.PseudoElements)
            {
                if (PaintedPseudoElements.Contains(pseudo.Name)) return true;
            }
            return false;
        }

        private static bool HostsStyledText(SvgElement element) =>
            element != null && element.Name is "text" or "tspan" or "textPath";

        private static string UnsupportedSelectorReason(SvgElement element, SelectorChain chain)
        {
            // A painted pseudo-element on a text host is the one rejected selector whose
            // missing piece is nameable: it repaints part of a run, and naming which
            // pseudo-element and which text model gap is what separates it from an
            // unknown pseudo-class the cascade simply cannot answer.
            if (HostsStyledText(element))
            {
                foreach (var segment in chain.Segments)
                {
                    if (!HasPaintedPseudoElement(segment)) continue;
                    return
                        $"SVG CSS pseudo-element '::{PaintedPseudoElementName(segment)}' repaints a chunk of " +
                        "the first formatted line; requires compatibility fallback";
                }
            }
            return "SVG unsupported selector requires compatibility fallback";
        }

        private static string PaintedPseudoElementName(SelectorSegment segment)
        {
            foreach (var pseudo in segment.PseudoElements)
            {
                if (PaintedPseudoElements.Contains(pseudo.Name)) return pseudo.Name;
            }
            return string.Empty;
        }

        private static SelectorSupport ClassifyArguments(SvgElement element, PseudoSelector pseudo, int depth)
        {
            var arguments = pseudo.ParsedArgsOrNull;
            if (arguments == null) return SelectorSupport.Matched;
            foreach (var chain in arguments)
            {
                if (ClassifyChain(element, chain, depth + 1) == SelectorSupport.Unsupported)
                {
                    return SelectorSupport.Unsupported;
                }
            }
            return SelectorSupport.Matched;
        }

        private static SelectorSupport ClassifyNthArguments(SvgElement element, PseudoSelector pseudo, int depth)
        {
            if (!TryParseNthArguments(pseudo.Args, out string formula, out string ofSelector) ||
                !TryParseNthFormula(formula, out _, out _))
            {
                return SelectorSupport.Unsupported;
            }
            bool hasFilter = !string.IsNullOrWhiteSpace(ofSelector);
            bool hasParsedFilter = pseudo.ParsedArgsOrNull != null && pseudo.ParsedArgsOrNull.Count > 0;
            if (hasFilter != hasParsedFilter) return SelectorSupport.Unsupported;
            if (!hasFilter) return SelectorSupport.Matched;
            return ClassifyArguments(element, pseudo, depth + 1);
        }

        private static bool TryParseNthArguments(string args, out string formula, out string ofSelector)
        {
            formula = null;
            ofSelector = null;
            if (string.IsNullOrWhiteSpace(args)) return false;

            int depth = 0;
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']')
                {
                    if (depth > 0) depth--;
                }
                else if (depth == 0 && char.IsWhiteSpace(c) && IsNthOfKeyword(args, i))
                {
                    formula = args.Substring(0, i).Trim();
                    int start = i + 1;
                    while (start < args.Length && char.IsWhiteSpace(args[start])) start++;
                    ofSelector = args.Substring(start).Trim();
                    break;
                }
            }
            formula ??= args.Trim();
            return ofSelector == null || ofSelector.Length > 0;
        }

        private static bool IsNthOfKeyword(string args, int whitespaceIndex)
        {
            int start = whitespaceIndex + 1;
            if (start + 1 >= args.Length) return false;
            if (args[start] != 'o' || args[start + 1] != 'f') return false;
            int after = start + 2;
            return after >= args.Length || char.IsWhiteSpace(args[after]);
        }

        private static bool TryParseNthFormula(string formula, out int a, out int b)
        {
            a = 0;
            b = 0;
            string text = CompactNthFormula(formula);
            if (text.Length == 0) return false;
            if (text == "odd") { a = 2; b = 1; return true; }
            if (text == "even") { a = 2; b = 0; return true; }

            int n = text.IndexOf('n');
            if (n < 0)
            {
                return TryParseNthInteger(text, 0, text.Length, out b);
            }

            string coefficient = text.Substring(0, n);
            if (coefficient.Length == 0 || coefficient == "+") a = 1;
            else if (coefficient == "-") a = -1;
            else if (!TryParseNthInteger(coefficient, 0, coefficient.Length, out a)) return false;

            string rest = text.Substring(n + 1);
            if (rest.Length == 0) { b = 0; return true; }
            if (rest[0] != '+' && rest[0] != '-') return false;
            if (!TryParseNthInteger(rest, 0, rest.Length, out b)) return false;
            if (rest[0] == '-') b = -b;
            return true;
        }

        private static string CompactNthFormula(string formula)
        {
            int start = 0;
            int end = formula.Length;
            while (start < end && char.IsWhiteSpace(formula[start])) start++;
            while (end > start && char.IsWhiteSpace(formula[end - 1])) end--;
            bool clean = start == 0 && end == formula.Length;
            for (int i = start; i < end && clean; i++) clean = !char.IsWhiteSpace(formula[i]);
            if (clean) return formula.ToLowerInvariant();

            var builder = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(formula[i])) builder.Append(char.ToLowerInvariant(formula[i]));
            }
            return builder.ToString();
        }

        private static bool TryParseNthInteger(string text, int start, int end, out int value)
        {
            value = 0;
            if (start >= end || end - start > 9) return false;
            int index = start;
            bool negative = false;
            if (text[index] == '+' || text[index] == '-')
            {
                negative = text[index] == '-';
                index++;
            }
            if (index >= end) return false;
            int accumulated = 0;
            for (; index < end; index++)
            {
                char c = text[index];
                if (c < '0' || c > '9') return false;
                accumulated = accumulated * 10 + (c - '0');
            }
            value = negative ? -accumulated : accumulated;
            return true;
        }

        private static bool MatchesNthIndex(int a, int b, int index)
        {
            if (index <= 0) return false;
            if (a == 0) return index == b;
            int offset = index - b;
            if (a > 0) return offset >= 0 && offset % a == 0;
            return offset <= 0 && -offset % -a == 0;
        }

        private static bool MatchesNthPseudo(
            SvgElement element, PseudoSelector pseudo, int depth, bool ofType, bool fromEnd, SvgElement shadowRoot)
        {
            if (!TryParseNthArguments(pseudo.Args, out string formula, out string ofSelector) ||
                !TryParseNthFormula(formula, out int a, out int b))
            {
                return false;
            }
            var filter = pseudo.ParsedArgsOrNull;
            bool hasFilter = filter != null && filter.Count > 0;
            if (!string.IsNullOrWhiteSpace(ofSelector) != hasFilter) return false;
            var parent = MatchedParent(element, shadowRoot);
            if (parent == null) return false;

            int count = 0;
            int index = 0;
            var siblings = parent.Children;
            for (int step = 0; step < siblings.Count; step++)
            {
                var sibling = siblings[fromEnd ? siblings.Count - 1 - step : step];
                if (ofType && !string.Equals(sibling.Name, element.Name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (hasFilter && !MatchesAny(sibling, filter, depth, shadowRoot)) continue;
                count++;
                if (ReferenceEquals(sibling, element))
                {
                    index = count;
                    break;
                }
            }
            return MatchesNthIndex(a, b, index);
        }

        private static bool MatchesChain(
            SvgElement element,
            SelectorChain chain,
            int index,
            int depth,
            SvgElement shadowRoot)
        {
            if (element == null || chain == null || index < 0 || depth > MaxSelectorMatchDepth) return false;
            var segment = chain.Segments[index];
            if (!MatchesSegment(element, segment, depth + 1, shadowRoot)) return false;
            if (index == 0) return true;

            switch (segment.Combinator)
            {
                case '>':
                    return MatchesChain(
                        MatchedParent(element, shadowRoot), chain, index - 1, depth + 1, shadowRoot);
                case '+':
                    return MatchesChain(
                        MatchedPreviousSibling(element, shadowRoot), chain, index - 1, depth + 1, shadowRoot);
                case '~':
                    for (var sibling = MatchedPreviousSibling(element, shadowRoot);
                         sibling != null;
                         sibling = sibling.PreviousElementSibling)
                    {
                        if (MatchesChain(sibling, chain, index - 1, depth + 1, shadowRoot)) return true;
                    }
                    return false;
                default:
                    // A use-element shadow tree is its own tree: the clone root is
                    // the referenced element and its parent is the <use> element, so
                    // an ancestor walk must stop there instead of leaking into the
                    // source tree that owns the referenced element.
                    for (var ancestor = MatchedParent(element, shadowRoot);
                         ancestor != null;
                         ancestor = ReferenceEquals(ancestor, shadowRoot) ? null : ancestor.Parent)
                    {
                        if (MatchesChain(ancestor, chain, index - 1, depth + 1, shadowRoot)) return true;
                    }
                    return false;
            }
        }

        private static bool MatchesSegment(
            SvgElement element, SelectorSegment segment, int depth, SvgElement shadowRoot)
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
                if (!MatchesPseudo(element, pseudo, depth + 1, shadowRoot)) return false;
            }
            return true;
        }

        private static bool MatchesPseudo(
            SvgElement element, PseudoSelector pseudo, int depth, SvgElement shadowRoot)
        {
            switch (pseudo.Name)
            {
                case "root": return shadowRoot == null && element.Parent == null;
                // A document-level stylesheet has no style scoping root of its own, so
                // :scope resolves to the document element, exactly like :root.
                case "scope": return shadowRoot == null && element.Parent == null;
                case "empty": return element.Children.Count == 0 && string.IsNullOrEmpty(element.TextContent);
                case "first-child": return MatchedPreviousSibling(element, shadowRoot) == null;
                case "last-child":
                    {
                        var parent = MatchedParent(element, shadowRoot);
                        return parent == null || ReferenceEquals(parent.Children[^1], element);
                    }
                case "only-child":
                    {
                        var parent = MatchedParent(element, shadowRoot);
                        return parent == null || parent.Children.Count == 1;
                    }
                case "first-of-type": return !HasPreviousOfType(element, shadowRoot);
                case "last-of-type": return !HasNextOfType(element, shadowRoot);
                case "only-of-type":
                    return !HasPreviousOfType(element, shadowRoot) && !HasNextOfType(element, shadowRoot);
                case "lang": return MatchesLanguage(element, pseudo.Args, depth + 1, shadowRoot);
                case "nth-child": return MatchesNthPseudo(element, pseudo, depth, false, false, shadowRoot);
                case "nth-last-child": return MatchesNthPseudo(element, pseudo, depth, false, true, shadowRoot);
                case "nth-of-type": return MatchesNthPseudo(element, pseudo, depth, true, false, shadowRoot);
                case "nth-last-of-type": return MatchesNthPseudo(element, pseudo, depth, true, true, shadowRoot);
                case "is":
                case "where":
                    return MatchesAny(element, pseudo.ParsedArgsOrNull, depth + 1, shadowRoot);
                case "not":
                    return !MatchesAny(element, pseudo.ParsedArgsOrNull, depth + 1, shadowRoot);
                default:
                    return false;
            }
        }

        private static bool MatchesLanguage(
            SvgElement element, string argument, int depth, SvgElement shadowRoot)
        {
            if (element == null || string.IsNullOrWhiteSpace(argument)) return false;
            for (var current = element;
                 current != null && depth <= MaxSelectorMatchDepth;
                 current = MatchedParent(current, shadowRoot), depth++)
            {
                string language = current.GetAttribute("lang");
                if (string.IsNullOrWhiteSpace(language)) language = current.GetAttribute("xml:lang");
                if (string.IsNullOrWhiteSpace(language)) continue;
                return MatchesAnyLanguageRange(language.Trim(), argument);
            }
            return false;
        }

        private static bool MatchesAnyLanguageRange(string language, string argument)
        {
            foreach (string part in argument.Split(','))
            {
                string range = part.Trim();
                if (range.Length == 0) continue;
                if (range.Equals("*", StringComparison.Ordinal)) return true;
                if (language.Length < range.Length) continue;
                if (!language.StartsWith(range, StringComparison.OrdinalIgnoreCase)) continue;
                if (language.Length == range.Length || language[range.Length] == '-') return true;
            }
            return false;
        }

        private static bool MatchesAny(
            SvgElement element, IReadOnlyList<SelectorChain> chains, int depth, SvgElement shadowRoot)
        {
            if (chains == null) return false;
            for (int i = 0; i < chains.Count; i++)
            {
                if (MatchesChain(element, chains[i], chains[i].Segments.Count - 1, depth + 1, shadowRoot)) return true;
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

        private static bool HasPreviousOfType(SvgElement element, SvgElement shadowRoot)
        {
            for (var sibling = MatchedPreviousSibling(element, shadowRoot);
                 sibling != null;
                 sibling = sibling.PreviousElementSibling)
            {
                if (string.Equals(sibling.Name, element.Name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool HasNextOfType(SvgElement element, SvgElement shadowRoot)
        {
            var parent = MatchedParent(element, shadowRoot);
            if (parent == null) return false;
            bool found = false;
            foreach (var sibling in parent.Children)
            {
                if (found && string.Equals(sibling.Name, element.Name, StringComparison.Ordinal)) return true;
                if (ReferenceEquals(sibling, element)) found = true;
            }
            return false;
        }

        private static ushort ClampSpecificity(int value) =>
            (ushort)Math.Clamp(value, 0, ushort.MaxValue);

        private enum SelectorSupport
        {
            Matched,
            NotApplicable,
            Unsupported
        }

        private enum MediaTruth : byte
        {
            NoMatch,
            Match,
            Undetermined
        }

        private readonly struct MediaOutcome
        {
            public static readonly MediaOutcome NoMatch = new(MediaTruth.NoMatch, null, false);
            public static readonly MediaOutcome Match = new(MediaTruth.Match, null, false);

            public readonly MediaTruth Truth;
            public readonly string Subject;
            public readonly bool SubjectIsType;

            private MediaOutcome(MediaTruth truth, string subject, bool subjectIsType)
            {
                Truth = truth;
                Subject = subject;
                SubjectIsType = subjectIsType;
            }

            public static MediaOutcome Undetermined(string subject, bool subjectIsType) =>
                new(MediaTruth.Undetermined, subject, subjectIsType);

            public static MediaOutcome And(in MediaOutcome left, in MediaOutcome right)
            {
                if (left.Truth == MediaTruth.NoMatch || right.Truth == MediaTruth.NoMatch)
                    return NoMatch;
                if (left.Truth == MediaTruth.Match && right.Truth == MediaTruth.Match)
                    return Match;
                return left.Truth == MediaTruth.Undetermined ? left : right;
            }

            public static MediaOutcome Or(in MediaOutcome left, in MediaOutcome right)
            {
                if (left.Truth == MediaTruth.Match || right.Truth == MediaTruth.Match)
                    return Match;
                if (left.Truth == MediaTruth.NoMatch && right.Truth == MediaTruth.NoMatch)
                    return NoMatch;
                return left.Truth == MediaTruth.Undetermined ? left : right;
            }

            public static MediaOutcome Not(in MediaOutcome value) =>
                value.Truth == MediaTruth.Undetermined
                    ? value
                    : value.Truth == MediaTruth.Match ? NoMatch : Match;
        }

        private readonly record struct RuleEntry(
            CssStyleRule Rule,
            int SourceOrder,
            int LayerOrder);

        private readonly record struct Winner(string Value, CascadeKey Key);

        private readonly record struct FontLonghand(string Property, string Value);

        private static readonly ConditionalWeakTable<SvgElement, UseShadowCascade> UseShadowCascades = new();

        private sealed class UseShadowCascade
        {
            public SvgElement[] Order;
            public Dictionary<string, string>[] Cascaded;
            public Dictionary<string, string>[] Custom;
        }

        internal struct UseShadowScope : IDisposable
        {
            private SvgElement[] _order;
            private Dictionary<string, string>[] _savedCascaded;
            private Dictionary<string, string>[] _savedCustom;

            public static UseShadowScope Enter(SvgElement shadowRoot)
            {
                var scope = new UseShadowScope();
                if (shadowRoot == null ||
                    !UseShadowCascades.TryGetValue(shadowRoot, out var context))
                {
                    return scope;
                }

                int count = context.Order.Length;
                var savedCascaded = new Dictionary<string, string>[count];
                var savedCustom = new Dictionary<string, string>[count];
                int installed = 0;
                try
                {
                    for (; installed < count; installed++)
                    {
                        var element = context.Order[installed];
                        savedCascaded[installed] = element.CascadedDeclarations;
                        savedCustom[installed] = element.CustomProperties;
                        element.CascadedDeclarations = context.Cascaded[installed];
                        element.CustomProperties = context.Custom[installed];
                    }
                }
                catch
                {
                    for (int i = installed - 1; i >= 0; i--)
                    {
                        context.Order[i].CascadedDeclarations = savedCascaded[i];
                        context.Order[i].CustomProperties = savedCustom[i];
                    }
                    throw;
                }

                scope._order = context.Order;
                scope._savedCascaded = savedCascaded;
                scope._savedCustom = savedCustom;
                return scope;
            }

            public void Dispose()
            {
                var order = _order;
                if (order == null) return;
                _order = null;
                for (int i = order.Length - 1; i >= 0; i--)
                {
                    order[i].CascadedDeclarations = _savedCascaded[i];
                    order[i].CustomProperties = _savedCustom[i];
                }
                _savedCascaded = null;
                _savedCustom = null;
            }
        }

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
