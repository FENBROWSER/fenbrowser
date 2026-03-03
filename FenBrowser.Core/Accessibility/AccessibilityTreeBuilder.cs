// FenBrowser.Core.Accessibility — DOM → A11y tree builder

using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// Builds an <see cref="AccessibilityNode"/> tree from a <see cref="Document"/>.
    /// Follows the ARIA spec §4.1.2 inclusion/exclusion rules.
    /// </summary>
    public static class AccessibilityTreeBuilder
    {
        /// <summary>
        /// Builds the accessibility tree rooted at <paramref name="doc"/>.
        /// Returns null when the document has no document element.
        /// </summary>
        public static AccessibilityNode Build(Document doc)
        {
            if (doc == null) return null;
            var root = doc.DocumentElement;
            if (root == null) return null;

            var ariaOwnsMap = BuildAriaOwnsMap(doc);
            var ownedElements = new HashSet<Element>(new RefEqComparer());
            foreach (var list in ariaOwnsMap.Values)
                foreach (var owned in list)
                    ownedElements.Add(owned);

            var visited = new HashSet<Element>(new RefEqComparer());
            return BuildNode(root, doc, ariaOwnsMap, ownedElements, visited, parentHidden: false);
        }

        // ---- Core recursive builder ----

        private static AccessibilityNode BuildNode(
            Element el,
            Document doc,
            Dictionary<Element, List<Element>> ariaOwnsMap,
            HashSet<Element> ownedElements,
            HashSet<Element> visited,
            bool parentHidden)
        {
            if (el == null) return null;
            if (!visited.Add(el)) return null; // Cycle guard

            // Always-excluded elements (structural, not user-visible)
            if (IsAlwaysExcluded(el)) return null;

            bool isHidden = parentHidden || IsAriaHidden(el) || IsCssHidden(el);

            // Determine role
            var role = AccessibilityRole.ResolveRole(el, doc);
            bool isPresentational = role == AriaRole.Presentation || role == AriaRole.None;

            // Build children (DOM children + aria-owned children)
            var childNodes = new List<AccessibilityNode>();

            // DOM children
            for (var child = el.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is not Element childEl) continue;
                // Skip elements already owned by another element (handled via aria-owns)
                if (ownedElements.Contains(childEl)) continue;

                var childVisited = new HashSet<Element>(visited, new RefEqComparer());
                var childNode = BuildNode(childEl, doc, ariaOwnsMap, ownedElements, childVisited, isHidden);
                if (childNode != null)
                    childNodes.Add(childNode);
            }

            // aria-owned children (appended after DOM children per ARIA spec)
            if (ariaOwnsMap.TryGetValue(el, out var ownedList))
            {
                foreach (var ownedEl in ownedList)
                {
                    var childVisited = new HashSet<Element>(visited, new RefEqComparer());
                    var childNode = BuildNode(ownedEl, doc, ariaOwnsMap, ownedElements, childVisited, isHidden);
                    if (childNode != null)
                        childNodes.Add(childNode);
                }
            }

            // For presentation/none roles: remove from tree but keep children
            if (isPresentational && !isHidden)
            {
                // Flatten children into parent — we return a "transparent" node
                // The caller will need to inline these. We return a sentinel with Role=None
                // and the children populated so the caller can unwrap.
                return new AccessibilityNode(el, AriaRole.None, "", "", isHidden, childNodes, null);
            }

            // Compute name and description (skip for hidden subtrees to save work)
            string name = "";
            string description = "";
            if (!isHidden)
            {
                name = AccNameCalculator.Compute(el, doc);
                description = AccDescCalculator.Compute(el, doc);
            }

            // Collect ARIA state attributes
            var states = CollectStates(el);

            return new AccessibilityNode(el, role, name, description, isHidden, childNodes, states);
        }

        // ---- aria-owns map ----

        private static Dictionary<Element, List<Element>> BuildAriaOwnsMap(Document doc)
        {
            var map = new Dictionary<Element, List<Element>>(new RefEqComparer());
            var visited = new HashSet<Element>(new RefEqComparer());

            foreach (var node in doc.Descendants())
            {
                if (node is not Element el) continue;
                var ariaOwns = el.GetAttribute("aria-owns");
                if (string.IsNullOrWhiteSpace(ariaOwns)) continue;

                var ownedList = new List<Element>();
                foreach (var id in ariaOwns.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var ownedEl = doc.GetElementById(id);
                    if (ownedEl == null || ReferenceEquals(ownedEl, el)) continue;
                    if (!visited.Add(ownedEl)) continue; // Prevent double-owning
                    ownedList.Add(ownedEl);
                }

                if (ownedList.Count > 0)
                    map[el] = ownedList;
            }

            return map;
        }

        // ---- Exclusion checks ----

        private static bool IsAlwaysExcluded(Element el)
        {
            var tag = el.LocalName;
            return tag == "script" || tag == "style" || tag == "meta" ||
                   tag == "link" || tag == "head" || tag == "noscript" ||
                   tag == "title" || tag == "template";
        }

        private static bool IsAriaHidden(Element el)
        {
            var hidden = el.GetAttribute("aria-hidden");
            return hidden?.Trim().ToLowerInvariant() == "true";
        }

        private static bool IsCssHidden(Element el)
        {
            // Check inline style only (full CSS cascade is expensive and often not needed)
            var style = el.GetAttribute("style");
            if (string.IsNullOrEmpty(style)) return false;
            var lower = style.ToLowerInvariant();
            return lower.Contains("display:none") || lower.Contains("display: none") ||
                   lower.Contains("visibility:hidden") || lower.Contains("visibility: hidden");
        }

        // ---- State collection ----

        private static IReadOnlyDictionary<string, string> CollectStates(Element el)
        {
            Dictionary<string, string> states = null;

            foreach (var attr in el.Attributes)
            {
                if (!attr.Name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase)) continue;
                states ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                states[attr.Name] = attr.Value;
            }

            return states;
        }

        // ---- Equality comparer ----

        private sealed class RefEqComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y) => ReferenceEquals(x, y);
            public int GetHashCode(Element obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
