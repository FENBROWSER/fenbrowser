using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
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

        public CascadeEngine(CssStylesheet stylesheet)
        {
            _stylesheet = stylesheet;
            _logCascade = FenBrowser.Core.Logging.DebugConfig.LogCssCascade;
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
            // Get the rightmost (key) segment from selector - this is what must match
            SelectorSegment keySeg = null;
            if (styleRule.Selector?.Chains?.Count > 0)
            {
                var firstChain = styleRule.Selector.Chains[0];
                if (firstChain.Segments?.Count > 0)
                {
                    keySeg = firstChain.Segments[firstChain.Segments.Count - 1];
                }
            }
            
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
            else if (!string.IsNullOrEmpty(keySeg.Tag) && keySeg.Tag != "*")
            {
                AddToIndex(_tagIndex, keySeg.Tag.ToUpperInvariant(), styleRule);
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

        public Dictionary<string, CssDeclaration> ComputeCascadedValues(Element element, string pseudoElement = null, RenderDeadline deadline = null)
        {
            EnsureIndex();
            var results = new List<MatchedDeclaration>();
            _processedRules.Clear();

            // 1. Gather all declarations from matching rules (priority order: ID > Classes > Tag > Universal)
            CollectMatches(element, results, pseudoElement);

            if (_logCascade && results.Count > 0)
            {
                var uniqueSources = results.Select(r => new { r.SelectorText, Spec = r.Specificity, r.Origin }).Distinct().OrderBy(x => x.Spec).ToList();
                var msg = new System.Text.StringBuilder();
                msg.AppendLine($"[CASCADE] Element <{element.Tag}> matched {uniqueSources.Count} rules:");
                foreach(var src in uniqueSources)
                {
                    msg.AppendLine($"   - [{src.Origin}] {src.SelectorText} :: {src.Spec}");
                }
                global::FenBrowser.Core.FenLogger.Log(msg.ToString().TrimEnd(), global::FenBrowser.Core.Logging.LogCategory.Cascade);
            }

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

            return computed;
        }

        private void CollectMatches(Element element, List<MatchedDeclaration> results, string pseudoElement)
        {
            // Get attributes via Attr dictionary
            string elemId = null;
            string elemClass = null;
            if (element.Attr != null)
            {
                element.Attr.TryGetValue("id", out elemId);
                element.Attr.TryGetValue("class", out elemClass);
            }
            
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
                var classes = elemClass.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
            string tag = element.Tag?.ToUpperInvariant();
            if (!string.IsNullOrEmpty(tag) && _tagIndex.TryGetValue(tag, out var tagRules))
            {
                foreach (var rule in tagRules)
                {
                    if (_processedRules.Add(rule))
                        TryMatchRule(element, rule, results, pseudoElement);
                }
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
                var current = element.Parent as Element;
                int dist = 1;
                bool scopeMatched = false;
                while (current != null)
                {
                    if (CssLoader.MatchesSelector(current, styleRule.ScopeSelector))
                    {
                        scopeProximity = dist;
                        scopeMatched = true;
                        break;
                    }
                    current = current.Parent as Element;
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
                        if (pe.name.Equals(pseudoElement, StringComparison.OrdinalIgnoreCase))
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
