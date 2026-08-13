using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Interaction
{
    /// <summary>
    /// Manages focus state, tabindex navigation, and active element tracking.
    /// </summary>
    public class FocusManager
    {
        public Element FocusedElement { get; private set; }

        public void SetFocus(Element element)
        {
            if (ReferenceEquals(element, FocusedElement)) return;

            // Programmatic focus may target tabindex=-1; sequential navigation is
            // filtered separately by IsSequentiallyFocusable.
            FocusedElement = IsFocusable(element) ? element : null;
        }

        public bool IsFocusable(Element element)
        {
            if (element == null || IsDisabledOrHidden(element)) return false;

            // Any valid tabindex makes an otherwise non-focusable element focusable,
            // including negative values (programmatic focus only).
            var rawTabIndex = element.GetAttribute("tabindex");
            if (rawTabIndex != null && int.TryParse(rawTabIndex, out _)) return true;

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            if (tag is "input" or "button" or "select" or "textarea") return true;
            if (tag == "a" && element.GetAttribute("href") != null) return true;

            var contentEditable = element.GetAttribute("contenteditable");
            if (contentEditable != null &&
                !string.Equals(contentEditable, "false", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public Element FindNextFocusable(Node root, bool reverse = false)
        {
            var focusables = CollectSequentiallyFocusableElements(root);
            if (focusables.Count == 0)
            {
                return null;
            }

            // HTML sequential focus order: positive tabindex values first in ascending
            // order, then tabindex=0 and implicitly focusable elements in tree order.
            // Negative tabindex values are intentionally absent from this collection.
            var ordered = focusables
                .Select((element, order) => new
                {
                    Element = element,
                    Order = order,
                    TabIndex = ParseTabIndex(element)
                })
                .OrderBy(item => item.TabIndex > 0 ? 0 : 1)
                .ThenBy(item => item.TabIndex > 0 ? item.TabIndex : 0)
                .ThenBy(item => item.Order)
                .Select(item => item.Element)
                .ToList();

            var currentIndex = FocusedElement != null ? ordered.IndexOf(FocusedElement) : -1;
            if (reverse)
            {
                var index = currentIndex <= 0 ? ordered.Count - 1 : currentIndex - 1;
                return ordered[index];
            }

            var next = (currentIndex + 1) % ordered.Count;
            return ordered[next];
        }

        private bool IsSequentiallyFocusable(Element element)
        {
            if (!IsFocusable(element)) return false;

            var raw = element.GetAttribute("tabindex");
            if (raw != null && int.TryParse(raw, out var tabIndex) && tabIndex < 0)
            {
                return false;
            }

            return true;
        }

        private static int ParseTabIndex(Element element)
        {
            var raw = element?.GetAttribute("tabindex");
            return int.TryParse(raw, out var value) ? value : 0;
        }

        private List<Element> CollectSequentiallyFocusableElements(Node root)
        {
            var results = new List<Element>();
            if (root == null)
            {
                return results;
            }

            if (root is Element rootElement && IsSequentiallyFocusable(rootElement))
            {
                results.Add(rootElement);
            }

            foreach (var node in root.Descendants())
            {
                if (node is Element element && IsSequentiallyFocusable(element))
                {
                    results.Add(element);
                }
            }

            return results;
        }

        private static bool IsDisabledOrHidden(Element element)
        {
            if (element.GetAttribute("hidden") != null) return true;

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            if (tag is "input" or "button" or "select" or "textarea" or "optgroup" or "option")
            {
                if (element.GetAttribute("disabled") != null) return true;
            }

            return false;
        }
    }
}
