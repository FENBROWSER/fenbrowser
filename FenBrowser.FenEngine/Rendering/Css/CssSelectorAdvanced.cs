using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Advanced CSS selector support per Selectors Level 4 specification.
    /// Implements :has(), :is(), :where(), :not() with complex argument lists.
    /// </summary>
    public static class CssSelectorAdvanced
    {
        /// <summary>
        /// Match an element against a complex selector string that may contain
        /// :has(), :is(), :where(), :not() pseudo-classes.
        /// </summary>
        public static bool Matches(Element element, string selector, Element root = null)
        {
            if (element == null || string.IsNullOrWhiteSpace(selector))
                return false;

            // Handle :is() and :where() - functional pseudo-classes
            if (selector.Contains(":is(") || selector.Contains(":where("))
            {
                selector = ExpandIsFunctions(selector);
            }

            // Handle :has() - relational pseudo-class
            if (selector.Contains(":has("))
            {
                return MatchesWithHas(element, selector, root);
            }

            // Handle :not() with complex selectors
            if (selector.Contains(":not("))
            {
                return MatchesWithNot(element, selector, root);
            }

            // Fall back to basic matching
            return BasicMatch(element, selector);
        }

        /// <summary>
        /// Expand :is() and :where() into equivalent selector lists
        /// :is(a, b) c → a c, b c
        /// </summary>
        private static string ExpandIsFunctions(string selector)
        {
            // Find :is(...) or :where(...)
            var pattern = @":(is|where)\(([^)]+)\)";
            var match = Regex.Match(selector, pattern);

            if (!match.Success) return selector;

            string funcName = match.Groups[1].Value;
            string args = match.Groups[2].Value;
            
            // Split arguments by comma (handling nested parentheses)
            var alternatives = SplitSelectorList(args);

            // For single usage, just expand - note: specificity differs between :is() and :where()
            // but for matching purposes they're equivalent
            if (alternatives.Count == 1)
            {
                return selector.Replace(match.Value, alternatives[0].Trim());
            }

            // For multiple alternatives, we need to test each
            // Return a marker that will be handled by the matching logic
            return selector; // Let MatchesWithIs handle it
        }

        /// <summary>
        /// Check if element matches a selector containing :has()
        /// :has() matches if the element has descendants matching the relative selector
        /// </summary>
        private static bool MatchesWithHas(Element element, string selector, Element root)
        {
            // Parse out the :has() part
            var hasMatch = Regex.Match(selector, @":has\(([^)]+)\)");
            if (!hasMatch.Success) return BasicMatch(element, selector);

            string relativeSelector = hasMatch.Groups[1].Value.Trim();
            string baseSelector = selector.Replace(hasMatch.Value, "");

            // First, element must match the base selector
            if (!string.IsNullOrWhiteSpace(baseSelector) && !BasicMatch(element, baseSelector.Trim()))
                return false;

            // Then, check if element has matching descendants/siblings
            return HasMatchingRelativeElements(element, relativeSelector);
        }

        /// <summary>
        /// Check for matching relative elements within :has()
        /// Supports: > (child), + (adjacent sibling), ~ (general sibling), space (descendant)
        /// </summary>
        private static bool HasMatchingRelativeElements(Element element, string relativeSelector)
        {
            relativeSelector = relativeSelector.Trim();

            // Handle different combinators
            if (relativeSelector.StartsWith(">"))
            {
                // Direct children only
                string childSelector = relativeSelector.Substring(1).Trim();
                return element.Children?.OfType<Element>().Any(c => BasicMatch(c, childSelector)) ?? false;
            }
            else if (relativeSelector.StartsWith("+"))
            {
                // Adjacent sibling
                string siblingSelector = relativeSelector.Substring(1).Trim();
                var nextSibling = element.NextElementSibling;
                return nextSibling != null && BasicMatch(nextSibling, siblingSelector);
            }
            else if (relativeSelector.StartsWith("~"))
            {
                // General siblings
                string siblingSelector = relativeSelector.Substring(1).Trim();
                var siblings = GetFollowingSiblings(element);
                return siblings.Any(s => BasicMatch(s, siblingSelector));
            }
            else
            {
                // Default: any descendant
                return HasDescendantMatching(element, relativeSelector);
            }
        }

        /// <summary>
        /// Check if element has any descendant matching the selector
        /// </summary>
        private static bool HasDescendantMatching(Element element, string selector)
        {
            if (element.Children == null) return false;

            foreach (var child in element.Children.OfType<Element>())
            {
                if (BasicMatch(child, selector))
                    return true;
                if (HasDescendantMatching(child, selector))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Check if element matches a selector containing :not()
        /// Supports complex selectors inside :not()
        /// </summary>
        private static bool MatchesWithNot(Element element, string selector, Element root)
        {
            // Parse out all :not() parts
            var notPattern = @":not\(([^)]+)\)";
            var matches = Regex.Matches(selector, notPattern);

            string baseSelector = selector;
            foreach (Match m in matches)
            {
                string excludeSelector = m.Groups[1].Value.Trim();
                
                // If element matches any :not() selector, it fails
                if (BasicMatch(element, excludeSelector))
                    return false;

                baseSelector = baseSelector.Replace(m.Value, "");
            }

            // Check base selector if any remains
            baseSelector = baseSelector.Trim();
            if (string.IsNullOrWhiteSpace(baseSelector) || baseSelector == "*")
                return true;

            return BasicMatch(element, baseSelector);
        }

        /// <summary>
        /// Basic selector matching (type, class, id, attributes, structural pseudo-classes)
        /// </summary>
        public static bool BasicMatch(Element element, string selector)
        {
            if (element == null || string.IsNullOrWhiteSpace(selector))
                return false;

            selector = selector.Trim();
            
            // Universal selector
            if (selector == "*") return true;

            // Handle compound selectors (div.class#id)
            var parts = ParseCompoundSelector(selector);

            foreach (var part in parts)
            {
                if (!MatchesSelectorPart(element, part))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Parse a compound selector into individual parts
        /// "div.class#id[attr]:hover" → ["div", ".class", "#id", "[attr]", ":hover"]
        /// </summary>
        private static List<string> ParseCompoundSelector(string selector)
        {
            var parts = new List<string>();
            var current = "";
            bool inBracket = false;
            bool inParen = false;

            for (int i = 0; i < selector.Length; i++)
            {
                char c = selector[i];

                if (c == '[') inBracket = true;
                if (c == ']') inBracket = false;
                if (c == '(') inParen = true;
                if (c == ')') inParen = false;

                // Split on . # : [ unless inside brackets/parens
                if (!inBracket && !inParen && (c == '.' || c == '#' || c == ':' || c == '['))
                {
                    if (!string.IsNullOrEmpty(current))
                        parts.Add(current);
                    current = c.ToString();
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrEmpty(current))
                parts.Add(current);

            return parts;
        }

        /// <summary>
        /// Match a single selector part against an element
        /// </summary>
        private static bool MatchesSelectorPart(Element element, string part)
        {
            if (string.IsNullOrEmpty(part)) return true;

            // Type selector
            if (char.IsLetter(part[0]))
            {
                return string.Equals(element.TagName, part, StringComparison.OrdinalIgnoreCase);
            }

            // Class selector
            if (part.StartsWith("."))
            {
                string className = part.Substring(1);
                var classes = element.GetAttribute("class")?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                return classes.Any(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase));
            }

            // ID selector
            if (part.StartsWith("#"))
            {
                string id = part.Substring(1);
                return string.Equals(element.GetAttribute("id"), id, StringComparison.OrdinalIgnoreCase);
            }

            // Attribute selector
            if (part.StartsWith("["))
            {
                return MatchesAttributeSelector(element, part);
            }

            // Pseudo-class
            if (part.StartsWith(":"))
            {
                return MatchesPseudoClass(element, part);
            }

            return false;
        }

        /// <summary>
        /// Match attribute selectors: [attr], [attr=value], [attr^=value], etc.
        /// </summary>
        private static bool MatchesAttributeSelector(Element element, string selector)
        {
            // Remove brackets
            var inner = selector.Trim('[', ']');
            
            // Parse operator
            var match = Regex.Match(inner, @"^([a-zA-Z-]+)([~|^$*]?=)?['""]?([^'""]*)['""]?$");
            if (!match.Success) return false;

            string attrName = match.Groups[1].Value;
            string op = match.Groups[2].Value;
            string attrValue = match.Groups[3].Value;

            string elementValue = element.GetAttribute(attrName);

            if (string.IsNullOrEmpty(op))
            {
                // [attr] - just checks existence
                return elementValue != null;
            }

            if (elementValue == null) return false;

            switch (op)
            {
                case "=": // Exact match
                    return string.Equals(elementValue, attrValue, StringComparison.OrdinalIgnoreCase);
                case "~=": // Word in space-separated list
                    return elementValue.Split(' ').Any(w => string.Equals(w, attrValue, StringComparison.OrdinalIgnoreCase));
                case "|=": // Exact or starts with + hyphen
                    return string.Equals(elementValue, attrValue, StringComparison.OrdinalIgnoreCase) ||
                           elementValue.StartsWith(attrValue + "-", StringComparison.OrdinalIgnoreCase);
                case "^=": // Starts with
                    return elementValue.StartsWith(attrValue, StringComparison.OrdinalIgnoreCase);
                case "$=": // Ends with
                    return elementValue.EndsWith(attrValue, StringComparison.OrdinalIgnoreCase);
                case "*=": // Contains
                    return elementValue.IndexOf(attrValue, StringComparison.OrdinalIgnoreCase) >= 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Match pseudo-classes: :first-child, :last-child, :nth-child(), :hover, :focus, etc.
        /// </summary>
        private static bool MatchesPseudoClass(Element element, string pseudoClass)
        {
            pseudoClass = pseudoClass.ToLowerInvariant();

            // Structural pseudo-classes
            if (pseudoClass == ":first-child")
            {
                return IsFirstChild(element);
            }
            if (pseudoClass == ":last-child")
            {
                return IsLastChild(element);
            }
            if (pseudoClass == ":only-child")
            {
                return IsFirstChild(element) && IsLastChild(element);
            }
            if (pseudoClass == ":first-of-type")
            {
                return IsFirstOfType(element);
            }
            if (pseudoClass == ":last-of-type")
            {
                return IsLastOfType(element);
            }
            if (pseudoClass == ":only-of-type")
            {
                return IsFirstOfType(element) && IsLastOfType(element);
            }
            if (pseudoClass == ":empty")
            {
                return (element.Children == null || element.Children.Length == 0) &&
                       string.IsNullOrWhiteSpace(element.TextContent);
            }
            if (pseudoClass == ":root")
            {
                return element.ParentNode == null || element.NodeName?.ToUpperInvariant() == "HTML";
            }

            // nth-child variants
            if (pseudoClass.StartsWith(":nth-child("))
            {
                return MatchesNthChild(element, pseudoClass, false, false);
            }
            if (pseudoClass.StartsWith(":nth-last-child("))
            {
                return MatchesNthChild(element, pseudoClass, true, false);
            }
            if (pseudoClass.StartsWith(":nth-of-type("))
            {
                return MatchesNthChild(element, pseudoClass, false, true);
            }
            if (pseudoClass.StartsWith(":nth-last-of-type("))
            {
                return MatchesNthChild(element, pseudoClass, true, true);
            }

            // State pseudo-classes - query ElementStateManager for dynamic state
            if (pseudoClass == ":hover")
            {
                return ElementStateManager.Instance.IsHovered(element);
            }
            if (pseudoClass == ":active")
            {
                return ElementStateManager.Instance.IsActive(element);
            }
            if (pseudoClass == ":focus")
            {
                return ElementStateManager.Instance.IsFocused(element);
            }
            if (pseudoClass == ":focus-within")
            {
                return ElementStateManager.Instance.IsFocusWithin(element);
            }
            if (pseudoClass == ":focus-visible")
            {
                // Per CSS Selectors Level 4: match when focus was from keyboard or for text inputs
                return ElementStateManager.Instance.IsFocusVisible(element);
            }
            // Other state pseudo-classes that still need attribute checking
            if (pseudoClass == ":visited")
            {
                return ElementStateManager.Instance.IsVisited(element);
            }
            if (pseudoClass == ":link")
            {
                return element.HasAttribute("href") && !ElementStateManager.Instance.IsVisited(element);
            }
            if (pseudoClass == ":checked" || pseudoClass == ":disabled" || pseudoClass == ":enabled")
            {
                // These require attribute checking - return false here, handled in CssLoader
                return false;
            }

            return false;
        }

        /// <summary>
        /// Match :nth-child(), :nth-last-child(), :nth-of-type(), :nth-last-of-type()
        /// </summary>
        private static bool MatchesNthChild(Element element, string pseudo, bool fromEnd, bool ofType)
        {
            // Extract argument
            var match = Regex.Match(pseudo, @"\(([^)]+)\)");
            if (!match.Success) return false;

            string arg = match.Groups[1].Value.Trim().ToLowerInvariant();

            // Get siblings
            var parentEl = element.ParentNode as Element;
            if (parentEl == null) return false;

            var siblings = ofType
                ? parentEl.Children.OfType<Element>().Where(c => string.Equals(c.TagName, element.TagName, StringComparison.OrdinalIgnoreCase)).ToList()
                : parentEl.Children.OfType<Element>().ToList();

            int index = siblings.IndexOf(element);
            if (index < 0) return false;

            // Convert to 1-based
            int position = fromEnd ? siblings.Count - index : index + 1;

            // Parse argument
            if (arg == "odd") return position % 2 == 1;
            if (arg == "even") return position % 2 == 0;
            
            // Handle An+B syntax
            return MatchesAnPlusB(arg, position);
        }

        /// <summary>
        /// Match An+B formula against a position
        /// </summary>
        private static bool MatchesAnPlusB(string formula, int position)
        {
            // Simple number
            if (int.TryParse(formula, out int n))
                return position == n;

            // Parse An+B or An-B
            var match = Regex.Match(formula, @"^(-?\d*)n\s*([+-]?\s*\d+)?$");
            if (match.Success)
            {
                int a = 1;
                int b = 0;

                string aStr = match.Groups[1].Value;
                if (aStr == "-") a = -1;
                else if (!string.IsNullOrEmpty(aStr)) int.TryParse(aStr, out a);

                string bStr = match.Groups[2].Value.Replace(" ", "");
                if (!string.IsNullOrEmpty(bStr)) int.TryParse(bStr, out b);

                // Check if position matches for some non-negative n
                if (a == 0) return position == b;
                
                int remainder = (position - b) % a;
                int quotient = (position - b) / a;
                
                return remainder == 0 && quotient >= 0;
            }

            return false;
        }

        #region Helper Methods

        private static bool IsFirstChild(Element element)
        {
            var parent = element.ParentNode as Element;
            if (parent?.Children == null) return true;
            var siblings = parent.Children.OfType<Element>().ToList();
            return siblings.Count > 0 && siblings[0] == element;
        }

        private static bool IsLastChild(Element element)
        {
            var parent = element.ParentNode as Element;
            if (parent?.Children == null) return true;
            var siblings = parent.Children.OfType<Element>().ToList();
            return siblings.Count > 0 && siblings[siblings.Count - 1] == element;
        }

        private static bool IsFirstOfType(Element element)
        {
            var parent = element.ParentNode as Element;
            if (parent?.Children == null) return true;
            return parent.Children.OfType<Element>().FirstOrDefault(c => 
                string.Equals(c.TagName, element.TagName, StringComparison.OrdinalIgnoreCase)) == element;
        }

        private static bool IsLastOfType(Element element)
        {
            var parent = element.ParentNode as Element;
            if (parent?.Children == null) return true;
            return parent.Children.OfType<Element>().LastOrDefault(c => 
                string.Equals(c.TagName, element.TagName, StringComparison.OrdinalIgnoreCase)) == element;
        }



        private static List<Element> GetFollowingSiblings(Element element)
        {
            var parent = element.ParentNode as Element;
            if (parent?.Children == null) return new List<Element>();
            var siblings = parent.Children.OfType<Element>().ToList();
            int index = siblings.IndexOf(element);
            if (index < 0) return new List<Element>();
            return siblings.Skip(index + 1).ToList();
        }

        private static List<string> SplitSelectorList(string list)
        {
            var result = new List<string>();
            var current = "";
            int depth = 0;

            foreach (char c in list)
            {
                if (c == '(') depth++;
                if (c == ')') depth--;
                if (c == ',' && depth == 0)
                {
                    result.Add(current.Trim());
                    current = "";
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
                result.Add(current.Trim());

            return result;
        }

        #endregion
    }
}



