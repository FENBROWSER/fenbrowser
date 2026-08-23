using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                        ApplyTransform(el, canvas);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        var next = inherited.ResolveOverrides(el, _report);
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
                        return;
                    }
                case "svg":
                    {
                        if (IsDisplayNone(el)) return;
                        DrawNestedSvg(el, canvas, viewport, inherited);
                        return;
                    }
                case "defs":
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
                        ApplyTransform(el, canvas);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        DrawShape(el, canvas, inherited);
                        return;
                    }
                case "image":
                    {
                        if (IsDisplayNone(el)) return;
                        // Same spec order as shapes: save -> transform -> clip
                        // -> draw (DrawImageElement adds its own opacity layer).
                        using var imgScope = new CanvasState(canvas);
                        ApplyTransform(el, canvas);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        DrawImageElement(el, canvas, inherited);
                        return;
                    }
                case "text":
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
            float w = ResolveViewportLength(el.GetAttribute("width"), outer.Width);
            float h = ResolveViewportLength(el.GetAttribute("height"), outer.Height);
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
            ApplyTransform(el, canvas);

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
            DrawChildren(el, canvas, inner, next);
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
                _report.UnsupportedFeatureIgnored = true;
                _report.Warn("use href ignored (external references are sandboxed off)");
                return;
            }

            string id = href.Substring(1);
            if (!_doc.ElementsById.TryGetValue(id, out var target))
            {
                return; // Dangling reference: silently nothing (browser behavior).
            }

            // Cycle defense: an id may not instantiate itself transitively.
            _activeUseIds ??= new HashSet<string>(System.StringComparer.Ordinal);
            if (_activeUseIds.Contains(id) || _activeUseIds.Count >= 32)
            {
                _report.Warn("use reference cycle detected; instance skipped");
                return;
            }

            using var scope = new CanvasState(canvas);
            ApplyTransform(el, canvas);
            ApplyClipPath(el, canvas, viewport, inherited);
            float ux = ResolveCoord(el.GetAttribute("x"));
            float uy = ResolveCoord(el.GetAttribute("y"));
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

        private static void ApplyTransform(SvgElement el, SKCanvas canvas)
        {
            var t = el.GetAttribute("transform");
            if (string.IsNullOrWhiteSpace(t))
            {
                return;
            }
            if (SvgValues.TryParseTransformList(t.AsSpan(), out var matrix) && !matrix.IsIdentity)
            {
                canvas.Concat(matrix);
            }
        }

        private static bool IsDisplayNone(SvgElement el)
        {
            return string.Equals(el.GetAttribute("display"), "none", System.StringComparison.OrdinalIgnoreCase);
        }

        private bool TryBeginGroupOpacity(SvgElement el, SKCanvas canvas, out SKPaint layerPaint)
        {
            float opacity = ReadClampedOpacity(el, "opacity", 1f);
            if (opacity >= 1f || _activeLayers >= MaxActiveLayers)
            {
                // S1/F1: every layer allocates a full-viewport surface; beyond
                // the cap we composite directly (slight fidelity loss for
                // pathological nesting instead of a multi-GB allocation).
                if (_activeLayers >= MaxActiveLayers && opacity < 1f)
                {
                    _report.Warn("layer budget exceeded; group composited without isolation");
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
            _report.UnsupportedFeatureIgnored = true;
            _report.Warn($"unsupported SVG feature '{feature}' ignored");
        }

        private void WarnUnknownOnce(string name)
        {
            _report.UnsupportedFeatureIgnored = true;
            _report.Warn($"unknown element '{name}' skipped");
        }

        // ------------------------------------------------------------ clipping

        /// <summary>Ids currently being resolved as clip paths (cycle guard).</summary>
        private HashSet<string> _activeClipIds;

        private const int MaxClipDepth = 8;

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
            var raw = el.GetAttribute("clip-path");
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
            if (_activeClipIds.Contains(fragment) || _activeClipIds.Count >= MaxClipDepth)
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

                using var clipPath = BuildClipGeometry(clipEl);
                if (clipPath == null || clipPath.IsEmpty)
                {
                    // Empty clip path hides the element entirely (spec).
                    canvas.ClipRect(new SKRect(0f, 0f, 0f, 0f));
                    return;
                }

                if (isObjectBoundingBox)
                {
                    var bbox = el.Name == "g" || el.Name == "a"
                        ? new SKRect(0f, 0f, viewport.Width, viewport.Height)
                        : SKRect.Create(1, 1); // shape case resolves below via geometry bounds fallback
                    // For shapes we approximate oBB by the current viewport box
                    // when no geometry exists yet; exact per-shape bbox mapping
                    // happens through BuildClipGeometryForShape in DrawShape.
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

        /// <summary>Union of direct shape children (plus one-level use refs).</summary>
        private SKPath BuildClipGeometry(SvgElement clipEl)
        {
            SKPath combined = null;
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

                var childPath = BuildGeometry(shapeEl);
                if (childPath == null)
                {
                    continue;
                }

                combined ??= new SKPath();
                combined.AddPath(childPath);
                childPath.Dispose();

                if (string.Equals(
                        shapeEl.GetAttribute("clip-rule") ?? shapeEl.GetAttribute("fill-rule"),
                        "evenodd",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    evenOdd = true;
                }
            }

            if (combined != null && evenOdd)
            {
                combined.FillType = SKPathFillType.EvenOdd;
            }
            return combined;
        }

        // --------------------------------------------------------------- images

        /// <summary>
        /// Renders raster images embedded as data: URIs. SECURITY: this is the
        /// ONLY image source - there is no fetch code path here, so external
        /// hrefs are logged and ignored (fail closed). Decoded pixel counts are
        /// bounded by MaxRasterPixels before drawing.
        /// </summary>
        private void DrawImageElement(SvgElement el, SKCanvas canvas, InheritedStyle inherited)
        {
            var href = el.GetAttribute("href") ?? el.GetLookup("xlink:href");
            if (string.IsNullOrEmpty(href))
            {
                return; // No source: renders nothing (spec).
            }
            if (!href.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
            {
                WarnUnsupportedOnce("image external reference");
                return;
            }

            var bytes = DecodeDataUriBytes(href, out var decodeError);
            if (bytes == null)
            {
                _report.Warn(decodeError);
                return;
            }

            SKBitmap bitmap;
            try
            {
                bitmap = SKBitmap.Decode(bytes);
            }
            catch (Exception)
            {
                bitmap = null; // Codec failure: treat as undecodable.
            }

            if (bitmap == null)
            {
                _report.Warn("image data URI failed to decode");
                return;
            }

            long pixels = (long)bitmap.Width * bitmap.Height;
            if (bitmap.Width > _maxRasterDim || bitmap.Height > _maxRasterDim || pixels > _maxRasterPixels)
            {
                bitmap.Dispose();
                _report.Warn("image decoded size exceeds raster budget; rejected");
                return;
            }

            float x = ResolveCoord(el.GetAttribute("x"));
            float y = ResolveCoord(el.GetAttribute("y"));
            float w = ResolveCoord(el.GetAttribute("width"));
            float h = ResolveCoord(el.GetAttribute("height"));
            if (w <= 0f) w = bitmap.Width;
            if (h <= 0f) h = bitmap.Height;

            var style = inherited.ResolveOverrides(el, _report);
            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                using var paint = new SKPaint { IsAntialias = true };
                paint.Color = style.Visibility
                    ? SKColors.Black
                    : SKColors.Transparent;
                var dest = new SKRect(x, y, x + w, y + h);
                canvas.DrawBitmap(bitmap, dest, paint);
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

        private long _maxRasterPixels = 16L * 1024 * 1024;
        private int _maxRasterDim = 8192;

        /// <summary>Called from TryRender with caller limits (keeps caps in sync).</summary>
        internal void ConfigureImageBudgets(long maxRasterPixels, int maxRasterDim)
        {
            _maxRasterPixels = maxRasterPixels;
            _maxRasterDim = maxRasterDim;
        }

        /// <summary>
        /// Strict data-URI byte extraction: requires an explicit base64 flag,
        /// bounds the payload length, and never throws on malformed input.
        /// </summary>
        internal static byte[] DecodeDataUriBytes(string href, out string error)
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
            if (payload.Length > 12 * 1024 * 1024)
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

    }
}
