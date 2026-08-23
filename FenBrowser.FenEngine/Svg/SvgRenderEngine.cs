using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// First-party SVG rendering pipeline: parsed tree -> styled draw -> SKPicture.
    ///
    /// Stage order follows engine convention:
    /// 1. source/DOM (sandboxed parser)
    /// 2. geometry (viewport, viewBox, transforms, shapes, paths)
    /// 3. paint (fills, strokes, gradients, group opacity)
    /// 4. raster preparation happens in <see cref="Adapters.FenSvgRenderer"/>.
    ///
    /// Threading: one engine instance PER RENDER CALL. Nothing static is mutated,
    /// so concurrent renders are safe.
    /// </summary>
    internal sealed class SvgRenderEngine
    {
        private readonly SvgParsedDocument _doc;
        private readonly SvgRenderLimits _limits;
        private readonly SvgParseReport _report;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _deadlineMs;
        private int _elementsVisited;

        private const int TimeCheckMask = 0x3F; // check every 64 elements
        private const float DefaultFontSize = 16f;

        private SvgRenderEngine(SvgParsedDocument doc, SvgRenderLimits limits)
        {
            _doc = doc;
            _limits = limits;
            _report = doc.Report;
            _deadlineMs = limits.MaxRenderTimeMs > 0 ? limits.MaxRenderTimeMs : long.MaxValue;
        }

        public static bool TryRender(
            string source,
            SvgRenderLimits limits,
            out SKPicture picture,
            out float width,
            out float height,
            out string error)
        {
            picture = null;
            width = 0f;
            height = 0f;
            error = null;

            if (!SvgMarkupParser.TryParse(source, limits, out var doc, out var fatalReason))
            {
                error = fatalReason;
                return false;
            }

            var engine = new SvgRenderEngine(doc, limits);
            try
            {
                engine.RenderRoot(out picture, out width, out height);
                return true;
            }
            catch (SvgTimeBudgetExceededException)
            {
                picture = null;
                error = $"SVG render exceeded time limit ({limits.MaxRenderTimeMs}ms, size={source.Length / 1024}KB)";
                return false;
            }
        }

        private void CheckTime()
        {
            if ((_elementsVisited & TimeCheckMask) == 0 && _clock.ElapsedMilliseconds > _deadlineMs)
            {
                throw new SvgTimeBudgetExceededException();
            }
        }

        // -------------------------------------------------------------- viewport

        private void RenderRoot(out SKPicture picture, out float width, out float height)
        {
            var root = _doc.Root;

            width = ResolveViewportLength(root.GetAttribute("width"), 0f);
            height = ResolveViewportLength(root.GetAttribute("height"), 0f);

            bool hasViewBox = TryParseViewBox(
                root.GetAttribute("viewBox"),
                out float vbX, out float vbY, out float vbW, out float vbH);

            // Intrinsic sizing: explicit px-ish lengths win; otherwise derive from
            // viewBox (legacy adapter parity); otherwise CSS replaced-element
            // default 300x150 keeps output deterministic.
            bool sizeComplete = width > 0f && height > 0f;
            if (!sizeComplete && hasViewBox)
            {
                if (width <= 0f) width = vbW;
                if (height <= 0f) height = vbH;
            }
            if (width <= 0f || !SvgValues.IsFinite(width)) width = 300f;
            if (height <= 0f || !SvgValues.IsFinite(height)) height = 150f;

            // Absolute upper bound independent of caller limits: a viewport this
            // large can never pass raster admission below anyway.
            width = System.Math.Min(width, 32767f);
            height = System.Math.Min(height, 32767f);

            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0f, 0f, width, height));

            var viewport = new ViewportContext(width, height);
            var inherited = new InheritedStyle();

            // The root <svg> participates in inheritance (e.g. fill="blue" on the
            // root cascades to children) - legacy adapter behavior preserved.
            var rootStyle = inherited.ResolveOverrides(root, _report);

            using (new CanvasState(canvas))
            {
                // Clip FIRST, under the identity CTM, so the clip is the raw
                // device viewport. Clipping after the viewBox transform would
                // transform the clip rectangle too (e.g. a large-negative
                // vbY would push the clip entirely off-content).
                canvas.ClipRect(new SKRect(0f, 0f, width, height));
                ApplyViewportTransform(canvas, viewport, hasViewBox, vbX, vbY, vbW, vbH, root.GetAttribute("preserveAspectRatio"));
                if (!rootStyle.Visibility)
                {
                    picture = recorder.EndRecording();
                    return;
                }
                DrawChildren(root, canvas, viewport, rootStyle);
            }

            picture = recorder.EndRecording();
        }

        private readonly struct ViewportContext
        {
            public ViewportContext(float w, float h)
            {
                Width = w;
                Height = h;
            }

            public float Width { get; }
            public float Height { get; }
        }

        private static void ApplyViewportTransform(
            SKCanvas canvas,
            ViewportContext viewport,
            bool hasViewBox,
            float vbX, float vbY, float vbW, float vbH,
            string parText)
        {
            if (!hasViewBox)
            {
                return;
            }

            ParsePreserveAspectRatio(parText, out ParAlign align, out ParMeet meet);

            if (align == ParAlign.None)
            {
                // Stretched to fill: independent scale factors, no letterboxing.
                canvas.Scale(viewport.Width / vbW, viewport.Height / vbH);
                canvas.Translate(-vbX, -vbY);
                return;
            }

            float scale = meet == ParMeet.Slice
                ? System.Math.Max(viewport.Width / vbW, viewport.Height / vbH)
                : System.Math.Min(viewport.Width / vbW, viewport.Height / vbH);

            float tx = 0f;
            float ty = 0f;
            float leftoverX = viewport.Width - vbW * scale;
            float leftoverY = viewport.Height - vbH * scale;

            if ((align & ParAlign.XMid) != 0) tx = leftoverX / 2f;
            else if ((align & ParAlign.XMax) != 0) tx = leftoverX;

            if ((align & ParAlign.YMid) != 0) ty = leftoverY / 2f;
            else if ((align & ParAlign.YMax) != 0) ty = leftoverY;

            canvas.Translate(tx, ty);
            canvas.Scale(scale, scale);
            canvas.Translate(-vbX, -vbY);
        }

        [System.Flags]
        private enum ParAlign { XMin = 0, XMid = 1, XMax = 2, YMin = 0, YMid = 4, YMax = 8, None = 16 }

        private enum ParMeet { Meet, Slice }

        private static void ParsePreserveAspectRatio(string text, out ParAlign align, out ParMeet meet)
        {
            // Defaults per spec: xMidYMid meet. Malformed input falls back to
            // these defaults rather than failing the document.
            align = ParAlign.XMid | ParAlign.YMid;
            meet = ParMeet.Meet;

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var parts = text.Trim().Split(new[] { ' ', ',', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var p = raw.ToLowerInvariant();
                if (p == "none") { align = ParAlign.None; continue; }
                if (p == "meet") { meet = ParMeet.Meet; continue; }
                if (p == "slice") { meet = ParMeet.Slice; continue; }
                if (p.Length < 6)
                {
                    continue;
                }

                // Alignment tokens are two 3-char words, e.g. "xMidYMid".
                if (TryMapParComponent(p.Substring(0, 3), isHorizontal: true, out var horizontal) &&
                    TryMapParComponent(p.Substring(3, 3), isHorizontal: false, out var vertical))
                {
                    align = horizontal | vertical;
                }
            }
        }

        private static bool TryMapParComponent(string word, bool isHorizontal, out ParAlign value)
        {
            switch (word)
            {
                case "min":
                    value = 0;
                    return true;
                case "mid":
                    value = isHorizontal ? ParAlign.XMid : ParAlign.YMid;
                    return true;
                case "max":
                    value = isHorizontal ? ParAlign.XMax : ParAlign.YMax;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        private static bool TryParseViewBox(string text, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var tok = SvgValues.CreateTokenizer(text.AsSpan());
            Span<float> vb = stackalloc float[4];
            int count = 0;
            while (tok.Next(out var token))
            {
                if (count >= 4 || !SvgValues.TryParseNumber(token, out float num))
                {
                    return false;
                }
                vb[count++] = num;
            }
            if (count != 4)
            {
                return false;
            }

            x = vb[0];
            y = vb[1];
            w = vb[2];
            h = vb[3];

            // Non-positive dimensions disable the viewport scaling entirely
            // (spec: element renders nothing scaled; never divide by zero).
            if (w <= 0f || h <= 0f || !SvgValues.IsFinite(w) || !SvgValues.IsFinite(h))
            {
                return false;
            }
            return true;
        }

        private float ResolveViewportLength(string raw, float parentDim)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0f;
            }
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit))
            {
                return 0f;
            }
            if (value < 0f)
            {
                return 0f; // Negative viewport lengths are ignored (not errors).
            }
            if (unit == SvgValues.SvgUnit.Percent)
            {
                return parentDim > 0f ? value * 0.01f * parentDim : 0f;
            }
            if (unit == SvgValues.SvgUnit.Em || unit == SvgValues.SvgUnit.Ex)
            {
                return 0f; // Font-relative intrinsic sizes unsupported at viewport level.
            }
            return SvgValues.ResolveUnits(value, unit, DefaultFontSize, 1f);
        }

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

            switch (el.Name)
            {
                case "g":
                case "a":
                    {
                        if (IsDisplayNone(el)) return;
                        using var scope = new CanvasState(canvas);
                        ApplyTransform(el, canvas);
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
                        DrawShape(el, canvas, inherited);
                        return;
                    }
                case "image":
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

        private void DrawShape(SvgElement el, SKCanvas canvas, InheritedStyle inherited)
        {
            var style = inherited.ResolveOverrides(el, _report);
            if (!style.Visibility)
            {
                return; // visibility:hidden suppresses this shape (children may override).
            }

            using var path = BuildGeometry(el);
            if (path == null || path.IsEmpty)
            {
                return;
            }

            TryApplyFillRule(el, path);

            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                using var fillPaint = BuildFillPaint(el, style, path);
                using var strokePaint = BuildStrokePaint(el, style);

                // Spec paint order: fill first, then stroke.
                if (fillPaint != null)
                {
                    canvas.DrawPath(path, fillPaint);
                }
                if (strokePaint != null)
                {
                    canvas.DrawPath(path, strokePaint);
                }
            }
            finally
            {
                if (layered)
                {
                    canvas.Restore();
                    layerPaint.Dispose();
                }
            }
        }

        private SKPath BuildGeometry(SvgElement el)
        {
            if (el.Name == "path")
            {
                string d = el.GetAttribute("d");
                if (string.IsNullOrEmpty(d))
                {
                    return null;
                }
                if (!SvgPathParser.TryBuildPath(d.AsSpan(), out var parsed, _report))
                {
                    return null;
                }
                if (parsed.IsEmpty)
                {
                    parsed.Dispose();
                    return null;
                }
                return parsed;
            }

            using var builder = new SKPathBuilder();
            bool ok;
            switch (el.Name)
            {
                case "rect":
                    ok = AppendRect(el, builder);
                    break;
                case "circle":
                    ok = AppendCircle(el, builder);
                    break;
                case "ellipse":
                    ok = AppendEllipse(el, builder);
                    break;
                case "line":
                    ok = AppendLine(el, builder);
                    break;
                case "polyline":
                case "polygon":
                    ok = AppendPoints(el, builder, closePolygon: el.Name == "polygon");
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
            {
                return null;
            }

            var path = builder.Detach();
            if (path.IsEmpty)
            {
                path.Dispose();
                return null;
            }
            return path;
        }

        private float Attr(SvgElement el, string name) => ResolveCoord(el.GetAttribute(name));

        private float ResolveCoord(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit))
            {
                return 0f;
            }
            float resolved = SvgValues.ResolveUnits(value, unit, DefaultFontSize, 0f);
            return SvgValues.ClampCoord(resolved);
        }

        private bool AppendRect(SvgElement el, SKPathBuilder path)
        {
            float x = Attr(el, "x");
            float y = Attr(el, "y");
            float w = Attr(el, "width");
            float h = Attr(el, "height");
            if (w <= 0f || h <= 0f)
            {
                return false; // Zero/negative extents disable rendering (spec).
            }

            float rx = Attr(el, "rx");
            float ry = Attr(el, "ry");
            bool hasRx = HasPositiveAttr(el, "rx");
            bool hasRy = HasPositiveAttr(el, "ry");

            if (hasRx && !hasRy) ry = rx;
            else if (hasRy && !hasRx) rx = ry;

            // Clamp radii to half side lengths (spec auto-scaling).
            rx = System.Math.Min(System.Math.Max(rx, 0f), w / 2f);
            ry = System.Math.Min(System.Math.Max(ry, 0f), h / 2f);

            var rect = new SKRect(x, y, x + w, y + h);
            if (rx <= 0f || ry <= 0f)
            {
                path.AddRect(rect);
            }
            else
            {
                path.AddRoundRect(new SKRoundRect(rect, rx, ry));
            }
            return true;
        }

        private bool HasPositiveAttr(SvgElement el, string name)
        {
            var raw = el.GetAttribute(name);
            return !string.IsNullOrWhiteSpace(raw) &&
                   SvgValues.TryParseLength(raw.AsSpan(), out float v, out _) &&
                   v > 0f;
        }

        private bool AppendCircle(SvgElement el, SKPathBuilder path)
        {
            float r = Attr(el, "r");
            if (r <= 0f)
            {
                return false;
            }
            path.AddCircle(Attr(el, "cx"), Attr(el, "cy"), r);
            return true;
        }

        private bool AppendEllipse(SvgElement el, SKPathBuilder path)
        {
            float rx = Attr(el, "rx");
            float ry = Attr(el, "ry");
            if (rx <= 0f || ry <= 0f)
            {
                return false;
            }
            var oval = new SKRect(
                Attr(el, "cx") - rx, Attr(el, "cy") - ry,
                Attr(el, "cx") + rx, Attr(el, "cy") + ry);
            path.AddOval(oval);
            return true;
        }

        private bool AppendLine(SvgElement el, SKPathBuilder path)
        {
            path.MoveTo(Attr(el, "x1"), Attr(el, "y1"));
            path.LineTo(Attr(el, "x2"), Attr(el, "y2"));
            return true;
        }

        private bool AppendPoints(SvgElement el, SKPathBuilder path, bool closePolygon)
        {
            string raw = el.GetAttribute("points");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            // Streaming pair consumption: no token list is materialized, and the
            // emitted-point budget bounds output regardless of input length.
            var tok = SvgValues.CreateTokenizer(raw.AsSpan());
            int emitted = 0;
            while (true)
            {
                if (!tok.Next(out var tx) || !tok.Next(out var ty))
                {
                    break; // Odd trailing coordinate: ignored (browser-style).
                }
                if (emitted >= SvgPathParser.MaxSegments)
                {
                    _report.Warn("points list truncated at segment budget");
                    break;
                }
                if (!SvgValues.TryParseNumber(tx, out float x) ||
                    !SvgValues.TryParseNumber(ty, out float y))
                {
                    break; // Render the valid prefix (browser-style recovery).
                }
                x = SvgValues.ClampCoord(x);
                y = SvgValues.ClampCoord(y);
                if (emitted == 0)
                {
                    path.MoveTo(x, y);
                }
                else
                {
                    path.LineTo(x, y);
                }
                emitted++;
            }

            if (emitted < 1)
            {
                return false;
            }
            if (closePolygon)
            {
                path.Close();
            }
            return true;
        }

        // ---------------------------------------------------------------- paint

        private bool TryApplyFillRule(SvgElement el, SKPath path)
        {
            var rule = el.GetAttribute("fill-rule") ?? el.GetAttribute("clip-rule");
            if (string.Equals(rule, "evenodd", System.StringComparison.OrdinalIgnoreCase))
            {
                path.FillType = SKPathFillType.EvenOdd;
                return true;
            }
            path.FillType = SKPathFillType.Winding;
            return true;
        }

        private SKPaint BuildFillPaint(SvgElement el, InheritedStyle style, SKPath path)
        {
            var spec = style.Fill;
            if (spec.Kind == PaintKind.None)
            {
                return null;
            }

            float opacity = ReadClampedOpacity(el, "fill-opacity", 1f);
            if (opacity <= 0f)
            {
                return null;
            }

            var paint = new SKPaint { IsStroke = false, IsAntialias = true };

            if (spec.Kind == PaintKind.Solid)
            {
                var c = spec.Color;
                byte alpha = (byte)(c.Alpha * opacity);
                if (alpha == 0)
                {
                    paint.Dispose();
                    return null;
                }
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, alpha);
                return paint;
            }

            // ServerRef
            var shader = BuildServerShader(spec.Fragment, path, fallbackText: spec.Fallback, style);
            if (shader == null)
            {
                paint.Dispose();
                return null;
            }
            paint.Shader = shader;
            paint.Color = SKColors.White.WithAlpha((byte)(255 * opacity));
            return paint;
        }

        private SKPaint BuildStrokePaint(SvgElement el, InheritedStyle style)
        {
            var spec = style.Stroke;
            if (spec.Kind == PaintKind.None)
            {
                return null;
            }

            float strokeWidth = style.StrokeWidth;
            if (!(strokeWidth > 0f))
            {
                return null; // Zero/negative disables stroking.
            }

            float opacity = ReadClampedOpacity(el, "stroke-opacity", 1f);
            if (opacity <= 0f)
            {
                return null;
            }

            var paint = new SKPaint
            {
                IsStroke = true,
                IsAntialias = true,
                StrokeWidth = strokeWidth,
                StrokeCap = style.Cap,
                StrokeJoin = style.Join,
                StrokeMiter = style.MiterLimit
            };

            if (style.Dash != null && style.Dash.Length > 0)
            {
                // All-zero dash arrays hang some Skia versions; neutralize.
                bool anyNonZero = false;
                foreach (float d in style.Dash)
                {
                    if (d > 0f) { anyNonZero = true; break; }
                }
                if (anyNonZero)
                {
                    paint.PathEffect = SKPathEffect.CreateDash(style.Dash, style.DashOffset);
                }
            }

            if (spec.Kind == PaintKind.Solid)
            {
                var c = spec.Color;
                byte alpha = (byte)(c.Alpha * opacity);
                if (alpha == 0)
                {
                    paint.Dispose();
                    return null;
                }
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, alpha);
                return paint;
            }

            var shader = BuildServerShader(spec.Fragment, path: null, fallbackText: spec.Fallback, style);
            if (shader == null)
            {
                paint.Dispose();
                return null;
            }
            paint.Shader = shader;
            paint.Color = SKColors.White.WithAlpha((byte)(255 * opacity));
            return paint;
        }

        private float ReadClampedOpacity(SvgElement el, string name, float defaultValue)
        {
            var raw = el.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(raw) || !SvgValues.TryParseNumber(raw.AsSpan(), out float v))
            {
                return defaultValue;
            }
            if (!SvgValues.IsFinite(v)) return defaultValue;
            return System.Math.Clamp(v, 0f, 1f);
        }

        private SKShader BuildServerShader(string fragment, SKPath path, string fallbackText, InheritedStyle style)
        {
            if (fragment == null ||
                !_doc.ElementsById.TryGetValue(fragment, out var server) ||
                (server.Name != "linearGradient" && server.Name != "radialGradient"))
            {
                // Invalid reference: fall back color if provided, else paint none.
                return null;
            }

            var stops = CollectStops(server, style, visited: null);
            if (stops == null || stops.Length == 0)
            {
                return null;
            }

            bool isObjectBoundingBox =
                !string.Equals(server.GetAttribute("gradientUnits"), "userSpaceOnUse", System.StringComparison.Ordinal);

            SKMatrix extra = SKMatrix.Identity;
            var gt = server.GetAttribute("gradientTransform");
            if (!string.IsNullOrWhiteSpace(gt))
            {
                if (!SvgValues.TryParseTransformList(gt.AsSpan(), out extra))
                {
                    return null; // Malformed transform disables the paint server.
                }
            }

            SKShader shader;
            if (server.Name == "linearGradient")
            {
                float x1 = GradientCoord(server, "x1", 0f, horizontal: true);
                float y1 = GradientCoord(server, "y1", 0f, horizontal: false);
                float x2 = GradientCoord(server, "x2", 1f, horizontal: true);
                float y2 = GradientCoord(server, "y2", 0f, horizontal: false);

                if (isObjectBoundingBox)
                {
                    var (matrix, degenerate) = ObjectBoundingBoxMatrix(path, extra);
                    if (degenerate)
                    {
                        return null; // Zero-area bbox disables gradient (spec).
                    }
                    var pts = new[]
                    {
                        new SKPoint(x1, y1),
                        new SKPoint(x2, y2)
                    };
                    shader = SKShader.CreateLinearGradient(
                        pts[0], pts[1],
                        StopsColors(stops), StopsPositions(stops),
                        TileModeOf(server));
                    shader = shader.WithLocalMatrix(matrix);
                }
                else
                {
                    shader = SKShader.CreateLinearGradient(
                        new SKPoint(x1, y1), new SKPoint(x2, y2),
                        StopsColors(stops), StopsPositions(stops),
                        TileModeOf(server));
                    if (!extra.IsIdentity)
                    {
                        shader = shader.WithLocalMatrix(extra);
                    }
                }
            }
            else
            {
                float cx = GradientCoord(server, "cx", 0.5f, horizontal: true);
                float cy = GradientCoord(server, "cy", 0.5f, horizontal: false);
                float r = GradientRadius(server);

                if (r <= 0f)
                {
                    return null; // Degenerate radial paints nothing (spec).
                }

                SKPoint center = new SKPoint(cx, cy);
                SKPoint focus = center;
                var fxRaw = server.GetAttribute("fx");
                var fyRaw = server.GetAttribute("fy");
                if (!string.IsNullOrWhiteSpace(fxRaw) &&
                    SvgValues.TryParseLength(fxRaw.AsSpan(), out float fxv, out var fu))
                {
                    focus.X = SvgValues.ResolveUnits(fxv, fu, DefaultFontSize, 1f);
                }
                if (!string.IsNullOrWhiteSpace(fyRaw) &&
                    SvgValues.TryParseLength(fyRaw.AsSpan(), out float fyv, out var fv))
                {
                    focus.Y = SvgValues.ResolveUnits(fyv, fv, DefaultFontSize, 1f);
                }

                if (isObjectBoundingBox)
                {
                    var (matrix, degenerate) = ObjectBoundingBoxMatrix(path, extra);
                    if (degenerate)
                    {
                        return null;
                    }
                    shader = SKShader.CreateRadialGradient(
                        center, r,
                        StopsColors(stops), StopsPositions(stops),
                        TileModeOf(server));
                    shader = shader.WithLocalMatrix(matrix);
                }
                else
                {
                    shader = SKShader.CreateTwoPointConicalGradient(
                        focus, 0f, center, r,
                        StopsColors(stops), StopsPositions(stops),
                        TileModeOf(server));
                    if (!extra.IsIdentity)
                    {
                        shader = shader.WithLocalMatrix(extra);
                    }
                }
            }

            return shader;
        }

        private (SKMatrix matrix, bool degenerate) ObjectBoundingBoxMatrix(SKPath path, SKMatrix extra)
        {
            if (path == null)
            {
                // Stroked server refs without geometry cannot resolve oBB space.
                return (SKMatrix.Identity, true);
            }

            var bbox = path.TightBounds;
            if (bbox.Width <= 0f || bbox.Height <= 0f ||
                !SvgValues.IsFinite(bbox.Width) || !SvgValues.IsFinite(bbox.Height))
            {
                return (SKMatrix.Identity, true);
            }

            // unit square -> bbox: translate(bx,by) * scale(bw,bh)
            var m = SKMatrix.Concat(
                SKMatrix.CreateTranslation(bbox.Left, bbox.Top),
                SKMatrix.CreateScale(bbox.Width, bbox.Height));
            m = SKMatrix.Concat(m, extra);
            return (m, false);
        }

        private SKShaderTileMode TileModeOf(SvgElement server)
        {
            var spread = server.GetAttribute("spreadMethod");
            if (string.Equals(spread, "repeat", System.StringComparison.OrdinalIgnoreCase))
            {
                return SKShaderTileMode.Repeat;
            }
            if (string.Equals(spread, "reflect", System.StringComparison.OrdinalIgnoreCase))
            {
                return SKShaderTileMode.Mirror;
            }
            return SKShaderTileMode.Clamp; // Default and unrecognized values.
        }

        private float GradientCoord(SvgElement server, string name, float defaultValue, bool horizontal)
        {
            var raw = server.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float v, out var unit))
            {
                return defaultValue;
            }
            return SvgValues.ResolveUnits(v, unit, DefaultFontSize, 1f);
        }

        private float GradientRadius(SvgElement server)
        {
            var raw = server.GetAttribute("r");
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float v, out var unit))
            {
                return 0.5f;
            }
            float resolved = SvgValues.ResolveUnits(v, unit, DefaultFontSize, 1f);
            return SvgValues.ClampCoord(resolved);
        }

        /// <summary>
        /// Gathers stop colors/positions following xlink:href chains between
        /// gradient servers, with a hop budget defeating reference cycles.
        /// </summary>
        private SKColor[] StopsColors((float offset, SKColor color)[] stops)
        {
            var colors = new SKColor[stops.Length];
            for (int i = 0; i < stops.Length; i++)
            {
                colors[i] = stops[i].color;
            }
            return colors;
        }

        private float[] StopsPositions((float offset, SKColor color)[] stops)
        {
            var positions = new float[stops.Length];
            for (int i = 0; i < stops.Length; i++)
            {
                positions[i] = stops[i].offset;
            }
            return positions;
        }

        private (float offset, SKColor color)[] CollectStops(SvgElement server, InheritedStyle style, HashSet<SvgElement> visited)
        {
            visited ??= new HashSet<SvgElement>();
            if (visited.Contains(server) || visited.Count >= 8)
            {
                _report.Warn("gradient reference chain cyclic or too deep");
                return System.Array.Empty<(float, SKColor)>();
            }
            visited.Add(server);

            var own = new List<(float offset, SKColor color)>();
            var children = server.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Name != "stop")
                {
                    continue;
                }
                var stop = children[i];

                float offset = 0f;
                var offRaw = stop.GetAttribute("offset");
                if (!string.IsNullOrWhiteSpace(offRaw))
                {
                    var trimmed = offRaw.TrimEnd('%');
                    bool wasPercent = trimmed.Length != offRaw.Length;
                    if (SvgValues.TryParseNumber(trimmed.AsSpan(), out float ov) && SvgValues.IsFinite(ov))
                    {
                        offset = wasPercent ? ov / 100f : ov;
                    }
                }
                offset = System.Math.Clamp(offset, 0f, 1f);

                var colorRaw = stop.GetAttribute("stop-color") ?? "black";
                var color = SKColors.Black;
                if (colorRaw.Equals("currentColor", System.StringComparison.OrdinalIgnoreCase))
                {
                    color = style.CurrentColor;
                }
                else if (!SvgValues.TryParseColor(colorRaw.AsSpan(), out color))
                {
                    color = SKColors.Black;
                }

                float stopOpacity = 1f;
                var soRaw = stop.GetAttribute("stop-opacity");
                if (!string.IsNullOrWhiteSpace(soRaw) &&
                    SvgValues.TryParseNumber(soRaw.AsSpan(), out float sov) &&
                    SvgValues.IsFinite(sov))
                {
                    stopOpacity = System.Math.Clamp(sov, 0f, 1f);
                }

                byte a = (byte)(color.Alpha * stopOpacity);
                own.Add((offset, new SKColor(color.Red, color.Green, color.Blue, a)));
            }

            if (own.Count > 0)
            {
                return own.ToArray();
            }

            // No local stops: follow template link (cycle-guarded, bounded).
            string href = server.GetAttribute("href") ?? server.GetLookup("xlink:href");
            if (!string.IsNullOrEmpty(href) && href.StartsWith("#", System.StringComparison.Ordinal) &&
                _doc.ElementsById.TryGetValue(href.Substring(1), out var template) &&
                (template.Name == "linearGradient" || template.Name == "radialGradient"))
            {
                var inheritedStops = CollectStops(template, style, visited);
                if (inheritedStops.Length > 0)
                {
                    return inheritedStops;
                }
            }

            return System.Array.Empty<(float, SKColor)>();
        }

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
            if (opacity >= 1f)
            {
                layerPaint = null;
                return false;
            }
            layerPaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha((byte)(opacity * 255))
            };
            canvas.SaveLayer(layerPaint);
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

        // ------------------------------------------------------- style carrier

        internal enum PaintKind
        {
            None,
            Solid,
            ServerRef,
            Unresolved
        }

        private readonly struct PaintSpec
        {
            public PaintSpec(PaintKind kind, SKColor color, string fragment, string fallback)
            {
                Kind = kind;
                Color = color;
                Fragment = fragment;
                Fallback = fallback;
            }

            public PaintKind Kind { get; }
            public SKColor Color { get; }
            public string Fragment { get; }
            public string Fallback { get; }

            public static readonly PaintSpec Black = new(PaintKind.Solid, SKColors.Black, null, null);
            public static readonly PaintSpec None = new(PaintKind.None, default, null, null);
        }

        private sealed class InheritedStyle
        {
            public PaintSpec Fill = PaintSpec.Black;      // spec default: black
            public PaintSpec Stroke = PaintSpec.None;     // spec default: none
            public float StrokeWidth = 1f;
            public SKStrokeCap Cap = SKStrokeCap.Butt;
            public SKStrokeJoin Join = SKStrokeJoin.Miter;
            public float MiterLimit = 4f;
            public float[] Dash;
            public float DashOffset;
            public SKColor CurrentColor = SKColors.Black;
            public bool Visibility = true;

            public InheritedStyle Clone() => (InheritedStyle)MemberwiseClone();

            /// <summary>
            /// Applies THIS element's presentation attributes on top of the
            /// inherited state. Returns a new instance (copy-on-write semantics
            /// keep sibling branches independent).
            /// </summary>
            public InheritedStyle ResolveOverrides(SvgElement el, SvgParseReport report)
            {
                var s = this;

                var colorRaw = el.GetAttribute("color");
                if (!string.IsNullOrWhiteSpace(colorRaw) &&
                    SvgValues.TryParseColor(colorRaw.AsSpan(), out var cc))
                {
                    s = s.Clone();
                    s.CurrentColor = cc;
                }

                var fill = ParsePaintSpec(el.GetAttribute("fill"), s, allowUnspecifiedKeep: true);
                if (fill.HasValue)
                {
                    s = s.Clone();
                    s.Fill = fill.Value;
                }

                var stroke = ParsePaintSpec(el.GetAttribute("stroke"), s, allowUnspecifiedKeep: true);
                if (stroke.HasValue)
                {
                    s = s.Clone();
                    s.Stroke = stroke.Value;
                }

                var swRaw = el.GetAttribute("stroke-width");
                if (!string.IsNullOrWhiteSpace(swRaw) &&
                    SvgValues.TryParseLength(swRaw.AsSpan(), out float swv, out var swu))
                {
                    if (swv >= 0f)
                    {
                        s = s.Clone();
                        s.StrokeWidth = SvgValues.ClampCoord(SvgValues.ResolveUnits(swv, swu, DefaultFontSize, 1f));
                    }
                    // Negative: invalid per spec -> keep inherited value.
                }

                var capRaw = el.GetAttribute("stroke-linecap");
                if (!string.IsNullOrWhiteSpace(capRaw))
                {
                    var cap = capRaw.ToLowerInvariant() switch
                    {
                        "round" => SKStrokeCap.Round,
                        "square" => SKStrokeCap.Square,
                        "butt" => SKStrokeCap.Butt,
                        _ => s.Cap
                    };
                    if (cap != s.Cap)
                    {
                        s = s.Clone();
                        s.Cap = cap;
                    }
                }

                var joinRaw = el.GetAttribute("stroke-linejoin");
                if (!string.IsNullOrWhiteSpace(joinRaw))
                {
                    var join = joinRaw.ToLowerInvariant() switch
                    {
                        "round" => SKStrokeJoin.Round,
                        "bevel" => SKStrokeJoin.Bevel,
                        "miter" => SKStrokeJoin.Miter,
                        _ => s.Join
                    };
                    if (join != s.Join)
                    {
                        s = s.Clone();
                        s.Join = join;
                    }
                }

                var mlRaw = el.GetAttribute("stroke-miterlimit");
                if (!string.IsNullOrWhiteSpace(mlRaw) &&
                    SvgValues.TryParseNumber(mlRaw.AsSpan(), out float ml) &&
                    SvgValues.IsFinite(ml) && ml >= 1f)
                {
                    s = s.Clone();
                    s.MiterLimit = ml;
                }

                var dashRaw = el.GetAttribute("stroke-dasharray");
                if (dashRaw != null)
                {
                    var dashes = ParseDashArray(dashRaw);
                    if (!ReferenceEquals(dashes, InvalidDash))
                    {
                        s = s.Clone();
                        s.Dash = dashes;

                        var doffRaw = el.GetAttribute("stroke-dashoffset");
                        if (!string.IsNullOrWhiteSpace(doffRaw) &&
                            SvgValues.TryParseLength(doffRaw.AsSpan(), out float dov, out var dou))
                        {
                            s.DashOffset = System.Math.Max(0f, SvgValues.ResolveUnits(dov, dou, DefaultFontSize, 1f));
                        }
                    }
                }

                var visRaw = el.GetAttribute("visibility");
                if (!string.IsNullOrWhiteSpace(visRaw))
                {
                    var v = visRaw.ToLowerInvariant() switch
                    {
                        "visible" => true,
                        "hidden" => false,
                        "collapse" => false,
                        _ => s.Visibility
                    };
                    if (v != s.Visibility)
                    {
                        s = s.Clone();
                        s.Visibility = v;
                    }
                }

                return s;
            }

            private static readonly float[] InvalidDash = System.Array.Empty<float>();

            private static float[] ParseDashArray(string raw)
            {
                if (string.Equals(raw.Trim(), "none", System.StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                var tok = SvgValues.CreateTokenizer(raw.AsSpan());
                var list = new List<float>(8);
                while (tok.Next(out var token))
                {
                    if (list.Count >= 64)
                    {
                        return InvalidDash; // Excessive dash count: property invalid.
                    }
                    if (!SvgValues.TryParseLength(token, out float v, out var u) ||
                        v < 0f || !SvgValues.IsFinite(v))
                    {
                        return InvalidDash; // Any invalid entry kills the whole list.
                    }
                    list.Add(SvgValues.ResolveUnits(v, u, DefaultFontSize, 1f));
                }

                if (list.Count == 0)
                {
                    return InvalidDash;
                }

                if (list.Count == 1)
                {
                    list.Add(list[0]); // Spec: odd counts are duplicated.
                }
                return list.ToArray();
            }

            private static PaintSpec? ParsePaintSpec(string raw, InheritedStyle s, bool allowUnspecifiedKeep)
            {
                if (raw == null)
                {
                    return null; // Not specified: inherit.
                }

                if (!SvgValues.TryParsePaint(
                        raw.AsSpan(),
                        out var kind,
                        out var color,
                        out var fragment,
                        out var fallbackText))
                {
                    return allowUnspecifiedKeep ? (PaintSpec?)null : PaintSpec.None;
                }

                switch (kind)
                {
                    case SvgValues.PaintKind.None:
                        return PaintSpec.None;
                    case SvgValues.PaintKind.CurrentColor:
                        return new PaintSpec(PaintKind.Solid, s.CurrentColor, null, null);
                    case SvgValues.PaintKind.Color:
                        return new PaintSpec(PaintKind.Solid, color, null, null);
                    case SvgValues.PaintKind.ServerRef:
                        return new PaintSpec(PaintKind.ServerRef, default, fragment, fallbackText);
                    default:
                        return null; // inherit / unspecified
                }
            }
        }

        private sealed class CanvasState : System.IDisposable
        {
            private readonly SKCanvas _canvas;

            public CanvasState(SKCanvas canvas)
            {
                _canvas = canvas;
                _canvas.Save();
            }

            public void Dispose() => _canvas.Restore();
        }
    }

    internal sealed class SvgTimeBudgetExceededException : System.Exception
    {
    }
}
