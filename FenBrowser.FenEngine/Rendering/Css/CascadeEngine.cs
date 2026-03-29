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
        private readonly CssStylesheet _stylesheet;
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

        public CascadeEngine(CssStylesheet stylesheet)
        {
            _stylesheet = stylesheet;
            _logCascade = true; // FenBrowser.Core.Logging.DebugConfig.LogCssCascade;
            FenBrowser.Core.FenLogger.Info($"[DEBUG-CASCADE] Created Engine with {_stylesheet.Rules.Count} rules", FenBrowser.Core.Logging.LogCategory.CSS);
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
            
            IndexRules(_stylesheet.Rules);
        }
        
        private void IndexRules(IEnumerable<CssRule> rules)
        {
            foreach (var rule in rules)
            {
                if (rule is CssStyleRule styleRule)
                {
                    IndexStyleRule(styleRule);
                }
                else if (rule is CssMediaRule mediaRule)
                {
                    IndexRules(mediaRule.Rules);
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
                        if (seg.PseudoElements != null)
                        {
                            foreach (var pe in seg.PseudoElements)
                            {
                                _pseudoElementsWithRules.Add(pe.Name.ToLowerInvariant());
                            }
                        }
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
                if (upperTag == "DIV")
                {
                    var props = string.Join(", ", styleRule.Declarations.Select(d => d.Property));
                    FenBrowser.Core.FenLogger.Info($"[CASCADE-INDEX] Indexed DIV rule: {styleRule.Selector?.Raw} -> [{props}]", LogCategory.CSS);
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
                global::FenBrowser.Core.FenLogger.Log(msg.ToString().TrimEnd(), global::FenBrowser.Core.Logging.LogCategory.Cascade);
            }
            */

            // 2. Sort declarations
            deadline?.Check();
            results.Sort();
            deadline?.Check();

            // 3. Apply standard cascade (Winner per property)
            var computed = new Dictionary<string, CssDeclaration>();
            foreach (var match in results)
            {
                deadline?.Check();
                computed[match.Declaration.Property] = match.Declaration;
            }

            // 4. Expand shorthand properties into longhands
            ExpandShorthands(computed);

            return computed;
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
                if (tag == "DIV")
                {
                    FenBrowser.Core.FenLogger.Info($"[CASCADE-DIV] Found {tagRules.Count} indexed rules for DIV element", LogCategory.CSS);
                }
                foreach (var rule in tagRules)
                {
                    if (_processedRules.Add(rule))
                        TryMatchRule(element, rule, results, pseudoElement);
                }
            }
            else if (tag == "DIV")
            {
                FenBrowser.Core.FenLogger.Warn($"[CASCADE-DIV] NO rules found in _tagIndex for DIV! Index contains: {string.Join(", ", _tagIndex.Keys.Take(20))}", LogCategory.CSS);
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

            if (matchedChain != null)
            {
                var lastSeg = matchedChain.Segments.LastOrDefault();
                bool ruleHasPseudo = lastSeg != null && lastSeg.PseudoElements.Count > 0;
                
                if (string.IsNullOrEmpty(pseudoElement))
                {
                    if (ruleHasPseudo) return;
                }
                else
                {
                    if (!ruleHasPseudo) return;
                    
                    bool found = false;
                    foreach (var pe in lastSeg.PseudoElements)
                    {
                        if (pe.Name.Equals(pseudoElement, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                if (!found) return;
            }

            foreach (var decl in styleRule.Declarations)
            {
                results.Add(new MatchedDeclaration
                {
                        Declaration = decl,
                        Origin = styleRule.Origin,
                        Specificity = matchedChain.Specificity,
                        Order = styleRule.Order,
                        SelectorText = styleRule.Selector.ToString(),
                        LayerOrder = styleRule.LayerOrder,
                        ScopeProximity = scopeProximity
                    });
                }
            }
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

            // Simple cases: single color, none, or transparent
            if (value == "none" || value == "transparent")
            {
                SetIfNotExplicit(computed, "background-color", "transparent", decl);
                SetIfNotExplicit(computed, "background-image", "none", decl);
                return;
            }

            // If it looks like just a color (no url, no gradient keywords)
            if (!value.Contains("url(") && !value.Contains("gradient("))
            {
                // Might be: "red", "#fff", "rgb(...)", etc. possibly with position/repeat
                var parts = SplitCssValue(value);
                foreach (var p in parts)
                {
                    if (IsBackgroundRepeat(p))
                        SetIfNotExplicit(computed, "background-repeat", p, decl);
                    else if (IsBackgroundPosition(p))
                        SetIfNotExplicit(computed, "background-position", p, decl);
                    else if (p == "fixed" || p == "scroll" || p == "local")
                        SetIfNotExplicit(computed, "background-attachment", p, decl);
                    else
                        SetIfNotExplicit(computed, "background-color", p, decl);
                }
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
    }

    public class MatchedDeclaration : IComparable<MatchedDeclaration>
    {
        public CssDeclaration Declaration { get; set; }
        public CssOrigin Origin { get; set; }
        public Specificity Specificity { get; set; }
        public int Order { get; set; }
        public string SelectorText { get; set; }
        public int LayerOrder { get; set; } // 0 for unlayered, >0 for layered
        public int ScopeProximity { get; set; } // 0 for no scope, >0 for scoped, smaller is closer

        public int CompareTo(MatchedDeclaration other)
        {
            // 1. Origin & Importance & Layers
            int thisWeight = GetWeight(Origin, Declaration.IsImportant, LayerOrder);
            int otherWeight = GetWeight(other.Origin, other.Declaration.IsImportant, other.LayerOrder);
            int weightDiff = thisWeight.CompareTo(otherWeight);
            if (weightDiff != 0) return weightDiff;

            // 2. Scope Proximity (Tie-breaker before specificity)
            // Smaller proximity means directly inside a shallower scope root? 
            // Actually, spec says scope with HEAVIER specificity or SHORTER proximity?
            // "Scope proximity" tie-breaker: the rule whose scope root is a CLOSER ancestor wins.
            // So SMALLER proximity value (closer) should have HIGHER weight.
            // Since CompareTo is ascending, we use reverse comparison for priority.
            if (ScopeProximity != other.ScopeProximity)
                return other.ScopeProximity.CompareTo(ScopeProximity);

            // 3. Specificity
            int specDiff = Specificity.CompareTo(other.Specificity);
            if (specDiff != 0) return specDiff;

            // 4. Order
            return Order.CompareTo(other.Order);
        }

        private int GetWeight(CssOrigin origin, bool important, int layerOrder)
        {
            // Standard Cascade Order (Level 5):
            // Normal: UA < User < Author Layer 1 < Layer 2 < Unlayered
            // Important: Unlayered !important < Layer 2 !important < Layer 1 !important < User !important < UA !important
            
            // We map this into a single integer score for sorting.
            // Base ranges:
            // 0-100: UA normal
            // 100-200: User normal
            // 200-300: Author layered (200 + layerOrder)
            // 300-310: Author unlayered
            // 400-410: Author unlayered !important
            // 410-510: Author layered !important (510 - layerOrder)
            // 600-700: User !important
            // 700-800: UA !important

            if (!important)
            {
                if (origin == CssOrigin.UserAgent) return 0;
                if (origin == CssOrigin.User) return 100;
                if (origin == CssOrigin.Author)
                {
                    if (layerOrder == 0) return 300; // Unlayered author rules win over layered
                    return 200 + Math.Min(layerOrder, 99); // Higher layerOrder wins
                }
            }
            else
            {
                if (origin == CssOrigin.Author)
                {
                    if (layerOrder == 0) return 400; // Unlayered important author rules are lowest priority in !important block
                    return 510 - Math.Min(layerOrder, 100); // LOWER layerOrder wins for !important
                }
                if (origin == CssOrigin.User) return 600;
                if (origin == CssOrigin.UserAgent) return 700;
            }
            
            return 0;
        }
    }
}

