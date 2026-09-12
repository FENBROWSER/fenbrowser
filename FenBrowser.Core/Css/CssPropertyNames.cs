using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// The CSS property names the engine exposes through CSSOM (CSSOM §6.7.3:
    /// every supported property gets a camel-cased IDL attribute on
    /// CSSStyleDeclaration, plus <c>cssFloat</c> for <c>float</c>). Shorthands are
    /// included because script reads and writes them (<c>style.margin</c>,
    /// <c>style.border</c>); values are passed through untouched.
    /// </summary>
    public static class CssPropertyNames
    {
        public static readonly IReadOnlyList<string> All = new[]
        {
            // Box model
            "display", "position", "top", "right", "bottom", "left", "inset", "inset-block", "inset-block-start", "inset-block-end",
            "inset-inline", "inset-inline-start", "inset-inline-end", "float", "clear", "z-index",
            "width", "height", "min-width", "min-height", "max-width", "max-height",
            "inline-size", "block-size", "min-inline-size", "min-block-size", "max-inline-size", "max-block-size",
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "margin-block", "margin-block-start", "margin-block-end", "margin-inline", "margin-inline-start", "margin-inline-end",
            "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
            "padding-block", "padding-block-start", "padding-block-end", "padding-inline", "padding-inline-start", "padding-inline-end",
            "box-sizing", "aspect-ratio", "overflow", "overflow-x", "overflow-y", "overflow-wrap", "overflow-anchor", "overflow-clip-margin",
            "visibility", "opacity", "clip", "clip-path", "resize", "contain", "content-visibility", "isolation", "mix-blend-mode",
            "object-fit", "object-position", "vertical-align",

            // Borders and outlines
            "border", "border-width", "border-style", "border-color",
            "border-top", "border-top-width", "border-top-style", "border-top-color",
            "border-right", "border-right-width", "border-right-style", "border-right-color",
            "border-bottom", "border-bottom-width", "border-bottom-style", "border-bottom-color",
            "border-left", "border-left-width", "border-left-style", "border-left-color",
            "border-block", "border-block-start", "border-block-end", "border-inline", "border-inline-start", "border-inline-end",
            "border-block-width", "border-block-style", "border-block-color", "border-inline-width", "border-inline-style", "border-inline-color",
            "border-radius", "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius",
            "border-start-start-radius", "border-start-end-radius", "border-end-start-radius", "border-end-end-radius",
            "border-image", "border-image-source", "border-image-slice", "border-image-width", "border-image-outset", "border-image-repeat",
            "border-collapse", "border-spacing", "outline", "outline-width", "outline-style", "outline-color", "outline-offset",
            "box-shadow",

            // Backgrounds
            "background", "background-color", "background-image", "background-repeat", "background-position", "background-position-x",
            "background-position-y", "background-size", "background-attachment", "background-origin", "background-clip", "background-blend-mode",

            // Text and fonts
            "color", "font", "font-family", "font-size", "font-weight", "font-style", "font-variant", "font-variant-caps",
            "font-variant-numeric", "font-variant-ligatures", "font-stretch", "font-size-adjust", "font-kerning", "font-feature-settings",
            "font-variation-settings", "font-optical-sizing", "font-synthesis", "font-display", "line-height", "letter-spacing", "word-spacing",
            "text-align", "text-align-last", "text-decoration", "text-decoration-line", "text-decoration-style", "text-decoration-color",
            "text-decoration-thickness", "text-decoration-skip-ink", "text-underline-offset", "text-underline-position", "text-transform",
            "text-indent", "text-shadow", "text-overflow", "text-rendering", "text-wrap", "text-justify", "text-orientation",
            "text-emphasis", "text-emphasis-style", "text-emphasis-color", "text-emphasis-position", "text-size-adjust",
            "white-space", "white-space-collapse", "word-break", "word-wrap", "line-break", "hyphens", "tab-size", "direction",
            "unicode-bidi", "writing-mode", "quotes", "hanging-punctuation", "text-combine-upright",

            // Lists, tables, generated content
            "list-style", "list-style-type", "list-style-position", "list-style-image", "counter-reset", "counter-increment", "counter-set",
            "content", "table-layout", "caption-side", "empty-cells",

            // Flexbox and grid
            "flex", "flex-direction", "flex-wrap", "flex-flow", "flex-grow", "flex-shrink", "flex-basis", "order",
            "justify-content", "justify-items", "justify-self", "align-items", "align-self", "align-content", "place-items", "place-content", "place-self",
            "gap", "row-gap", "column-gap", "grid", "grid-area", "grid-template", "grid-template-areas", "grid-template-columns", "grid-template-rows",
            "grid-auto-columns", "grid-auto-rows", "grid-auto-flow", "grid-column", "grid-column-start", "grid-column-end",
            "grid-row", "grid-row-start", "grid-row-end", "grid-gap", "grid-row-gap", "grid-column-gap",

            // Multi-column
            "columns", "column-count", "column-width", "column-gap", "column-rule", "column-rule-width", "column-rule-style", "column-rule-color",
            "column-span", "column-fill", "break-before", "break-after", "break-inside", "page-break-before", "page-break-after", "page-break-inside",
            "orphans", "widows",

            // Transforms, transitions, animations
            "transform", "transform-origin", "transform-style", "transform-box", "translate", "rotate", "scale", "perspective", "perspective-origin",
            "backface-visibility", "transition", "transition-property", "transition-duration", "transition-timing-function", "transition-delay",
            "transition-behavior", "animation", "animation-name", "animation-duration", "animation-timing-function", "animation-delay",
            "animation-iteration-count", "animation-direction", "animation-fill-mode", "animation-play-state", "animation-composition",
            "animation-timeline", "will-change", "offset", "offset-path", "offset-distance", "offset-rotate", "offset-anchor",

            // Interaction and misc
            "cursor", "pointer-events", "user-select", "touch-action", "caret-color", "accent-color", "appearance", "scroll-behavior",
            "scroll-snap-type", "scroll-snap-align", "scroll-snap-stop", "scroll-margin", "scroll-margin-top", "scroll-margin-right",
            "scroll-margin-bottom", "scroll-margin-left", "scroll-padding", "scroll-padding-top", "scroll-padding-right", "scroll-padding-bottom",
            "scroll-padding-left", "scrollbar-width", "scrollbar-color", "scrollbar-gutter", "overscroll-behavior", "overscroll-behavior-x",
            "overscroll-behavior-y", "filter", "backdrop-filter", "mask", "mask-image", "mask-size", "mask-position", "mask-repeat", "mask-mode",
            "mask-clip", "mask-origin", "mask-composite", "mask-type", "shape-outside", "shape-margin", "shape-image-threshold",
            "image-rendering", "image-orientation", "color-scheme", "forced-color-adjust", "print-color-adjust", "zoom", "all",
            "container", "container-type", "container-name", "view-transition-name", "paint-order",

            // SVG presentation properties
            "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin",
            "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "stop-color", "stop-opacity", "flood-color", "flood-opacity",
            "lighting-color", "marker", "marker-start", "marker-mid", "marker-end", "clip-rule", "color-interpolation",
            "color-interpolation-filters", "dominant-baseline", "alignment-baseline", "baseline-shift", "text-anchor", "vector-effect",
            "shape-rendering", "cx", "cy", "r", "rx", "ry", "x", "y", "d",
        };

        /// <summary>CSSOM §6.7.3: the camel-cased attribute for a dashed property name.</summary>
        public static string ToCamelCase(string property)
        {
            if (string.IsNullOrEmpty(property) || property.IndexOf('-') < 0)
            {
                return property ?? string.Empty;
            }

            var chars = new char[property.Length];
            var length = 0;
            var upper = false;
            foreach (var c in property)
            {
                if (c == '-')
                {
                    upper = true;
                    continue;
                }

                chars[length++] = upper ? char.ToUpperInvariant(c) : c;
                upper = false;
            }

            return new string(chars, 0, length);
        }
    }
}
