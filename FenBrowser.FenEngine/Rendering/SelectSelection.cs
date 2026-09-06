using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// The selection state machine for &lt;select&gt; (HTML 4.10.10).
    ///
    /// An option's selectedness is internal state; the "selected" content
    /// attribute is only its default. Two rules make a select more than a bag of
    /// independent booleans, and both live here so every caller gets them:
    /// setting one option's selectedness in a single-selection list clears the
    /// others, and "ask for a reset" gives a dropdown an implicit selection when
    /// the markup names none.
    /// </summary>
    internal static class SelectSelection
    {
        /// <summary>
        /// The select's list of options in tree order. Options may sit inside an
        /// optgroup, so this walks descendants rather than direct children.
        /// </summary>
        internal static List<Element> Options(Element select)
        {
            if (select == null) return new List<Element>();

            return select
                .Descendants()
                .OfType<Element>()
                .Where(el => string.Equals(el.NodeName, "OPTION", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// True when the select may have more than one option selected at a time.
        /// A "multiple" select or a list box (size &gt; 1) may; a dropdown may not.
        /// </summary>
        internal static bool AllowsMultiple(Element select)
        {
            if (select == null) return false;
            if (select.HasAttribute("multiple")) return true;

            return int.TryParse(select.GetAttribute("size"), out var size) && size > 1;
        }

        /// <summary>The nearest ancestor select, or null for a detached option.</summary>
        internal static Element OwnerSelect(Element option)
        {
            for (var cursor = option?.ParentElement; cursor != null; cursor = cursor.ParentElement)
            {
                if (string.Equals(cursor.NodeName, "SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return cursor;
                }
            }

            return null;
        }

        internal static bool IsSelected(Element option)
            => ElementStateManager.Instance.IsOptionSelected(option);

        /// <summary>
        /// HTML "selectedness setting algorithm": set one option's selectedness,
        /// and in a single-selection list clear every sibling, because the list can
        /// only be showing one of them.
        /// </summary>
        internal static void SetSelected(Element option, bool selected)
        {
            if (option == null) return;

            var select = OwnerSelect(option);
            ElementStateManager.Instance.SetOptionSelected(option, selected);

            if (select == null) return;

            if (selected && !AllowsMultiple(select))
            {
                foreach (var other in Options(select))
                {
                    if (!ReferenceEquals(other, option))
                    {
                        ElementStateManager.Instance.SetOptionSelected(other, false);
                    }
                }
            }

            AskForReset(select);
        }

        /// <summary>
        /// HTML "ask for a reset": a single-selection select always displays
        /// exactly one option when it has any to show. If several ended up
        /// selected the last one wins; if none did, the first selectable option is
        /// picked -- which is why a dropdown whose markup selects nothing still
        /// submits its first entry.
        /// </summary>
        internal static void AskForReset(Element select)
        {
            if (select == null || AllowsMultiple(select)) return;

            var options = Options(select);
            if (options.Count == 0) return;

            var selected = options.Where(IsSelected).ToList();

            if (selected.Count > 1)
            {
                var keep = selected[selected.Count - 1];
                foreach (var option in selected)
                {
                    if (!ReferenceEquals(option, keep))
                    {
                        ElementStateManager.Instance.SetOptionSelected(option, false);
                    }
                }

                return;
            }

            if (selected.Count == 0)
            {
                var first = options.FirstOrDefault(option => !IsDisabled(option));
                if (first != null)
                {
                    ElementStateManager.Instance.SetOptionSelected(first, true);
                }
            }
        }

        /// <summary>
        /// The options that contribute to a submission: every selected, non-disabled
        /// option. A "multiple" select contributes one entry per selection, so this
        /// is a list rather than a single value.
        /// </summary>
        internal static List<Element> SuccessfulOptions(Element select)
        {
            if (select == null) return new List<Element>();

            AskForReset(select);
            return Options(select)
                .Where(option => IsSelected(option) && !IsDisabled(option))
                .ToList();
        }

        /// <summary>
        /// HTML dom-select-value: the value of the first selected option, or the
        /// empty string when nothing is selected.
        /// </summary>
        internal static string Value(Element select)
        {
            var selected = SuccessfulOptions(select).FirstOrDefault()
                ?? Options(select).FirstOrDefault(IsSelected);
            return selected == null ? string.Empty : FormControlValue.Read(selected);
        }

        /// <summary>
        /// Selects the first option whose value matches. No match deselects
        /// everything, which for a dropdown then falls back to its first option.
        /// </summary>
        internal static void SetValue(Element select, string value)
        {
            if (select == null) return;

            var options = Options(select);
            var match = options.FirstOrDefault(
                option => string.Equals(FormControlValue.Read(option), value, StringComparison.Ordinal));

            foreach (var option in options)
            {
                ElementStateManager.Instance.SetOptionSelected(option, ReferenceEquals(option, match));
            }

            if (match == null)
            {
                AskForReset(select);
            }
        }

        /// <summary>
        /// HTML dom-select-selectedindex: the index of the first selected option,
        /// or -1 when none is.
        /// </summary>
        internal static int SelectedIndex(Element select)
        {
            var options = Options(select);
            for (var i = 0; i < options.Count; i++)
            {
                if (IsSelected(options[i])) return i;
            }

            return -1;
        }

        /// <summary>
        /// Selects the option at an index, deselecting the rest. An out-of-range
        /// index -- -1 included -- deselects everything.
        /// </summary>
        internal static void SetSelectedIndex(Element select, int index)
        {
            if (select == null) return;

            var options = Options(select);
            for (var i = 0; i < options.Count; i++)
            {
                ElementStateManager.Instance.SetOptionSelected(options[i], i == index);
            }
        }

        /// <summary>
        /// Clears the selection overrides under a select so every option goes back
        /// to its "selected" attribute, then re-applies the dropdown rule. This is
        /// the select's half of a form reset.
        /// </summary>
        internal static void Reset(Element select)
        {
            if (select == null) return;

            foreach (var option in Options(select))
            {
                ElementStateManager.Instance.ResetFormControlState(option);
            }

            AskForReset(select);
        }

        private static bool IsDisabled(Element option)
        {
            if (option == null) return true;

            for (var cursor = option; cursor != null; cursor = cursor.ParentElement)
            {
                if (cursor.HasAttribute("disabled")) return true;
                if (string.Equals(cursor.NodeName, "SELECT", StringComparison.OrdinalIgnoreCase)) break;
            }

            return false;
        }
    }
}
