using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Interaction
{
    /// <summary>
    /// Manages focus state, TabIndex navigation, and active element tracking.
    /// </summary>
    public class FocusManager
    {
        public Element FocusedElement { get; private set; }

        public void SetFocus(Element element)
        {
            if (element == FocusedElement) return;

            // 1. Blur current
            if (FocusedElement != null)
            {
                // Dispatch 'blur' event
                // FocusedElement.DispatchEvent(new FocusEvent("blur"));
            }

            // 2. Check if focusable
            if (IsFocusable(element))
            {
                FocusedElement = element;
                // Dispatch 'focus' event
            }
            else
            {
                // If clicking non-focusable, maybe clear focus? Or keep previous?
                // Usually body is fallback.
                FocusedElement = null; 
            }
        }

        public bool IsFocusable(Element element)
        {
            if (element == null) return false;
             
            // Check TabIndex
            if (element.GetAttribute("tabindex") != null) return true;
            
            // Implicitly focusable elements
            var tag = element.TagName.ToLowerInvariant();
            if (tag == "input" || tag == "button" || tag == "select" || tag == "textarea") return true;
            if (tag == "a" && element.GetAttribute("href") != null) return true;
             
            return false;
        }

        public Element FindNextFocusable(Node root, bool reverse = false)
        {
            var focusables = CollectFocusableElements(root);
            if (focusables.Count == 0)
            {
                return null;
            }

            // tabindex ordering: positive values first ascending, then zero/implicit tree order.
            var ordered = focusables
                .Select((element, order) => new
                {
                    Element = element,
                    Order = order,
                    TabIndex = ParseTabIndex(element)
                })
                .OrderBy(item => item.TabIndex <= 0 ? int.MaxValue : item.TabIndex)
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

        private static int ParseTabIndex(Element element)
        {
            var raw = element?.GetAttribute("tabindex");
            if (int.TryParse(raw, out var value))
            {
                return value;
            }

            return 0;
        }

        private List<Element> CollectFocusableElements(Node root)
        {
            var results = new List<Element>();
            if (root == null)
            {
                return results;
            }

            if (root is Element rootElement && IsFocusable(rootElement))
            {
                results.Add(rootElement);
            }

            foreach (var node in root.Descendants())
            {
                if (node is Element element && IsFocusable(element))
                {
                    results.Add(element);
                }
            }

            return results;
        }
    }
}

