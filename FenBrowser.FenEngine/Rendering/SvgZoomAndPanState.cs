using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// The zoom-and-pan transform of each outermost svg element (SVG 2 §5.1.1): the
    /// state behind SVGSVGElement.currentScale and currentTranslate. It lives beside
    /// the DOM rather than in it, so it is never serialized or visible as markup.
    /// Script sets it; painting applies it to a standalone SVG document's root.
    /// </summary>
    public static class SvgZoomAndPanState
    {
        private sealed class Box
        {
            public SvgZoomAndPan Value = new(1f, 0f, 0f);
        }

        private static readonly ConditionalWeakTable<Element, Box> States = new();

        /// <summary>The element's transform; the identity when never set.</summary>
        public static SvgZoomAndPan Get(Element element) =>
            element != null && States.TryGetValue(element, out var box)
                ? box.Value
                : new SvgZoomAndPan(1f, 0f, 0f);

        /// <summary>Stores a transform. False, with nothing changed, for an invalid one.</summary>
        public static bool TrySet(Element element, SvgZoomAndPan value)
        {
            if (element == null || !value.IsValid)
            {
                return false;
            }

            States.GetOrCreateValue(element).Value = value;
            return true;
        }

        /// <summary>
        /// The transform painting applies to <paramref name="svg"/>: only a document's
        /// root svg element is zoomed and panned (the standalone SVG case). Null when
        /// there is nothing to apply.
        /// </summary>
        public static SvgZoomAndPan? ForPainting(Element svg)
        {
            if (svg == null || !ReferenceEquals(svg.OwnerDocument?.DocumentElement, svg))
            {
                return null;
            }

            var value = Get(svg);
            return value.IsIdentity ? null : value;
        }
    }
}
