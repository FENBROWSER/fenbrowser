using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>What a restyle changed, from nothing to geometry.</summary>
    internal enum StyleChange
    {
        None = 0,
        Paint = 1,
        Layout = 2,
    }

    /// <summary>
    /// Compares an element's computed style before and after a restyle. The incremental
    /// recascade marked every restyled subtree for layout whatever changed, and layout is
    /// a full document pass here, so a class or attribute toggle that matched no rule, or
    /// only changed a colour, cost a whole relayout at the next geometry read. YouTube
    /// toggles such attributes thousands of times per load, each followed by a
    /// getBoundingClientRect. Structural changes (insertion, removal, text) mark layout
    /// themselves; this only decides what a style change needs.
    /// </summary>
    internal static class StyleChangeClassifier
    {
        // Properties whose value never moves or resizes a box: they are read at paint
        // time only. Anything not listed is treated as able to change layout.
        private static readonly HashSet<string> PaintOnlyProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "opacity", "visibility", "cursor", "pointer-events", "user-select", "-webkit-user-select",
            "caret-color", "accent-color", "-webkit-tap-highlight-color", "will-change",
            "background", "background-color", "background-image", "background-position", "background-position-x",
            "background-position-y", "background-repeat", "background-size", "background-clip", "background-origin",
            "background-attachment", "background-blend-mode",
            "border-color", "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
            "border-block-start-color", "border-block-end-color", "border-inline-start-color", "border-inline-end-color",
            "outline", "outline-color", "outline-style", "outline-width", "outline-offset",
            "box-shadow", "text-shadow", "filter", "backdrop-filter", "mix-blend-mode",
            "text-decoration", "text-decoration-color", "text-decoration-line", "text-decoration-style",
            "text-decoration-thickness", "text-underline-offset",
            "fill", "fill-opacity", "stroke", "stroke-opacity", "stroke-width", "stroke-dasharray", "stroke-dashoffset",
            "stop-color", "stop-opacity", "flood-color", "flood-opacity", "lighting-color",
        };

        public static StyleChange Classify(CssComputed before, CssComputed after)
        {
            if (ReferenceEquals(before, after))
            {
                return StyleChange.None;
            }

            if (before == null || after == null)
            {
                return StyleChange.Layout;
            }

            var change = CompareMaps(before.Map, after.Map);
            if (change == StyleChange.Layout)
            {
                return change;
            }

            change = Max(change, ComparePseudo(before.Before, after.Before));
            change = Max(change, ComparePseudo(before.After, after.After));
            change = Max(change, ComparePseudo(before.Marker, after.Marker));
            change = Max(change, ComparePseudo(before.FirstLine, after.FirstLine));
            change = Max(change, ComparePseudo(before.FirstLetter, after.FirstLetter));
            change = Max(change, ComparePseudo(before.Placeholder, after.Placeholder));
            if (change == StyleChange.Layout)
            {
                return change;
            }

            // ::selection only ever paints.
            if (ComparePseudo(before.Selection, after.Selection) != StyleChange.None)
            {
                change = Max(change, StyleChange.Paint);
            }

            // Generated content naming attr() reads the attribute at box-building time,
            // so a changed attribute changes the text without changing any declaration.
            if (UsesAttributeContent(after.Before) || UsesAttributeContent(after.After))
            {
                return StyleChange.Layout;
            }

            return change;
        }

        public static StyleChange Max(StyleChange a, StyleChange b) => a >= b ? a : b;

        private static StyleChange ComparePseudo(CssComputed before, CssComputed after)
        {
            if (before == null && after == null)
            {
                return StyleChange.None;
            }

            if (before == null || after == null)
            {
                return StyleChange.Layout;
            }

            return CompareMaps(before.Map, after.Map);
        }

        private static StyleChange CompareMaps(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            if (before == null || after == null)
            {
                return before == null && after == null ? StyleChange.None : StyleChange.Layout;
            }

            var change = StyleChange.None;
            foreach (var entry in after)
            {
                if (before.TryGetValue(entry.Key, out var previous) &&
                    string.Equals(previous, entry.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!PaintOnlyProperties.Contains(entry.Key) || IsCollapseVisibility(entry.Key, previous, entry.Value))
                {
                    return StyleChange.Layout;
                }

                change = StyleChange.Paint;
            }

            foreach (var key in before.Keys)
            {
                if (after.ContainsKey(key))
                {
                    continue;
                }

                if (!PaintOnlyProperties.Contains(key))
                {
                    return StyleChange.Layout;
                }

                change = StyleChange.Paint;
            }

            return change;
        }

        // visibility only paints, except collapse, which removes table rows and columns
        // from table layout (CSS 2.1 17.5.5).
        private static bool IsCollapseVisibility(string property, string before, string after) =>
            string.Equals(property, "visibility", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(before?.Trim(), "collapse", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(after?.Trim(), "collapse", StringComparison.OrdinalIgnoreCase));

        private static bool UsesAttributeContent(CssComputed pseudo) =>
            pseudo?.Map != null &&
            pseudo.Map.TryGetValue("content", out var content) &&
            content != null &&
            content.Contains("attr(", StringComparison.OrdinalIgnoreCase);
    }
}
