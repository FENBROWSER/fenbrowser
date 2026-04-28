// SpecRef: CSS Cascading and Inheritance Level 4, Cascade order
// CapabilityId: CSS-CASCADE-ORDER-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;

namespace FenBrowser.FenEngine.Rendering
{
    public class CascadeEngine
    {
        private readonly StyleSet _styleSet;
        private readonly bool _logCascade;
        
        // PERF: Multi-level indexing for fast rule lookup
        private Dictionary<string, List<CssStyleRule>> _idIndex;     // #id rules
        private Dictionary<string, List<CssStyleRule>> _classIndex;  // .class rules
        private Dictionary<string, List<CssStyleRule>> _tagIndex;    // tag rules
        private List<CssStyleRule> _universalRules;                  // * and attribute-only rules
        private bool _indexed = false;
        private HashSet<CssStyleRule> _processedRules;               // Track duplicates
        
        // PERF: Track which pseudo-elements have any rules to skip cascade for unused ones
        private HashSet<string> _pseudoElementsWithRules;
        // Compatibility list for legacy single-colon pseudo-elements that may be
        // parsed as pseudo-classes by some selector paths.
        private static readonly HashSet<string> LegacyPseudoElementNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "before",
            "after",
            "first-line",
            "first-letter",
            "placeholder",
            "selection"
        };

        public CascadeEngine(StyleSet styleSet)
        {
            _styleSet = styleSet ?? new StyleSet();
            _logCascade = FenBrowser.Core.Logging.DebugConfig.LogCssCascade;
            if (_logCascade)
            {
                FenBrowser.Core.EngineLogCompat.Info($"[DEBUG-CASCADE] Created Engine with {_styleSet.Count} sheets", FenBrowser.Core.Logging.LogCategory.CSS);
            }
        }
        
        /// <summary>
        /// Returns true if any CSS rule targets the specified pseudo-element.
        /// Use this to skip ComputeCascadedValues calls for pseudo-elements with no rules.
        /// </summary>
        public bool HasPseudoRules(string pseudoElement)
        {
            EnsureIndex();
            return _pseudoElementsWithRules?.Contains(pseudoElement.ToLowerInvariant()) ?? false;
        }

        private void EnsureIndex()
        {
            if (_indexed) return;
            _indexed = true;
            _idIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _classIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _tagIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _universalRules = new List<CssStyleRule>();
            _processedRules = new HashSet<CssStyleRule>();
            _pseudoElementsWithRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            for (int i = 0; i < _styleSet.Count; i++)
            {
                var sheet = _styleSet.Sheets[i];
                var origin = _styleSet.Origins[i];
                var sourceOrder = _styleSet.SourceOrders[i];
                IndexRules(sheet.Rules, origin, sourceOrder);
            }
        }
        
        private void IndexRules(IEnumerable<CssRule> rules, CssOrigin defaultOrigin, int sourceOrder)
        {
            foreach (var rule in rules)
            {
                rule.StylesheetSourceOrder = sourceOrder;
                if (rule.Origin == CssOrigin.UserAgent && defaultOrigin != CssOrigin.UserAgent)
                {
                    rule.Origin = defaultOrigin;
                }

                if (rule is CssStyleRule styleRule)
                {
                    IndexStyleRule(styleRule);
                }
                else if (rule is CssMediaRule mediaRule)
                {
                    IndexRules(mediaRule.Rules, defaultOrigin, sourceOrder);
                }
            }
        }
        
        private void IndexStyleRule(CssStyleRule styleRule)
        {
            if (styleRule.Selector?.Chains?.Count > 0)
            {
                // PERF: Track pseudo-elements used by this rule
                foreach (var chain in styleRule.Selector.Chains)
                {
                    if (chain.Segments == null) continue;
                    foreach (var seg in chain.Segments)
                    {
                        AddIndexedPseudoElementNames(seg);
                    }

                    // Index based on this chain's key segment (Rightmost)
                    if (chain.Segments.Count > 0)
                    {
                        var keySeg = chain.Segments[chain.Segments.Count - 1];
                        IndexKeySegment(keySeg, styleRule);
                    }
                    else
                    {
                        _universalRules.Add(styleRule);
                    }
                }
            }
            else
            {
                _universalRules.Add(styleRule);
            }
        }

        private void AddIndexedPseudoElementNames(SelectorSegment seg)
        {
            if (seg == null)
            {
                return;
            }

            if (seg.PseudoElements != null)
            {
                foreach (var pe in seg.PseudoElements)
                {
                    if (TryNormalizePseudoElementName(pe?.Name, out var normalized))
                    {
                        _pseudoElementsWithRules.Add(normalized);
                    }
                }
            }

            // Some selector parsing paths still emit legacy pseudo-elements (:before/:after/etc.)
            // under PseudoClasses. Track those names too so pseudo cascade remains reachable.
            if (seg.PseudoClasses != null)
            {
                foreach (var pc in seg.PseudoClasses)
                {
                    if (TryNormalizeLegacyPseudoElementName(pc?.Name, out var normalized))
                    {
                        _pseudoElementsWithRules.Add(normalized);
                    }
                }
            }
        }

        private static bool TryNormalizePseudoElementName(string rawName, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return false;
            }

            normalized = rawName.Trim().TrimStart(':').ToLowerInvariant();
            return normalized.Length > 0;
        }

        private static bool TryNormalizeLegacyPseudoElementName(string rawName, out string normalized)
        {
            normalized = null;
            if (!TryNormalizePseudoElementName(rawName, out var candidate))
            {
                return false;
            }

            if (!LegacyPseudoElementNames.Contains(candidate))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }

        private void IndexKeySegment(SelectorSegment keySeg, CssStyleRule styleRule)
        {
            if (keySeg == null)
            {
                _universalRules.Add(styleRule);
                return;
            }

            // Priority: ID > Class > Tag > Universal
            // Index by the most specific key available
            if (!string.IsNullOrEmpty(keySeg.Id))
            {
                AddToIndex(_idIndex, keySeg.Id, styleRule);
            }
            else if (keySeg.Classes != null && keySeg.Classes.Count > 0)
            {
                // Index by first class (most rules have 1-2 classes)
                AddToIndex(_classIndex, keySeg.Classes[0], styleRule);
            }
            else if (!string.IsNullOrEmpty(keySeg.TagName) && keySeg.TagName != "*")
            {
                string upperTag = keySeg.TagName.ToUpperInvariant();
                AddToIndex(_tagIndex, upperTag, styleRule);
                // DEBUG: Log div rules
                if (upperTag == "DIV" && FenBrowser.Core.Logging.DebugConfig.LogCssCascade)
                {
                    var props = string.Join(", ", styleRule.Declarations.Select(d => d.Property));
                    FenBrowser.Core.EngineLogCompat.Info($"[CASCADE-INDEX] Indexed DIV rule: {styleRule.Selector?.Raw} -> [{props}]", LogCategory.CSS);
                }
            }
            else
            {
                _universalRules.Add(styleRule);
            }
        }
        
        private void AddToIndex(Dictionary<string, List<CssStyleRule>> index, string key, CssStyleRule rule)
        {
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<CssStyleRule>();
                index[key] = list;
            }
            list.Add(rule);
        }

        public Dictionary<string, CssDeclaration> ComputeCascadedValues(Element element, string pseudoElement = null, FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            EnsureIndex();
            var results = new List<MatchedDeclaration>();
            _processedRules.Clear();

            // 1. Gather all declarations from matching rules (priority order: ID > Classes > Tag > Universal)
            CollectMatches(element, results, pseudoElement);

            /*
            if (_logCascade && results.Count > 0)
            {
                var uniqueSources = results.Select(r => new { r.SelectorText, Spec = r.Specificity, r.Origin }).Distinct().OrderBy(x => x.Spec).ToList();
                var msg = new System.Text.StringBuilder();
                msg.AppendLine($"[CASCADE] Element <{element.TagName}> matched {uniqueSources.Count} rules:");
                foreach(var src in uniqueSources)
                {
                    msg.AppendLine($"   - [{src.Origin}] {src.SelectorText} :: {src.Spec}");
                }
                global::FenBrowser.Core.EngineLogCompat.Log(msg.ToString().TrimEnd(), global::FenBrowser.Core.Logging.LogCategory.Cascade);
            }
            */

            // 2. Sort declarations
            deadline?.Check();
            results.Sort();
            deadline?.Check();

            if (_logCascade && results.Count > 0)
            {
                LogCascadeWinners(element, pseudoElement, results);
            }

            // 3. Apply the cascade declaration-by-declaration so shorthand expansion
            // participates in origin/specificity/order resolution. Expanding after
            // selecting winners per declared property is incorrect because a higher-
            // priority shorthand (for example author `background`) must override a
            // lower-priority longhand (for example UA `background-color`).
            // CSS custom properties are case-sensitive and must not be merged by
            // case-insensitive dictionary keys.
            var computed = new Dictionary<string, CssDeclaration>(StringComparer.Ordinal);
            foreach (var match in results)
            {
                deadline?.Check();
                ApplyDeclaration(computed, match.Declaration);
            }

            return computed;
        }

        private void LogCascadeWinners(Element element, string pseudoElement, List<MatchedDeclaration> results)
        {
            if (element == null || results == null || results.Count == 0)
            {
                return;
            }

            var winners = new Dictionary<string, MatchedDeclaration>(StringComparer.Ordinal);
            foreach (var match in results)
            {
                var property = match?.Declaration?.Property;
                if (string.IsNullOrWhiteSpace(property))
                {
                    continue;
                }

                winners[property] = match;
            }

            if (winners.Count == 0)
            {
                return;
            }

            string elementLabel = BuildElementLabel(element, pseudoElement);
            var msg = new System.Text.StringBuilder();
            msg.AppendLine($"[CASCADE-WINNERS] {elementLabel}");

            foreach (var entry in winners.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var winner = entry.Value;
                msg.Append("  ");
                msg.Append(entry.Key);
                msg.Append(" = ");
                msg.Append(winner.Declaration.Value);
                msg.Append(" | key=");
                msg.Append(winner.Key.ToString());
                msg.Append($",selector={winner.SelectorText}");
                msg.AppendLine();
            }

            FenBrowser.Core.EngineLogCompat.Info(msg.ToString().TrimEnd(), LogCategory.CSS);
        }

        private static string BuildElementLabel(Element element, string pseudoElement)
        {
            var parts = new List<string>();
            string tag = string.IsNullOrWhiteSpace(element.TagName) ? "unknown" : element.TagName.ToLowerInvariant();
            parts.Add(tag);

            if (!string.IsNullOrWhiteSpace(element.Id))
            {
                parts.Add("#" + element.Id);
            }

            foreach (var cls in element.ClassList)
            {
                if (!string.IsNullOrWhiteSpace(cls))
                {
                    parts.Add("." + cls);
                }
            }

            if (!string.IsNullOrWhiteSpace(pseudoElement))
            {
                parts.Add("::" + pseudoElement.TrimStart(':'));
            }

            return string.Join(string.Empty, parts);
        }

        private void CollectMatches(Element element, List<MatchedDeclaration> results, string pseudoElement)
        {
            // Get attributes via properties
            string elemId = element.Id;
            string elemClass = element.GetAttribute("class");
            
            // 1. Check ID-specific rules (highest priority index)
            if (!string.IsNullOrEmpty(elemId) && _idIndex.TryGetValue(elemId, out var idRules))
            {
                foreach (var rule in idRules)
                {
                    if (_processedRules.Add(rule))
                        TryMatchRule(element, rule, results, pseudoElement);
                }
            }
            
            // 2. Check class-specific rules
            if (!string.IsNullOrEmpty(elemClass))
            {
                var classes = element.ClassList; // Use ClassList for splitting
                foreach (var cls in classes)
                {
                    if (_classIndex.TryGetValue(cls, out var classRules))
                    {
                        foreach (var rule in classRules)
                        {
                            if (_processedRules.Add(rule))
                                TryMatchRule(element, rule, results, pseudoElement);
                        }
                    }
                }
            }
            
            // 3. Check tag-specific rules
            string tag = element.TagName?.ToUpperInvariant();
            if (!string.IsNullOrEmpty(tag) && _tagIndex.TryGetValue(tag, out var tagRules))
            {
                // DEBUG: Log div cascade
                if (tag == "DIV" && FenBrowser.Core.Logging.DebugConfig.LogCssCascade)
                {
                    FenBrowser.Core.EngineLogCompat.Info($"[CASCADE-DIV] Found {tagRules.Count} indexed rules for DIV element", LogCategory.CSS);
                }
                foreach (var rule in tagRules)
                {
                    if (_processedRules.Add(rule))
                        TryMatchRule(element, rule, results, pseudoElement);
                }
            }
            else if (tag == "DIV" && FenBrowser.Core.Logging.DebugConfig.LogCssCascade)
            {
                FenBrowser.Core.EngineLogCompat.Warn($"[CASCADE-DIV] NO rules found in _tagIndex for DIV! Index contains: {string.Join(", ", _tagIndex.Keys.Take(20))}", LogCategory.CSS);
            }
            
            // 4. Always check universal rules
            foreach (var rule in _universalRules)
            {
                if (_processedRules.Add(rule))
                    TryMatchRule(element, rule, results, pseudoElement);
            }
        }
        
        private void TryMatchRule(Element element, CssStyleRule styleRule, List<MatchedDeclaration> results, string pseudoElement)
        {
            // 1. Check Scope if applicable
            int scopeProximity = 0;
            if (!string.IsNullOrEmpty(styleRule.ScopeSelector))
            {
                // Find closest ancestor matching the scope selector
                var current = element.ParentElement;
                int dist = 1;
                bool scopeMatched = false;
                while (current != null)
                {
                    if (SelectorMatcher.Matches(current, styleRule.ScopeSelector))
                    {
                        scopeProximity = dist;
                        scopeMatched = true;
                        break;
                    }
                    current = current.ParentElement;
                    dist++;
                }
                
                if (!scopeMatched) return; // Not inside the required scope
            }

            // 2. Match the actual selector
            var matchedChain = SelectorMatcher.GetMatchingChain(element, styleRule.Selector);
            if (matchedChain == null)
            {
                return;
            }

            if (matchedChain != null)
            {
                var lastSeg = matchedChain.Segments.LastOrDefault();
                var pseudoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (lastSeg != null)
                {
                    if (lastSeg.PseudoElements != null)
                    {
                        foreach (var pe in lastSeg.PseudoElements)
                        {
                            if (TryNormalizePseudoElementName(pe?.Name, out var normalized))
                            {
                                pseudoNames.Add(normalized);
                            }
                        }
                    }

                    if (lastSeg.PseudoClasses != null)
                    {
                        foreach (var pc in lastSeg.PseudoClasses)
                        {
                            if (TryNormalizeLegacyPseudoElementName(pc?.Name, out var normalized))
                            {
                                pseudoNames.Add(normalized);
                            }
                        }
                    }
                }

                bool ruleHasPseudo = pseudoNames.Count > 0;
                
                if (string.IsNullOrEmpty(pseudoElement))
                {
                    if (ruleHasPseudo) return;
                }
                else
                {
                    if (!ruleHasPseudo) return;

                    string requestedPseudo = pseudoElement.Trim().TrimStart(':').ToLowerInvariant();
                    if (!pseudoNames.Contains(requestedPseudo)) return;
                }
            }

            for (int declarationIndex = 0; declarationIndex < styleRule.Declarations.Count; declarationIndex++)
            {
                var decl = styleRule.Declarations[declarationIndex];
                var key = new CascadeKey(
                    styleRule.Origin,
                    decl.IsImportant,
                    (ushort)matchedChain.Specificity.A,
                    (ushort)matchedChain.Specificity.B,
                    (ushort)matchedChain.Specificity.C,
                    styleRule.LayerOrder,
                    scopeProximity,
                    styleRule.StylesheetSourceOrder,
                    styleRule.Order,
                    declarationIndex
                );

                results.Add(new MatchedDeclaration
                {
                    Declaration = decl,
                    Key = key,
                    SelectorText = styleRule.Selector?.Raw ?? styleRule.Selector?.ToString() ?? string.Empty
                });
            }
        }

        private static void ApplyDeclaration(Dictionary<string, CssDeclaration> computed, CssDeclaration declaration)
        {
            if (declaration == null || string.IsNullOrEmpty(declaration.Property))
            {
                return;
            }

            var property = NormalizePropertyKey(declaration.Property);
            var value = declaration.Value?.Trim() ?? string.Empty;
            if (!IsValidDeclarationValue(property, value))
            {
                return;
            }

            computed[property] = CloneDeclaration(declaration, property, declaration.Value);

            switch (property)
            {
                case "margin":
                    ApplyBoxShorthand(computed, declaration, value, "margin-top", "margin-right", "margin-bottom", "margin-left");
                    break;
                case "padding":
                    ApplyBoxShorthand(computed, declaration, value, "padding-top", "padding-right", "padding-bottom", "padding-left");
                    break;
                case "border":
                    ApplyBorderShorthand(computed, declaration, value, null);
                    break;
                case "border-top":
                case "border-right":
                case "border-bottom":
                case "border-left":
                    ApplyBorderShorthand(computed, declaration, value, property);
                    break;
                case "background":
                    ApplyBackgroundShorthand(computed, declaration, value);
                    break;
                case "flex-flow":
                    ApplyFlexFlowShorthand(computed, declaration, value);
                    break;
                case "overflow":
                    ApplyOverflowShorthand(computed, declaration, value);
                    break;
                case "outline":
                    ApplyOutlineShorthand(computed, declaration, value);
                    break;
                case "list-style":
                    ApplyListStyleShorthand(computed, declaration, value);
                    break;
                case "gap":
                    ApplyGapShorthand(computed, declaration, value);
                    break;
                case "border-radius":
                    ApplyBorderRadiusShorthand(computed, declaration, value);
                    break;
                case "inset":
                    ApplyInsetShorthand(computed, declaration, value);
                    break;
            }
        }

        private static string NormalizePropertyKey(string property)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                return string.Empty;
            }

            // Custom properties are case-sensitive by spec.
            if (property.StartsWith("--", StringComparison.Ordinal))
            {
                return property;
            }

            return property.ToLowerInvariant();
        }

        private static bool IsValidDeclarationValue(string property, string value)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            switch (property.ToLowerInvariant())
            {
                case "color":
                case "background-color":
                case "border-top-color":
                case "border-right-color":
                case "border-bottom-color":
                case "border-left-color":
                case "outline-color":
                case "column-rule-color":
                case "text-decoration-color":
                case "caret-color":
                case "accent-color":
                    return IsValidColorValue(value);
                case "width":
                case "height":
                case "min-width":
                case "min-height":
                case "max-width":
                case "max-height":
                case "inline-size":
                case "block-size":
                case "min-inline-size":
                case "min-block-size":
                case "max-inline-size":
                case "max-block-size":
                    return IsValidSizingValue(value);
                case "background":
                    return IsValidBackgroundShorthand(value);
                default:
                    return true;
            }
        }

        private static bool IsValidColorValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "inherit":
                case "initial":
                case "unset":
                case "revert":
                case "revert-layer":
                case "currentcolor":
                    return true;
            }

            // Color custom-property references are valid at parse/cascade time and
            // resolved during computed-style resolution.
            if (trimmed.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return CssParser.ParseColor(trimmed).HasValue;
        }

        private static bool IsValidSizingValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "auto":
                case "inherit":
                case "initial":
                case "unset":
                case "revert":
                case "revert-layer":
                case "min-content":
                case "max-content":
                case "fit-content":
                    return true;
            }

            if (IsCssLength(trimmed))
            {
                return true;
            }

            return trimmed.Contains("(", StringComparison.Ordinal);
        }

        private static bool IsValidBackgroundShorthand(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int colorCount = 0;
            foreach (var part in SplitCssValue(trimmed))
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                if (part.Equals("/", StringComparison.Ordinal) ||
                    IsBackgroundRepeat(part) ||
                    IsBackgroundPosition(part) ||
                    part.Equals("fixed", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("contain", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("gradient(", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CssParser.ParseColor(part).HasValue)
                {
                    colorCount++;
                    if (colorCount > 1)
                    {
                        return false;
                    }
                    continue;
                }
            }

            return true;
        }

        private static CssDeclaration CloneDeclaration(CssDeclaration source, string property, string value)
        {
            return new CssDeclaration
            {
                Property = property,
                Value = value,
                IsImportant = source?.IsImportant ?? false
            };
        }

        private static void SetExpanded(Dictionary<string, CssDeclaration> computed, string property, string value, CssDeclaration source)
        {
            computed[property] = CloneDeclaration(source, property, value);
        }

        private static void ApplyBoxShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value,
            string top, string right, string bottom, string left)
        {
            if (string.IsNullOrEmpty(value)) return;

            var parts = SplitCssValue(value);
            if (parts.Length == 0) return;

            string vTop, vRight, vBottom, vLeft;
            switch (parts.Length)
            {
                case 1:
                    vTop = vRight = vBottom = vLeft = parts[0];
                    break;
                case 2:
                    vTop = vBottom = parts[0];
                    vRight = vLeft = parts[1];
                    break;
                case 3:
                    vTop = parts[0];
                    vRight = vLeft = parts[1];
                    vBottom = parts[2];
                    break;
                default:
                    vTop = parts[0];
                    vRight = parts[1];
                    vBottom = parts[2];
                    vLeft = parts[3];
                    break;
            }

            SetExpanded(computed, top, vTop, source);
            SetExpanded(computed, right, vRight, source);
            SetExpanded(computed, bottom, vBottom, source);
            SetExpanded(computed, left, vLeft, source);
        }

        private static void ApplyBorderShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value, string sideProperty)
        {
            if (string.IsNullOrEmpty(value)) return;

            if (value == "none" || value == "0")
            {
                if (string.IsNullOrEmpty(sideProperty))
                {
                    SetExpanded(computed, "border-top-width", "0", source);
                    SetExpanded(computed, "border-right-width", "0", source);
                    SetExpanded(computed, "border-bottom-width", "0", source);
                    SetExpanded(computed, "border-left-width", "0", source);
                    SetExpanded(computed, "border-top-style", "none", source);
                    SetExpanded(computed, "border-right-style", "none", source);
                    SetExpanded(computed, "border-bottom-style", "none", source);
                    SetExpanded(computed, "border-left-style", "none", source);
                }
                else
                {
                    SetExpanded(computed, sideProperty + "-width", "0", source);
                    SetExpanded(computed, sideProperty + "-style", "none", source);
                }
                return;
            }

            var parts = SplitCssValue(value);
            string width = null, style = null, color = null;
            foreach (var part in parts)
            {
                if (IsBorderStyle(part)) style = part;
                else if (IsCssLength(part)) width = part;
                else color = part;
            }

            if (string.IsNullOrEmpty(sideProperty))
            {
                if (width != null)
                {
                    SetExpanded(computed, "border-top-width", width, source);
                    SetExpanded(computed, "border-right-width", width, source);
                    SetExpanded(computed, "border-bottom-width", width, source);
                    SetExpanded(computed, "border-left-width", width, source);
                }
                if (style != null)
                {
                    SetExpanded(computed, "border-top-style", style, source);
                    SetExpanded(computed, "border-right-style", style, source);
                    SetExpanded(computed, "border-bottom-style", style, source);
                    SetExpanded(computed, "border-left-style", style, source);
                }
                if (color != null)
                {
                    SetExpanded(computed, "border-top-color", color, source);
                    SetExpanded(computed, "border-right-color", color, source);
                    SetExpanded(computed, "border-bottom-color", color, source);
                    SetExpanded(computed, "border-left-color", color, source);
                }
            }
            else
            {
                if (width != null) SetExpanded(computed, sideProperty + "-width", width, source);
                if (style != null) SetExpanded(computed, sideProperty + "-style", style, source);
                if (color != null) SetExpanded(computed, sideProperty + "-color", color, source);
            }
        }

        private static void ApplyBackgroundShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            ExpandSingleLayerBackgroundShorthand(
                value,
                (property, shorthandValue) => SetExpanded(computed, property, shorthandValue, source),
                setDefaults: true);
        }

        private static void ApplyFlexFlowShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            foreach (var part in SplitCssValue(value))
            {
                if (part == "wrap" || part == "nowrap" || part == "wrap-reverse")
                    SetExpanded(computed, "flex-wrap", part, source);
                else
                    SetExpanded(computed, "flex-direction", part, source);
            }
        }

        private static void ApplyOverflowShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            var parts = SplitCssValue(value);
            if (parts.Length == 1)
            {
                SetExpanded(computed, "overflow-x", parts[0], source);
                SetExpanded(computed, "overflow-y", parts[0], source);
            }
            else if (parts.Length >= 2)
            {
                SetExpanded(computed, "overflow-x", parts[0], source);
                SetExpanded(computed, "overflow-y", parts[1], source);
            }
        }

        private static void ApplyOutlineShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            foreach (var part in SplitCssValue(value))
            {
                if (IsBorderStyle(part)) SetExpanded(computed, "outline-style", part, source);
                else if (IsCssLength(part)) SetExpanded(computed, "outline-width", part, source);
                else SetExpanded(computed, "outline-color", part, source);
            }
        }

        private static void ApplyListStyleShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            foreach (var part in SplitCssValue(value))
            {
                if (part == "inside" || part == "outside")
                    SetExpanded(computed, "list-style-position", part, source);
                else if (part.StartsWith("url("))
                    SetExpanded(computed, "list-style-image", part, source);
                else
                    SetExpanded(computed, "list-style-type", part, source);
            }
        }

        private static void ApplyGapShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            var parts = SplitCssValue(value);
            if (parts.Length == 1)
            {
                SetExpanded(computed, "row-gap", parts[0], source);
                SetExpanded(computed, "column-gap", parts[0], source);
            }
            else if (parts.Length >= 2)
            {
                SetExpanded(computed, "row-gap", parts[0], source);
                SetExpanded(computed, "column-gap", parts[1], source);
            }
        }

        private static void ApplyBorderRadiusShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            var slashIdx = value.IndexOf('/');
            var mainPart = slashIdx >= 0 ? value.Substring(0, slashIdx).Trim() : value;
            var parts = SplitCssValue(mainPart);
            if (parts.Length == 0) return;

            string tl, tr, br, bl;
            switch (parts.Length)
            {
                case 1: tl = tr = br = bl = parts[0]; break;
                case 2: tl = br = parts[0]; tr = bl = parts[1]; break;
                case 3: tl = parts[0]; tr = bl = parts[1]; br = parts[2]; break;
                default: tl = parts[0]; tr = parts[1]; br = parts[2]; bl = parts[3]; break;
            }

            SetExpanded(computed, "border-top-left-radius", tl, source);
            SetExpanded(computed, "border-top-right-radius", tr, source);
            SetExpanded(computed, "border-bottom-right-radius", br, source);
            SetExpanded(computed, "border-bottom-left-radius", bl, source);
        }

        private static void ApplyInsetShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            var parts = SplitCssValue(value);
            if (parts.Length == 0) return;

            string vTop, vRight, vBottom, vLeft;
            switch (parts.Length)
            {
                case 1: vTop = vRight = vBottom = vLeft = parts[0]; break;
                case 2: vTop = vBottom = parts[0]; vRight = vLeft = parts[1]; break;
                case 3: vTop = parts[0]; vRight = vLeft = parts[1]; vBottom = parts[2]; break;
                default: vTop = parts[0]; vRight = parts[1]; vBottom = parts[2]; vLeft = parts[3]; break;
            }

            SetExpanded(computed, "top", vTop, source);
            SetExpanded(computed, "right", vRight, source);
            SetExpanded(computed, "bottom", vBottom, source);
            SetExpanded(computed, "left", vLeft, source);
        }

        /// <summary>
        /// Expand CSS shorthand properties into their longhand equivalents.
        /// Only sets longhands that weren't explicitly declared with higher specificity.
        /// </summary>
        private static void ExpandShorthands(Dictionary<string, CssDeclaration> computed)
        {
            // Process each shorthand → longhand expansion
            ExpandBoxShorthand(computed, "margin", "margin-top", "margin-right", "margin-bottom", "margin-left");
            ExpandBoxShorthand(computed, "padding", "padding-top", "padding-right", "padding-bottom", "padding-left");
            ExpandBorderShorthand(computed);
            ExpandBackgroundShorthand(computed);
            ExpandFlexFlowShorthand(computed);
            ExpandOverflowShorthand(computed);
            ExpandOutlineShorthand(computed);
            ExpandListStyleShorthand(computed);
            ExpandGapShorthand(computed);
            ExpandBorderRadiusShorthand(computed);
            ExpandInsetShorthand(computed);
        }

        /// <summary>
        /// Expand box model shorthands (margin, padding) using 1-4 value syntax.
        /// margin: 10px → all four sides 10px
        /// margin: 10px 20px → top/bottom 10px, left/right 20px
        /// margin: 10px 20px 30px → top 10px, left/right 20px, bottom 30px
        /// margin: 10px 20px 30px 40px → top right bottom left
        /// </summary>
        private static void ExpandBoxShorthand(Dictionary<string, CssDeclaration> computed,
            string shorthand, string top, string right, string bottom, string left)
        {
            if (!computed.TryGetValue(shorthand, out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            var parts = SplitCssValue(value);
            string vTop, vRight, vBottom, vLeft;

            switch (parts.Length)
            {
                case 1:
                    vTop = vRight = vBottom = vLeft = parts[0];
                    break;
                case 2:
                    vTop = vBottom = parts[0];
                    vRight = vLeft = parts[1];
                    break;
                case 3:
                    vTop = parts[0];
                    vRight = vLeft = parts[1];
                    vBottom = parts[2];
                    break;
                default: // 4+
                    vTop = parts[0];
                    vRight = parts[1];
                    vBottom = parts[2];
                    vLeft = parts[3];
                    break;
            }

            SetIfNotExplicit(computed, top, vTop, decl);
            SetIfNotExplicit(computed, right, vRight, decl);
            SetIfNotExplicit(computed, bottom, vBottom, decl);
            SetIfNotExplicit(computed, left, vLeft, decl);
        }

        /// <summary>
        /// Expand border shorthand: border: 1px solid black → border-width, border-style, border-color
        /// </summary>
        private static void ExpandBorderShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("border", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;
            if (value == "none" || value == "0")
            {
                SetIfNotExplicit(computed, "border-top-width", "0", decl);
                SetIfNotExplicit(computed, "border-right-width", "0", decl);
                SetIfNotExplicit(computed, "border-bottom-width", "0", decl);
                SetIfNotExplicit(computed, "border-left-width", "0", decl);
                SetIfNotExplicit(computed, "border-top-style", "none", decl);
                SetIfNotExplicit(computed, "border-right-style", "none", decl);
                SetIfNotExplicit(computed, "border-bottom-style", "none", decl);
                SetIfNotExplicit(computed, "border-left-style", "none", decl);
                return;
            }

            var parts = SplitCssValue(value);
            string width = null, style = null, color = null;

            foreach (var p in parts)
            {
                if (IsBorderStyle(p)) style = p;
                else if (IsCssLength(p)) width = p;
                else color = p;
            }

            if (width != null)
            {
                SetIfNotExplicit(computed, "border-top-width", width, decl);
                SetIfNotExplicit(computed, "border-right-width", width, decl);
                SetIfNotExplicit(computed, "border-bottom-width", width, decl);
                SetIfNotExplicit(computed, "border-left-width", width, decl);
            }
            if (style != null)
            {
                SetIfNotExplicit(computed, "border-top-style", style, decl);
                SetIfNotExplicit(computed, "border-right-style", style, decl);
                SetIfNotExplicit(computed, "border-bottom-style", style, decl);
                SetIfNotExplicit(computed, "border-left-style", style, decl);
            }
            if (color != null)
            {
                SetIfNotExplicit(computed, "border-top-color", color, decl);
                SetIfNotExplicit(computed, "border-right-color", color, decl);
                SetIfNotExplicit(computed, "border-bottom-color", color, decl);
                SetIfNotExplicit(computed, "border-left-color", color, decl);
            }

            // Also expand border-top/right/bottom/left if present
            foreach (var side in new[] { "border-top", "border-right", "border-bottom", "border-left" })
            {
                if (!computed.TryGetValue(side, out var sideDecl)) continue;
                var sv = sideDecl.Value?.Trim();
                if (string.IsNullOrEmpty(sv)) continue;
                var sp = SplitCssValue(sv);
                string sw = null, ss = null, sc = null;
                foreach (var p in sp)
                {
                    if (IsBorderStyle(p)) ss = p;
                    else if (IsCssLength(p)) sw = p;
                    else sc = p;
                }
                if (sw != null) SetIfNotExplicit(computed, side + "-width", sw, sideDecl);
                if (ss != null) SetIfNotExplicit(computed, side + "-style", ss, sideDecl);
                if (sc != null) SetIfNotExplicit(computed, side + "-color", sc, sideDecl);
            }
        }

        private static void ExpandBackgroundShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("background", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            ExpandSingleLayerBackgroundShorthand(
                value,
                (property, shorthandValue) => SetIfNotExplicit(computed, property, shorthandValue, decl),
                setDefaults: true);
        }

        private static void ExpandSingleLayerBackgroundShorthand(
            string value,
            Action<string, string> assign,
            bool setDefaults)
        {
            if (string.IsNullOrWhiteSpace(value) || assign == null)
            {
                return;
            }

            if (value == "none" || value == "transparent")
            {
                assign("background-color", "transparent");
                assign("background-image", "none");
                return;
            }

            string color = null;
            string image = null;
            string repeat = null;
            string attachment = null;
            var positionTokens = new List<string>();

            foreach (var token in SplitCssValue(value))
            {
                if (string.IsNullOrWhiteSpace(token) || token == "/")
                {
                    continue;
                }

                if (token.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                    token.Contains("gradient(", StringComparison.OrdinalIgnoreCase))
                {
                    image = token;
                    continue;
                }

                if (IsBackgroundRepeat(token))
                {
                    repeat = token;
                    continue;
                }

                if (token == "fixed" || token == "scroll" || token == "local")
                {
                    attachment = token;
                    continue;
                }

                if (IsBackgroundPositionToken(token))
                {
                    positionTokens.Add(token);
                    continue;
                }

                color = token;
            }

            if (color != null)
            {
                assign("background-color", color);
            }

            if (image != null)
            {
                assign("background-image", image);
            }
            else if (setDefaults)
            {
                assign("background-image", "none");
            }

            if (repeat != null)
            {
                assign("background-repeat", repeat);
            }

            if (attachment != null)
            {
                assign("background-attachment", attachment);
            }

            if (positionTokens.Count > 0)
            {
                assign("background-position", string.Join(" ", positionTokens.Take(2)));
            }
        }

        private static void ExpandFlexFlowShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("flex-flow", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            foreach (var p in parts)
            {
                if (p == "wrap" || p == "nowrap" || p == "wrap-reverse")
                    SetIfNotExplicit(computed, "flex-wrap", p, decl);
                else
                    SetIfNotExplicit(computed, "flex-direction", p, decl);
            }
        }

        private static void ExpandOverflowShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("overflow", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            if (parts.Length == 1)
            {
                SetIfNotExplicit(computed, "overflow-x", parts[0], decl);
                SetIfNotExplicit(computed, "overflow-y", parts[0], decl);
            }
            else if (parts.Length >= 2)
            {
                SetIfNotExplicit(computed, "overflow-x", parts[0], decl);
                SetIfNotExplicit(computed, "overflow-y", parts[1], decl);
            }
        }

        private static void ExpandOutlineShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("outline", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            foreach (var p in parts)
            {
                if (IsBorderStyle(p)) SetIfNotExplicit(computed, "outline-style", p, decl);
                else if (IsCssLength(p)) SetIfNotExplicit(computed, "outline-width", p, decl);
                else SetIfNotExplicit(computed, "outline-color", p, decl);
            }
        }

        private static void ExpandListStyleShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("list-style", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            foreach (var p in parts)
            {
                if (p == "inside" || p == "outside")
                    SetIfNotExplicit(computed, "list-style-position", p, decl);
                else if (p.StartsWith("url("))
                    SetIfNotExplicit(computed, "list-style-image", p, decl);
                else
                    SetIfNotExplicit(computed, "list-style-type", p, decl);
            }
        }

        private static void ExpandGapShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("gap", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            if (parts.Length == 1)
            {
                SetIfNotExplicit(computed, "row-gap", parts[0], decl);
                SetIfNotExplicit(computed, "column-gap", parts[0], decl);
            }
            else if (parts.Length >= 2)
            {
                SetIfNotExplicit(computed, "row-gap", parts[0], decl);
                SetIfNotExplicit(computed, "column-gap", parts[1], decl);
            }
        }

        private static void ExpandBorderRadiusShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("border-radius", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            // Handle slash syntax for elliptical: "10px / 5px"
            // For simplicity, just handle the before-slash part
            var slashIdx = value.IndexOf('/');
            var mainPart = slashIdx >= 0 ? value.Substring(0, slashIdx).Trim() : value;
            var parts = SplitCssValue(mainPart);

            string tl, tr, br, bl;
            switch (parts.Length)
            {
                case 1: tl = tr = br = bl = parts[0]; break;
                case 2: tl = br = parts[0]; tr = bl = parts[1]; break;
                case 3: tl = parts[0]; tr = bl = parts[1]; br = parts[2]; break;
                default: tl = parts[0]; tr = parts[1]; br = parts[2]; bl = parts[3]; break;
            }

            SetIfNotExplicit(computed, "border-top-left-radius", tl, decl);
            SetIfNotExplicit(computed, "border-top-right-radius", tr, decl);
            SetIfNotExplicit(computed, "border-bottom-right-radius", br, decl);
            SetIfNotExplicit(computed, "border-bottom-left-radius", bl, decl);
        }

        private static void ExpandInsetShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("inset", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            string vTop, vRight, vBottom, vLeft;
            switch (parts.Length)
            {
                case 1: vTop = vRight = vBottom = vLeft = parts[0]; break;
                case 2: vTop = vBottom = parts[0]; vRight = vLeft = parts[1]; break;
                case 3: vTop = parts[0]; vRight = vLeft = parts[1]; vBottom = parts[2]; break;
                default: vTop = parts[0]; vRight = parts[1]; vBottom = parts[2]; vLeft = parts[3]; break;
            }
            SetIfNotExplicit(computed, "top", vTop, decl);
            SetIfNotExplicit(computed, "right", vRight, decl);
            SetIfNotExplicit(computed, "bottom", vBottom, decl);
            SetIfNotExplicit(computed, "left", vLeft, decl);
        }

        /// <summary>
        /// Set a longhand property only if it wasn't explicitly declared (higher specificity wins).
        /// </summary>
        private static void SetIfNotExplicit(Dictionary<string, CssDeclaration> computed, string property, string value, CssDeclaration source)
        {
            if (computed.ContainsKey(property)) return; // Explicit longhand wins
            computed[property] = new CssDeclaration
            {
                Property = property,
                Value = value,
                IsImportant = source.IsImportant
            };
        }

        private static string[] SplitCssValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
            // Handle function parentheses (don't split inside them)
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if ((c == ' ' || c == '\t') && depth == 0)
                {
                    if (i > start)
                        parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < value.Length)
                parts.Add(value.Substring(start));
            return parts.ToArray();
        }

        private static bool IsBorderStyle(string value)
        {
            switch (value)
            {
                case "none": case "hidden": case "dotted": case "dashed": case "solid":
                case "double": case "groove": case "ridge": case "inset": case "outset":
                    return true;
                default: return false;
            }
        }

        private static bool IsCssLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value == "0") return true;
            if (value == "thin" || value == "medium" || value == "thick") return true;
            // Check if it ends with a unit
            return value.EndsWith("px") || value.EndsWith("em") || value.EndsWith("rem") ||
                   value.EndsWith("pt") || value.EndsWith("vh") || value.EndsWith("vw") ||
                   value.EndsWith("%") || value.EndsWith("ch") || value.EndsWith("ex") ||
                   value.EndsWith("cm") || value.EndsWith("mm") || value.EndsWith("in") ||
                   value.EndsWith("pc");
        }

        private static bool IsBackgroundRepeat(string value)
        {
            return value == "repeat" || value == "no-repeat" || value == "repeat-x" ||
                   value == "repeat-y" || value == "space" || value == "round";
        }

        private static bool IsBackgroundPosition(string value)
        {
            return value == "center" || value == "top" || value == "bottom" ||
                   value == "left" || value == "right";
        }

        private static bool IsBackgroundPositionToken(string value)
        {
            return IsBackgroundPosition(value) || IsCssLength(value);
        }
    }

    public class MatchedDeclaration : IComparable<MatchedDeclaration>
    {
        public CssDeclaration Declaration { get; set; }
        public CascadeKey Key { get; set; }
        public string SelectorText { get; set; }

        public int CompareTo(MatchedDeclaration other)
        {
            return Key.CompareTo(other.Key);
        }
    }
}

