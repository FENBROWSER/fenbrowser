// W3C Accessible Name and Description Computation 1.1
// https://www.w3.org/TR/accname-1.1/
// FenBrowser.Core.Accessibility

using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// Computes the accessible name for an element following the W3C AccName 1.1 algorithm.
    /// </summary>
    public static class AccNameCalculator
    {
        private const int MaxNameLength = 4096;

        /// <summary>
        /// Computes the accessible name for <paramref name="el"/>.
        /// Returns an empty string when no name can be computed.
        /// </summary>
        public static string Compute(Element el, Document doc)
        {
            if (el == null) return "";
            var visited = new HashSet<Element>(ReferenceEqualityComparer.Instance);
            var name = ComputeInternal(el, doc, visited, isTraversal: false);
            // Normalize whitespace
            return NormalizeWhitespace(name);
        }

        // ---- Core algorithm (recursive, used for aria-labelledby traversal too) ----

        private static string ComputeInternal(
            Element el, Document doc, HashSet<Element> visited, bool isTraversal)
        {
            if (el == null) return "";
            if (!visited.Add(el)) return ""; // Cycle guard

            // Step 2A: aria-labelledby (only when not already traversing a labelledby reference)
            if (!isTraversal)
            {
                var labelledby = el.GetAttribute("aria-labelledby");
                if (!string.IsNullOrWhiteSpace(labelledby))
                {
                    var sb = new StringBuilder();
                    foreach (var id in labelledby.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var refEl = doc?.GetElementById(id);
                        if (refEl == null || ReferenceEquals(refEl, el)) continue;
                        var refVisited = new HashSet<Element>(visited, ReferenceEqualityComparer.Instance);
                        var refName = ComputeInternal(refEl, doc, refVisited, isTraversal: true);
                        if (!string.IsNullOrEmpty(refName))
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(refName);
                        }
                    }
                    var result = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(result))
                        return TruncateName(result);
                }
            }

            // Step 2B: aria-label
            var ariaLabel = el.GetAttribute("aria-label");
            if (!string.IsNullOrWhiteSpace(ariaLabel))
                return TruncateName(ariaLabel.Trim());

            // Step 2C: Host-language native labelling mechanisms
            var tag = el.LocalName;

            // <img> alt attribute
            if (tag == "img")
            {
                var alt = el.GetAttribute("alt");
                if (alt != null) // empty alt is intentional, not null
                    return TruncateName(alt.Trim());
            }

            // <area> alt attribute
            if (tag == "area")
            {
                var alt = el.GetAttribute("alt") ?? "";
                return TruncateName(alt.Trim());
            }

            // <input> and <textarea> — find associated <label>
            if (tag == "input" || tag == "textarea" || tag == "select")
            {
                // aria-labelledby / aria-label already handled above
                // Check for associated label via 'for' attribute or wrapping label
                var labelText = FindAssociatedLabelText(el, doc, visited);
                if (!string.IsNullOrEmpty(labelText))
                    return TruncateName(labelText);

                // placeholder as fallback for textbox-like inputs
                if (tag == "input" || tag == "textarea")
                {
                    var placeholder = el.GetAttribute("placeholder");
                    if (!string.IsNullOrWhiteSpace(placeholder))
                        return TruncateName(placeholder.Trim());
                }
            }

            // <table> → <caption> child
            if (tag == "table")
            {
                var caption = FindFirstChildByTag(el, "caption");
                if (caption != null)
                    return TruncateName(CollectTextContent(caption, doc, visited));
            }

            // <fieldset> → <legend> child
            if (tag == "fieldset")
            {
                var legend = FindFirstChildByTag(el, "legend");
                if (legend != null)
                    return TruncateName(CollectTextContent(legend, doc, visited));
            }

            // <figure> → <figcaption> child
            if (tag == "figure")
            {
                var figcaption = FindFirstChildByTag(el, "figcaption");
                if (figcaption != null)
                    return TruncateName(CollectTextContent(figcaption, doc, visited));
            }

            // Step 2D: name-from-contents (for roles where content provides the name)
            var role = AccessibilityRole.ResolveRole(el, doc);
            if (IsNameFromContents(role))
            {
                var text = CollectTextContent(el, doc, visited);
                if (!string.IsNullOrEmpty(text))
                    return TruncateName(text);
            }

            // When traversing (called from aria-labelledby), collect text content regardless of role
            if (isTraversal)
            {
                var text = CollectTextContent(el, doc, visited);
                if (!string.IsNullOrEmpty(text))
                    return TruncateName(text);
            }

            // Step 2E: title attribute
            var title = el.GetAttribute("title");
            if (!string.IsNullOrWhiteSpace(title))
                return TruncateName(title.Trim());

            return "";
        }

        // ---- Text content collection ----

        private static string CollectTextContent(Element el, Document doc, HashSet<Element> visited)
        {
            var sb = new StringBuilder();
            CollectTextRecursive(el, doc, visited, sb);
            return sb.ToString().Trim();
        }

        private static void CollectTextRecursive(
            Node node, Document doc, HashSet<Element> visited, StringBuilder sb)
        {
            if (node == null) return;

            if (node is Text text)
            {
                var data = text.Data;
                if (!string.IsNullOrEmpty(data))
                    sb.Append(data);
                return;
            }

            if (node is not Element el) return;

            // Exclude hidden elements from text content
            if (IsAriaHidden(el)) return;
            if (IsExcludedFromTree(el)) return;

            // Check for pseudo-content from aria-label/aria-labelledby on children
            var childAriaLabel = el.GetAttribute("aria-label");
            if (!string.IsNullOrWhiteSpace(childAriaLabel))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
                sb.Append(childAriaLabel.Trim());
                return;
            }

            // Recurse into children
            for (var child = el.FirstChild; child != null; child = child.NextSibling)
            {
                var prevLen = sb.Length;
                CollectTextRecursive(child, doc, visited, sb);

                // Add space between block-level elements
                if (child is Element childEl && IsBlockLike(childEl) && sb.Length > prevLen)
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                        sb.Append(' ');
                }
            }
        }

        // ---- Label lookup ----

        private static string FindAssociatedLabelText(Element el, Document doc, HashSet<Element> visited)
        {
            // Method 1: explicit <label for="id">
            var id = el.GetAttribute("id");
            if (!string.IsNullOrEmpty(id) && doc != null)
            {
                // Walk the document looking for <label for="id">
                foreach (var node in doc.Descendants())
                {
                    if (node is Element labelEl && labelEl.LocalName == "label")
                    {
                        var forAttr = labelEl.GetAttribute("for");
                        if (forAttr == id)
                        {
                            var labelVisited = new HashSet<Element>(visited, ReferenceEqualityComparer.Instance);
                            return CollectTextContent(labelEl, doc, labelVisited);
                        }
                    }
                }
            }

            // Method 2: wrapping <label> ancestor
            for (var ancestor = el.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (ancestor.LocalName == "label")
                {
                    var labelVisited = new HashSet<Element>(visited, ReferenceEqualityComparer.Instance);
                    // Collect text excluding the input itself
                    return CollectLabelTextExcludingInput(ancestor, el, doc, labelVisited);
                }
            }

            return "";
        }

        private static string CollectLabelTextExcludingInput(
            Element label, Element inputEl, Document doc, HashSet<Element> visited)
        {
            var sb = new StringBuilder();
            for (var child = label.FirstChild; child != null; child = child.NextSibling)
            {
                if (ReferenceEquals(child, inputEl)) continue;
                CollectTextRecursive(child, doc, visited, sb);
            }
            return sb.ToString().Trim();
        }

        // ---- Utility helpers ----

        private static Element FindFirstChildByTag(Element parent, string tag)
        {
            for (var child = parent.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && el.LocalName == tag)
                    return el;
            }
            return null;
        }

        private static bool IsAriaHidden(Element el)
        {
            var hidden = el.GetAttribute("aria-hidden");
            return hidden != null && hidden.Trim().ToLowerInvariant() == "true";
        }

        private static bool IsExcludedFromTree(Element el)
        {
            var tag = el.LocalName;
            return tag == "script" || tag == "style" || tag == "meta" ||
                   tag == "link" || tag == "head" || tag == "noscript";
        }

        private static bool IsBlockLike(Element el)
        {
            var tag = el.LocalName;
            return tag == "div" || tag == "p" || tag == "section" || tag == "article" ||
                   tag == "header" || tag == "footer" || tag == "nav" || tag == "aside" ||
                   tag == "main" || tag == "blockquote" || tag == "li" || tag == "td" ||
                   tag == "th" || tag == "br";
        }

        private static bool IsNameFromContents(AriaRole role)
        {
            if (AriaSpec.Roles.TryGetValue(role, out var info))
                return info.NameFromContents;
            return false;
        }

        private static string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool lastWasSpace = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        private static string TruncateName(string s)
        {
            if (s == null) return "";
            return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength) : s;
        }

        // Custom ReferenceEqualityComparer for .NET 5+ compatibility fallback
        private sealed class ReferenceEqualityComparer : IEqualityComparer<Element>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public bool Equals(Element x, Element y) => ReferenceEquals(x, y);
            public int GetHashCode(Element obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
