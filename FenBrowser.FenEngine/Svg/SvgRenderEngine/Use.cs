using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        // ------------------------------------------------------------------ use

        private void DrawUse(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            CheckDeadline();
            string href = el.GetAttribute("href") ?? el.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href))
            {
                return; // No reference: the element is not rendered (browser behavior).
            }
            if (!SvgValues.TryParseLocalReference(href, out string rawId))
            {
                if (!IsOpaqueOriginUseReference(href))
                {
                    // A reference into another document is one a browser may paint,
                    // so the frame cannot be settled without fetching it. Log and
                    // fail closed rather than report Success for a document with a
                    // hole in it.
                    _report.RejectResource("use external reference rejected by SVG resource policy");
                    return;
                }
                WarnOpaqueOriginUseReferenceOnce();
                return;
            }

            string id = DecodeFragmentEscapes(rawId);
            if (!_doc.ElementsById.TryGetValue(id, out var target))
            {
                return; // Dangling reference: silently nothing (browser behavior).
            }

            if (IsTextContentElement(target))
            {
                // A use instance rooted at text content is not a graphics element,
                // so a browser instantiates nothing at all. Painting the tspan here
                // would be the fail-open this engine exists to prevent.
                return;
            }

            _activeUseIds ??= new HashSet<string>(System.StringComparer.Ordinal);
            _activeUseElements ??= new HashSet<SvgElement>();
            if (_activeUseIds.Contains(id) || _activeUseElements.Contains(target))
            {
                // A cycle is not a budget: a browser stops instantiating it too, so
                // the frame it paints is missing this instance. Reporting success
                // would certify a frame with a hole in it.
                _report.RequireFallback(
                    "SVG use reference cycle; the instance requires compatibility rendering");
                return;
            }
            if (_activeUseIds.Count >= _maxReferenceDepth || _depth >= MaxRenderDepth)
            {
                _report.RequireFallback(
                    "SVG use reference depth budget exceeded; the instance requires " +
                    "compatibility rendering");
                return;
            }
            _activeUseIds.Add(id);
            _activeUseElements.Add(target);

            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, viewport, inherited);
            ApplyClipPath(el, canvas, viewport, inherited);
            float ux = ResolveGeometryCoordinate(el, "x", viewport, viewport.Width);
            float uy = ResolveGeometryCoordinate(el, "y", viewport, viewport.Height);
            if (ux != 0f || uy != 0f)
            {
                canvas.Translate(ux, uy);
            }

            try
            {
                var next = inherited.ResolveOverrides(el, _report);
                next = next.Clone();
                next.ContextFill = next.Fill;
                next.ContextStroke = next.Stroke;

                using var shadowScope = SvgCssCascade.UseShadowScope.Enter(target);

                SvgElement outerInstance = _useInstanceSpace;
                _useInstanceSpace = el;
                try
                {
                DrawWithEffects(el, canvas, viewport, () =>
                {
                    // S8/F8: opacity on <use> composites the instantiated subtree.
                    bool layered = TryBeginGroupOpacity(el, canvas, out var useLayer);
                    try
                    {
                        if (target.Name == "svg")
                        {
                            DrawNestedSvg(target, canvas, viewport, next, el);
                        }
                        else if (target.Name == "symbol")
                        {
                            // A used symbol establishes an svg-equivalent viewport whose
                            // default width/height is 100% of the referencing viewport.
                            DrawSymbolInstance(target, canvas, viewport, next, el);
                        }
                        else
                        {
                            DrawElement(target, canvas, viewport, next);
                        }
                    }
                    finally
                    {
                        if (layered)
                        {
                            canvas.Restore();
                            _activeLayers--;
                            useLayer.Dispose();
                        }
                    }
                }, next);
                }
                finally
                {
                    _useInstanceSpace = outerInstance;
                }
            }
            finally
            {
                _activeUseIds.Remove(id);
                _activeUseElements.Remove(target);
            }
        }

        private HashSet<string> _activeUseIds;
        private HashSet<SvgElement> _activeUseElements;
        private bool _warnedOpaqueOriginUseReference;

        /// <summary>
        /// A data: URL is not a same-origin external document, and the use href is
        /// same-origin only. Safari has never resolved one, Firefox stopped in 122
        /// and Chrome in 120, both behind a pref or a policy because the reference
        /// was an XSS and Trusted Types bypass. The reference is therefore a
        /// reference that resolves to no element rather than a resource this
        /// document declined to fetch, so the instance contributes nothing, the
        /// same as a dangling fragment. The payload is never decoded, so no
        /// admission cap is reached because none is ever spent.
        /// </summary>
        private static bool IsOpaqueOriginUseReference(string href) =>
            href.TrimStart().StartsWith("data:", System.StringComparison.OrdinalIgnoreCase);

        private void WarnOpaqueOriginUseReferenceOnce()
        {
            if (_warnedOpaqueOriginUseReference) return;
            _warnedOpaqueOriginUseReference = true;
            _report.Warn(
                "use reference is a data: URL, which is not a same-origin external document; " +
                "instance omitted");
        }

        internal static string DecodeFragmentEscapes(string fragment)
        {
            int firstPercent = fragment.IndexOf('%');
            if (firstPercent < 0)
            {
                return fragment;
            }
            if (fragment.Length - firstPercent > SvgMarkupParser.MaxIdChars)
            {
                return fragment;
            }
            var buffer = new char[fragment.Length];
            int written = 0;
            for (int read = 0; read < fragment.Length; read++)
            {
                char c = fragment[read];
                if (c == '%' && TryDecodeEscape(fragment, read, out char decoded))
                {
                    buffer[written++] = decoded;
                    read += 2;
                    continue;
                }
                buffer[written++] = c;
            }
            return written == fragment.Length ? fragment : new string(buffer, 0, written);
        }

        private static bool TryDecodeEscape(string value, int start, out char decoded)
        {
            decoded = '\0';
            if (start + 2 >= value.Length) return false;
            int high = HexValue(value[start + 1]);
            int low = HexValue(value[start + 2]);
            if (high < 0 || low < 0) return false;
            decoded = (char)((high << 4) | low);
            return true;
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };

        private void DrawSymbolInstance(
            SvgElement symbol,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            SvgElement instance)
        {
            if (IsDisplayNone(symbol)) return;
            bool resolvedWidth = TryResolveViewportBoxExtent(
                instance?.GetAttribute("width"), false, 0f,
                symbol.GetAttribute("width"), viewport.Width, viewport.Width,
                "symbol", "width", out float w);
            bool resolvedHeight = TryResolveViewportBoxExtent(
                instance?.GetAttribute("height"), false, 0f,
                symbol.GetAttribute("height"), viewport.Height, viewport.Height,
                "symbol", "height", out float h);
            if (!resolvedWidth || !resolvedHeight) return;
            if (w <= 0f || h <= 0f) return;

            var inner = new ViewportContext(w, h);
            bool hasViewBox = TryParseViewBox(
                symbol.GetAttribute("viewBox"),
                out float vbX, out float vbY, out float vbW, out float vbH,
                out bool viewBoxDisablesRendering);

            if (viewBoxDisablesRendering)
            {
                return;
            }

            using var scope = new CanvasState(canvas);
            if (!ApplyNestedSvgViewport(
                    symbol, canvas, inner, hasViewBox, vbX, vbY, vbW, vbH))
                return;
            var userViewport = hasViewBox
                ? new ViewportContext(vbW, vbH)
                : inner;
            DrawNestedSvgBody(symbol, canvas, userViewport, inherited.ResolveOverrides(symbol, _report));
        }
    }
}
