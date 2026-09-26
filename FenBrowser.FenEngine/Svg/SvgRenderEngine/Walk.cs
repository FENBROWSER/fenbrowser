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
                _report.Warn("render depth budget exceeded; subtree skipped");
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
                        });
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
                        DrawWithEffects(el, canvas, viewport,
                            () => DrawImageElement(el, canvas, viewport, inherited));
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
            string widthAttribute = el.GetAttribute("width");
            string heightAttribute = el.GetAttribute("height");
            string instanceWidth = instance?.GetAttribute("width");
            string instanceHeight = instance?.GetAttribute("height");
            bool hasInstanceWidth = !string.IsNullOrWhiteSpace(instanceWidth);
            bool hasInstanceHeight = !string.IsNullOrWhiteSpace(instanceHeight);
            float w = hasInstanceWidth
                ? ResolveViewportLength(instanceWidth, outer.Width)
                : hasCssWidth
                ? cssWidth
                : string.IsNullOrWhiteSpace(widthAttribute)
                    ? outer.Width
                    : ResolveViewportLength(widthAttribute, outer.Width);
            float h = hasInstanceHeight
                ? ResolveViewportLength(instanceHeight, outer.Height)
                : hasCssHeight
                ? cssHeight
                : string.IsNullOrWhiteSpace(heightAttribute)
                    ? outer.Height
                    : ResolveViewportLength(heightAttribute, outer.Height);
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
            DrawWithEffects(el, canvas, inner, () =>
            {
                if (TryBeginGroupOpacity(el, canvas, out var layerPaint))
                {
                    try
                    {
                        DrawNestedSvgBody(el, canvas, userViewport, inherited);
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
                    DrawNestedSvgBody(el, canvas, userViewport, inherited);
                }
            });
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
        private static bool TryResolveNestedSvgCssSizing(
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
            return SvgCssLengthEvaluator.TryEvaluate(
                       trimmed, property == "width" ? viewportWidth : viewportHeight,
                       fontSize, rootFontSize,
                       viewportWidth, viewportHeight, out value) &&
                   value >= 0f;
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
            InheritedStyle inherited)
        {
            var next = inherited.ResolveOverrides(el, _report);
            DrawChildren(el, canvas, userViewport, next);
        }

        // ------------------------------------------------------- foreignObject

        private const string SvgNamespaceUri = "http://www.w3.org/2000/svg";
        private const int MaxForeignObjectSubtreeNodes = 4096;

        /// <summary>
        /// Element names a foreignObject subtree may contain without naming a
        /// specific blocker. The set mirrors the draw dispatch above - graphics
        /// elements, containers, and referenced-only or metadata content - but it is
        /// a diagnostic whitelist, not a renderable set: none of it is painted inside
        /// a foreignObject, because the whole viewport is refused for want of box
        /// layout. Anything outside it - XHTML, a foreign namespace, unknown SVG,
        /// 'animation' - lets the refusal name the actual element rather than
        /// falling back to the generic reason.
        /// </summary>
        private static readonly HashSet<string> ForeignObjectKnownNames = new(StringComparer.Ordinal)
        {
            "g", "a", "view", "svg", "foreignObject", "switch", "use",
            "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
            "text", "tspan", "textPath", "image",
            "defs", "style", "title", "desc", "metadata", "link", "meta",
            "h:link", "h:meta", "html:link", "html:meta",
            "script", "h:script", "html:script",
            "symbol", "marker", "pattern", "linearGradient", "radialGradient", "stop",
            "filter", "mask", "clipPath", "cursor", "color-profile",
            "font", "font-face", "font-face-src", "font-face-uri", "font-face-name",
            "font-face-format", "glyph", "missing-glyph", "hkern", "vkern",
            "altGlyphDef", "altGlyphItem", "glyphRef", "altGlyphRef",
            "mesh", "meshgradient", "meshrow", "meshpatch",
            "animate", "animateColor", "animateMotion", "animateTransform", "set"
        };

        /// <summary>
        /// Decides a foreignObject. A foreignObject establishes a viewport from its
        /// x/y/width/height geometry, and the walk resolves that geometry here so the
        /// only case it certifies is the one it can prove: a viewport with no area.
        /// An empty clip admits nothing no matter what the subtree holds, so the
        /// document needs no reason code.
        ///
        /// A viewport with area is refused, and the refusal is structural rather than
        /// a missing feature. A foreignObject's content is laid out as a foreign
        /// namespace: element content needs XHTML box layout, and a bare text node
        /// becomes an anonymous block inside the viewport. This engine has no box
        /// layout for either, so no subtree can be painted and Success would report a
        /// frame a browser does not produce. The refusal therefore does not rest on
        /// what the bounded parse happened to record: it holds for a viewport with
        /// area whether or not the tree faithfully carries the content.
        ///
        /// TryDescribeForeignObjectBlocker only refines the diagnostic by naming the
        /// first missing capability it can see. A clean scan is not a licence to
        /// paint, and a named blocker is not what makes the refusal sound.
        /// The canvas and inherited style are part of the uniform draw-handler
        /// contract the dispatch calls every case through; the certified case paints
        /// nothing and the refused case never reaches the canvas.
        ///
        /// This decision governs only the foreignObjects the walk is reached for, so
        /// it is not a substitute for the parse-time gate in
        /// SvgFeatureSupport.FallbackElements. A paintable foreignObject can be
        /// skipped before it gets here - DrawSwitch does not offer 'foreignObject' as
        /// a selectable branch, a requiredExtensions branch is dropped without a
        /// reason, and the render-depth and use-chain budgets warn rather than
        /// refuse. Any removal of that gate has to close those paths first, or
        /// struct/reftests/requiredextensions-xhtml.tentative.svg paints a red
        /// fallback rect where a browser paints green.
        /// </summary>
        private void DrawForeignObject(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext outer,
            InheritedStyle inherited)
        {
            ResolveGeometryFontContext(el, out float fontSize, out float rootFontSize);
            if (!TryResolveForeignObjectExtent(
                    el, "width", outer, fontSize, rootFontSize, out float w) ||
                !TryResolveForeignObjectExtent(
                    el, "height", outer, fontSize, rootFontSize, out float h))
            {
                return;
            }
            if (!(w > 0f) || !(h > 0f)) return;

            if (!TryDescribeForeignObjectBlocker(el, out string blocker))
            {
                _report.RequireFallback(
                    "SVG foreignObject content requires XHTML box layout");
                return;
            }
            _report.RequireFallback($"SVG foreignObject content {blocker} requires compatibility fallback");
        }

        /// <summary>
        /// Resolves a foreignObject width/height. An absent, 'auto' or 'none'
        /// value is zero rather than an error, per the geometry property definition.
        /// Every other value must resolve: an extent this engine cannot compute
        /// would silently become a zero viewport, turning content the browser paints
        /// into a certified non-rendering subtree, so it refuses the document instead.
        /// </summary>
        private bool TryResolveForeignObjectExtent(
            SvgElement element,
            string property,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize,
            out float value)
        {
            value = 0f;
            string raw = element.GetPresentationProperty(property);
            if (string.IsNullOrWhiteSpace(raw)) return true;
            string trimmed = raw.Trim();
            if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            float percentReference = property == "width"
                ? viewport.Width
                : viewport.Height;
            if (TryResolveGeometryLength(
                    element, property, viewport, percentReference,
                    fontSize, rootFontSize, out value))
            {
                return true;
            }
            _report.RequireFallback(
                $"SVG foreignObject geometry property '{property}: {trimmed}' requires compatibility fallback");
            return false;
        }

        /// <summary>
        /// Names the first reason the subtree cannot be painted, so the refusal says
        /// which feature is missing rather than reporting every case identically.
        /// </summary>
        private bool TryDescribeForeignObjectBlocker(SvgElement root, out string blocker)
        {
            blocker = null;
            var pending = new Stack<SvgElement>();
            pending.Push(root);
            int inspected = 0;
            while (pending.Count != 0)
            {
                CheckTime();
                SvgElement current = pending.Pop();
                if (++inspected > MaxForeignObjectSubtreeNodes)
                {
                    blocker = "exceeding the first-party render budget";
                    return true;
                }
                if (IsSvgTestSuiteMetadata(current)) continue;
                if (IsXhtmlForeignElement(current) ||
                    (current.NamespaceUri != null &&
                     !string.Equals(current.NamespaceUri, SvgNamespaceUri, StringComparison.Ordinal)))
                {
                    blocker =
                        $"'{SvgDiagnosticText.Identifier(current.Name)}' needs XHTML box layout";
                    return true;
                }
                if (!ForeignObjectKnownNames.Contains(current.Name))
                {
                    blocker =
                        $"'{SvgDiagnosticText.Identifier(current.Name)}' is not renderable here";
                    return true;
                }
                var children = current.Children;
                for (int i = 0; i < children.Count; i++) pending.Push(children[i]);
            }
            return false;
        }

        private void DrawSwitch(SvgElement el, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
            SvgElement selected = null;
            var children = el.Children;
            for (int i = 0; i < children.Count; i++)
            {
                CheckDeadline();
                var child = children[i];
                if (!PassesConditionalProcessing(child)) continue;
                switch (child.Name)
                {
                    case "title":
                    case "desc":
                    case "metadata":
                        continue;
                    case "g":
                    case "a":
                    case "svg":
                    case "switch":
                    case "use":
                    case "path":
                    case "rect":
                    case "circle":
                    case "ellipse":
                    case "line":
                    case "polyline":
                    case "polygon":
                    case "text":
                    case "image":
                        selected = child;
                        break;
                    default:
                        continue;
                }
                if (selected != null) break;
            }
            if (selected == null) return;

            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, viewport, inherited);
            ApplyClipPath(el, canvas, viewport, inherited);
            var next = inherited.ResolveOverrides(el, _report);
            DrawWithEffects(el, canvas, viewport, () =>
            {
                bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
                try
                {
                    DrawElement(selected, canvas, viewport, next);
                }
                finally
                {
                    if (layered)
                    {
                        canvas.Restore();
                        _activeLayers--;
                        layerPaint.Dispose();
                    }
                }
            });
        }

        private static bool PassesConditionalProcessing(SvgElement element)
        {
            return PassesRequiredExtensions(element) && PassesRequiredFeatures(element);
        }

        private static bool PassesRequiredExtensions(SvgElement element)
        {
            return element.GetAttribute("requiredExtensions") == null;
        }

        private static bool PassesRequiredFeatures(SvgElement element)
        {
            string required = element.GetAttribute("requiredFeatures");
            if (required == null) return true;
            var tokenizer = SvgValues.CreateTokenizer(required.AsSpan());
            bool any = false;
            while (tokenizer.Next(out var token))
            {
                if (!IsSupportedRequiredFeature(token)) return false;
                any = true;
            }
            return any;
        }

        private static bool IsSupportedRequiredFeature(ReadOnlySpan<char> token)
        {
            string value = token.ToString();
            const string svg11 = "http://www.w3.org/TR/SVG11/feature#";
            const string svg11Https = "https://www.w3.org/TR/SVG11/feature#";
            const string svg2 = "http://www.w3.org/TR/SVG2/feature#";
            const string svg2Https = "https://www.w3.org/TR/SVG2/feature#";
            string feature = null;
            if (value.StartsWith(svg11, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg11.Length);
            else if (value.StartsWith(svg11Https, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg11Https.Length);
            else if (value.StartsWith(svg2, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg2.Length);
            else if (value.StartsWith(svg2Https, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg2Https.Length);
            if (feature == null) return false;

            return feature.Equals("SVG", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicStructure", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("ContainerAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("ConditionalProcessing", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Style", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("ViewportAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Shape", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicText", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("PaintAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicPaintAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("GraphicsAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicGraphicsAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("OpacityAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Gradient", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Pattern", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Clip", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicClip", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Mask", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Filter", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Marker", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("XlinkAttribute", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ use

        private void DrawUse(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            CheckDeadline();
            string href = el.GetAttribute("href") ?? el.GetLookup("xlink:href");
            if (!SvgValues.TryParseLocalReference(href, out string rawId))
            {
                // Remote references have no code path here by construction; log
                // and ignore (fail closed).
                _report.RejectResource("use external reference rejected by SVG resource policy");
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
            if (_activeUseIds.Contains(id) || _activeUseElements.Contains(target) ||
                _activeUseIds.Count >= _maxReferenceDepth || _depth >= MaxRenderDepth)
            {
                _report.Warn("use reference cycle or depth budget exceeded; instance skipped");
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
                });
            }
            finally
            {
                _activeUseIds.Remove(id);
                _activeUseElements.Remove(target);
            }
        }

        private HashSet<string> _activeUseIds;
        private HashSet<SvgElement> _activeUseElements;

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
            string instanceWidth = instance?.GetAttribute("width");
            string instanceHeight = instance?.GetAttribute("height");
            string width = !string.IsNullOrWhiteSpace(instanceWidth)
                ? instanceWidth
                : symbol.GetAttribute("width");
            string height = !string.IsNullOrWhiteSpace(instanceHeight)
                ? instanceHeight
                : symbol.GetAttribute("height");
            float w = ResolveViewportLength(width, viewport.Width);
            if (string.IsNullOrWhiteSpace(width)) w = viewport.Width;
            float h = ResolveViewportLength(height, viewport.Height);
            if (string.IsNullOrWhiteSpace(height)) h = viewport.Height;
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
            DrawNestedSvgBody(symbol, canvas, userViewport, inherited);
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

        // ------------------------------------------------------------ clipping

        /// <summary>Ids currently being resolved as clip paths (cycle guard).</summary>
        private HashSet<string> _activeClipIds;
        private HashSet<SvgElement> _activeObjectBounds;

        /// <summary>
        /// Applies the element's clip-path attribute, if any. Must be called
        /// inside a CanvasState scope so the clip unwinds with the element.
        /// Supported: userSpaceOnUse (default) and objectBoundingBox units for
        /// shapes; direct shape children of &lt;clipPath&gt; (plus one level of
        /// use-to-shape). Everything else degrades to "no clip" with a warning.
        /// </summary>
        private bool TryResolveClipPathDefinition(
            SvgElement clip,
            out SvgElement contentOwner,
            out string clipPathUnits,
            out SvgElement transformOwner)
        {
            contentOwner = null;
            clipPathUnits = null;
            transformOwner = null;
            var visited = new HashSet<SvgElement>();
            SvgElement current = clip;
            while (current != null)
            {
                CheckDeadline();
                if (current.Name != "clipPath" || !visited.Add(current) ||
                    visited.Count > _maxReferenceDepth)
                {
                    _report.Warn("clipPath href cycle or depth budget exceeded");
                    _report.RequireFallback("SVG clipPath href cycle or depth budget exceeded");
                    return false;
                }
                if (clipPathUnits == null)
                    clipPathUnits = current.GetAttribute("clipPathUnits");
                if (transformOwner == null && current.GetAttribute("transform") != null)
                    transformOwner = current;
                if (contentOwner == null)
                {
                    foreach (var child in current.Children)
                    {
                        if (child.Name is not ("title" or "desc" or "metadata"))
                        {
                            contentOwner = current;
                            break;
                        }
                    }
                }

                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (href == null) break;
                if (!SvgValues.TryParseLocalReference(href, out string id))
                {
                    _report.RejectResource("clipPath external href rejected by SVG resource policy");
                    return false;
                }
                if (!_doc.ElementsById.TryGetValue(id, out var next) || next.Name != "clipPath")
                {
                    _report.Warn("clipPath href reference unresolved");
                    break;
                }
                current = next;
            }
            return true;
        }

        private void ApplyClipPath(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            var raw = el.GetPresentationProperty("clip-path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            if (!SvgValues.TryParsePaint(raw.AsSpan(), out var kind, out _, out var fragment, out _))
            {
                if (!raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                    _report.RequireFallback("SVG clip-path value requires compatibility fallback");
                return;
            }
            if (kind != SvgValues.PaintKind.ServerRef || fragment == null)
            {
                // none / remote reference: nothing to apply (remote is fail-closed).
                return;
            }
            if (!_doc.ElementsById.TryGetValue(fragment, out var clipEl) ||
                clipEl.Name != "clipPath" ||
                !TryResolveClipPathDefinition(
                    clipEl, out _, out string clipPathUnits, out _))
            {
                _report.Warn("clip-path reference unresolved");
                return;
            }

            _activeClipIds ??= new HashSet<string>(System.StringComparer.Ordinal);
            if (_activeClipIds.Contains(fragment) || _activeClipIds.Count >= _maxReferenceDepth)
            {
                _report.Warn("clip-path cycle or depth exceeded; clip ignored");
                return;
            }
            _activeClipIds.Add(fragment);
            try
            {
                bool isObjectBoundingBox = string.Equals(
                    clipPathUnits,
                    "objectBoundingBox",
                    System.StringComparison.Ordinal);

                var clipViewport = isObjectBoundingBox
                    ? new ViewportContext(1f, 1f)
                    : viewport;
                using var clipPath = BuildClipGeometry(clipEl, clipViewport);
                if (clipPath == null || clipPath.IsEmpty)
                {
                    // Empty clip path hides the element entirely (spec).
                    canvas.ClipRect(new SKRect(0f, 0f, 0f, 0f));
                    return;
                }

                if (isObjectBoundingBox)
                {
                    if (!TryResolveObjectBounds(el, viewport, out var bbox))
                    {
                        _report.RequireFallback(
                            $"objectBoundingBox clip on '{el.Name}' requires compatibility fallback");
                        bbox = new SKRect(0f, 0f, viewport.Width, viewport.Height);
                    }
                    using var mapped = new SKPath();
                    clipPath.Transform(SKMatrix.Concat(
                        SKMatrix.CreateTranslation(bbox.Left, bbox.Top),
                        SKMatrix.CreateScale(bbox.Width, bbox.Height)), mapped);
                    canvas.ClipPath(mapped, SKClipOperation.Intersect, antialias: true);
                }
                else
                {
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                }
            }
            finally
            {
                _activeClipIds.Remove(fragment);
            }
        }

        private bool TryResolveObjectBounds(
            SvgElement element,
            ViewportContext viewport,
            out SKRect bounds)
        {
            CheckDeadline();
            _activeObjectBounds ??= new HashSet<SvgElement>();
            if (element == null || _activeObjectBounds.Contains(element) ||
                _activeObjectBounds.Count >= _maxReferenceDepth)
            {
                _report.Warn("object-bounds reference cycle or depth budget exceeded");
                bounds = default;
                return false;
            }

            _activeObjectBounds.Add(element);
            try
            {
                return TryResolveObjectBoundsCore(element, viewport, out bounds);
            }
            finally
            {
                _activeObjectBounds.Remove(element);
            }
        }

        private bool TryResolveObjectBoundsTransform(
            SvgElement element,
            ViewportContext viewport,
            out SKMatrix matrix)
        {
            CheckDeadline();
            matrix = SKMatrix.Identity;
            if (!TryResolveCssZoomMatrix(element, out var zoom))
            {
                matrix = SKMatrix.Identity;
                return false;
            }
            string cssTransform = element.GetCascadedPresentationProperty("transform");
            if (string.IsNullOrWhiteSpace(cssTransform) &&
                string.IsNullOrWhiteSpace(element.GetAttribute("transform")))
            {
                matrix = zoom;
                return SvgValues.IsFinite(matrix);
            }
            if (!string.IsNullOrWhiteSpace(cssTransform) &&
                cssTransform.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                matrix = zoom;
                return SvgValues.IsFinite(matrix);
            }

            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            if (!string.IsNullOrWhiteSpace(cssTransform))
            {
                string boxRaw = element.GetPresentationProperty("transform-box");
                if (!TryBuildTransformFillBox(element, viewport, boxRaw, out SKRect? fillBox))
                    return false;
                bool originUsesAttributeSyntax =
                    element.CascadedDeclarations == null ||
                    !element.CascadedDeclarations.ContainsKey("transform-origin");
                var status = SvgCssTransform.TryResolve(
                    cssTransform,
                    element.GetPresentationProperty("transform-origin"),
                    boxRaw,
                    viewport.Width,
                    viewport.Height,
                    fontSize,
                    rootFontSize,
                    fillBox,
                    originUsesAttributeSyntax,
                    out matrix,
                    out _);
                if (status == SvgCssTransformStatus.Unsupported)
                {
                    _report.RequireFallback(
                        $"SVG CSS transform '{cssTransform.Trim()}' requires compatibility fallback");
                    return false;
                }
                matrix = SKMatrix.Concat(zoom, matrix);
                if (!SvgValues.IsFinite(matrix))
                {
                    _report.RequireFallback("SVG transform is not finite and bounded");
                    return false;
                }
                return true;
            }

            string transformText = element.GetAttribute("transform");
            if (string.IsNullOrWhiteSpace(transformText))
            {
                matrix = zoom;
                return SvgValues.IsFinite(matrix);
            }
            if (!SvgValues.TryParseTransformList(transformText.AsSpan(), out matrix))
            {
                _report.RequireFallback("SVG transform attribute requires compatibility fallback");
                return false;
            }

            string originText = element.GetPresentationProperty("transform-origin");
            string boxText = element.GetPresentationProperty("transform-box");
            if (!string.IsNullOrWhiteSpace(originText) || !string.IsNullOrWhiteSpace(boxText))
            {
                if (!TryBuildTransformFillBox(element, viewport, boxText, out SKRect? fillBox))
                    return false;
                bool originUsesAttributeSyntax =
                    element.CascadedDeclarations == null ||
                    !element.CascadedDeclarations.ContainsKey("transform-origin");
                var originStatus = SvgCssTransform.TryResolveReferenceOrigin(
                    originText,
                    boxText,
                    viewport.Width,
                    viewport.Height,
                    fontSize,
                    rootFontSize,
                    fillBox,
                    originUsesAttributeSyntax,
                    out var origin);
                if (originStatus == SvgCssTransformStatus.Unsupported)
                {
                    _report.RequireFallback("SVG transform origin/box requires compatibility fallback");
                    return false;
                }
                if (origin.X != 0f || origin.Y != 0f)
                {
                    matrix = SKMatrix.Concat(
                        SKMatrix.Concat(SKMatrix.CreateTranslation(origin.X, origin.Y), matrix),
                        SKMatrix.CreateTranslation(-origin.X, -origin.Y));
                    if (!SvgValues.TryNormalizeMatrix(matrix, out SKMatrix normalized))
                    {
                        _report.RequireFallback("SVG transform origin composition is not finite");
                        return false;
                    }
                    matrix = normalized;
                }
            }
            matrix = SKMatrix.Concat(zoom, matrix);
            if (!SvgValues.IsFinite(matrix))
            {
                _report.RequireFallback("SVG transform is not finite and bounded");
                return false;
            }
            return true;
        }

        private bool TryResolveObjectBoundsCore(
            SvgElement element,
            ViewportContext viewport,
            out SKRect bounds)
        {
            CheckDeadline();
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
                        if (geometry != null && !geometry.IsEmpty)
                        {
                            bounds = geometry.TightBounds;
                            return SvgValues.IsFinite(bounds.Left) &&
                                SvgValues.IsFinite(bounds.Top) &&
                                SvgValues.IsFinite(bounds.Right) &&
                                SvgValues.IsFinite(bounds.Bottom) &&
                                bounds.Width > 0f && bounds.Height > 0f;
                        }
                    }
                    break;
                case "image":
                    if (TryResolveImageBox(element, viewport, 0f, 0f, out bounds))
                        return true;
                    break;
                case "svg":
                    bounds = new SKRect(0f, 0f, viewport.Width, viewport.Height);
                    return true;
                case "use":
                    string href = element.GetAttribute("href") ?? element.GetLookup("xlink:href");
                    if (SvgValues.TryParseLocalReference(href, out string useId) &&
                        _doc.ElementsById.TryGetValue(useId, out var useTarget) &&
                        useTarget.Name is not ("svg" or "symbol") &&
                        TryResolveObjectBounds(useTarget, viewport, out bounds))
                    {
                        if (!TryResolveObjectBoundsTransform(
                                useTarget, viewport, out var targetTransform) ||
                            !TryResolveObjectBoundsTransform(
                                element, viewport, out _))
                        {
                            bounds = default;
                            return false;
                        }
                        bounds = targetTransform.MapRect(bounds);
                        float useX = ResolveGeometryCoordinate(element, "x", viewport, viewport.Width);
                        float useY = ResolveGeometryCoordinate(element, "y", viewport, viewport.Height);
                        if (useX != 0f || useY != 0f)
                        {
                            if (!SvgValues.IsFinite(useX) || !SvgValues.IsFinite(useY))
                            {
                                bounds = default;
                                return false;
                            }
                            bounds = SKMatrix.CreateTranslation(useX, useY).MapRect(bounds);
                        }
                        return SvgValues.IsFinite(bounds.Left) &&
                            SvgValues.IsFinite(bounds.Top) &&
                            SvgValues.IsFinite(bounds.Right) &&
                            SvgValues.IsFinite(bounds.Bottom) &&
                            bounds.Width > 0f && bounds.Height > 0f;
                    }
                    break;
                case "g":
                case "a":
                    bool hasBounds = false;
                    SKRect combined = default;
                    foreach (var child in element.Children)
                    {
                        CheckDeadline();
                        if (!TryResolveObjectBounds(child, viewport, out var childBounds))
                            continue;

                        SKMatrix childTransform = SKMatrix.Identity;
                        if (!TryResolveObjectBoundsTransform(
                                child, viewport, out childTransform))
                        {
                            bounds = default;
                            return false;
                        }
                        if (!childTransform.IsIdentity)
                            childBounds = childTransform.MapRect(childBounds);

                        combined = hasBounds ? SKRect.Union(combined, childBounds) : childBounds;
                        hasBounds = true;
                    }
                    if (hasBounds && combined.Width > 0f && combined.Height > 0f &&
                        SvgValues.IsFinite(combined.Left) && SvgValues.IsFinite(combined.Top) &&
                        SvgValues.IsFinite(combined.Right) && SvgValues.IsFinite(combined.Bottom))
                    {
                        bounds = combined;
                        return true;
                    }
                    break;
            }

            bounds = default;
            return false;
        }

        private string ResolveInheritedClipRule(
            SvgElement shape,
            SvgElement reference,
            SvgElement clipRoot)
        {
            if (TryGetClipRule(shape, out string rule)) return rule;
            if (reference != null && !ReferenceEquals(reference, shape) &&
                TryGetClipRule(reference, out rule)) return rule;
            for (SvgElement current = shape; current != null; current = current.Parent)
            {
                if (TryGetClipRule(current, out rule)) return rule;
                if (ReferenceEquals(current, clipRoot)) break;
            }
            for (SvgElement current = clipRoot; current != null; current = current.Parent)
                if (TryGetClipRule(current, out rule)) return rule;
            return null;
        }

        private static bool TryGetClipRule(SvgElement element, out string rule)
        {
            rule = element?.GetPresentationProperty("clip-rule");
            if (string.IsNullOrWhiteSpace(rule))
                rule = element?.GetPresentationProperty("fill-rule");
            return !string.IsNullOrWhiteSpace(rule) &&
                (rule.Equals("evenodd", StringComparison.OrdinalIgnoreCase) ||
                 rule.Equals("nonzero", StringComparison.OrdinalIgnoreCase));
        }

        private SKPath BuildClipGeometry(SvgElement clipEl, ViewportContext viewport)
        {
            if (!TryResolveClipPathDefinition(
                    clipEl, out var contentOwner, out _, out var transformOwner) ||
                contentOwner == null)
                return null;

            using var combinedBuilder = new SKPathBuilder();
            bool hasGeometry = false;
            bool evenOdd = false;

            foreach (var child in contentOwner.Children)
            {
                CheckDeadline();
                if (!PassesConditionalProcessing(child)) continue;
                SvgElement shapeEl = child;
                if (child.Name == "use")
                {
                    var href = child.GetAttribute("href") ?? child.GetLookup("xlink:href");
                    if (!SvgValues.TryParseLocalReference(href, out string id) ||
                        !_doc.ElementsById.TryGetValue(id, out var target))
                    {
                        continue;
                    }
                    shapeEl = target;
                }

                switch (shapeEl.Name)
                {
                    case "path":
                    case "rect":
                    case "circle":
                    case "ellipse":
                    case "polygon":
                    case "polyline":
                        break;
                    default:
                        WarnUnsupportedOnce($"clipPath child '{child.Name}'");
                        continue;
                }

                using var childPath = BuildGeometry(shapeEl, viewport);
                if (childPath == null)
                {
                    continue;
                }

                ApplyPathTransform(shapeEl, childPath);
                if (child.Name == "use")
                {
                    float useX = ResolveGeometryCoordinate(child, "x", viewport, viewport.Width);
                    float useY = ResolveGeometryCoordinate(child, "y", viewport, viewport.Height);
                    if (useX != 0f || useY != 0f)
                    {
                        childPath.Transform(SKMatrix.CreateTranslation(useX, useY));
                    }
                    ApplyPathTransform(child, childPath);
                }
                combinedBuilder.AddPath(childPath, SKPathAddMode.Append);
                hasGeometry = true;

                if (string.Equals(
                        ResolveInheritedClipRule(shapeEl, child, contentOwner),
                        "evenodd",
                        StringComparison.OrdinalIgnoreCase))
                {
                    evenOdd = true;
                }
            }

            if (!hasGeometry)
            {
                return null;
            }

            var combined = combinedBuilder.Detach();
            ApplyPathTransform(transformOwner ?? clipEl, combined);
            if (evenOdd)
            {
                combined.FillType = SKPathFillType.EvenOdd;
            }
            return combined;
        }

        private void ApplyPathTransform(SvgElement element, SKPath path)
        {
            string css = element.GetCascadedPresentationProperty("transform");
            if (!string.IsNullOrWhiteSpace(css))
            {
                _report.RequireFallback(
                    "SVG CSS transform on clipPath content requires compatibility fallback");
                return;
            }
            var transformText = element.GetAttribute("transform");
            if (!string.IsNullOrWhiteSpace(transformText))
            {
                if (!SvgValues.TryParseTransformList(transformText.AsSpan(), out var transform))
                {
                    _report.RequireFallback("SVG clipPath transform requires compatibility fallback");
                    return;
                }
                path.Transform(transform);
            }
        }

        private bool TryResolveImageBox(
            SvgElement element,
            ViewportContext viewport,
            float defaultWidth,
            float defaultHeight,
            out SKRect box)
        {
            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            float x = TryResolveGeometryLength(
                element, "x", viewport, viewport.Width, fontSize, rootFontSize, out float xValue)
                ? xValue
                : 0f;
            float y = TryResolveGeometryLength(
                element, "y", viewport, viewport.Height, fontSize, rootFontSize, out float yValue)
                ? yValue
                : 0f;

            string widthText = element.GetPresentationProperty("width");
            string heightText = element.GetPresentationProperty("height");
            bool widthIsAuto = widthText == null ||
                               widthText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            bool heightIsAuto = heightText == null ||
                                heightText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            float width;
            if (widthIsAuto)
            {
                width = defaultWidth;
            }
            else if (!TryResolveGeometryLength(
                         element, "width", viewport, viewport.Width, fontSize, rootFontSize,
                         out width) || width <= 0f)
            {
                box = default;
                return false;
            }

            float height;
            if (heightIsAuto)
            {
                height = defaultHeight;
            }
            else if (!TryResolveGeometryLength(
                         element, "height", viewport, viewport.Height, fontSize, rootFontSize,
                         out height) || height <= 0f)
            {
                box = default;
                return false;
            }

            if (widthIsAuto && !heightIsAuto && defaultHeight > 0f)
                width = height * (defaultWidth / defaultHeight);
            else if (heightIsAuto && !widthIsAuto && defaultWidth > 0f)
                height = width * (defaultHeight / defaultWidth);

            if (!SvgValues.IsFinite(x) || !SvgValues.IsFinite(y) ||
                !SvgValues.IsFinite(width) || !SvgValues.IsFinite(height) ||
                width <= 0f || height <= 0f ||
                !SvgValues.IsFinite(x + width) || !SvgValues.IsFinite(y + height))
            {
                box = default;
                return false;
            }
            box = new SKRect(x, y, x + width, y + height);
            return true;
        }

        // --------------------------------------------------------------- images

        /// <summary>
        /// Renders raster images embedded as data: URIs. SECURITY: this is the
        /// ONLY image source - there is no fetch code path here, so external
        /// hrefs are logged and ignored (fail closed). Decoded pixel counts are
        /// bounded by MaxDecodedImagePixels before drawing and charged against the
        /// cumulative decoded budget that also covers every other image in the
        /// render, nested documents included.
        /// </summary>
        private void DrawImageElement(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            var href = el.GetAttribute("href") ?? el.GetLookup("xlink:href");
            if (string.IsNullOrEmpty(href))
            {
                return; // No source: renders nothing (spec).
            }
            if (!href.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
            {
                SplitReferenceFragment(href, out string target, out string referenceFragment);
                if (target.Length == 0)
                {
                    return;
                }
                DrawResolvedImage(el, canvas, viewport, inherited, target, referenceFragment);
                return;
            }
            if (IsSvgDataUri(href))
            {
                SplitReferenceFragment(href, out string payload, out string svgFragment);
                if (payload.Length == 0)
                {
                    return;
                }
                DrawEmbeddedSvgImage(el, canvas, viewport, inherited, payload, svgFragment);
                return;
            }

            CheckDeadline();
            var bytes = DecodeDataUriBytes(href, _resources.MaxDecodedImageBytes, out var decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
                return;
            }
            if (!_resources.TryAdmit(bytes.Length))
            {
                _report.RejectResource("embedded raster image cumulative resource budget exceeded");
                return;
            }

            if (!TryBorrowOrDecodeImage(bytes, _resources, out var image, out var imageError))
            {
                _report.RejectResource(imageError);
                return;
            }

            DrawBorrowedImage(el, canvas, viewport, inherited, image);
        }

        private static void SplitReferenceFragment(string reference, out string target, out string fragment)
        {
            int hash = reference.IndexOf('#');
            if (hash < 0)
            {
                target = reference;
                fragment = null;
                return;
            }
            target = reference.Substring(0, hash);
            fragment = reference.Substring(hash + 1);
            if (fragment.Length == 0) fragment = null;
        }

        private void DrawEmbeddedSvgImage(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            string href,
            string fragment)
        {
            CheckDeadline();
            byte[] bytes = DecodeSvgDataUriBytes(href, _resources.MaxDecodedImageBytes, out string decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
                return;
            }

            if (!_resources.TryAdmit(bytes.Length))
            {
                _report.RejectResource("embedded SVG cumulative byte budget exceeded");
                return;
            }

            DrawSvgImageBytes(element, canvas, viewport, inherited, bytes, _baseUri, fragment);
        }

        private void DrawSvgImageBytes(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            byte[] bytes,
            Uri resourceUri,
            string fragment)
        {
            int maxDepth = Math.Min(16, _limits.MaxReferenceDepth);
            if (_resourceDepth >= maxDepth)
            {
                _report.RejectResource("embedded SVG resource depth budget exceeded");
                return;
            }

            string source;
            try
            {
                source = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                _report.RejectResource("embedded SVG is not valid UTF-8");
                return;
            }

            ReferencedSvgRender referenced =
                RenderReferencedSvg(source, fragment, element, viewport, resourceUri);
            if (referenced.ViewFragmentRejected)
            {
                return;
            }
            if (referenced.Picture == null)
            {
                _report.RejectResource(
                    "embedded SVG failed bounded first-party rendering: " +
                    (string.IsNullOrWhiteSpace(referenced.Error) ? "unknown nested failure" : referenced.Error));
                return;
            }

            using (referenced.Picture)
            {
                if (referenced.RequiresFallback || referenced.ResourceRejected)
                {
                    _report.RejectResource(
                        referenced.RequiresFallback
                            ? "embedded SVG requires unsupported compatibility rendering"
                            : "embedded SVG contained a rejected nested resource");
                    return;
                }

                if (referenced.Width <= 0f || referenced.Height <= 0f ||
                    !TryResolveImageBox(
                        element, viewport, referenced.Width, referenced.Height, out var imageViewport))
                    return;

                var style = inherited.ResolveOverrides(element, _report);
                if (!style.Visibility) return;
                bool layered = TryBeginGroupOpacity(element, canvas, out var layerPaint);
                try
                {
                    using var state = new CanvasState(canvas);
                    string preserveAspectRatio = element.GetAttribute("preserveAspectRatio");
                    if (string.IsNullOrWhiteSpace(preserveAspectRatio))
                        preserveAspectRatio = referenced.RootPreserveAspectRatio;
                    var destination = ResolveImageDestination(
                        imageViewport, referenced.Width, referenced.Height,
                        preserveAspectRatio);
                    if (destination.IsEmpty) return;
                    canvas.ClipRect(imageViewport);
                    canvas.Translate(destination.Left, destination.Top);
                    canvas.Scale(
                        destination.Width / referenced.Width,
                        destination.Height / referenced.Height);
                    canvas.DrawPicture(referenced.Picture);
                }
                finally
                {
                    if (layered)
                    {
                        canvas.Restore();
                        _activeLayers--;
                        layerPaint.Dispose();
                    }
                }
            }
        }

        private sealed class ReferencedSvgRender
        {
            public SKPicture Picture;
            public float Width;
            public float Height;
            public string RootPreserveAspectRatio;
            public string Error;
            public bool RequiresFallback;
            public bool ResourceRejected;
            public bool ViewFragmentRejected;
        }

        private ReferencedSvgRender RenderReferencedSvg(
            string source,
            string fragment,
            SvgElement element,
            ViewportContext viewport,
            Uri resourceUri)
        {
            var result = new ReferencedSvgRender();
            if (!SvgMarkupParser.TryParse(source, _limits, out SvgParsedDocument doc, out string error))
            {
                result.Error = error;
                return result;
            }

            if (!EstablishReferencedViewport(doc, fragment, element, viewport))
            {
                result.ViewFragmentRejected = true;
                return result;
            }
            result.RootPreserveAspectRatio = doc.Root.GetAttribute("preserveAspectRatio");

            var nested = new SvgRenderEngine(
                doc, _limits, _resources, _resourceDepth + 1,
                resourceUri, _resourceResolver, _documentTimeSeconds);
            SKPicture picture = null;
            float width = 0f;
            float height = 0f;
            try
            {
                nested.ApplySmilSnapshot(doc.Root);
                nested.RenderRoot(out picture, out width, out height);
            }
            catch (SvgTimeBudgetExceededException)
            {
                picture = null;
                result.Error = $"SVG render exceeded time limit ({_limits.MaxRenderTimeMs}ms)";
            }
            catch (SvgSandboxViolationException ex)
            {
                picture = null;
                result.Error = ex.Message;
            }
            result.Picture = picture;
            result.Width = width;
            result.Height = height;
            result.RequiresFallback = nested._report.UnsupportedFeatureIgnored;
            result.ResourceRejected = nested._report.ResourceRejected;
            return result;
        }

        private bool EstablishReferencedViewport(
            SvgParsedDocument doc,
            string fragment,
            SvgElement element,
            ViewportContext viewport)
        {
            var root = doc.Root;
            if (!string.IsNullOrEmpty(fragment))
            {
                if (!doc.ElementsById.TryGetValue(fragment, out SvgElement view) ||
                    view.Name != "view")
                {
                    _report.RequireFallback(
                        "referenced SVG view fragment is unresolved or invalid");
                    return false;
                }
                string viewBox = view.GetAttribute("viewBox");
                if (!TryParseViewBox(
                        viewBox, out _, out _, out _, out _, out bool viewBoxDisablesRendering))
                {
                    if (!viewBoxDisablesRendering)
                    {
                        return true;
                    }
                    _report.RequireFallback(
                        "referenced SVG view fragment is unresolved or invalid");
                    return false;
                }
                SetReferencedRootAttribute(root, "viewBox", viewBox);
                string viewPreserveAspectRatio = view.GetAttribute("preserveAspectRatio");
                if (!string.IsNullOrWhiteSpace(viewPreserveAspectRatio))
                    SetReferencedRootAttribute(root, "preserveAspectRatio", viewPreserveAspectRatio);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(root.GetAttribute("viewBox")) ||
                !string.IsNullOrWhiteSpace(root.GetAttribute("width")) ||
                !string.IsNullOrWhiteSpace(root.GetAttribute("height")) ||
                !TryResolveImageBox(element, viewport, 0f, 0f, out SKRect imageViewport))
                return true;
            SetReferencedRootSize(root, imageViewport.Width, imageViewport.Height);
            return true;
        }

        private static void SetReferencedRootSize(SvgElement root, float width, float height)
        {
            SetReferencedRootAttribute(root, "width", FormatReferencedRootLength(width));
            SetReferencedRootAttribute(root, "height", FormatReferencedRootLength(height));
        }

        private static string FormatReferencedRootLength(float value) =>
            NormalizeGeometryValue(SvgValues.ClampCoord(value))
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static void SetReferencedRootAttribute(SvgElement root, string name, string value)
        {
            var attributes = root.Attributes;
            if (attributes != null)
            {
                for (int i = 0; i < attributes.Length; i++)
                {
                    if (!string.Equals(
                            attributes[i].Key, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    attributes[i] = new KeyValuePair<string, string>(attributes[i].Key, value);
                    return;
                }
                var grown = new KeyValuePair<string, string>[attributes.Length + 1];
                System.Array.Copy(attributes, grown, attributes.Length);
                attributes = grown;
                root.Attributes = grown;
            }
            else
            {
                attributes = new KeyValuePair<string, string>[1];
                root.Attributes = attributes;
            }
            attributes[attributes.Length - 1] = new KeyValuePair<string, string>(name, value);
        }

        private void DrawResolvedImage(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            string reference,
            string fragment)
        {
            if (!TryResolveResource(reference, SvgResourceKind.Image, out var resource)) return;
            byte[] bytes = resource.Content.ToArray();
            if (!_resources.TryAdmit(bytes.Length))
            {
                _report.RejectResource("resolved image cumulative resource budget exceeded");
                return;
            }

            bool isSvg = string.Equals(
                             resource.ContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
                         resource.Uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeSvg(bytes);
            if (isSvg)
            {
                DrawSvgImageBytes(element, canvas, viewport, inherited, bytes, resource.Uri, fragment);
                return;
            }

            if (!TryBorrowOrDecodeImage(
                    bytes, _resources, out var image, out var imageError))
            {
                _report.RejectResource(imageError);
                return;
            }
            DrawBorrowedImage(element, canvas, viewport, inherited, image);
        }

        private bool TryResolveResource(
            string reference,
            SvgResourceKind kind,
            out SvgResolvedResource resource)
        {
            resource = default;
            if (!_limits.AllowExternalReferences)
            {
                _report.RejectResource(
                    "external SVG reference rejected because external references are disabled");
                return false;
            }
            if (_baseUri == null || _resourceResolver == null ||
                !Uri.TryCreate(_baseUri, reference, out var absolute) || !absolute.IsAbsoluteUri)
            {
                _report.RejectResource("external SVG resource has no authorized resolver context");
                return false;
            }
            var fetchUri = new UriBuilder(absolute) { Fragment = string.Empty }.Uri;
            if (!IsSameOrigin(_baseUri, fetchUri))
            {
                _report.RejectResource("cross-origin SVG resource rejected by renderer policy");
                return false;
            }
            if (!_resourceResolver.TryResolve(fetchUri, kind, out resource, out string error))
            {
                _report.RejectResource(string.IsNullOrWhiteSpace(error)
                    ? "authorized SVG resource resolver returned no resource"
                    : error);
                return false;
            }
            if (resource.Uri == null || resource.Uri != fetchUri)
            {
                _report.RejectResource("SVG resource resolver returned mismatched URI");
                resource = default;
                return false;
            }
            if (resource.Content.Length <= 0 || resource.Content.Length > _resources.MaxDecodedImageBytes)
            {
                _report.RejectResource("resolved SVG resource exceeds byte budget");
                resource = default;
                return false;
            }
            return true;
        }

        private static bool IsSameOrigin(Uri first, Uri second) =>
            first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase) &&
            first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) &&
            first.Port == second.Port;

        private static bool LooksLikeSvg(byte[] bytes)
        {
            int length = Math.Min(bytes.Length, 512);
            string prefix;
            try { prefix = Encoding.UTF8.GetString(bytes, 0, length); }
            catch { return false; }
            return prefix.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawBorrowedImage(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            SKImage image)
        {
            if (!TryResolveImageBox(
                    el, viewport, image.Width, image.Height, out var imageViewport))
            {
                return;
            }

            var style = inherited.ResolveOverrides(el, _report);
            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                using var state = new CanvasState(canvas);
                using var paint = new SKPaint { IsAntialias = true };
                paint.Color = style.Visibility ? SKColors.Black : SKColors.Transparent;
                var source = new SKRect(0f, 0f, image.Width, image.Height);
                var destination = ResolveImageDestination(
                    imageViewport, image.Width, image.Height,
                    el.GetAttribute("preserveAspectRatio"));
                if (destination.IsEmpty) return;
                canvas.ClipRect(imageViewport);
                canvas.DrawImage(image, source, destination, SKSamplingOptions.Default, paint);
            }
            finally
            {
                if (layered)
                {
                    canvas.Restore();
                    _activeLayers--;
                    layerPaint.Dispose();
                }
            }
        }

        private static byte[] DecodeSvgDataUriBytes(string href, int maxBytes, out string error)
        {
            int comma = href.IndexOf(',');
            if (comma < 0)
            {
                error = "embedded SVG data URI has no payload separator";
                return null;
            }
            string header = href.Substring(5, comma - 5);
            if (header.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0)
                return DecodeDataUriBytes(href, maxBytes, out error);

            string payload = href.Substring(comma + 1);
            if ((long)payload.Length > (long)maxBytes * 3L)
            {
                error = "embedded SVG payload exceeds admission budget";
                return null;
            }
            var bytes = new byte[Math.Min(payload.Length, maxBytes)];
            int count = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                if (count >= maxBytes)
                {
                    error = "embedded SVG payload exceeds admission budget";
                    return null;
                }
                char c = payload[i];
                if (c == '%')
                {
                    if (i + 2 >= payload.Length ||
                        !TryHex(payload[i + 1], out int high) || !TryHex(payload[i + 2], out int low))
                    {
                        error = "embedded SVG data URI has invalid percent encoding";
                        return null;
                    }
                    bytes[count++] = (byte)((high << 4) | low);
                    i += 2;
                }
                else if (c <= 0x7f)
                {
                    bytes[count++] = (byte)c;
                }
                else
                {
                    error = "embedded SVG data URI must percent-encode non-ASCII bytes";
                    return null;
                }
            }
            if (count == bytes.Length)
            {
                error = null;
                return bytes;
            }
            Array.Resize(ref bytes, count);
            error = null;
            return bytes;

            static bool TryHex(char c, out int value)
            {
                if (c is >= '0' and <= '9') { value = c - '0'; return true; }
                if (c is >= 'a' and <= 'f') { value = c - 'a' + 10; return true; }
                if (c is >= 'A' and <= 'F') { value = c - 'A' + 10; return true; }
                value = 0;
                return false;
            }
        }

        /// <summary>
        /// Decodes an embedded raster only after the codec header has passed the
        /// dimension and pixel budgets. This prevents compressed image bombs from
        /// allocating their advertised surface before admission control runs.
        /// </summary>
        internal static bool TryDecodeEmbeddedBitmap(
            byte[] bytes,
            long maxPixels,
            int maxDimension,
            out SKBitmap bitmap,
            out string error) =>
            TryDecodeEmbeddedSurface(
                bytes, maxPixels, maxDimension, null, out bitmap, out error);

        private static bool TryBorrowOrDecodeImage(
            byte[] bytes,
            SvgRenderResources resources,
            out SKImage image,
            out string error)
        {
            image = null;
            if (resources == null)
            {
                error = "image decode has no render resource scope";
                return false;
            }

            if (resources.TryBorrowDecodedImage(bytes, out image, out _))
            {
                error = null;
                return true;
            }

            if (!TryDecodeEmbeddedSurface(
                    bytes,
                    resources.MaxDecodedImagePixels,
                    resources.MaxRasterDimension,
                    resources,
                    out var bitmap,
                    out error))
            {
                return false;
            }

            try
            {
                image = SKImage.FromBitmap(bitmap);
            }
            finally
            {
                bitmap.Dispose();
            }

            if (image == null)
            {
                error = "image raster allocation refused";
                return false;
            }

            resources.RetainDecodedImage(bytes, image);
            return true;
        }

        private static bool TryDecodeEmbeddedSurface(
            byte[] bytes,
            long maxPixels,
            int maxDimension,
            SvgRenderResources resources,
            out SKBitmap bitmap,
            out string error)
        {
            bitmap = null;
            error = null;

            if (bytes == null || bytes.Length == 0)
            {
                error = "image data URI has an empty payload";
                return false;
            }

            try
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec == null)
                {
                    error = "image data URI failed to decode";
                    return false;
                }

                var sourceInfo = codec.Info;
                long pixels = (long)sourceInfo.Width * sourceInfo.Height;
                if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0 ||
                    sourceInfo.Width > maxDimension || sourceInfo.Height > maxDimension ||
                    pixels <= 0 || pixels > maxPixels)
                {
                    error = "image decoded size exceeds raster budget; rejected";
                    return false;
                }

                if (resources != null && !resources.TryAdmitDecodedPixels(pixels))
                {
                    error = "image cumulative decoded raster budget exceeded";
                    return false;
                }

                var decodeInfo = new SKImageInfo(
                    sourceInfo.Width,
                    sourceInfo.Height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul);
                var candidate = new SKBitmap();
                if (!candidate.TryAllocPixels(decodeInfo))
                {
                    candidate.Dispose();
                    error = "image raster allocation refused";
                    return false;
                }

                var decodeResult = codec.GetPixels(decodeInfo, candidate.GetPixels());
                if (decodeResult != SKCodecResult.Success)
                {
                    candidate.Dispose();
                    error = $"image data URI decode failed ({decodeResult})";
                    return false;
                }

                bitmap = candidate;
                return true;
            }
            catch (Exception)
            {
                error = "image data URI failed to decode";
                return false;
            }
        }

        private static SKRect ResolveImageDestination(
            SKRect viewport,
            float sourceWidth,
            float sourceHeight,
            string preserveAspectRatio)
        {
            ParsePreserveAspectRatio(preserveAspectRatio, out var align, out var meet);
            if (!SvgValues.IsFinite(viewport.Left) || !SvgValues.IsFinite(viewport.Top) ||
                !SvgValues.IsFinite(viewport.Right) || !SvgValues.IsFinite(viewport.Bottom) ||
                !SvgValues.IsFinite(sourceWidth) || !SvgValues.IsFinite(sourceHeight) ||
                sourceWidth <= 0f || sourceHeight <= 0f || viewport.Width <= 0f || viewport.Height <= 0f)
            {
                return SKRect.Empty;
            }
            if (align == ParAlign.None)
            {
                return viewport;
            }

            float scaleX = viewport.Width / sourceWidth;
            float scaleY = viewport.Height / sourceHeight;
            float scale = meet == ParMeet.Slice
                ? System.Math.Max(scaleX, scaleY)
                : System.Math.Min(scaleX, scaleY);
            float width = sourceWidth * scale;
            float height = sourceHeight * scale;
            if (!SvgValues.IsFinite(scaleX) || !SvgValues.IsFinite(scaleY) ||
                !SvgValues.IsFinite(scale) || !SvgValues.IsFinite(width) ||
                !SvgValues.IsFinite(height) || width <= 0f || height <= 0f)
                return SKRect.Empty;
            float x = viewport.Left;
            float y = viewport.Top;

            if ((align & ParAlign.XMid) != 0) x += (viewport.Width - width) / 2f;
            else if ((align & ParAlign.XMax) != 0) x += viewport.Width - width;
            if ((align & ParAlign.YMid) != 0) y += (viewport.Height - height) / 2f;
            else if ((align & ParAlign.YMax) != 0) y += viewport.Height - height;

            if (!SvgValues.IsFinite(x) || !SvgValues.IsFinite(y) ||
                !SvgValues.IsFinite(x + width) || !SvgValues.IsFinite(y + height))
                return SKRect.Empty;
            return new SKRect(x, y, x + width, y + height);
        }

        /// <summary>
        /// Strict data-URI byte extraction: requires an explicit base64 flag,
        /// bounds the payload length, and never throws on malformed input.
        /// </summary>
        internal static byte[] DecodeDataUriBytes(string href, int maxDecodedBytes, out string error)
        {
            error = null;
            // data:[<mime>][;base64],<payload>
            int comma = href.IndexOf(',');
            if (comma < 0)
            {
                error = "image data URI missing payload separator";
                return null;
            }

            var header = href.Substring(5, comma - 5); // skip "data:"
            bool isBase64 = header.EndsWith(";base64", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(header, "base64", System.StringComparison.OrdinalIgnoreCase);
            if (!isBase64)
            {
                error = "only base64 image payloads are supported";
                return null;
            }

            string payload = href.Substring(comma + 1);
            // Base64 expands decoded data by 4/3. Reject from encoded length
            // before allocating the decoded byte array.
            long maxEncodedChars = ((long)maxDecodedBytes + 2L) / 3L * 4L;
            if (payload.Length > maxEncodedChars)
            {
                error = "image payload exceeds admission budget";
                return null;
            }

            try
            {
                return Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                error = "image payload is not valid base64";
                return null;
            }
        }

        private static bool IsSvgDataUri(string href)
        {
            int comma = href.IndexOf(',');
            if (comma < 5) return false;
            var header = href.AsSpan(5, comma - 5);
            int semicolon = header.IndexOf(';');
            var mediaType = (semicolon < 0 ? header : header.Slice(0, semicolon)).Trim();
            return mediaType.Equals("image/svg+xml".AsSpan(), System.StringComparison.OrdinalIgnoreCase);
        }

    }
}
