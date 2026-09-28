using System;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A text control's selection as HTML 4.10.5.2.10 defines it: a start, an end
    /// and a direction held per element, kept while the control is unfocused and
    /// read and written through selectionStart, selectionEnd, selectionDirection,
    /// setSelectionRange(), select() and setRangeText().
    ///
    /// The keyboard editor in BrowserApi owns the caret while the user types and
    /// publishes it here after every edit. A script write bumps the element's
    /// version, which is how the editor knows to adopt the script's selection the
    /// next time it handles a key.
    /// </summary>
    internal static class FormControlSelection
    {
        private sealed class State
        {
            public int Start;
            public int End;
            public string Direction = "none";
            public int ScriptVersion;
        }

        private static readonly ConditionalWeakTable<Element, State> States = new();
        private static readonly object Gate = new();

        /// <summary>
        /// The selection APIs apply to textarea and to input types text, search,
        /// url, tel and password; everywhere else they return null or throw.
        /// </summary>
        internal static bool Applies(Element element)
        {
            if (element == null) return false;
            if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase)) return false;
            return FormControlValue.ReadInputType(element) is "text" or "search" or "url" or "tel" or "password";
        }

        internal static (int Start, int End, string Direction) Get(Element element)
        {
            var length = FormControlValue.Read(element).Length;
            lock (Gate)
            {
                if (!States.TryGetValue(element, out var state))
                {
                    return (0, 0, "none");
                }

                var end = Math.Clamp(state.End, 0, length);
                return (Math.Clamp(state.Start, 0, end), end, state.Direction);
            }
        }

        /// <summary>
        /// HTML "set the selection range": offsets are clamped to the value, a
        /// start past the end collapses to the end, and a direction other than
        /// forward or backward is none.
        /// </summary>
        internal static void Set(Element element, int start, int end, string direction, bool fromScript)
        {
            if (element == null) return;
            var length = FormControlValue.Read(element).Length;
            end = Math.Clamp(end, 0, length);
            start = Math.Clamp(start, 0, end);
            direction = direction is "forward" or "backward" ? direction : "none";

            lock (Gate)
            {
                var state = States.GetOrCreateValue(element);
                state.Start = start;
                state.End = end;
                state.Direction = direction;
                if (fromScript)
                {
                    state.ScriptVersion++;
                }
            }
        }

        /// <summary>
        /// HTML: setting the value of a text control moves the selection to the end
        /// of the new value when the value actually changed.
        /// </summary>
        internal static void CollapseToEnd(Element element)
        {
            var length = FormControlValue.Read(element).Length;
            Set(element, length, length, "none", fromScript: true);
        }

        internal static int ScriptVersion(Element element)
        {
            if (element == null) return 0;
            lock (Gate)
            {
                return States.TryGetValue(element, out var state) ? state.ScriptVersion : 0;
            }
        }
    }
}
