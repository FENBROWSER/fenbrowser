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
                        DrawShape(el, canvas, viewport, inherited);
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
                        DrawImageElement(el, canvas, viewport, inherited);
                        return;
                    }
                case "text":
                    {
                        if (IsDisplayNone(el)) return;
                        using var textScope = new CanvasState(canvas);
                        ApplyTransform(el, canvas);
                        ApplyClipPath(el, canvas, viewport, inherited);
                        DrawTextElement(el, canvas, viewport, inherited);
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
            if (_activeUseIds.Contains(id) || _activeUseIds.Count >= _maxReferenceDepth)
            {
                _report.Warn("use reference cycle detected; instance skipped");
                return;
            }

            using var scope = new CanvasState(canvas);
            ApplyTransform(el, canvas);
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
                        shapeEl.GetAttribute("clip-rule") ?? shapeEl.GetAttribute("fill-rule"),
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

        private static void ApplyPathTransform(SvgElement element, SKPath path)
        {
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
                WarnUnsupportedOnce("image external reference");
                return;
            }
            if (IsSvgDataUri(href))
            {
                _report.RequireFallback("embedded SVG image requires compatibility support");
                _report.RejectResource("embedded SVG image rejected from legacy fallback for sandbox isolation");
                return;
            }

            var bytes = DecodeDataUriBytes(href, _maxDecodedImageBytes, out var decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
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

            float x = ResolveCoord(el.GetAttribute("x"), viewport.Width);
            float y = ResolveCoord(el.GetAttribute("y"), viewport.Height);
            float w = ResolveCoord(el.GetAttribute("width"), viewport.Width);
            float h = ResolveCoord(el.GetAttribute("height"), viewport.Height);
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
                var imageViewport = new SKRect(x, y, x + w, y + h);
                var source = new SKRect(0f, 0f, bitmap.Width, bitmap.Height);
                var destination = ResolveImageDestination(
                    imageViewport,
                    bitmap.Width,
                    bitmap.Height,
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
