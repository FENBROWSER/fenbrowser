// W3C Accessible Name and Description Computation 1.1 — Description computation
// https://www.w3.org/TR/accname-1.1/#mapping_additional_nd_description
// FenBrowser.Core.Accessibility

using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// Computes the accessible description for an element following AccName 1.1.
    /// </summary>
    public static class AccDescCalculator
    {
        /// <summary>
        /// Computes the accessible description for <paramref name="el"/>.
        /// Returns an empty string when no description can be determined.
        /// </summary>
        public static string Compute(Element el, Document doc)
        {
            if (el == null) return "";

            // Step 1: aria-describedby
            var describedby = el.GetAttribute("aria-describedby");
            if (!string.IsNullOrWhiteSpace(describedby))
            {
                var sb = new StringBuilder();
                var visited = new HashSet<Element>(new ReferenceEqComparer());
                visited.Add(el);

                foreach (var id in describedby.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var refEl = doc?.GetElementById(id);
                    if (refEl == null) continue;
                    if (!visited.Add(refEl)) continue;

                    var text = GetTextContent(refEl, visited);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(text.Trim());
                    }
                }

                var result = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(result))
                    return result;
            }

            // Step 2: title attribute (only when not used as accessible name)
            // The title is used as description when the accessible name was obtained by other means.
            var name = AccNameCalculator.Compute(el, doc);
            var title = el.GetAttribute("title");
            if (!string.IsNullOrWhiteSpace(title) && title.Trim() != name)
                return title.Trim();

            return "";
        }

        private static string GetTextContent(Element el, HashSet<Element> visited)
        {
            var sb = new StringBuilder();
            AppendTextContent(el, visited, sb);
            return sb.ToString().Trim();
        }

        private static void AppendTextContent(Node node, HashSet<Element> visited, StringBuilder sb)
        {
            if (node is Text t)
            {
                sb.Append(t.Data);
                return;
            }
            if (node is not Element el) return;
            if (el.GetAttribute("aria-hidden")?.Trim().ToLowerInvariant() == "true") return;

            for (var child = el.FirstChild; child != null; child = child.NextSibling)
                AppendTextContent(child, visited, sb);
        }

        private sealed class ReferenceEqComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y) => ReferenceEquals(x, y);
            public int GetHashCode(Element obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
