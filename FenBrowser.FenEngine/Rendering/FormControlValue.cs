using System;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A form control's value and checkedness as HTML defines them: internal state
    /// that starts out tracking a content attribute and stops the moment the user
    /// or a script changes it (HTML 4.10.5.1, "value mode" and the dirty value
    /// flag).
    ///
    /// The distinction matters wherever the browser reads a control back. Reading
    /// the "value" content attribute returns the page's initial markup, not what is
    /// on screen, so a submission built that way sends stale data -- and an
    /// assignment written through to the attribute is visible to getAttribute and
    /// survives a reset, neither of which should happen.
    /// </summary>
    internal static class FormControlValue
    {
        /// <summary>
        /// The input's type keyword, normalized. Anything unrecognized is "text",
        /// which is also the missing-value default.
        /// </summary>
        internal static string ReadInputType(Element element)
        {
            var type = (element?.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
            return type switch
            {
                "hidden" or "text" or "search" or "tel" or "url" or "email" or "password" or
                "date" or "month" or "week" or "time" or "datetime-local" or "number" or
                "range" or "color" or "checkbox" or "radio" or "file" or "submit" or "image" or
                "reset" or "button" => type,
                _ => "text"
            };
        }

        /// <summary>
        /// True for controls in HTML's "value" mode, the only ones whose value is
        /// held as internal state. "default" mode (hidden, submit, reset, button,
        /// image), "default/on" mode (checkbox, radio) and "filename" mode (file)
        /// all reflect a content attribute instead.
        /// </summary>
        internal static bool UsesDirtyValueState(Element element)
        {
            if (element == null) return false;

            if (IsTag(element, "textarea")) return true;
            if (!IsTag(element, "input")) return false;

            return ReadInputType(element) switch
            {
                "hidden" or "submit" or "reset" or "button" or "image" or
                "checkbox" or "radio" or "file" => false,
                _ => true
            };
        }

        /// <summary>The control's current value, honouring the dirty value flag.</summary>
        internal static string Read(Element element)
        {
            if (element == null) return string.Empty;

            if (UsesDirtyValueState(element))
            {
                return ElementStateManager.Instance.GetValue(element);
            }

            var attribute = element.GetAttribute("value");
            if (attribute != null) return attribute;

            // "default/on" mode: a checkbox or radio with no value attribute
            // submits the literal string "on".
            if (IsTag(element, "input") && ReadInputType(element) is "checkbox" or "radio")
            {
                return "on";
            }

            // An option with no value attribute takes its text as the value.
            if (IsTag(element, "option")) return element.TextContent ?? string.Empty;

            return string.Empty;
        }

        /// <summary>
        /// Sets the value. A value-mode control records this as internal state and
        /// raises its dirty value flag rather than writing the content attribute.
        /// </summary>
        internal static void Write(Element element, string value)
        {
            if (element == null) return;
            value ??= string.Empty;

            if (UsesDirtyValueState(element))
            {
                var changed = !string.Equals(ElementStateManager.Instance.GetValue(element), value, StringComparison.Ordinal);
                ElementStateManager.Instance.SetValue(element, value);
                if (changed && FormControlSelection.Applies(element))
                {
                    FormControlSelection.CollapseToEnd(element);
                }
                return;
            }

            element.SetAttribute("value", value);
        }

        /// <summary>
        /// The default value, which defaultValue exposes and a form reset restores:
        /// a textarea's child text content, otherwise the "value" content attribute.
        /// </summary>
        internal static string ReadDefault(Element element)
            => ElementStateManager.Instance.GetDefaultValue(element);

        internal static void WriteDefault(Element element, string value)
        {
            if (element == null) return;
            value ??= string.Empty;

            if (IsTag(element, "textarea"))
            {
                element.TextContent = value;
                return;
            }

            element.SetAttribute("value", value);
        }

        /// <summary>
        /// Live checkedness, which is what decides whether a checkbox or radio is
        /// successful. The "checked" content attribute is only the default.
        /// </summary>
        internal static bool IsChecked(Element element)
            => element != null && ElementStateManager.Instance.IsChecked(element);

        private static bool IsTag(Element element, string tag)
            => string.Equals(element?.TagName, tag, StringComparison.OrdinalIgnoreCase);
    }
}
