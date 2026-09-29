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
        private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

        // ------------------------------------------------------------ draw walk

        private void DrawChildren(SvgElement container, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
            if (!PassesConditionalProcessing(container)) return;
            if (container.Parent == null)
            {
                // The outermost <svg> transform establishes the user space its
                // children draw in, after the viewBox transform RenderRoot applied.
                RejectsScriptDrivenDocument(container);
                ApplyTransformProperty(container, canvas, viewport, inherited);
            }
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                CheckTime();
                DrawElement(children[i], canvas, viewport, inherited);
            }
        }

        private void DrawElement(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            _elementsVisited++;
            CheckTime();

            // S2/F2: independent recursion guard. Parser depth caps the TREE,
            // but use/symbol expansion multiplies visits; without this, deep
            // distinct-id chains could overflow the managed stack.
            if (++_depth > MaxRenderDepth)
            {
                _depth--;
                _report.RequireFallback(
                    "SVG render depth budget exceeded; the subtree below this point " +
                    "requires compatibility rendering");
                return;
            }

            try
            {
                DrawElementInner(el, canvas, viewport, inherited);
            }
            finally
            {
                _depth--;
            }
        }

        private void ReportUnsupportedMaskHref(SvgElement element)
        {
            string raw = element.GetPresentationProperty("mask");
            if (!SvgValues.TryParsePaint(
                    raw.AsSpan(), out var kind, out _, out string fragment, out _) ||
                kind != SvgValues.PaintKind.ServerRef || fragment == null ||
                !_doc.ElementsById.TryGetValue(fragment, out var mask) ||
                mask.Name != "mask")
                return;

            var visited = new HashSet<SvgElement>();
            SvgElement current = mask;
            bool hasHref = false;
            while (current != null)
            {
                CheckDeadline();
                if (current.Name != "mask" || !visited.Add(current) ||
                    visited.Count > _maxReferenceDepth)
                {
                    _report.RequireFallback("SVG mask href cycle or depth budget exceeded");
                    return;
                }
                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (href == null) break;
                hasHref = true;
                if (!SvgValues.TryParseLocalReference(href, out string id))
                {
                    _report.RejectResource("SVG mask external href rejected by SVG resource policy");
                    return;
                }
                if (!_doc.ElementsById.TryGetValue(id, out current))
                {
                    _report.Warn("SVG mask href reference unresolved");
                    _report.RequireFallback("SVG mask href reference requires compatibility fallback");
                    return;
                }
            }
            if (hasHref)
                _report.RequireFallback("SVG mask href inheritance requires compatibility fallback");
        }

        private void DrawElementInner(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            if (IsXhtmlForeignElement(el))
            {
                if (el.UnclosedForeignElement)
                    WarnUnknownOnce(el.Name);
                return;
            }
            if (!PassesConditionalProcessing(el)) return;
            ReportUnsupportedMaskHref(el);

            switch (el.Name)
            {
                case "g":
                case "a":
                case "view":
                    {
                        if (IsDisplayNone(el)) return;
            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, viewport, inherited);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        var next = inherited.ResolveOverrides(el, _report);
                        DrawWithEffects(el, canvas, viewport, () =>
                        {
                            if (TryBeginGroupOpacity(el, canvas, out var layerPaint))
                            {
                                try
                                {
                                    DrawChildren(el, canvas, viewport, next);
                                }
                                finally
                                {
                                    // Restore composites the layer using the paint,
                                    // so the paint must outlive this call.
                                    canvas.Restore();
                                    _activeLayers--;
                                    layerPaint.Dispose();
                                }
                            }
                            else
                            {
                                DrawChildren(el, canvas, viewport, next);
                            }
                        }, next);
                        return;
                    }
                case "svg":
                    {
                        if (IsDisplayNone(el)) return;
                        DrawNestedSvg(el, canvas, viewport, inherited);
                        return;
                    }
                case "foreignObject":
                    {
                        if (IsDisplayNone(el)) return;
                        DrawForeignObject(el, canvas, viewport, inherited);
                        return;
                    }
                case "defs":
                case "style":
                case "title":
                case "desc":
                case "metadata":
                case "link":
                case "meta":
                case "h:link":
                case "h:meta":
                case "html:link":
                case "html:meta":
                case "script":
                case "h:script":
                case "html:script":
                case "symbol":
                case "marker":
                case "pattern":
                case "linearGradient":
                case "radialGradient":
                case "stop":
                case "filter":
                case "mask":
                case "clipPath":
                case "cursor":
                case "color-profile":
                case "font":
                case "font-face":
                case "font-face-src":
                case "font-face-uri":
                case "font-face-name":
                case "font-face-format":
                case "glyph":
                case "missing-glyph":
                case "hkern":
                case "vkern":
                case "altGlyphDef":
                case "altGlyphItem":
                case "glyphRef":
                case "altGlyphRef":
                case "mesh":
                case "meshgradient":
                case "meshrow":
                case "meshpatch":
                    return; // Referenced-only content: never drawn inline.
                case "switch":
                    {
                        if (IsDisplayNone(el)) return;
                        DrawSwitch(el, canvas, viewport, inherited);
                        return;
                    }
                case "use":
                    {
                        if (IsDisplayNone(el)) return;
                        DrawUse(el, canvas, viewport, inherited);
                        return;
                    }
                case "path":
                case "rect":
                case "circle":
                case "ellipse":
                case "line":
                case "polyline":
                case "polygon":
                    {
                        if (IsDisplayNone(el)) return;
                        using var shapeScope = new CanvasState(canvas);
                        // Spec order: save -> element transform -> clip -> draw
                        // -> restore (unwinds via CanvasState on any exit).
                        ApplyElementTransform(el, canvas, viewport, inherited);
                        ApplyInitialMotionTransform(el, canvas, viewport);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        DrawShape(el, canvas, viewport, inherited);
                        return;
                    }
                case "image":
                    {
                        if (IsDisplayNone(el)) return;
                        // Same spec order as shapes: save -> transform -> clip
                        // -> draw (DrawImageElement adds its own opacity layer).
                        using var imgScope = new CanvasState(canvas);
                        ApplyElementTransform(el, canvas, viewport, inherited);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        var next = inherited.ResolveOverrides(el, _report);
                        DrawWithEffects(el, canvas, viewport,
                            () => DrawImageElement(el, canvas, viewport, next), next);
                        return;
                    }
                case "text":
                    {
                        if (IsDisplayNone(el)) return;
                        using var textScope = new CanvasState(canvas);
                        ApplyElementTransform(el, canvas, viewport, inherited);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        DrawWithEffects(el, canvas, viewport,
                            () => DrawTextElement(el, canvas, viewport, inherited));
                        return;
                    }
                case "tspan":
                case "textPath":
                case "animation":
                    WarnUnsupportedOnce(el.Name);
                    return;
                case "animate":
                case "animateColor":
                case "animateMotion":
                case "animateTransform":
                case "set":
                    return;
                default:
                    if (IsSvgTestSuiteMetadata(el))
                    {
                        return;
                    }
                    WarnUnknownOnce(el.Name);
                    return; // Unknown elements are never rendered; subtree skipped.
            }
        }

        private static bool IsSvgTestSuiteMetadata(SvgElement element)
        {
            // The imported W3C SVG 1.1 tests carry a sibling annotation tree in
            // this foreign namespace. It is not renderable SVG content, so
            // skipping it must not force the whole image through the legacy
            // backend. Match both the qualified name and its declaration: an
            // arbitrary prefixed element must remain an explicit fallback.
            return element.Name == "d:SVGTestCase" &&
                string.Equals(
                    element.GetAttribute("xmlns:d"),
                    "http://www.w3.org/2000/02/svg/testsuite/description/",
                    StringComparison.Ordinal);
        }

        private static bool IsXhtmlForeignElement(SvgElement el)
        {
            string ns = el.NamespaceUri;
            return ns != null &&
                string.Equals(ns, XhtmlNamespace, StringComparison.Ordinal);
        }

        private static bool IsTextContentElement(SvgElement el)
        {
            return el.Name is "tspan" or "textPath";
        }

        private void DrawNestedSvg(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext outer,
            InheritedStyle inherited,
            SvgElement instance = null)
        {
            // CSS sizing participates on nested <svg> only for forms whose
            // resolution requires the containing block or nearest viewport:
            // fill/stretch sizing keywords, calc-size(), and viewport-unit
            // lengths. Plain length/percentage/calc declarations never override
            // XML geometry here; omitted dimensions default to 100%.
            bool hasCssWidth = TryResolveNestedSvgCssSizing(
                el, "width", outer.Width, outer.Height, out float cssWidth);
            bool hasCssHeight = TryResolveNestedSvgCssSizing(
                el, "height", outer.Width, outer.Height, out float cssHeight);
            bool resolvedWidth = TryResolveViewportBoxExtent(
                instance?.GetAttribute("width"), hasCssWidth, cssWidth,
                el.GetAttribute("width"), outer.Width, outer.Width,
                "nested <svg>", "width", out float w);
            bool resolvedHeight = TryResolveViewportBoxExtent(
                instance?.GetAttribute("height"), hasCssHeight, cssHeight,
                el.GetAttribute("height"), outer.Height, outer.Height,
                "nested <svg>", "height", out float h);
            if (!resolvedWidth || !resolvedHeight) return;
            if (w <= 0f || h <= 0f)
            {
                return;
            }
            w = System.Math.Min(w, 32767f);
            h = System.Math.Min(h, 32767f);

            var inner = new ViewportContext(w, h);
            bool hasViewBox = TryParseViewBox(
                el.GetAttribute("viewBox"),
                out float vbX, out float vbY, out float vbW, out float vbH,
                out bool viewBoxDisablesRendering);

            if (viewBoxDisablesRendering)
            {
                return;
            }

            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, outer, inherited);
            float x = ResolveGeometryCoordinate(el, "x", outer, outer.Width);
            float y = ResolveGeometryCoordinate(el, "y", outer, outer.Height);
            if (x != 0f || y != 0f)
                canvas.Translate(x, y);
            if (!ApplyNestedSvgViewport(
                    el, canvas, inner, hasViewBox, vbX, vbY, vbW, vbH))
                return;
            var userViewport = hasViewBox
                ? new ViewportContext(vbW, vbH)
                : inner;
            ApplyClipPath(el, canvas, userViewport, inherited);
            var next = inherited.ResolveOverrides(el, _report);
            DrawWithEffects(el, canvas, inner, () =>
            {
                if (TryBeginGroupOpacity(el, canvas, out var layerPaint))
                {
                    try
                    {
                        DrawNestedSvgBody(el, canvas, userViewport, next);
                    }
                    finally
                    {
                        canvas.Restore();
                        _activeLayers--;
                        layerPaint.Dispose();
                    }
                }
                else
                {
                    DrawNestedSvgBody(el, canvas, userViewport, next);
                }
            }, next);
        }

        private bool TryResolveViewportBoxExtent(
            string instanceAttribute,
            bool hasCssSizing,
            float cssSizing,
            string attribute,
            float percentReference,
            float defaultExtent,
            string subject,
            string property,
            out float value)
        {
            if (IsSpecifiedViewportLength(instanceAttribute))
                return TryResolveViewportExtent(
                    instanceAttribute, percentReference, subject, property, out value);
            if (hasCssSizing)
            {
                value = cssSizing;
                return true;
            }
            if (!IsSpecifiedViewportLength(attribute))
            {
                value = defaultExtent;
                return true;
            }
            return TryResolveViewportExtent(
                attribute, percentReference, subject, property, out value);
        }

        private static bool IsSpecifiedViewportLength(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Equals("auto", StringComparison.OrdinalIgnoreCase);

        private bool TryResolveViewportExtent(
            string raw,
            float parentDim,
            string subject,
            string property,
            out float value)
        {
            if (TryResolveViewportLength(raw, parentDim, out value)) return true;
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Length > SvgMarkupParser.MaxAttributeValueChars ||
                SvgCssLengthEvaluator.HasViewportUnitDimension(raw))
                return false;
            RejectsIntrinsicReplacedSizing(subject, property, raw);
            return false;
        }

        private void RejectsIntrinsicReplacedSizing(
            string subject,
            string property,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > SvgMarkupParser.MaxAttributeValueChars)
                return;
            _report.RequireFallback(
                $"SVG {subject} sizing property '{property}: {value.Trim()}' requires intrinsic " +
                "replaced-element sizing that this renderer does not compute");
        }

        private void RejectsUnresolvedGeometrySizing(
            string subject,
            string property,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > SvgMarkupParser.MaxAttributeValueChars ||
                SvgCssLengthEvaluator.HasViewportUnitDimension(value) ||
                SvgCssLengthEvaluator.RequiresUnsupportedUnitSupport(value))
                return;
            RejectsIntrinsicReplacedSizing(subject, property, value);
        }

        private float ResolveGeometryCoordinate(
            SvgElement element,
            string property,
            ViewportContext viewport,
            float percentReference)
        {
            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            return TryResolveGeometryLength(
                element, property, viewport, percentReference, fontSize, rootFontSize,
                out float value)
                ? value
                : 0f;
        }

        /// <summary>
        /// Resolves a cascaded CSS width/height declaration on a nested &lt;svg&gt;
        /// when it is one of the supported viewport-dependent forms. Returns false
        /// for every other value, leaving XML attribute/default sizing in charge.
        /// Negative results are invalid per spec and likewise return false.
        /// </summary>
        private bool TryResolveNestedSvgCssSizing(
            SvgElement element,
            string property,
            float viewportWidth,
            float viewportHeight,
            out float value)
        {
            value = 0f;
            string resolved = element.GetCascadedPresentationProperty(property);
            if (string.IsNullOrWhiteSpace(resolved) ||
                resolved.Length > SvgMarkupParser.MaxAttributeValueChars)
                return false;
            string trimmed = resolved.Trim();

            // The fill-available sizing keywords and calc-size() do not apply to a
            // nested <svg>: an SVG viewport has no intrinsic size, so there is
            // nothing for them to size against and the geometry attributes stay
            // in charge (svgwg#1059). Resolving them to the containing viewport
            // extent instead silently overrode the authored width/height.
            if (SvgCssLengthEvaluator.IsNestedSvgSizingKeyword(trimmed) ||
                trimmed.StartsWith("calc-size(", System.StringComparison.OrdinalIgnoreCase))
                return false;

            if (!SvgCssLengthEvaluator.HasViewportUnitDimension(trimmed))
                return false;
            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            if (SvgCssLengthEvaluator.TryEvaluate(
                       trimmed, property == "width" ? viewportWidth : viewportHeight,
                       fontSize, rootFontSize,
                       viewportWidth, viewportHeight, out value))
                return value >= 0f;
            RejectsIntrinsicReplacedSizing("nested <svg>", property, trimmed);
            return false;
        }

        private bool ApplyNestedSvgViewport(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext inner,
            bool hasViewBox,
            float vbX, float vbY, float vbW, float vbH)
        {
            bool overflowVisible = string.Equals(
                el.GetPresentationProperty("overflow")?.Trim(),
                "visible",
                System.StringComparison.OrdinalIgnoreCase);
            if (!overflowVisible)
                canvas.ClipRect(new SKRect(0f, 0f, inner.Width, inner.Height));
            return !hasViewBox ||
                ApplyViewportTransform(
                    canvas, inner, true, vbX, vbY, vbW, vbH,
                    el.GetAttribute("preserveAspectRatio"));
        }

        private void DrawNestedSvgBody(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext userViewport,
            InheritedStyle style)
        {
            DrawChildren(el, canvas, userViewport, style);
        }

        // --------------------------------------------------------------- shapes

        // ---------------------------------------------------------------- misc

        /// <summary>
        /// Applies an element's transform. A cascaded CSS transform property
        /// (rules or inline style) replaces the XML transform attribute per the
        /// cascade and resolves through the bounded CSS path below; attribute
        /// values keep the legacy SVG syntax parser.
        /// </summary>
        private void ApplyElementTransform(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            ApplyCssZoom(el, canvas);
            ApplyTransformProperty(el, canvas, viewport, inherited);
        }

        private void ApplyTransformProperty(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            bool animated = TryGetAnimatedTransformBase(
                el, out SKMatrix sampled, out bool replacesBase);
            if (!animated || !replacesBase)
            {
                string cssTransform = el.GetCascadedPresentationProperty("transform");
                if (!string.IsNullOrWhiteSpace(cssTransform))
                {
                    ApplyCssTransform(el, cssTransform, canvas, viewport, inherited);
                }
                else
                {
                    ApplyAttributeTransform(el, canvas, viewport, inherited);
                }
            }
            if (animated && !sampled.IsIdentity)
                canvas.Concat(sampled);
        }

        private void ApplyCssZoom(SvgElement element, SKCanvas canvas)
        {
            if (!TryResolveCssZoomMatrix(element, out var matrix)) return;
            if (!matrix.IsIdentity)
                canvas.Concat(matrix);
        }

        private bool TryResolveCssZoomMatrix(SvgElement element, out SKMatrix matrix)
        {
            matrix = SKMatrix.Identity;
            string raw = element.GetPresentationProperty("zoom");
            if (string.IsNullOrWhiteSpace(raw)) return true;
            string value = raw.Trim();
            if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) return true;

            float zoom;
            if (value.EndsWith("%", StringComparison.Ordinal))
            {
                if (!SvgValues.TryParseNumber(value.AsSpan(0, value.Length - 1), out zoom)) return true;
                zoom *= 0.01f;
            }
            else if (!SvgValues.TryParseNumber(value.AsSpan(), out zoom))
            {
                if (value.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("min(", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("max(", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase))
                {
                    _report.RequireFallback("SVG CSS zoom math requires compatibility fallback");
                    return false;
                }
                return true;
            }

            if (!float.IsFinite(zoom) || zoom < 0f) return true;
            if (zoom > 4096f)
            {
                _report.RequireFallback("SVG CSS zoom exceeds the bounded scale limit");
                return false;
            }
            if (zoom != 1f)
                matrix = SKMatrix.CreateScale(zoom, zoom);
            return SvgValues.IsFinite(matrix);
        }

        /// <summary>
        /// Applies an XML <c>transform</c> attribute, honoring the SVG2
        /// <c>transform-origin</c> presentation attribute (bare numbers are
        /// user units) when present.
        /// </summary>
        private void ApplyAttributeTransform(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            var t = el.GetAttribute("transform");
            if (string.IsNullOrWhiteSpace(t))
            {
                return;
            }
            if (!SvgValues.TryParseTransformList(t.AsSpan(), out var matrix))
            {
                _report.RequireFallback("SVG transform attribute requires compatibility fallback");
                return;
            }

            string originText = el.GetPresentationProperty("transform-origin");
            string boxText = el.GetPresentationProperty("transform-box");
            if (!string.IsNullOrWhiteSpace(originText) || !string.IsNullOrWhiteSpace(boxText))
            {
                if (!TryBuildTransformFillBox(el, viewport, boxText, out SKRect? fillBox))
                {
                    return;
                }
                bool originUsesAttributeSyntax =
                    el.CascadedDeclarations == null ||
                    !el.CascadedDeclarations.ContainsKey("transform-origin");
                var originStatus = SvgCssTransform.TryResolveReferenceOrigin(
                    originText,
                    boxText,
                    viewport.Width,
                    viewport.Height,
                    inherited.FontSize,
                    inherited.RootFontSize,
                    fillBox,
                    originUsesAttributeSyntax,
                    out var origin);
                if (originStatus == SvgCssTransformStatus.Unsupported)
                {
                    _report.RequireFallback(
                        $"SVG transform origin/box requires compatibility fallback");
                    return;
                }
                if (origin.X != 0f || origin.Y != 0f)
                {
                    matrix = SKMatrix.Concat(
                        SKMatrix.Concat(SKMatrix.CreateTranslation(origin.X, origin.Y), matrix),
                        SKMatrix.CreateTranslation(-origin.X, -origin.Y));
                    if (!SvgValues.TryNormalizeMatrix(matrix, out SKMatrix normalized))
                    {
                        _report.RequireFallback("SVG transform origin composition is not finite");
                        return;
                    }
                    matrix = normalized;
                }
            }
            if (!matrix.IsIdentity)
            {
                canvas.Concat(matrix);
            }
        }

        /// <summary>
        /// Applies the CSS transform property (which wins over the XML transform
        /// attribute per the cascade) with bounded transform-origin/transform-box
        /// resolution. Anything outside the supported 2-D subset requires
        /// compatibility fallback instead of guessing pixels.
        /// </summary>
        private void ApplyCssTransform(
            SvgElement el,
            string value,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            // transform-origin and transform-box have no visual effect while the
            // transform computes to none. Do not force compatibility merely
            // because an otherwise unsupported reference box is inert.
            if (string.IsNullOrWhiteSpace(value) ||
                value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string boxRaw = el.GetPresentationProperty("transform-box");
            if (!TryBuildTransformFillBox(el, viewport, boxRaw, out SKRect? fillBox))
            {
                return;
            }

            string originRaw = el.GetPresentationProperty("transform-origin");
            bool originUsesAttributeSyntax =
                el.CascadedDeclarations == null ||
                !el.CascadedDeclarations.ContainsKey("transform-origin");

            var status = SvgCssTransform.TryResolve(
                value,
                originRaw,
                boxRaw,
                viewport.Width,
                viewport.Height,
                inherited.FontSize,
                inherited.RootFontSize,
                fillBox,
                originUsesAttributeSyntax,
                out var matrix,
                out var _);
            switch (status)
            {
                case SvgCssTransformStatus.None:
                case SvgCssTransformStatus.Identity:
                    return;
                case SvgCssTransformStatus.Unsupported:
                    _report.RequireFallback(
                        $"SVG CSS transform '{value.Trim()}' requires compatibility fallback");
                    return;
            }

            // TryResolve already composed the transform-origin pivot into the
            // matrix; apply it as resolved.
            if (!matrix.IsIdentity)
            {
                canvas.Concat(matrix);
            }
        }

        private bool TryBuildTransformFillBox(
            SvgElement element,
            ViewportContext viewport,
            string boxRaw,
            out SKRect? fillBox)
        {
            fillBox = null;
            if (string.IsNullOrWhiteSpace(boxRaw) ||
                boxRaw.Trim().Equals("view-box", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string box = boxRaw.Trim();
            if (!box.Equals("fill-box", StringComparison.OrdinalIgnoreCase))
            {
                _report.RequireFallback(
                    $"SVG CSS transform-box '{box}' requires compatibility fallback");
                return false;
            }
            switch (element.Name)
            {
                case "path":
                case "rect":
                case "circle":
                case "ellipse":
                case "line":
                case "polyline":
                case "polygon":
                    using (var geometry = BuildGeometry(element, viewport))
                    {
                        if (geometry == null) return false;
                        fillBox = geometry.Bounds;
                    }
                    return true;
                default:
                    _report.RequireFallback(
                        $"SVG CSS transform-box fill-box on '{element.Name}' requires compatibility fallback");
                    return false;
            }
        }

        private static bool IsDisplayNone(SvgElement el)
        {
            return string.Equals(el.GetPresentationProperty("display"), "none", System.StringComparison.OrdinalIgnoreCase);
        }

        private bool TryBeginGroupOpacity(SvgElement el, SKCanvas canvas, out SKPaint layerPaint)
        {
            float opacity = ReadClampedOpacity(el, "opacity", 1f);
            if (opacity >= 1f || _activeLayers >= _maxActiveLayers)
            {
                // S1/F1: every layer allocates a full-viewport surface; beyond
                // the cap we composite directly (slight fidelity loss for
                // pathological nesting instead of a multi-GB allocation).
                if (_activeLayers >= _maxActiveLayers && opacity < 1f)
                {
                    _report.RequireFallback(
                        "layer budget exceeded; compatibility fallback required for isolated opacity");
                }
                layerPaint = null;
                return false;
            }
            layerPaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha((byte)(opacity * 255))
            };
            canvas.SaveLayer(layerPaint);
            _activeLayers++;
            return true;
        }

        private void WarnUnsupportedOnce(string feature)
        {
            _report.RequireFallback($"unsupported SVG feature '{feature}' ignored");
        }

        private void WarnUnknownOnce(string name)
        {
            _report.RequireFallback($"unknown element '{name}' skipped");
        }
    }
}
