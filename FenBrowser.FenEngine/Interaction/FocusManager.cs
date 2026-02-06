using System.Collections.Generic;
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

        // TODO: Implement Tab Navigation (FindNextFocusable)
    }
}

