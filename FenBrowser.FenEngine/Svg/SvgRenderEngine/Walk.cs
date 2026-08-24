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

        // ------------------------------------------------------------ draw walk

        private void DrawChildren(SvgElement container, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
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

        private void DrawElementInner(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            switch (el.Name)
            {
                case "g":
                case "a":
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
                case "symbol":
                case "marker":
                case "pattern":
                case "linearGradient":
                case "radialGradient":
                case "stop":
                case "filter":
                case "mask":
                case "clipPath":
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
                case "foreignObject":
                case "animation":
                case "animate":
                case "animateTransform":
                case "animateMotion":
                case "set":
                    WarnUnsupportedOnce(el.Name);
                    return;
                default:
                    WarnUnknownOnce(el.Name);
                    return; // Unknown elements are never rendered; subtree skipped.
            }
        }

        private void DrawNestedSvg(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext outer,
            InheritedStyle inherited)
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
            float w = hasCssWidth
                ? cssWidth
                : string.IsNullOrWhiteSpace(widthAttribute)
                    ? outer.Width
                    : ResolveViewportLength(widthAttribute, outer.Width);
            float h = hasCssHeight
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
                out float vbX, out float vbY, out float vbW, out float vbH);

            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, outer, inherited);
            DrawWithEffects(el, canvas, inner, () =>
            {
                if (TryBeginGroupOpacity(el, canvas, out var layerPaint))
                {
                    try
                    {
                        DrawNestedSvgBody(el, canvas, inner, inherited, hasViewBox, vbX, vbY, vbW, vbH);
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
                    DrawNestedSvgBody(el, canvas, inner, inherited, hasViewBox, vbX, vbY, vbW, vbH);
                }
            });
        }

        /// <summary>
        /// Resolves a cascaded CSS width/height declaration on a nested &lt;svg&gt;
        /// when it is one of the supported viewport-dependent forms. Returns false
        /// for every other value (including valid plain lengths and percentages),
        /// leaving XML attribute/default sizing in charge. Negative results are
        /// invalid per spec and likewise return false.
        /// </summary>
        private static bool TryResolveNestedSvgCssSizing(
            SvgElement element,
            string property,
            float viewportWidth,
            float viewportHeight,
            out float value)
        {
            value = 0f;
            if (element.CascadedDeclarations == null ||
                !element.CascadedDeclarations.ContainsKey(property))
                return false;
            string resolved = element.GetPresentationProperty(property);
            if (string.IsNullOrWhiteSpace(resolved) ||
                resolved.Length > SvgMarkupParser.MaxAttributeValueChars)
                return false;
            string trimmed = resolved.Trim();
            float percentReference = property == "width" ? viewportWidth : viewportHeight;

            if (SvgCssLengthEvaluator.IsNestedSvgSizingKeyword(trimmed))
            {
                // Fill-available sizing: an SVG viewport has no intrinsic size,
                // so stretch/fit-content/min-content/max-content all resolve to
                // the containing viewport extent.
                value = percentReference;
                return true;
            }

            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            if (trimmed.StartsWith("calc-size(", System.StringComparison.OrdinalIgnoreCase))
            {
                return SvgCssLengthEvaluator.TryEvaluateCalcSize(
                           trimmed, percentReference, fontSize, rootFontSize,
                           viewportWidth, viewportHeight, out value) &&
                       value >= 0f;
            }

            if (!SvgCssLengthEvaluator.HasViewportUnitDimension(trimmed))
                return false;
            return SvgCssLengthEvaluator.TryEvaluate(
                       trimmed, percentReference, fontSize, rootFontSize,
                       viewportWidth, viewportHeight, out value) &&
                   value >= 0f;
        }

        private void DrawNestedSvgBody(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext inner,
            InheritedStyle inherited,
            bool hasViewBox,
            float vbX, float vbY, float vbW, float vbH)
        {
            canvas.ClipRect(new SKRect(0f, 0f, inner.Width, inner.Height));
            if (hasViewBox)
            {
                ApplyViewportTransform(canvas, inner, hasViewBox, vbX, vbY, vbW, vbH, el.GetAttribute("preserveAspectRatio"));
            }
            var next = inherited.ResolveOverrides(el, _report);
            var userViewport = hasViewBox
                ? new ViewportContext(vbW, vbH)
                : inner;
            DrawChildren(el, canvas, userViewport, next);
        }

        private void DrawSwitch(SvgElement el, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
            // Direct-rendering-child selection: v1 picks the first child that is
            // a known drawable element (required* features are treated as pass).
            var children = el.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                switch (child.Name)
                {
                    case "title":
                    case "desc":
                        continue;
                    case "g":
                    case "a":
                    case "svg":
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
                        DrawElement(child, canvas, viewport, inherited);
                        return;
                    default:
                        continue;
                }
            }
        }

        // ------------------------------------------------------------------ use

        private void DrawUse(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            string href = el.GetAttribute("href") ?? el.GetLookup("xlink:href");
            if (string.IsNullOrEmpty(href) || href[0] != '#')
            {
                // Remote references have no code path here by construction; log
                // and ignore (fail closed).
                _report.RejectResource("use external reference rejected by SVG resource policy");
                return;
            }

            string id = href.Substring(1);
            if (!_doc.ElementsById.TryGetValue(id, out var target))
            {
                return; // Dangling reference: silently nothing (browser behavior).
            }

            // Cycle defense: an id may not instantiate itself transitively.
            _activeUseIds ??= new HashSet<string>(System.StringComparer.Ordinal);
            if (_activeUseIds.Contains(id) || _activeUseIds.Count >= _maxReferenceDepth)
            {
                _report.Warn("use reference cycle detected; instance skipped");
                return;
            }

            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, viewport, inherited);
            ApplyClipPath(el, canvas, viewport, inherited);
            float ux = ResolveCoord(el.GetAttribute("x"), viewport.Width);
            float uy = ResolveCoord(el.GetAttribute("y"), viewport.Height);
            if (ux != 0f || uy != 0f)
            {
                canvas.Translate(ux, uy);
            }

            _activeUseIds.Add(id);
            try
            {
                var next = inherited.ResolveOverrides(el, _report);

                // S8/F8: opacity on <use> composites the instantiated subtree.
                bool layered = TryBeginGroupOpacity(el, canvas, out var useLayer);
                try
                {
                    if (target.Name == "svg")
                    {
                        DrawNestedSvg(target, canvas, viewport, next);
                    }
                    else if (target.Name == "symbol")
                    {
                        // A used symbol establishes an svg-equivalent viewport whose
                        // default width/height is 100% of the referencing viewport.
                        DrawSymbolInstance(target, canvas, viewport, next);
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
            }
            finally
            {
                _activeUseIds.Remove(id);
            }
        }

        private HashSet<string> _activeUseIds;

        private void DrawSymbolInstance(
            SvgElement symbol,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            float w = ResolveViewportLength(symbol.GetAttribute("width"), viewport.Width);
            if (w <= 0f) w = viewport.Width;
            float h = ResolveViewportLength(symbol.GetAttribute("height"), viewport.Height);
            if (h <= 0f) h = viewport.Height;

            var inner = new ViewportContext(w, h);
            bool hasViewBox = TryParseViewBox(
                symbol.GetAttribute("viewBox"),
                out float vbX, out float vbY, out float vbW, out float vbH);

            using var scope = new CanvasState(canvas);
            DrawNestedSvgBody(symbol, canvas, inner, inherited, hasViewBox, vbX, vbY, vbW, vbH);
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
            if (el.CascadedDeclarations != null &&
                el.CascadedDeclarations.TryGetValue("transform", out string css) &&
                !string.IsNullOrWhiteSpace(css))
            {
                // GetPresentationProperty applies custom-property substitution.
                ApplyCssTransform(el, el.GetPresentationProperty("transform"), canvas, viewport, inherited);
                return;
            }
            ApplyAttributeTransform(el, canvas, viewport, inherited);
        }

        private void ApplyCssZoom(SvgElement element, SKCanvas canvas)
        {
            string raw = element.GetPresentationProperty("zoom");
            if (string.IsNullOrWhiteSpace(raw)) return;
            string value = raw.Trim();
            if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) return;

            float zoom;
            if (value.EndsWith("%", StringComparison.Ordinal))
            {
                if (!SvgValues.TryParseNumber(value.AsSpan(0, value.Length - 1), out zoom)) return;
                zoom *= 0.01f;
            }
            else if (!SvgValues.TryParseNumber(value.AsSpan(), out zoom))
            {
                if (value.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("min(", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("max(", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase))
                    _report.RequireFallback("SVG CSS zoom math requires compatibility fallback");
                return;
            }

            if (!float.IsFinite(zoom) || zoom < 0f) return;
            if (zoom > 4096f)
            {
                _report.RequireFallback("SVG CSS zoom exceeds the bounded scale limit");
                return;
            }
            if (zoom != 1f)
                canvas.Concat(SKMatrix.CreateScale(zoom, zoom));
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

        /// <summary>
        /// Applies the element's clip-path attribute, if any. Must be called
        /// inside a CanvasState scope so the clip unwinds with the element.
        /// Supported: userSpaceOnUse (default) and objectBoundingBox units for
        /// shapes; direct shape children of &lt;clipPath&gt; (plus one level of
        /// use-to-shape). Everything else degrades to "no clip" with a warning.
        /// </summary>
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
                return;
            }
            if (kind != SvgValues.PaintKind.ServerRef || fragment == null)
            {
                // none / remote reference: nothing to apply (remote is fail-closed).
                return;
            }
            if (!_doc.ElementsById.TryGetValue(fragment, out var clipEl) ||
                clipEl.Name != "clipPath")
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
                    clipEl.GetAttribute("clipPathUnits"),
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
                            return bounds.Width > 0f && bounds.Height > 0f;
                        }
                    }
                    break;
                case "image":
                    float x = ResolveCoord(element.GetAttribute("x"), viewport.Width);
                    float y = ResolveCoord(element.GetAttribute("y"), viewport.Height);
                    float width = ResolveCoord(element.GetAttribute("width"), viewport.Width);
                    float height = ResolveCoord(element.GetAttribute("height"), viewport.Height);
                    if (width > 0f && height > 0f)
                    {
                        bounds = new SKRect(x, y, x + width, y + height);
                        return true;
                    }
                    break;
                case "svg":
                    bounds = new SKRect(0f, 0f, viewport.Width, viewport.Height);
                    return true;
            }

            bounds = default;
            return false;
        }

        /// <summary>Union of direct shape children (plus one-level use refs).</summary>
        private SKPath BuildClipGeometry(SvgElement clipEl, ViewportContext viewport)
        {
            using var combinedBuilder = new SKPathBuilder();
            bool hasGeometry = false;
            bool evenOdd = false;

            foreach (var child in clipEl.Children)
            {
                SvgElement shapeEl = child;
                if (child.Name == "use")
                {
                    var href = child.GetAttribute("href") ?? child.GetLookup("xlink:href");
                    if (string.IsNullOrEmpty(href) || href.Length < 2 || href[0] != '#' ||
                        !_doc.ElementsById.TryGetValue(href.Substring(1), out var target))
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
                    float useX = ResolveCoord(child.GetAttribute("x"), viewport.Width);
                    float useY = ResolveCoord(child.GetAttribute("y"), viewport.Height);
                    if (useX != 0f || useY != 0f)
                    {
                        childPath.Transform(SKMatrix.CreateTranslation(useX, useY));
                    }
                    ApplyPathTransform(child, childPath);
                }
                combinedBuilder.AddPath(childPath, SKPathAddMode.Append);
                hasGeometry = true;

                if (string.Equals(
                        shapeEl.GetPresentationProperty("clip-rule") ?? shapeEl.GetPresentationProperty("fill-rule"),
                        "evenodd",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    evenOdd = true;
                }
            }

            if (!hasGeometry)
            {
                return null;
            }

            var combined = combinedBuilder.Detach();
            ApplyPathTransform(clipEl, combined);
            if (evenOdd)
            {
                combined.FillType = SKPathFillType.EvenOdd;
            }
            return combined;
        }

        private void ApplyPathTransform(SvgElement element, SKPath path)
        {
            if (element.CascadedDeclarations != null &&
                element.CascadedDeclarations.TryGetValue("transform", out string css) &&
                !string.IsNullOrWhiteSpace(css))
            {
                // CSS transforms do apply to clipPath children in browsers;
                // ignoring them here would misclip silently.
                _report.RequireFallback(
                    "SVG CSS transform on clipPath content requires compatibility fallback");
                return;
            }
            var transformText = element.GetAttribute("transform");
            if (!string.IsNullOrWhiteSpace(transformText) &&
                SvgValues.TryParseTransformList(transformText.AsSpan(), out var transform))
            {
                path.Transform(transform);
            }
        }

        // --------------------------------------------------------------- images

        /// <summary>
        /// Renders raster images embedded as data: URIs. SECURITY: this is the
        /// ONLY image source - there is no fetch code path here, so external
        /// hrefs are logged and ignored (fail closed). Decoded pixel counts are
        /// bounded by MaxRasterPixels before drawing.
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
                DrawResolvedImage(el, canvas, viewport, inherited, href);
                return;
            }
            if (IsSvgDataUri(href))
            {
                DrawEmbeddedSvgImage(el, canvas, viewport, inherited, href);
                return;
            }

            var bytes = DecodeDataUriBytes(href, _maxDecodedImageBytes, out var decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
                return;
            }
            if (!_resourceBudget.TryAdmit(bytes.Length))
            {
                _report.RejectResource("embedded raster image cumulative resource budget exceeded");
                return;
            }

            if (!TryDecodeEmbeddedBitmap(
                    bytes,
                    _maxRasterPixels,
                    _maxRasterDim,
                    out var bitmap,
                    out var bitmapError))
            {
                _report.RejectResource(bitmapError);
                return;
            }

            DrawOwnedBitmap(el, canvas, viewport, inherited, bitmap);
        }

        private long _maxRasterPixels = 16L * 1024 * 1024;
        private int _maxDecodedImageBytes = 8 * 1024 * 1024;
        private int _maxRasterDim = 8192;

        /// <summary>Called from TryRender with caller limits (keeps caps in sync).</summary>
        internal void ConfigureImageBudgets(long maxRasterPixels, int maxDecodedImageBytes, int maxRasterDim)
        {
            _maxRasterPixels = maxRasterPixels;
            _maxDecodedImageBytes = maxDecodedImageBytes;
            _maxRasterDim = maxRasterDim;
        }

        private void DrawEmbeddedSvgImage(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            string href)
        {
            byte[] bytes = DecodeSvgDataUriBytes(href, _maxDecodedImageBytes, out string decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
                return;
            }

            if (!_resourceBudget.TryAdmit(bytes.Length))
            {
                _report.RejectResource("embedded SVG cumulative byte budget exceeded");
                return;
            }

            DrawSvgImageBytes(element, canvas, viewport, inherited, bytes, _baseUri);
        }

        private void DrawSvgImageBytes(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            byte[] bytes,
            Uri resourceUri)
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

            if (!TryRenderInternal(
                    source, _limits, _resourceBudget, _resourceDepth + 1,
                    resourceUri, _resourceResolver,
                    out var picture, out float sourceWidth, out float sourceHeight,
                    out string nestedError, out _, out _, out _,
                    out bool requiresFallback, out bool resourceRejected))
            {
                _report.RejectResource(
                    "embedded SVG failed bounded first-party rendering: " +
                    (string.IsNullOrWhiteSpace(nestedError) ? "unknown nested failure" : nestedError));
                return;
            }

            using (picture)
            {
                if (requiresFallback || resourceRejected)
                {
                    _report.RejectResource(
                        requiresFallback
                            ? "embedded SVG requires unsupported compatibility rendering"
                            : "embedded SVG contained a rejected nested resource");
                    return;
                }

                float x = ResolveCoord(element.GetPresentationProperty("x"), viewport.Width);
                float y = ResolveCoord(element.GetPresentationProperty("y"), viewport.Height);
                float width = ResolveCoord(element.GetPresentationProperty("width"), viewport.Width);
                float height = ResolveCoord(element.GetPresentationProperty("height"), viewport.Height);
                if (width <= 0f) width = sourceWidth;
                if (height <= 0f) height = sourceHeight;
                if (width <= 0f || height <= 0f || sourceWidth <= 0f || sourceHeight <= 0f) return;

                var style = inherited.ResolveOverrides(element, _report);
                if (!style.Visibility) return;
                bool layered = TryBeginGroupOpacity(element, canvas, out var layerPaint);
                try
                {
                    using var state = new CanvasState(canvas);
                    var imageViewport = new SKRect(x, y, x + width, y + height);
                    var destination = ResolveImageDestination(
                        imageViewport, sourceWidth, sourceHeight,
                        element.GetAttribute("preserveAspectRatio"));
                    canvas.ClipRect(imageViewport);
                    canvas.Translate(destination.Left, destination.Top);
                    canvas.Scale(destination.Width / sourceWidth, destination.Height / sourceHeight);
                    canvas.DrawPicture(picture);
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

        private void DrawResolvedImage(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            string reference)
        {
            if (!TryResolveResource(reference, SvgResourceKind.Image, out var resource)) return;
            byte[] bytes = resource.Content.ToArray();
            if (!_resourceBudget.TryAdmit(bytes.Length))
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
                DrawSvgImageBytes(element, canvas, viewport, inherited, bytes, resource.Uri);
                return;
            }

            if (!TryDecodeEmbeddedBitmap(
                    bytes, _maxRasterPixels, _maxRasterDim,
                    out var bitmap, out var bitmapError))
            {
                _report.RejectResource(bitmapError);
                return;
            }
            DrawOwnedBitmap(element, canvas, viewport, inherited, bitmap);
        }

        private bool TryResolveResource(
            string reference,
            SvgResourceKind kind,
            out SvgResolvedResource resource)
        {
            resource = default;
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
            if (resource.Content.Length <= 0 || resource.Content.Length > _maxDecodedImageBytes)
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

        private void DrawOwnedBitmap(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited,
            SKBitmap bitmap)
        {
            float x = ResolveCoord(el.GetPresentationProperty("x"), viewport.Width);
            float y = ResolveCoord(el.GetPresentationProperty("y"), viewport.Height);
            float w = ResolveCoord(el.GetPresentationProperty("width"), viewport.Width);
            float h = ResolveCoord(el.GetPresentationProperty("height"), viewport.Height);
            if (w <= 0f) w = bitmap.Width;
            if (h <= 0f) h = bitmap.Height;

            var style = inherited.ResolveOverrides(el, _report);
            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                using var state = new CanvasState(canvas);
                using var paint = new SKPaint { IsAntialias = true };
                paint.Color = style.Visibility ? SKColors.Black : SKColors.Transparent;
                var imageViewport = new SKRect(x, y, x + w, y + h);
                var source = new SKRect(0f, 0f, bitmap.Width, bitmap.Height);
                var destination = ResolveImageDestination(
                    imageViewport, bitmap.Width, bitmap.Height,
                    el.GetAttribute("preserveAspectRatio"));
                canvas.ClipRect(imageViewport);
                canvas.DrawBitmap(bitmap, source, destination, SKSamplingOptions.Default, paint);
            }
            finally
            {
                if (layered)
                {
                    canvas.Restore();
                    _activeLayers--;
                    layerPaint.Dispose();
                }
                bitmap.Dispose();
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
            if (align == ParAlign.None || sourceWidth <= 0f || sourceHeight <= 0f)
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
            float x = viewport.Left;
            float y = viewport.Top;

            if ((align & ParAlign.XMid) != 0) x += (viewport.Width - width) / 2f;
            else if ((align & ParAlign.XMax) != 0) x += viewport.Width - width;
            if ((align & ParAlign.YMid) != 0) y += (viewport.Height - height) / 2f;
            else if ((align & ParAlign.YMax) != 0) y += viewport.Height - height;

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
