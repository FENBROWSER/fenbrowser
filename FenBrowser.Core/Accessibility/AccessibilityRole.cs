// HTML element → ARIA implicit role mapping per ARIA in HTML spec
// https://www.w3.org/TR/html-aria/
// FenBrowser.Core.Accessibility

using System;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// Maps HTML elements to their implicit ARIA roles and resolves the effective role
    /// considering any explicit role attribute override.
    /// </summary>
    public static class AccessibilityRole
    {
        /// <summary>
        /// Returns the implicit ARIA role for <paramref name="el"/> as defined by ARIA in HTML.
        /// Context (parent element, attributes) is considered where relevant.
        /// </summary>
        public static AriaRole GetImplicitRole(Element el, Document doc)
        {
            if (el == null) return AriaRole.None;

            var tag = el.LocalName;
            switch (tag)
            {
                case "a":
                    // <a> with href → link; without href → generic
                    return el.HasAttribute("href") ? AriaRole.Link : AriaRole.Generic;

                case "abbr":
                    return AriaRole.None;

                case "address":
                    return AriaRole.Group;

                case "area":
                    return el.HasAttribute("href") ? AriaRole.Link : AriaRole.None;

                case "article":
                    return AriaRole.Article;

                case "aside":
                    return IsDescendantOfLandmark(el) ? AriaRole.Complementary : AriaRole.Complementary;

                case "audio":
                    return AriaRole.None;

                case "b":
                    return AriaRole.Generic;

                case "bdi":
                case "bdo":
                    return AriaRole.Generic;

                case "blockquote":
                    return AriaRole.None; // ARIA 1.2: blockquote role

                case "body":
                    return AriaRole.Generic;

                case "br":
                    return AriaRole.None;

                case "button":
                    return AriaRole.Button;

                case "canvas":
                    return AriaRole.None;

                case "caption":
                    return AriaRole.Caption;

                case "cite":
                    return AriaRole.None;

                case "code":
                    return AriaRole.Code;

                case "col":
                case "colgroup":
                    return AriaRole.None;

                case "data":
                    return AriaRole.Generic;

                case "datalist":
                    return AriaRole.Listbox;

                case "dd":
                    return AriaRole.Definition;

                case "del":
                    return AriaRole.Deletion;

                case "details":
                    return AriaRole.Group;

                case "dfn":
                    return AriaRole.Term;

                case "dialog":
                    return AriaRole.Dialog;

                case "div":
                    return AriaRole.Generic;

                case "dl":
                    return AriaRole.None; // No corresponding ARIA role

                case "dt":
                    return AriaRole.Term;

                case "em":
                    return AriaRole.Emphasis;

                case "embed":
                    return AriaRole.None;

                case "fieldset":
                    return AriaRole.Group;

                case "figcaption":
                    return AriaRole.None;

                case "figure":
                    return AriaRole.Figure;

                case "footer":
                    return IsLandmarkContext(el) ? AriaRole.Generic : AriaRole.Contentinfo;

                case "form":
                    // <form> with accessible name → form role; otherwise generic
                    return HasAccessibleName(el, doc) ? AriaRole.Form : AriaRole.Generic;

                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    return AriaRole.Heading;

                case "head":
                case "html":
                    return AriaRole.None;

                case "header":
                    return IsLandmarkContext(el) ? AriaRole.Generic : AriaRole.Banner;

                case "hgroup":
                    return AriaRole.Generic;

                case "hr":
                    return AriaRole.Separator;

                case "i":
                    return AriaRole.Generic;

                case "iframe":
                    return AriaRole.None;

                case "img":
                {
                    var alt = el.GetAttribute("alt");
                    if (alt != null && alt.Length == 0)
                        return AriaRole.Presentation; // alt="" → decorative
                    return AriaRole.Img;
                }

                case "input":
                    return GetInputRole(el);

                case "ins":
                    return AriaRole.Insertion;

                case "kbd":
                    return AriaRole.None;

                case "label":
                    return AriaRole.None;

                case "legend":
                    return AriaRole.None;

                case "li":
                    return IsInList(el) ? AriaRole.Listitem : AriaRole.Generic;

                case "link":
                case "meta":
                case "script":
                case "style":
                case "title":
                    return AriaRole.None;

                case "main":
                    return AriaRole.Main;

                case "map":
                    return AriaRole.None;

                case "mark":
                    return AriaRole.None;

                case "math":
                    return AriaRole.Math;

                case "menu":
                    return AriaRole.List;

                case "meter":
                    return AriaRole.None;

                case "nav":
                    return AriaRole.Navigation;

                case "noscript":
                    return AriaRole.None;

                case "object":
                    return AriaRole.None;

                case "ol":
                    return AriaRole.List;

                case "optgroup":
                    return AriaRole.Group;

                case "option":
                    return AriaRole.Option;

                case "output":
                    return AriaRole.Status;

                case "p":
                    return AriaRole.Paragraph;

                case "picture":
                    return AriaRole.None;

                case "progress":
                    return AriaRole.Progressbar;

                case "q":
                    return AriaRole.None;

                case "rp":
                case "rt":
                case "ruby":
                    return AriaRole.None;

                case "s":
                    return AriaRole.Deletion;

                case "samp":
                    return AriaRole.Generic;

                case "search":
                    return AriaRole.Search;

                case "section":
                    // <section> with accessible name → region; otherwise generic
                    return HasAccessibleName(el, doc) ? AriaRole.Region : AriaRole.Generic;

                case "select":
                {
                    var multiple = el.HasAttribute("multiple");
                    var sizeAttr = el.GetAttribute("size");
                    var size = 1;
                    int.TryParse(sizeAttr, out size);
                    return (multiple || size > 1) ? AriaRole.Listbox : AriaRole.Combobox;
                }

                case "small":
                    return AriaRole.Generic;

                case "span":
                    return AriaRole.Generic;

                case "strong":
                    return AriaRole.Strong;

                case "sub":
                    return AriaRole.Subscript;

                case "summary":
                    return AriaRole.Button;

                case "sup":
                    return AriaRole.Superscript;

                case "svg":
                    return AriaRole.None; // SVG root has no implicit ARIA role

                case "table":
                    return AriaRole.Table;

                case "tbody":
                case "thead":
                case "tfoot":
                    return AriaRole.Rowgroup;

                case "td":
                    return IsInGridOrTreegrid(el) ? AriaRole.Gridcell : AriaRole.Cell;

                case "template":
                    return AriaRole.None;

                case "textarea":
                    return AriaRole.Textbox;

                case "th":
                {
                    var scope = el.GetAttribute("scope");
                    if (scope != null)
                    {
                        if (scope == "row" || scope == "rowgroup")
                            return AriaRole.Rowheader;
                        return AriaRole.Columnheader; // col / colgroup / auto-column
                    }
                    // No scope: columnheader by default
                    return AriaRole.Columnheader;
                }

                case "time":
                    return AriaRole.Time;

                case "tr":
                    return AriaRole.Row;

                case "track":
                    return AriaRole.None;

                case "u":
                    return AriaRole.Generic;

                case "ul":
                    return AriaRole.List;

                case "var":
                    return AriaRole.None;

                case "video":
                    return AriaRole.None;

                case "wbr":
                    return AriaRole.None;

                default:
                    // Custom elements / unknown elements
                    return AriaRole.None;
            }
        }

        /// <summary>
        /// Returns the effective ARIA role for <paramref name="el"/>:
        /// explicit role attribute (first recognized token) overrides the implicit role.
        /// </summary>
        public static AriaRole ResolveRole(Element el, Document doc)
        {
            if (el == null) return AriaRole.None;

            var roleAttr = el.GetAttribute("role");
            if (!string.IsNullOrWhiteSpace(roleAttr))
            {
                var explicitRole = AriaSpec.ParseRole(roleAttr);
                if (explicitRole != AriaRole.None)
                    return explicitRole;
            }

            return GetImplicitRole(el, doc);
        }

        // ---- Private helpers ----

        private static AriaRole GetInputRole(Element el)
        {
            var type = (el.GetAttribute("type") ?? "text").ToLowerInvariant().Trim();
            switch (type)
            {
                case "button":
                case "image":
                case "reset":
                case "submit":
                    return AriaRole.Button;

                case "checkbox":
                    return AriaRole.Checkbox;

                case "color":
                    return AriaRole.None;

                case "date":
                case "datetime-local":
                case "month":
                case "time":
                case "week":
                    return AriaRole.None;

                case "email":
                case "tel":
                case "url":
                case "text":
                case "password":
                    return AriaRole.Textbox;

                case "file":
                    return AriaRole.None;

                case "hidden":
                    return AriaRole.None;

                case "number":
                    return AriaRole.Spinbutton;

                case "radio":
                    return AriaRole.Radio;

                case "range":
                    return AriaRole.Slider;

                case "search":
                    return AriaRole.Searchbox;

                default:
                    return AriaRole.Textbox;
            }
        }

        /// <summary>
        /// Returns true when the element has an accessible name (used for form/section roles).
        /// Checks only aria-labelledby / aria-label / title attributes (not full AccName) to avoid
        /// circular dependency at role-resolution time.
        /// </summary>
        private static bool HasAccessibleName(Element el, Document doc)
        {
            var labelledby = el.GetAttribute("aria-labelledby");
            if (!string.IsNullOrWhiteSpace(labelledby))
            {
                // Check at least one referenced element exists and is non-empty
                foreach (var id in labelledby.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var refEl = doc?.GetElementById(id);
                    if (refEl != null && !string.IsNullOrEmpty(refEl.TextContent?.Trim()))
                        return true;
                }
            }

            var ariaLabel = el.GetAttribute("aria-label");
            if (!string.IsNullOrWhiteSpace(ariaLabel))
                return true;

            var title = el.GetAttribute("title");
            if (!string.IsNullOrWhiteSpace(title))
                return true;

            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="el"/> is a sectioning content or sectioning root ancestor
        /// that causes footer/header to lose their landmark semantics.
        /// </summary>
        private static bool IsLandmarkContext(Element el)
        {
            for (var parent = el.ParentElement; parent != null; parent = parent.ParentElement)
            {
                var ln = parent.LocalName;
                if (ln == "article" || ln == "aside" || ln == "main" ||
                    ln == "nav" || ln == "section")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the element is a direct child of &lt;ul&gt;, &lt;ol&gt;, or &lt;menu&gt;.
        /// </summary>
        private static bool IsInList(Element el)
        {
            var parent = el.ParentElement;
            if (parent == null) return false;
            var ln = parent.LocalName;
            return ln == "ul" || ln == "ol" || ln == "menu";
        }

        /// <summary>
        /// Returns true when the nearest table ancestor has role grid or treegrid (from explicit role).
        /// </summary>
        private static bool IsInGridOrTreegrid(Element el)
        {
            for (var parent = el.ParentElement; parent != null; parent = parent.ParentElement)
            {
                var ln = parent.LocalName;
                if (ln == "table")
                {
                    var roleAttr = parent.GetAttribute("role");
                    if (!string.IsNullOrEmpty(roleAttr))
                    {
                        var r = roleAttr.ToLowerInvariant().Trim();
                        if (r == "grid" || r == "treegrid") return true;
                    }
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if the element is nested inside a landmark content element
        /// (used for aside implicit role — always complementary per current spec).
        /// </summary>
        private static bool IsDescendantOfLandmark(Element el)
        {
            // aside is always complementary per HTML-AAM 2021
            return true;
        }
    }
}
