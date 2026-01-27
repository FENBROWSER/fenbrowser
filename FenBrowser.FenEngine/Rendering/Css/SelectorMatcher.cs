using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS selector matcher implementing CSS Selectors Level 4.
    /// Replaces regex-based matching with proper parsing.
    /// </summary>
    public static class SelectorMatcher
    {
        #region Public API

        public static bool Matches(Element element, CssSelector selector, int depth = 0)
        {
            if (selector == null || depth > 64) return false;
            // Use pre-parsed chains if available
            if (selector.Chains != null && selector.Chains.Count > 0)
            {
                return selector.Chains.Any(chain => MatchesChain(element, chain, depth + 1));
            }
            // Fallback to raw parsing
            return Matches(element, selector.Raw, depth + 1);
        }

        public static SelectorChain GetMatchingChain(Element element, CssSelector selector, int depth = 0)
        {
            if (selector == null || element == null || depth > 64) return null;
            if (selector.Chains != null && selector.Chains.Count > 0)
            {
                SelectorChain best = null;
                foreach (var chain in selector.Chains)
                {
                    if (MatchesChain(element, chain, depth + 1))
                    {
                        if (best == null || chain.Specificity.CompareTo(best.Specificity) > 0)
                            best = chain;
                    }
                }
                return best;
            }
            // Fallback for raw string
            if (selector.Chains == null)
            {
                 selector.Chains = ParseSelectorList(selector.Raw);
            }
            
            if (selector.Chains != null && selector.Chains.Count > 0)
            {
                SelectorChain best = null;
                foreach (var chain in selector.Chains)
                {
                    if (MatchesChain(element, chain, depth + 1))
                    {
                        if (best == null || chain.Specificity.CompareTo(best.Specificity) > 0)
                            best = chain;
                    }
                }
                return best;
            }
            return null;
        }

        public static Specificity? GetMatchingSpecificity(Element element, CssSelector selector)
        {
            var chain = GetMatchingChain(element, selector);
            return chain?.Specificity;
        }

        /// <summary>
        /// Check if an element matches a selector string.
        /// </summary>
        public static bool Matches(Element element, string selector, int depth = 0)
        {
            if (element == null || string.IsNullOrWhiteSpace(selector) || depth > 64) return false;
            
            var parsed = ParseSelectorList(selector);
            return parsed.Any(chain => MatchesChain(element, chain, depth + 1));
        }

        /// <summary>
        /// Get the specificity of a selector.
        /// Returns (a, b, c) where a = IDs, b = classes/attributes/pseudo-classes, c = elements/pseudo-elements.
        /// </summary>
        public static (int a, int b, int c) GetSpecificity(string selector)
        {
            var parsed = ParseSelectorList(selector);
            if (parsed.Count == 0) return (0, 0, 0);
            
            // CRITICAL FIX: Use FirstOrDefault to prevent "Sequence contains no elements" exception
            var s = parsed.Select(c => c.Specificity).OrderByDescending(x => x).FirstOrDefault();
            return (s.A, s.B, s.C);
        }

        #endregion

        #region Selector Parsing

        /// <summary>
        /// Parse a selector list (comma-separated selectors).
        /// </summary>
        public static List<SelectorChain> ParseSelectorList(string selector)
        {
            var result = new List<SelectorChain>();
            if (string.IsNullOrWhiteSpace(selector)) return result;

            // Split by comma (respecting parentheses)
            var parts = SplitByComma(selector.Trim());
            
            foreach (var part in parts)
            {
                var chain = ParseChain(part.Trim());
                if (chain != null && chain.Segments.Count > 0)
                {
                    result.Add(chain);
                }
            }

            return result;
        }

        /// <summary>
        /// Parse a single selector chain (e.g., "div.class > span").
        /// </summary>
        private static SelectorChain ParseChain(string selector)
        {
            var chain = new SelectorChain();
            if (string.IsNullOrWhiteSpace(selector)) return chain;

            int i = 0;
            char combinator = ' ';

            while (i < selector.Length)
            {
                // Skip whitespace
                while (i < selector.Length && char.IsWhiteSpace(selector[i])) i++;
                if (i >= selector.Length) break;

                // Check for combinators
                char c = selector[i];
                if (c == '>' || c == '+' || c == '~')
                {
                    combinator = c;
                    i++;
                    continue;
                }

                // Parse simple selector
                var (segment, newPos) = ParseSimpleSelector(selector, i);
                if (segment != null)
                {
                    segment.Combinator = combinator;
                    chain.Segments.Add(segment);
                    combinator = ' '; // Reset to descendant
                }
                i = newPos;
            }

            return chain;
        }

        /// <summary>
        /// Parse a simple selector (tag, class, id, attributes, pseudo).
        /// </summary>
        private static (SelectorSegment segment, int newPos) ParseSimpleSelector(string selector, int start)
        {
            var segment = new SelectorSegment();
            int i = start;

            while (i < selector.Length)
            {
                char c = selector[i];

                // End of simple selector
                if (char.IsWhiteSpace(c) || c == '>' || c == '+' || c == '~' || c == ',')
                    break;

                // Class
                if (c == '.')
                {
                    i++;
                    var name = ReadIdent(selector, ref i);
                    if (!string.IsNullOrEmpty(name))
                        segment.Classes.Add(name);
                    continue;
                }

                // ID
                if (c == '#')
                {
                    i++;
                    segment.Id = ReadIdent(selector, ref i);
                    continue;
                }

                // Attribute selector
                if (c == '[')
                {
                    var (attr, endPos) = ParseAttributeSelector(selector, i);
                    if (attr != null) segment.Attributes.Add(attr);
                    i = endPos;
                    continue;
                }

                // Pseudo-class or pseudo-element
                if (c == ':')
                {
                    i++;
                    bool isElement = i < selector.Length && selector[i] == ':';
                    if (isElement) i++;

                    var name = ReadIdent(selector, ref i);
                    
                    // Check for functional pseudo-class
                    string args = null;
                    if (i < selector.Length && selector[i] == '(')
                    {
                        int depth = 1;
                        int argStart = ++i;
                        while (i < selector.Length && depth > 0)
                        {
                            if (selector[i] == '(') depth++;
                            else if (selector[i] == ')') depth--;
                            i++;
                        }
                        args = selector.Substring(argStart, i - argStart - 1);
                    }

                    if (isElement)
                        segment.PseudoElements.Add(new PseudoSelector { Name = name, Args = args });
                    else
                        segment.PseudoClasses.Add(new PseudoSelector { Name = name, Args = args });
                    continue;
                }

                // Tag name or universal
                if (c == '*')
                {
                    segment.Tag = "*";
                    i++;
                    continue;
                }

                if (char.IsLetter(c) || c == '-' || c == '_')
                {
                    segment.Tag = ReadIdent(selector, ref i);
                    continue;
                }

                i++;
            }

            return (segment.IsEmpty ? null : segment, i);
        }

        private static string ReadIdent(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_'))
                i++;
            return s.Substring(start, i - start);
        }

        private static (AttributeSelector attr, int endPos) ParseAttributeSelector(string s, int start)
        {
            if (s[start] != '[') return (null, start);
            
            int end = s.IndexOf(']', start);
            if (end == -1) return (null, s.Length);

            string content = s.Substring(start + 1, end - start - 1).Trim();
            var attr = new AttributeSelector();

            // Parse attribute name and optional operator/value
            int opIdx = -1;
            string[] ops = { "~=", "|=", "^=", "$=", "*=", "=" };
            foreach (var op in ops)
            {
                opIdx = content.IndexOf(op);
                if (opIdx >= 0)
                {
                    attr.Name = content.Substring(0, opIdx).Trim();
                    attr.Operator = op;
                    attr.Value = content.Substring(opIdx + op.Length).Trim().Trim('"', '\'');
                    
                    // Check for case-insensitive flag
                    if (attr.Value.EndsWith(" i") || attr.Value.EndsWith(" I"))
                    {
                        attr.CaseInsensitive = true;
                        attr.Value = attr.Value.Substring(0, attr.Value.Length - 2).Trim();
                    }
                    break;
                }
            }

            if (opIdx == -1)
            {
                attr.Name = content;
                attr.Operator = null; // Just presence check
            }

            return (attr, end + 1);
        }

        private static List<string> SplitByComma(string s)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(s.Substring(start));
            return result;
        }

        #endregion

        #region Matching

        private static bool MatchesChain(Element element, SelectorChain chain, int depth = 0)
        {
            if (chain == null || chain.Segments.Count == 0 || depth > 64) return false;
            return MatchesChainRecursive(element, chain, chain.Segments.Count - 1, depth + 1);
        }

        private static bool MatchesChainRecursive(Element element, SelectorChain chain, int index, int depth)
        {
            if (element == null || depth > 64) return false;
            
            var seg = chain.Segments[index];
            if (!MatchesSegment(element, seg, depth + 1)) return false;

            // Base case: matched the leftmost segment
            if (index == 0) return true;

            var combinator = seg.Combinator; // Combinator connecting this segment to the previous one (to the left)
            
            if (combinator == ' ') // Descendant
            {
                // Phase 2.1: Ancestor Bloom Filter Optimization
                if (CanFastReject(element, chain, index - 1))
                {
                    return false;
                }

                var ancestor = element.Parent as Element;
                while (ancestor != null)
                {
                    if (MatchesChainRecursive(ancestor, chain, index - 1, depth + 1))
                        return true;
                    ancestor = ancestor.Parent as Element;
                }
                return false;
            }
            else if (combinator == '>') // Child
            {
                return MatchesChainRecursive(element.Parent as Element, chain, index - 1, depth + 1);
            }
            else if (combinator == '+') // Adjacent Sibling
            {
                var prev = GetPreviousSibling(element);
                return MatchesChainRecursive(prev, chain, index - 1, depth + 1);
            }
            else if (combinator == '~') // General Sibling
            {
                var prev = GetPreviousSibling(element);
                while (prev != null)
                {
                    if (MatchesChainRecursive(prev, chain, index - 1, depth + 1))
                        return true;
                    prev = GetPreviousSibling(prev);
                }
                return false;
            }

            return false;
        }

        private static Element GetPreviousSibling(Element el)
        {
            var p = el.PreviousSibling;
            while (p != null)
            {
                if (p is Element e) return e;
                p = p.PreviousSibling;
            }
            return null;
        }

        private static bool MatchesSegment(Element el, SelectorSegment seg, int depth)
        {
            if (el == null || depth > 64) return false;

            // Tag
            if (!string.IsNullOrEmpty(seg.Tag) && seg.Tag != "*")
            {
                if (!string.Equals(el.Tag, seg.Tag, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // ID
            if (!string.IsNullOrEmpty(seg.Id))
            {
                string elId = el.Attr?.ContainsKey("id") == true ? el.Attr["id"] : null;
                if (!string.Equals(elId, seg.Id, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Classes
            if (seg.Classes.Count > 0)
            {
                string elClass = el.Attr?.ContainsKey("class") == true ? el.Attr["class"] : "";
                var elClasses = new HashSet<string>(
                    elClass.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);
                
                foreach (var cls in seg.Classes)
                {
                    if (!elClasses.Contains(cls)) return false;
                }
            }

            // Attributes
            foreach (var attr in seg.Attributes)
            {
                if (!MatchesAttribute(el, attr)) return false;
            }

            // Pseudo-classes
            foreach (var ps in seg.PseudoClasses)
            {
                if (!MatchesPseudoClass(el, ps.Name, ps.Args, ps.ParsedArgs, depth + 1)) return false;
            }
            
            // Pseudo-elements (::slotted)
            foreach (var ps in seg.PseudoElements)
            {
                if (string.Equals(ps.Name, "slotted", StringComparison.OrdinalIgnoreCase))
                {
                    if (el.Parent is Element parent && parent.ShadowRoot != null)
                    {
                        return string.IsNullOrEmpty(ps.Args) || Matches(el, ps.Args, depth + 1);
                    }
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesAttribute(Element el, AttributeSelector attr)
        {
            string val = el.Attr?.ContainsKey(attr.Name) == true ? el.Attr[attr.Name] : null;

            if (attr.Operator == null)
                return val != null; // Presence check

            if (val == null) return false;

            var comp = attr.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return attr.Operator switch
            {
                "=" => string.Equals(val, attr.Value, comp),
                "~=" => val.Split(' ').Any(v => string.Equals(v, attr.Value, comp)),
                "|=" => string.Equals(val, attr.Value, comp) || val.StartsWith(attr.Value + "-", comp),
                "^=" => val.StartsWith(attr.Value, comp),
                "$=" => val.EndsWith(attr.Value, comp),
                "*=" => val.IndexOf(attr.Value, comp) >= 0,
                _ => false
            };
        }

        private static bool MatchesPseudoClass(Element el, string name, string args, List<SelectorChain> parsedArgs, int depth)
        {
            if (depth > 64) return false;
            switch (name.ToLowerInvariant())
            {
                case "first-child": return IsFirstChild(el);
                case "last-child": return IsLastChild(el);
                case "only-child": return IsOnlyChild(el);
                case "first-of-type": return IsFirstOfType(el);
                case "last-of-type": return IsLastOfType(el);
                case "only-of-type": return IsFirstOfType(el) && IsLastOfType(el);
                case "empty": return el.Children == null || el.Children.Count == 0;
                case "root": return el.Parent == null || el.Tag?.ToUpperInvariant() == "HTML";
                case "not": 
                {
                    if (parsedArgs != null && parsedArgs.Count > 0)
                        return !parsedArgs.Any(chain => MatchesChain(el, chain, depth + 1));
                    return !Matches(el, args, depth + 1);
                }
                case "is":
                case "where": 
                {
                    if (parsedArgs != null && parsedArgs.Count > 0)
                        return parsedArgs.Any(chain => MatchesChain(el, chain, depth + 1));
                    return ParseSelectorList(args).Any(chain => MatchesChain(el, chain, depth + 1));
                }
                case "has": return MatchesHas(el, args, depth + 1);
                case "nth-child": return MatchesNthChild(el, args);
                case "nth-last-child": return MatchesNthLastChild(el, args);
                case "nth-of-type": return MatchesNthOfType(el, args);
                case "nth-last-of-type": return MatchesNthLastOfType(el, args);
                case "checked": return el.Attr?.ContainsKey("checked") == true;
                case "disabled": return el.Attr?.ContainsKey("disabled") == true;
                case "enabled": return el.Attr?.ContainsKey("disabled") != true;
                case "focus":
                case "hover":
                case "active":
                case "visited":
                case "link": return false; // State-dependent not implemented in static matcher
                
                // Shadow DOM Scoping
                case "host": 
                    // Matches if element is a Shadow Host
                    // Ideally we should check if we are matching a rule from the corresponding ShadowRoot,
                    // but without context, we check if it *is* a host.
                    // If args provided :host(.foo), check that too.
                    if (el.ShadowRoot == null) return false;
                    return string.IsNullOrEmpty(args) || Matches(el, args, depth + 1);

                case "host-context":
                    // Matches if element is a Shadow Host AND has an ancestor matching the selector
                    if (el.ShadowRoot == null) return false;
                    if (string.IsNullOrEmpty(args)) return true;
                    
                    // Check ancestors
                    var curr = el; // host-context checks self? Spec says "search up the tree... including the host itself".
                    while (curr != null)
                    {
                        if (Matches(curr, args, depth + 1)) return true;
                        curr = curr.Parent as Element;
                    }
                    return false;

                default: return false;
            }
        }

        private static bool IsFirstChild(Element el)
        {
            if (el.Parent?.Children == null) return true;
            return el.Parent.Children.FirstOrDefault(c => !c.IsText) == el;
        }

        private static bool IsLastChild(Element el)
        {
            if (el.Parent?.Children == null) return true;
            return el.Parent.Children.LastOrDefault(c => !c.IsText) == el;
        }

        private static bool IsOnlyChild(Element el)
        {
            if (el.Parent?.Children == null) return true;
            return el.Parent.Children.Count(c => !c.IsText) == 1;
        }



        private static bool IsFirstOfType(Element el)
        {
             if (el.Parent?.Children == null) return true;
             return el.Parent.Children.OfType<Element>().FirstOrDefault(c => string.Equals(c.Tag, el.Tag, StringComparison.OrdinalIgnoreCase)) == el;
        }

        private static bool IsLastOfType(Element el)
        {
             if (el.Parent?.Children == null) return true;
             return el.Parent.Children.OfType<Element>().LastOrDefault(c => string.Equals(c.Tag, el.Tag, StringComparison.OrdinalIgnoreCase)) == el;
        }

        private static bool MatchesNthChild(Element el, string args)
        {
            if (el.Parent?.Children == null) return false;
            int index = el.Parent.Children.Where(c => !c.IsText).ToList().IndexOf(el) + 1;
            return MatchesNthFormula(index, args);
        }

        private static bool MatchesNthLastChild(Element el, string args)
        {
            if (el.Parent?.Children == null) return false;
            var siblings = el.Parent.Children.Where(c => !c.IsText).ToList();
            int index = siblings.Count - siblings.IndexOf(el);
            return MatchesNthFormula(index, args);
        }

        private static bool MatchesNthOfType(Element el, string args)
        {
            if (el.Parent?.Children == null) return false;
            var siblings = el.Parent.Children.OfType<Element>().Where(c => string.Equals(c.Tag, el.Tag, StringComparison.OrdinalIgnoreCase)).ToList();
            int index = siblings.IndexOf(el) + 1;
            return MatchesNthFormula(index, args);
        }

        private static bool MatchesNthLastOfType(Element el, string args)
        {
            if (el.Parent?.Children == null) return false;
            var siblings = el.Parent.Children.OfType<Element>().Where(c => string.Equals(c.Tag, el.Tag, StringComparison.OrdinalIgnoreCase)).ToList();
            int index = siblings.Count - siblings.IndexOf(el);
            return MatchesNthFormula(index, args);
        }

        private static bool MatchesHas(Element el, string relativeSelectors, int depth)
        {
            var parsed = ParseSelectorList(relativeSelectors);
            foreach (var chain in parsed)
            {
                if (chain.Segments.Count == 0) continue;
                
                var first = chain.Segments[0];
                var combinator = first.Combinator;
                
                // Identify candidates based on combinator relative to 'el'
                IEnumerable<Element> candidates = null;
                
                if (combinator == '>') // Child
                {
                    candidates = el.Children?.OfType<Element>();
                }
                else if (combinator == '+') // Adjacent Sibling
                {
                    var next = GetNextSibling(el);
                    if (next != null) candidates = new[] { next };
                }
                else if (combinator == '~') // General Sibling
                {
                    candidates = GetFollowingSiblings(el);
                }
                else // Descendant (Space)
                {
                    candidates = el.Descendants().OfType<Element>();
                }
                
                if (candidates != null)
                {
                    foreach (var cand in candidates)
                    {
                        if (MatchesChain(cand, chain, depth + 1)) return true;
                    }
                }
            }
            return false;
        }

        private static Element GetNextSibling(Element el)
        {
            var p = el.NextSibling;
            while (p != null)
            {
                if (p is Element e) return e;
                p = p.NextSibling;
            }
            return null;
        }

        private static List<Element> GetFollowingSiblings(Element el)
        {
             var result = new List<Element>();
             var p = el.NextSibling;
             while (p != null)
             {
                 if (p is Element e) result.Add(e);
                 p = p.NextSibling;
             }
             return result;
        }

        private static bool MatchesNthFormula(int n, string formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return false;
            formula = formula.Trim().ToLowerInvariant();

            if (formula == "odd") return n % 2 == 1;
            if (formula == "even") return n % 2 == 0;
            if (int.TryParse(formula, out int exact)) return n == exact;

            // Parse An+B format
            int a = 0, b = 0;
            int nIdx = formula.IndexOf('n');
            if (nIdx >= 0)
            {
                string aStr = formula.Substring(0, nIdx).Trim();
                if (aStr == "" || aStr == "+") a = 1;
                else if (aStr == "-") a = -1;
                else int.TryParse(aStr, out a);

                string bStr = formula.Substring(nIdx + 1).Trim();
                if (!string.IsNullOrEmpty(bStr))
                    int.TryParse(bStr.Replace("+", "").Replace(" ", ""), out b);
            }
            else
            {
                int.TryParse(formula, out b);
            }

            if (a == 0) return n == b;
            return (n - b) % a == 0 && (n - b) / a >= 0;
        }

        #endregion
        #region Bloom Filter Optimization

        private static bool CanFastReject(Element element, SelectorChain chain, int index)
        {
            // We need to check if the ancestor filter LACKS any bits required by the target segment.
            // The target segment (at 'index') is what we are looking for in the ancestors.
            // If the selector is "div.container span", we are at span, looking for "div.container".
            // So we compute the hash of "div.container" (segment[index]) and check if element.AncestorFilter has it.
            
            var targetSegment = chain.Segments[index];
            long requiredMask = ComputeSegmentHash(targetSegment);

            // If requiredMask has bits that are 0 in AncestorFilter, then the ancestor definitely doesn't exist.
            // (element.AncestorFilter & requiredMask) must equal requiredMask
            return (element.AncestorFilter & requiredMask) != requiredMask;
        }

        private static long ComputeSegmentHash(SelectorSegment seg)
        {
            long hash = 0;

            // Tag
            if (!string.IsNullOrEmpty(seg.Tag) && seg.Tag != "*")
            {
                hash |= FilterHash(seg.Tag);
            }

            // ID
            if (!string.IsNullOrEmpty(seg.Id))
            {
                hash |= FilterHash("#" + seg.Id);
            }

            // Classes
            foreach (var cls in seg.Classes)
            {
                hash |= FilterHash("." + cls);
            }

            return hash;
        }

        private static long FilterHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++)
            {
                h = (h ^ s[i]) * 16777619;
            }
            int b1 = (int)(h % 64);
            int b2 = (int)((h >> 6) % 64);
            return (1L << b1) | (1L << b2);
        }

        #endregion
    }
}

