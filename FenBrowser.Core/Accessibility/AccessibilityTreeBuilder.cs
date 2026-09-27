// FenBrowser.Core.Accessibility — DOM → A11y tree builder

// SpecRef: WAI-ARIA role mapping and accessibility tree exposure contract
// CapabilityId: A11Y-TREE-ROLE-MAP-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// Builds an <see cref="AccessibilityNode"/> tree from a <see cref="Document"/>.
    /// Follows the ARIA spec §4.1.2 inclusion/exclusion rules.
    /// </summary>
    /// <summary>What a media element's user agent controls show, for their accessible nodes.</summary>
    public sealed record MediaControlsAccessibility(bool Paused, bool Muted, double CurrentTime, double Duration);

    public static class AccessibilityTreeBuilder
    {
        /// <summary>
        /// Answers the state of a media element's user agent controls (HTML §4.8.13), so the
        /// tree can expose them; the rendering engine installs it. Null means no controls.
        /// </summary>
        public static Func<Element, MediaControlsAccessibility> MediaControlsProvider { get; set; }

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
            var ownedElements = new HashSet<Element>(RefEqComparer.Instance);
            foreach (var list in ariaOwnsMap.Values)
                foreach (var owned in list)
                    ownedElements.Add(owned);

            var visited = new HashSet<Element>(RefEqComparer.Instance);
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

            if (IsAlwaysExcluded(el)) return null;

            bool isHidden = parentHidden || IsAriaHidden(el) || IsCssHidden(el);

            var role = AccessibilityRole.ResolveRole(el, doc);

            // Build children (DOM children + aria-owned children)
            var childNodes = BuildChildren(el, doc, ariaOwnsMap, ownedElements, visited, isHidden);

            // Compute name and description (skip for fully-hidden subtrees to save work)
            string name = "";
            string description = "";
            if (!isHidden)
            {
                name = AccNameCalculator.Compute(el, doc);
                description = AccDescCalculator.Compute(el, doc, name);
            }

            var states = CollectStates(el);
            return new AccessibilityNode(el, role, name, description, isHidden, childNodes, states);
        }

        /// <summary>
        /// Builds the ordered child list for <paramref name="parent"/>, honouring
        /// aria-owns reparenting and inlining explicit presentation/none elements.
        /// </summary>
        private static List<AccessibilityNode> BuildChildren(
            Element parent,
            Document doc,
            Dictionary<Element, List<Element>> ariaOwnsMap,
            HashSet<Element> ownedElements,
            HashSet<Element> visited,
            bool parentHidden)
        {
            var result = new List<AccessibilityNode>();

            // DOM children
            for (var child = parent.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is not Element childEl) continue;
                if (ownedElements.Contains(childEl)) continue; // handled via aria-owns

                AppendChild(childEl, doc, ariaOwnsMap, ownedElements, visited, parentHidden, result);
            }

            // aria-owned children (appended after DOM children per ARIA spec)
            if (ariaOwnsMap.TryGetValue(parent, out var ownedList))
            {
                foreach (var ownedEl in ownedList)
                    AppendChild(ownedEl, doc, ariaOwnsMap, ownedElements, visited, parentHidden, result);
            }

            AppendMediaControls(parent, parentHidden, result);
            return result;
        }

        /// <summary>
        /// HTML §4.8.13: a media element showing its user agent controls exposes them as a
        /// play/pause button, a seek slider and a mute button, the way the shipping engines'
        /// shadow controls do. They have no elements of their own, so they point at the
        /// media element and carry their state as ARIA properties.
        /// </summary>
        private static void AppendMediaControls(Element parent, bool parentHidden, List<AccessibilityNode> result)
        {
            var tag = parent.LocalName?.ToLowerInvariant();
            if (tag != "video" && tag != "audio") return;
            if (!parent.HasAttribute("controls")) return;
            var state = MediaControlsProvider?.Invoke(parent) ?? new MediaControlsAccessibility(true, false, 0, double.NaN);

            result.Add(new AccessibilityNode(parent, AriaRole.Button, state.Paused ? "Play" : "Pause", "", parentHidden,
                new List<AccessibilityNode>(), new Dictionary<string, string>(StringComparer.Ordinal)));

            var duration = double.IsNaN(state.Duration) || double.IsInfinity(state.Duration) ? 0 : state.Duration;
            var slider = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aria-valuemin"] = "0",
                ["aria-valuemax"] = duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["aria-valuenow"] = Math.Max(0, state.CurrentTime).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["aria-valuetext"] = FormatMediaTime(state.CurrentTime) + " of " + FormatMediaTime(duration),
            };
            result.Add(new AccessibilityNode(parent, AriaRole.Slider, "Seek", "", parentHidden, new List<AccessibilityNode>(), slider));

            result.Add(new AccessibilityNode(parent, AriaRole.Button, state.Muted ? "Unmute" : "Mute", "", parentHidden,
                new List<AccessibilityNode>(), new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        private static string FormatMediaTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "0:00";
            long total = (long)Math.Floor(seconds);
            long h = total / 3600, m = (total % 3600) / 60, s = total % 60;
            return h > 0
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
        }

        /// <summary>
        /// Builds a child node and appends it (or, for explicit presentation/none roles,
        /// inlines its children directly into <paramref name="result"/>).
        /// </summary>
        private static void AppendChild(
            Element childEl,
            Document doc,
            Dictionary<Element, List<Element>> ariaOwnsMap,
            HashSet<Element> ownedElements,
            HashSet<Element> parentVisited,
            bool parentHidden,
            List<AccessibilityNode> result)
        {
            if (IsAlwaysExcluded(childEl)) return;

            bool childHidden = parentHidden || IsAriaHidden(childEl) || IsCssHidden(childEl);

            // Explicit role="presentation" / role="none": skip the element, promote children.
            // Note: only EXPLICIT author-supplied role triggers this; implicit AriaRole.None
            // (elements with no corresponding ARIA role, e.g. <br>, <col>) are handled normally.
            if (!childHidden && HasExplicitPresentationRole(childEl))
            {
                // Guard against cycles before recursing into children
                var presentVisited = new HashSet<Element>(parentVisited, RefEqComparer.Instance);
                if (!presentVisited.Add(childEl)) return;

                var promoted = BuildChildren(childEl, doc, ariaOwnsMap, ownedElements,
                                             presentVisited, parentHidden);
                result.AddRange(promoted);
                return;
            }

            var childVisited = new HashSet<Element>(parentVisited, RefEqComparer.Instance);
            var childNode = BuildNode(childEl, doc, ariaOwnsMap, ownedElements, childVisited, parentHidden);
            if (childNode != null)
                result.Add(childNode);
        }

        // ---- aria-owns map ----

        private static Dictionary<Element, List<Element>> BuildAriaOwnsMap(Document doc)
        {
            var map = new Dictionary<Element, List<Element>>(RefEqComparer.Instance);
            // Tracks which elements have already been claimed to prevent double-owning.
            var claimed = new HashSet<Element>(RefEqComparer.Instance);

            foreach (var node in doc.Descendants())
            {
                if (node is not Element el) continue;
                var ariaOwns = el.GetAttribute("aria-owns");
                if (string.IsNullOrWhiteSpace(ariaOwns)) continue;

                var ownedList = new List<Element>();
                foreach (var id in ariaOwns.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var ownedEl = doc.GetElementById(id);
                    // Reject: not found, self-reference, or already owned by another element.
                    if (ownedEl == null) continue;
                    if (ReferenceEquals(ownedEl, el)) continue;
                    if (!claimed.Add(ownedEl)) continue;
                    ownedList.Add(ownedEl);
                }

                if (ownedList.Count > 0)
                    map[el] = ownedList;
            }

            return map;
        }

        // ---- Exclusion / visibility checks ----

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

        /// <summary>
        /// Heuristic check of the inline style attribute for display:none / visibility:hidden.
        /// Does not inspect the CSS cascade; computed styles are too expensive to resolve here.
        /// Uses a more robust approach (strips internal whitespace before comparing) to avoid
        /// being fooled by arbitrary whitespace in author stylesheets.
        /// </summary>
        private static bool IsCssHidden(Element el)
        {
            var style = el.GetAttribute("style");
            if (string.IsNullOrEmpty(style)) return false;

            // Compact to "prop:value" pairs for reliable substring matching.
            var compact = CompactCss(style);
            return compact.Contains("display:none") || compact.Contains("visibility:hidden");
        }

        /// <summary>
        /// Returns true if the element carries an explicit author-supplied role of
        /// "presentation" or "none" (first valid token from the role attribute).
        /// </summary>
        private static bool HasExplicitPresentationRole(Element el)
        {
            var roleAttr = el.GetAttribute("role");
            if (string.IsNullOrWhiteSpace(roleAttr)) return false;
            foreach (var token in roleAttr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim().ToLowerInvariant();
                if (t == "presentation" || t == "none") return true;
            }
            return false;
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

        // ---- Helpers ----

        /// <summary>
        /// Strips whitespace around ':' and ';' in a CSS string so substring matching is
        /// independent of author formatting (e.g. "display : none" → "display:none").
        /// </summary>
        private static string CompactCss(string style)
        {
            var sb = new System.Text.StringBuilder(style.Length);
            bool lastWasSep = false;
            foreach (char c in style.ToLowerInvariant())
            {
                if (c == ':' || c == ';')
                {
                    // Trim trailing space before separator
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                        sb.Remove(sb.Length - 1, 1);
                    sb.Append(c);
                    lastWasSep = true;
                }
                else if (c == ' ' && lastWasSep)
                {
                    // Skip leading space after separator
                }
                else
                {
                    sb.Append(c);
                    lastWasSep = false;
                }
            }
            return sb.ToString();
        }

        // ---- Equality comparer (shared instance) ----

        private sealed class RefEqComparer : IEqualityComparer<Element>
        {
            public static readonly RefEqComparer Instance = new RefEqComparer();
            public bool Equals(Element x, Element y) => ReferenceEquals(x, y);
            public int GetHashCode(Element obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
