// SpecRef: Selectors Level 4
// CapabilityId: CSS-SELECTOR-MATCH-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS selector matcher implementing CSS Selectors Level 4.
    /// Replaces regex-based matching with proper parsing.
    /// </summary>
    public static class SelectorMatcher
    {
        private const int MaxSelectorLength = 16384;
        private const int MaxSelectorParseDepth = 32;
        private const int MaxSelectorChains = 2048;
        private const int MaxSegmentsPerChain = 4096;

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
            return ParseSelectorListInternal(selector, 0);
        }

        private static List<SelectorChain> ParseSelectorListInternal(string selector, int depth)
        {
            var result = new List<SelectorChain>();
            if (string.IsNullOrWhiteSpace(selector)) return result;
            if (depth > MaxSelectorParseDepth) return result;
            if (selector.Length > MaxSelectorLength) return result;

            // Split by comma (respecting parentheses)
            var parts = SplitByComma(selector);
            
            foreach (var part in parts)
            {
                if (result.Count >= MaxSelectorChains)
                {
                    break;
                }

                var chain = ParseChain(part, depth);
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
        private static SelectorChain ParseChain(string selector, int depth)
        {
            var chain = new SelectorChain();
            if (string.IsNullOrWhiteSpace(selector)) return chain;

            int i = 0;
            char combinator = ' ';
            int safety = 0;

            while (i < selector.Length)
            {
                if (safety++ > selector.Length * 4 + 64)
                {
                    break;
                }

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

                // Unexpected comma in a chain should not trap parser progress.
                if (c == ',')
                {
                    i++;
                    continue;
                }

                // Parse simple selector
                int before = i;
                var (segment, newPos) = ParseSimpleSelector(selector, i, depth);
                if (segment != null)
                {
                    segment.Combinator = combinator;
                    chain.Segments.Add(segment);
                    combinator = ' '; // Reset to descendant

                    if (chain.Segments.Count >= MaxSegmentsPerChain)
                    {
                        break;
                    }
                }
                else if (newPos > before)
                {
                    // A malformed compound selector invalidates the whole chain.
                    chain.Segments.Clear();
                    return chain;
                }

                // Hard progress guard for malformed selectors.
                i = newPos <= before ? before + 1 : newPos;
            }

            return chain;
        }

        /// <summary>
        /// Parse a simple selector (tag, class, id, attributes, pseudo).
        /// </summary>
        private static (SelectorSegment segment, int newPos) ParseSimpleSelector(string selector, int start, int depth)
        {
            var segment = new SelectorSegment();
            int i = start;
            var isInvalid = false;

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
                    if (string.IsNullOrEmpty(name))
                    {
                        isInvalid = true;
                        break;
                    }

                    segment.Classes.Add(name);
                    continue;
                }

                // ID
                if (c == '#')
                {
                    i++;
                    var id = ReadIdent(selector, ref i);
                    if (string.IsNullOrEmpty(id))
                    {
                        isInvalid = true;
                        break;
                    }

                    segment.Id = id;
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
                    if (string.IsNullOrEmpty(name))
                    {
                        isInvalid = true;
                        break;
                    }

                    // CSS2 compatibility: :before/:after/:first-line/:first-letter
                    // are pseudo-elements even with a single colon.
                    if (!isElement)
                    {
                        string lower = name.ToLowerInvariant();
                        if (lower == "before" || lower == "after" || lower == "first-line" || lower == "first-letter")
                        {
                            isElement = true;
                        }
                    }
                    
                    // Check for functional pseudo-class
                    string args = null;
                    if (i < selector.Length && selector[i] == '(')
                    {
                        int parenDepth = 1;
                        int argStart = ++i;
                        while (i < selector.Length && parenDepth > 0)
                        {
                            if (selector[i] == '(') parenDepth++;
                            else if (selector[i] == ')') parenDepth--;
                            i++;
                        }
                        args = selector.Substring(argStart, i - argStart - 1);
                    }

                    var pseudo = new PseudoSelector { Name = name, Args = args };
                    if (!isElement && !string.IsNullOrWhiteSpace(args))
                    {
                        var lowerName = name.ToLowerInvariant();
                        if (lowerName == "is" || lowerName == "not" || lowerName == "where" || lowerName == "has")
                        {
                            pseudo.ParsedArgs = ParseSelectorListInternal(args, depth + 1);
                        }
                        else if (lowerName == "nth-child" || lowerName == "nth-last-child")
                        {
                            ParseNthArguments(args, out _, out var ofSelector);
                            if (!string.IsNullOrWhiteSpace(ofSelector))
                            {
                                pseudo.ParsedArgs = ParseSelectorListInternal(ofSelector, depth + 1);
                            }
                        }
                    }

                    if (isElement)
                        segment.PseudoElements.Add(pseudo);
                    else
                        segment.PseudoClasses.Add(pseudo);
                    continue;
                }

                // Tag name or universal
                if (c == '*')
                {
                    if (!string.IsNullOrEmpty(segment.TagName))
                    {
                        isInvalid = true;
                        break;
                    }

                    segment.TagName = "*";
                    i++;
                    continue;
                }

                if (char.IsLetter(c) || c == '-' || c == '_')
                {
                    if (!string.IsNullOrEmpty(segment.TagName))
                    {
                        isInvalid = true;
                        break;
                    }

                    segment.TagName = ReadIdent(selector, ref i);
                    continue;
                }

                isInvalid = true;
                i++;
                break;
            }

            return (isInvalid || segment.IsEmpty ? null : segment, i);
        }

        private static string ReadIdent(string s, ref int i)
        {
            var result = new System.Text.StringBuilder();

            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 127)
                {
                    result.Append(c);
                    i++;
                    continue;
                }

                if (c == '\\')
                {
                    if (!TryReadEscapedCodePoint(s, ref i, out var escaped))
                    {
                        break;
                    }

                    result.Append(escaped);
                    continue;
                }

                break;
            }

            return result.ToString();
        }

        private static string UnescapeCssValue(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            var result = new System.Text.StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '\\')
                {
                    if (!TryReadEscapedCodePoint(raw, ref i, out var escaped))
                    {
                        break;
                    }

                    result.Append(escaped);
                    continue;
                }

                result.Append(raw[i]);
                i++;
            }

            return result.ToString();
        }

        private static bool TryReadEscapedCodePoint(string s, ref int i, out string escaped)
        {
            escaped = string.Empty;
            if (i >= s.Length || s[i] != '\\')
            {
                return false;
            }

            i++;
            if (i >= s.Length)
            {
                return false;
            }

            int hexStart = i;
            int hexLen = 0;
            while (i < s.Length && hexLen < 6 && IsHexDigit(s[i]))
            {
                i++;
                hexLen++;
            }

            if (hexLen > 0)
            {
                string hex = s.Substring(hexStart, hexLen);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int codePoint))
                {
                    escaped = char.ConvertFromUtf32(Math.Clamp(codePoint, 0, 0x10FFFF));
                }
                else
                {
                    escaped = s.Substring(hexStart, hexLen);
                }

                if (i < s.Length && char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                return true;
            }

            escaped = s[i].ToString();
            i++;
            return true;
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
        }

        private static (AttributeSelector attr, int endPos) ParseAttributeSelector(string s, int start)
        {
            if (s[start] != '[') return (null, start);

            int end = FindAttributeClosingBracket(s, start + 1);
            if (end == -1) return (null, s.Length);

            string content = s.Substring(start + 1, end - start - 1).Trim();
            var attr = new AttributeSelector();

            // Parse attribute name and optional operator/value
            var ops = new[] { "~=", "|=", "^=", "$=", "*=", "=" };
            int opIdx = FindAttributeOperatorIndex(content, ops, out var matchedOperator);

            if (opIdx >= 0)
            {
                attr.Name = UnescapeCssValue(content.Substring(0, opIdx).Trim());
                attr.Operator = matchedOperator;

                var rhs = content.Substring(opIdx + matchedOperator.Length).Trim();
                ParseAttributeValueAndFlags(rhs, out var value, out var caseInsensitive);
                attr.Value = value;
                attr.CaseInsensitive = caseInsensitive;
            }
            else
            {
                attr.Name = UnescapeCssValue(content);
                attr.Operator = null; // Presence check
            }

            return (attr, end + 1);
        }

        private static int FindAttributeClosingBracket(string s, int start)
        {
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    i++;
                    continue;
                }

                if (!inDoubleQuote && c == '\'')
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (!inSingleQuote && c == '"')
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote && c == ']')
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindAttributeOperatorIndex(string content, string[] operators, out string matchedOperator)
        {
            matchedOperator = null;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '\\' && i + 1 < content.Length)
                {
                    i++;
                    continue;
                }

                if (!inDoubleQuote && c == '\'')
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (!inSingleQuote && c == '"')
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                {
                    continue;
                }

                foreach (var op in operators)
                {
                    if (i + op.Length <= content.Length &&
                        string.Compare(content, i, op, 0, op.Length, StringComparison.Ordinal) == 0)
                    {
                        matchedOperator = op;
                        return i;
                    }
                }
            }

            return -1;
        }

        private static void ParseAttributeValueAndFlags(string rhs, out string value, out bool caseInsensitive)
        {
            value = string.Empty;
            caseInsensitive = false;

            if (string.IsNullOrWhiteSpace(rhs))
            {
                return;
            }

            string tail = string.Empty;
            rhs = rhs.Trim();

            if (rhs.Length > 0 && (rhs[0] == '"' || rhs[0] == '\''))
            {
                char quote = rhs[0];
                int endQuote = 1;
                while (endQuote < rhs.Length)
                {
                    if (rhs[endQuote] == '\\' && endQuote + 1 < rhs.Length)
                    {
                        endQuote += 2;
                        continue;
                    }

                    if (rhs[endQuote] == quote)
                    {
                        break;
                    }

                    endQuote++;
                }

                if (endQuote >= rhs.Length)
                {
                    value = UnescapeCssValue(rhs.Substring(1));
                    return;
                }

                value = UnescapeCssValue(rhs.Substring(1, Math.Max(0, endQuote - 1)));
                tail = rhs.Substring(endQuote + 1).Trim();
            }
            else
            {
                int ws = 0;
                while (ws < rhs.Length)
                {
                    if (rhs[ws] == '\\' && ws + 1 < rhs.Length)
                    {
                        ws += 2;
                        continue;
                    }

                    if (char.IsWhiteSpace(rhs[ws]))
                    {
                        break;
                    }

                    ws++;
                }

                value = UnescapeCssValue(rhs.Substring(0, ws).Trim());
                tail = ws < rhs.Length ? rhs.Substring(ws).Trim() : string.Empty;
            }

            if (tail.Length == 0)
            {
                return;
            }

            // Attribute selector flags: i (ASCII case-insensitive), s (case-sensitive).
            // Case-sensitive is already default behavior; we only need to toggle for i.
            if (tail.Equals("i", StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = true;
            }
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

        public static bool MatchesChain(Element element, SelectorChain chain, int depth = 0)
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

                var ancestor = element.ParentElement; // Using ParentElement
                while (ancestor != null)
                {
                    if (MatchesChainRecursive(ancestor, chain, index - 1, depth + 1))
                        return true;
                    ancestor = ancestor.ParentElement;
                }
                return false;
            }
            else if (combinator == '>') // Child
            {
                return MatchesChainRecursive(element.ParentElement, chain, index - 1, depth + 1);
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
            var p = el.PreviousSibling; // Using V2 Node property
            while (p != null)
            {
                if (p is Element e) return e;
                p = p.PreviousSibling;
            }
            return null;
        }

        public static bool MatchesSegment(Element el, SelectorSegment seg, int depth)
        {
            if (el == null || depth > 64) return false;

            // Tag
            if (!string.IsNullOrEmpty(seg.TagName) && seg.TagName != "*")
                if (!string.Equals(el.TagName, seg.TagName, StringComparison.OrdinalIgnoreCase)) // TagName
                    return false;


            // ID
            if (!string.IsNullOrEmpty(seg.Id))
            {
                string elId = el.Id; // Using V2 Property
                if (!string.Equals(elId, seg.Id, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Classes
            if (seg.Classes.Count > 0)
            {
                // Using V2 ClassList
                foreach (var cls in seg.Classes)
                {
                    if (!el.ClassList.Contains(cls)) return false;
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
                    if (el.ParentElement is Element parent && parent.ShadowRoot != null) // ParentElement check
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
            string val = el.GetAttribute(attr.Name); // V2 GetAttribute

            if (attr.Operator == null)
                return val != null; // Presence check (V2 returns null if missing)

            if (val == null) return false;

            var comp = attr.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return attr.Operator switch
            {
                "=" => string.Equals(val, attr.Value, comp),
                "~=" => val.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries)
                           .Any(v => string.Equals(v, attr.Value, comp)),
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
                case "empty": return IsEmptyElement(el);
                case "scope": return IsScope(el);
                case "root": return el.ParentElement == null || el.TagName?.ToUpperInvariant() == "HTML";
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
                case "has": return MatchesHas(el, args, parsedArgs, depth + 1);
                case "nth-child": return MatchesNthChild(el, args, parsedArgs, depth + 1);
                case "nth-last-child": return MatchesNthLastChild(el, args, parsedArgs, depth + 1);
                case "nth-of-type": return MatchesNthOfType(el, args);
                case "nth-last-of-type": return MatchesNthLastOfType(el, args);
                case "checked": return IsCheckedFormControl(el);
                case "disabled": return SupportsEnabledDisabledPseudoClass(el) && ElementStateManager.Instance.IsDisabled(el);
                case "enabled": return SupportsEnabledDisabledPseudoClass(el) && !ElementStateManager.Instance.IsDisabled(el);
                case "focus": return ElementStateManager.Instance.IsFocused(el);
                case "hover": return ElementStateManager.Instance.IsHovered(el);
                case "active": return ElementStateManager.Instance.IsActive(el);
                case "visited": return ElementStateManager.Instance.IsVisited(el);
                case "link": 
                case "any-link":
                    if (!string.Equals(el.TagName, "a", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(el.TagName, "area", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (string.IsNullOrWhiteSpace(el.GetAttribute("href")))
                        return false;
                    return string.Equals(name, "any-link", StringComparison.OrdinalIgnoreCase) || !ElementStateManager.Instance.IsVisited(el);
                
                // Shadow DOM Scoping
                case "host": 
                    // Matches if element is a Shadow Host
                    if (el.ShadowRoot == null) return false;
                    return string.IsNullOrEmpty(args) || Matches(el, args, depth + 1);

                case "host-context":
                    // Matches if element is a Shadow Host AND has an ancestor matching the selector
                    if (el.ShadowRoot == null) return false;
                    if (string.IsNullOrEmpty(args)) return true;
                    
                    // Check ancestors
                    var curr = el; 
                    while (curr != null)
                    {
                        if (Matches(curr, args, depth + 1)) return true;
                        curr = curr.ParentElement;
                    }
                    return false;

                case "dir":
                    // Matches directionality: :dir(ltr) or :dir(rtl)
                    string requiredDir = args?.Trim().ToLowerInvariant();
                    if (requiredDir != "ltr" && requiredDir != "rtl") return false;

                    // Find effective direction
                    string effectiveDir = "ltr"; // Default
                    var dNode = el;
                    while (dNode != null)
                    {
                        var dVal = dNode.GetAttribute("dir"); // V2 GetAttribute
                        if (dVal != null)
                        {
                            var v = dVal.Trim().ToLowerInvariant();
                            if (v == "ltr" || v == "rtl")
                            {
                                effectiveDir = v;
                                break;
                            }
                        }
                        dNode = dNode.ParentElement;
                    }
                    return effectiveDir == requiredDir;

                default: return false;
            }
        }

        private static bool IsFirstChild(Element el)
        {
            var parent = el.ParentNode as ContainerNode; // Cast to ContainerNode
            if (parent == null) return true;
            return parent.ChildNodes.OfType<Element>().FirstOrDefault() == el;
        }

        private static bool IsEmptyElement(Element el)
        {
            for (var child = el.FirstChild; child != null; child = child.NextSibling)
            {
                // Per Selectors spec, any text node (including whitespace) disqualifies :empty.
                if (child is Text) return false;
                if (child is Element) return false;
            }

            return true;
        }

        private static bool SupportsEnabledDisabledPseudoClass(Element el)
        {
            var tagName = el?.TagName;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }

            return string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "button", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "select", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "textarea", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "fieldset", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCheckedFormControl(Element el)
        {
            var tagName = el?.TagName;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }

            if (string.Equals(tagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                return el.HasAttribute("selected");
            }

            if (!string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = el.GetAttribute("type");
            if (!string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ElementStateManager.Instance.IsChecked(el);
        }

        private static bool IsScope(Element el)
        {
            // For stylesheet matching without an explicit query scope context,
            // align :scope with the document root / detached root element.
            return el.OwnerDocument?.DocumentElement == el || el.ParentElement == null;
        }

        private static bool IsLastChild(Element el)
        {
            var parent = el.ParentNode as ContainerNode;
            if (parent == null) return true;
            return parent.ChildNodes.OfType<Element>().LastOrDefault() == el;
        }

        private static bool IsOnlyChild(Element el)
        {
            var parent = el.ParentNode as ContainerNode;
            if (parent == null) return true;
            return parent.ChildElementCount == 1; // V2 ContainerNode has ChildElementCount
        }

        private static bool IsFirstOfType(Element el)
        {
             var parent = el.ParentNode as ContainerNode;
             if (parent == null) return true;
             return parent.ChildNodes.OfType<Element>().FirstOrDefault(c => string.Equals(c.TagName, el.TagName, StringComparison.OrdinalIgnoreCase)) == el;
        }

        private static bool IsLastOfType(Element el)
        {
             var parent = el.ParentNode as ContainerNode;
             if (parent == null) return true;
             return parent.ChildNodes.OfType<Element>().LastOrDefault(c => string.Equals(c.TagName, el.TagName, StringComparison.OrdinalIgnoreCase)) == el;
        }

        private static bool MatchesNthChild(Element el, string args, List<SelectorChain> parsedArgs, int depth)
        {
            var parent = el.ParentNode as ContainerNode;
            if (parent == null) return false;

            ParseNthArguments(args, out var formula, out var ofSelector);
            var siblings = parent.ChildNodes.OfType<Element>().ToList();
            if (parsedArgs != null && parsedArgs.Count > 0)
            {
                siblings = siblings.Where(sibling => MatchesSelectorList(sibling, parsedArgs, depth + 1)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(ofSelector))
            {
                var chains = ParseSelectorList(ofSelector);
                siblings = siblings.Where(sibling => MatchesSelectorList(sibling, chains, depth + 1)).ToList();
            }

            int index = siblings.IndexOf(el);
            if (index < 0) return false;
            return MatchesNthFormula(index + 1, formula);
        }

        private static bool MatchesNthLastChild(Element el, string args, List<SelectorChain> parsedArgs, int depth)
        {
            var parent = el.ParentNode as ContainerNode;
            if (parent == null) return false;

            ParseNthArguments(args, out var formula, out var ofSelector);
            var siblings = parent.ChildNodes.OfType<Element>().ToList();
            if (parsedArgs != null && parsedArgs.Count > 0)
            {
                siblings = siblings.Where(sibling => MatchesSelectorList(sibling, parsedArgs, depth + 1)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(ofSelector))
            {
                var chains = ParseSelectorList(ofSelector);
                siblings = siblings.Where(sibling => MatchesSelectorList(sibling, chains, depth + 1)).ToList();
            }

            int index = siblings.IndexOf(el);
            if (index < 0) return false;
            return MatchesNthFormula(siblings.Count - index, formula);
        }

        private static bool MatchesNthOfType(Element el, string args)
        {
            var parent = el.ParentNode as ContainerNode;
            if (parent == null) return false;
            var siblings = parent.ChildNodes.OfType<Element>().Where(c => string.Equals(c.TagName, el.TagName, StringComparison.OrdinalIgnoreCase)).ToList();
            int index = siblings.IndexOf(el) + 1;
            return MatchesNthFormula(index, args);
        }

        private static bool MatchesNthLastOfType(Element el, string args)
        {
            var parent = el.ParentNode as ContainerNode;
            if (parent == null) return false;
            var siblings = parent.ChildNodes.OfType<Element>().Where(c => string.Equals(c.TagName, el.TagName, StringComparison.OrdinalIgnoreCase)).ToList();
            int index = siblings.Count - siblings.IndexOf(el);
            return MatchesNthFormula(index, args);
        }

        private static bool MatchesHas(Element el, string relativeSelectors, List<SelectorChain> parsedArgs, int depth)
        {
            var parsed = parsedArgs ?? ParseSelectorList(relativeSelectors);
            foreach (var chain in parsed)
            {
                if (chain.Segments.Count == 0)
                {
                    continue;
                }

                if (MatchesRelativeChain(el, chain, 0, depth + 1))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MatchesRelativeChain(Element anchor, SelectorChain chain, int segmentIndex, int depth)
        {
            if (anchor == null || chain == null || depth > 64) return false;
            if (segmentIndex < 0 || segmentIndex >= chain.Segments.Count) return false;

            var segment = chain.Segments[segmentIndex];
            var candidates = EnumerateRelatedElements(anchor, segment.Combinator);
            foreach (var candidate in candidates)
            {
                if (!MatchesSegment(candidate, segment, depth + 1))
                {
                    continue;
                }

                if (segmentIndex == chain.Segments.Count - 1)
                {
                    return true;
                }

                if (MatchesRelativeChain(candidate, chain, segmentIndex + 1, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<Element> EnumerateRelatedElements(Element anchor, char combinator)
        {
            return combinator switch
            {
                '>' => GetChildElements(anchor),
                '+' => GetAdjacentNextSibling(anchor),
                '~' => GetFollowingSiblings(anchor),
                _ => anchor.Descendants().OfType<Element>()
            };
        }

        private static IEnumerable<Element> GetChildElements(Element el)
        {
            if (el is ContainerNode container)
            {
                return container.ChildNodes.OfType<Element>();
            }

            return Array.Empty<Element>();
        }

        private static IEnumerable<Element> GetAdjacentNextSibling(Element el)
        {
            var next = GetNextSibling(el);
            if (next == null)
            {
                return Array.Empty<Element>();
            }

            return new[] { next };
        }

        private static bool MatchesSelectorList(Element element, List<SelectorChain> selectorList, int depth)
        {
            if (selectorList == null || selectorList.Count == 0 || depth > 64) return false;
            foreach (var chain in selectorList)
            {
                if (MatchesChain(element, chain, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ParseNthArguments(string args, out string formula, out string ofSelector)
        {
            formula = args?.Trim() ?? string.Empty;
            ofSelector = null;

            if (string.IsNullOrWhiteSpace(args))
            {
                return;
            }

            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int depth = 0;

            for (int i = 0; i < args.Length - 1; i++)
            {
                char c = args[i];

                if (c == '\\' && i + 1 < args.Length)
                {
                    i++;
                    continue;
                }

                if (!inDoubleQuote && c == '\'')
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (!inSingleQuote && c == '"')
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                {
                    continue;
                }

                if (c == '(' || c == '[')
                {
                    depth++;
                    continue;
                }

                if (c == ')' || c == ']')
                {
                    if (depth > 0) depth--;
                    continue;
                }

                if (depth == 0 && IsNthOfKeyword(args, i))
                {
                    formula = args.Substring(0, i).Trim();
                    ofSelector = args.Substring(i + 2).Trim();
                    return;
                }
            }
        }

        private static bool IsNthOfKeyword(string args, int index)
        {
            if (index < 0 || index + 2 > args.Length) return false;
            if (args[index] != 'o' && args[index] != 'O') return false;
            if (args[index + 1] != 'f' && args[index + 1] != 'F') return false;

            bool beforeOk = index == 0 || char.IsWhiteSpace(args[index - 1]);
            bool afterOk = index + 2 >= args.Length || char.IsWhiteSpace(args[index + 2]);
            return beforeOk && afterOk;
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
            var targetSegment = chain.Segments[index];
            long requiredMask = ComputeSegmentHash(targetSegment);

            // AncestorFilter is now public
            if (element.AncestorFilter == 0)
            {
                // If filter not populated, skip fast-reject to avoid false negatives.
                return false;
            }
            return (element.AncestorFilter & requiredMask) != requiredMask;
        }

        private static long ComputeSegmentHash(SelectorSegment seg)
        {
            long hash = 0;

            // Tag
            if (!string.IsNullOrEmpty(seg.TagName) && seg.TagName != "*")
            {
                hash |= FilterHash(NormalizeTagHashInput(seg.TagName));
            }

            // ID
            if (!string.IsNullOrEmpty(seg.Id))
            {
                hash |= FilterHash("#" + seg.Id.ToUpperInvariant());
            }

            // Classes
            foreach (var cls in seg.Classes)
            {
                hash |= FilterHash("." + cls);
            }

            return hash;
        }

        private static string NormalizeTagHashInput(string tagName)
        {
            return string.IsNullOrEmpty(tagName) ? tagName : tagName.ToUpperInvariant();
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

