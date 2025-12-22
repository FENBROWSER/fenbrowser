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

        /// <summary>
        /// Check if an element matches a selector string.
        /// </summary>
        public static bool Matches(Element element, string selector)
        {
            if (element == null || string.IsNullOrWhiteSpace(selector)) return false;
            
            var parsed = ParseSelectorList(selector);
            return parsed.Any(chain => MatchesChain(element, chain));
        }

        /// <summary>
        /// Get the specificity of a selector.
        /// Returns (a, b, c) where a = IDs, b = classes/attributes/pseudo-classes, c = elements/pseudo-elements.
        /// </summary>
        public static (int a, int b, int c) GetSpecificity(string selector)
        {
            var parsed = ParseSelectorList(selector);
            if (parsed.Count == 0) return (0, 0, 0);
            
            // Return highest specificity among selector list
            return parsed.Select(c => c.Specificity).OrderByDescending(s => s).First();
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
                        segment.PseudoElements.Add((name, args));
                    else
                        segment.PseudoClasses.Add((name, args));
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

        private static bool MatchesChain(Element element, SelectorChain chain)
        {
            if (chain.Segments.Count == 0) return false;

            // Match from right to left
            var current = element;
            for (int i = chain.Segments.Count - 1; i >= 0; i--)
            {
                var seg = chain.Segments[i];
                
                if (!MatchesSegment(current, seg))
                    return false;

                if (i == 0) break;

                // Navigate based on next combinator
                var nextSeg = chain.Segments[i - 1];
                current = FindMatchingAncestor(current, nextSeg.Combinator);
                if (current == null) return false;
            }

            return true;
        }

        private static Element FindMatchingAncestor(Element el, char combinator)
        {
            return combinator switch
            {
                ' ' => el.Parent as Element,  // Descendant - next segment can match any ancestor
                '>' => el.Parent as Element,  // Child - next segment must match parent
                '+' => GetPreviousSibling(el), // Adjacent sibling
                '~' => GetPreviousSibling(el), // General sibling
                _ => el.Parent as Element
            };
        }

        private static Element GetPreviousSibling(Element el)
        {
            var parent = el.Parent as Element;
            if (parent?.Children == null) return null;
            var siblings = parent.Children.OfType<Element>().ToList();
            int idx = siblings.IndexOf(el);
            return idx > 0 ? siblings[idx - 1] : null;
        }

        private static bool MatchesSegment(Element el, SelectorSegment seg)
        {
            if (el == null) return false;

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
            foreach (var (name, args) in seg.PseudoClasses)
            {
                if (!MatchesPseudoClass(el, name, args)) return false;
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

        private static bool MatchesPseudoClass(Element el, string name, string args)
        {
            return name.ToLowerInvariant() switch
            {
                "first-child" => IsFirstChild(el),
                "last-child" => IsLastChild(el),
                "only-child" => IsOnlyChild(el),
                "empty" => el.Children == null || el.Children.Count == 0,
                "root" => el.Parent == null || el.Tag?.ToUpperInvariant() == "HTML",
                "not" => !Matches(el, args),
                "is" or "where" => ParseSelectorList(args).Any(chain => MatchesChain(el, chain)),
                "has" => el.Children?.OfType<Element>().Any(c => Matches(c, args)) == true,
                "nth-child" => MatchesNthChild(el, args),
                "nth-last-child" => MatchesNthLastChild(el, args),
                "checked" => el.Attr?.ContainsKey("checked") == true,
                "disabled" => el.Attr?.ContainsKey("disabled") == true,
                "enabled" => el.Attr?.ContainsKey("disabled") != true,
                "focus" or "hover" or "active" or "visited" or "link" => false, // State-dependent
                _ => false
            };
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
    }

    #region Data Structures

    public class SelectorChain : IComparable<SelectorChain>
    {
        public List<SelectorSegment> Segments { get; } = new List<SelectorSegment>();

        public (int a, int b, int c) Specificity
        {
            get
            {
                int a = 0, b = 0, c = 0;
                foreach (var seg in Segments)
                {
                    if (!string.IsNullOrEmpty(seg.Id)) a++;
                    b += seg.Classes.Count + seg.Attributes.Count + seg.PseudoClasses.Count;
                    if (!string.IsNullOrEmpty(seg.Tag) && seg.Tag != "*") c++;
                    c += seg.PseudoElements.Count;
                }
                return (a, b, c);
            }
        }

        public int CompareTo(SelectorChain other)
        {
            var (a1, b1, c1) = Specificity;
            var (a2, b2, c2) = other.Specificity;
            int cmp = a1.CompareTo(a2);
            if (cmp != 0) return cmp;
            cmp = b1.CompareTo(b2);
            if (cmp != 0) return cmp;
            return c1.CompareTo(c2);
        }
    }

    public class SelectorSegment
    {
        public string Tag { get; set; }
        public string Id { get; set; }
        public List<string> Classes { get; } = new List<string>();
        public List<AttributeSelector> Attributes { get; } = new List<AttributeSelector>();
        public List<(string name, string args)> PseudoClasses { get; } = new List<(string, string)>();
        public List<(string name, string args)> PseudoElements { get; } = new List<(string, string)>();
        public char Combinator { get; set; } = ' ';

        public bool IsEmpty => string.IsNullOrEmpty(Tag) && string.IsNullOrEmpty(Id) && 
                               Classes.Count == 0 && Attributes.Count == 0 && 
                               PseudoClasses.Count == 0 && PseudoElements.Count == 0;
    }

    public class AttributeSelector
    {
        public string Name { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }
        public bool CaseInsensitive { get; set; }
    }

    #endregion
}

