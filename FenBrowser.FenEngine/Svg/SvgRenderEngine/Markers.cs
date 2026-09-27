using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private const int MaxMarkersPerElement = 4096;
        private HashSet<string> _activeMarkerIds;
        private Dictionary<SvgElement, InheritedStyle> _markerStyleCache;

        private void DrawMarkers(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style,
            SKPath path)
        {
            ResolveInheritedMarkerProperties(element, out string start, out string middle, out string end);
            if (!HasEffectValue(start) && !HasEffectValue(middle) && !HasEffectValue(end)) return;

            if (element.Name == "path")
            {
                string data = ResolvePathData(element);
                if (SvgPathParser.TryReadMarkerSubpaths(
                        (data ?? string.Empty).AsSpan(),
                        MaxMarkersPerElement,
                        out var subpaths,
                        _report,
                        CheckTime))
                {
                    DrawLinearPathMarkers(
                        element, canvas, viewport, style, subpaths, start, middle, end);
                    return;
                }
            }

            var vertices = ReadMarkerVertices(element, path);
            if (vertices.Count < 2) return;

            if (HasEffectValue(start))
                DrawMarkerInstance(start, element, canvas, viewport, style, vertices[0].Point,
                    vertices[0].Direction, isStart: true);

            if (HasEffectValue(middle))
            {
                if (element.Name == "path")
                {
                    _report.RequireFallback("marker-mid on non-polyline geometry requires compatibility fallback");
                }
                else if (element.Name == "polyline" || element.Name == "polygon")
                {
                    int limit = Math.Min(vertices.Count - 1, MaxMarkersPerElement);
                    for (int i = 1; i < limit; i++)
                    {
                        SKPoint incoming = Direction(vertices[i - 1].Point, vertices[i].Point);
                        SKPoint outgoing = Direction(vertices[i].Point, vertices[i + 1].Point);
                        DrawMarkerInstance(middle, element, canvas, viewport, style, vertices[i].Point,
                            BisectDirections(incoming, outgoing), false);
                    }
                    if (vertices.Count > MaxMarkersPerElement)
                        _report.RequireFallback("SVG marker instance budget exceeded");
                }
            }

            if (HasEffectValue(end))
            {
                int last = vertices.Count - 1;
                DrawMarkerInstance(end, element, canvas, viewport, style, vertices[last].Point,
                    vertices[last].Direction, isStart: false);
            }
        }

        private static void ResolveInheritedMarkerProperties(
            SvgElement element,
            out string start,
            out string middle,
            out string end)
        {
            string shorthand = null, resolvedStart = null, resolvedMiddle = null, resolvedEnd = null;
            for (SvgElement current = element; current != null; current = current.Parent)
            {
                if (resolvedStart == null) resolvedStart = ReadMarkerPropertyValue(current, "marker-start");
                if (resolvedMiddle == null) resolvedMiddle = ReadMarkerPropertyValue(current, "marker-mid");
                if (resolvedEnd == null) resolvedEnd = ReadMarkerPropertyValue(current, "marker-end");
                if (shorthand == null) shorthand = ReadMarkerShorthandValue(current);
                if (resolvedStart != null && resolvedMiddle != null && resolvedEnd != null &&
                    shorthand != null) break;
            }
            start = resolvedStart ?? shorthand;
            middle = resolvedMiddle ?? shorthand;
            end = resolvedEnd ?? shorthand;
        }

        private static string ReadMarkerPropertyValue(SvgElement element, string name) =>
            NormalizeMarkerKeyword(element.GetPresentationProperty(name));

        /// <summary>
        /// `marker` is a shorthand, and shorthand properties are not presentation
        /// attributes, so only a real CSS declaration - a stylesheet rule or the
        /// `style` attribute - sets it. Reading it through the presentation
        /// attribute fallback would honour `marker="url(#m)"`, which every
        /// browser drops; the longhands keep the attribute fallback.
        /// </summary>
        private static string ReadMarkerShorthandValue(SvgElement element) =>
            NormalizeMarkerKeyword(element.GetCascadedPresentationProperty("marker"));

        private static string NormalizeMarkerKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string keyword = value.Trim();
            if (keyword.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("revert", StringComparison.OrdinalIgnoreCase)) return null;
            return keyword.Equals("initial", StringComparison.OrdinalIgnoreCase) ? "none" : value;
        }

        private void DrawMarkerInstance(
            string raw,
            SvgElement source,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle sourceStyle,
            SKPoint point,
            SKPoint tangent,
            bool isStart)
        {
            if (!TryResolveLocalReference(raw, out string id) ||
                !_doc.ElementsById.TryGetValue(id, out var marker) || marker.Name != "marker")
            {
                _report.RequireFallback("SVG marker reference is invalid or unresolved");
                return;
            }
            _activeMarkerIds ??= new HashSet<string>(StringComparer.Ordinal);
            if (_activeMarkerIds.Contains(id) || _activeMarkerIds.Count >= _maxReferenceDepth)
            {
                _report.RequireFallback("SVG marker cycle or reference-depth budget exceeded");
                return;
            }

            float markerWidth = ResolveMarkerLength(
                marker.GetAttribute("markerWidth"), 3f, viewport.Width, sourceStyle.FontSize);
            float markerHeight = ResolveMarkerLength(
                marker.GetAttribute("markerHeight"), 3f, viewport.Height, sourceStyle.FontSize);
            if (markerWidth <= 0f || markerHeight <= 0f) return;
            float refX = ResolveMarkerLength(
                marker.GetAttribute("refX"), 0f, markerWidth, sourceStyle.FontSize);
            float refY = ResolveMarkerLength(
                marker.GetAttribute("refY"), 0f, markerHeight, sourceStyle.FontSize);
            bool strokeWidthUnits = !string.Equals(
                marker.GetAttribute("markerUnits"), "userSpaceOnUse", StringComparison.Ordinal);
            bool deviceSpaceStrokeUnits = strokeWidthUnits &&
                SvgFeatureSupport.SupportsNonScalingStroke(
                    source, source.GetPresentationProperty("vector-effect"));
            float unitScale = strokeWidthUnits ? Math.Max(0f, sourceStyle.StrokeWidth) : 1f;
            SKPoint markerPoint = point;
            SKPoint markerTangent = tangent;
            if (deviceSpaceStrokeUnits)
            {
                SKMatrix sourceMatrix = canvas.TotalMatrix;
                markerPoint = sourceMatrix.MapPoint(point);
                markerTangent = sourceMatrix.MapVector(tangent);
            }
            if (!TryResolveMarkerAngle(
                    marker.GetAttribute("orient"), markerTangent, isStart, out float angle))
            {
                _report.RequireFallback(
                    "SVG marker orient is not a finite representable angle");
                return;
            }

            _activeMarkerIds.Add(id);
            try
            {
                using var state = new CanvasState(canvas);
                if (deviceSpaceStrokeUnits) canvas.ResetMatrix();
                canvas.Translate(markerPoint.X, markerPoint.Y);
                canvas.RotateDegrees(angle);
                canvas.Scale(unitScale, unitScale);

                bool hasViewBox = TryParseViewBox(marker.GetAttribute("viewBox"),
                    out float vbX, out float vbY, out float vbW, out float vbH,
                    out bool viewBoxDisablesRendering);
                if (viewBoxDisablesRendering) return;
                var markerViewport = new ViewportContext(markerWidth, markerHeight);
                SKPoint mappedRef;
                if (hasViewBox)
                {
                    if (!TryMapMarkerReference(
                            refX, refY, markerViewport, vbX, vbY, vbW, vbH,
                            marker.GetAttribute("preserveAspectRatio"), out mappedRef))
                    {
                        _report.RequireFallback("SVG marker viewport transform is not finite and bounded");
                        return;
                    }
                }
                else
                {
                    mappedRef = new SKPoint(refX, refY);
                }
                canvas.Translate(-mappedRef.X, -mappedRef.Y);
                if (MarkerOverflowClipsViewport(marker))
                {
                    ClipMarkerViewport(canvas, markerWidth, markerHeight);
                }
                if (hasViewBox &&
                    !ApplyViewportTransform(canvas, markerViewport, true, vbX, vbY, vbW, vbH,
                        marker.GetAttribute("preserveAspectRatio")))
                {
                    return;
                }

                var markerStyle = ResolveMarkerInheritedStyle(marker).Clone();
                markerStyle.ContextFill = sourceStyle.Fill;
                markerStyle.ContextStroke = sourceStyle.Stroke;
                markerStyle = markerStyle.ResolveOverrides(marker, _report);
                DrawChildren(marker, canvas,
                    hasViewBox ? new ViewportContext(vbW, vbH) : markerViewport,
                    markerStyle);
            }
            finally
            {
                _activeMarkerIds.Remove(id);
            }
        }

        /// <summary>
        /// Decides the implicit marker viewport clip from <c>overflow</c>. The
        /// clip is conditional, not "everything but visible": SVG 2 only
        /// establishes it "if the overflow property on the <marker> element
        /// indicates that the marker needs to be clipped to its SVG viewport",
        /// and the browser-authored WPT oracle for <c>painting/marker-005</c>
        /// splits the values three ways. Its <c>auto</c> column is drawn from
        /// unclipped content - byte identical to its <c>visible</c> column -
        /// while <c>hidden</c>, <c>scroll</c> and the unspecified initial value
        /// are all drawn clipped to the viewport. So <c>auto</c> leaves the
        /// viewport and <c>scroll</c> does not.
        ///
        /// The initial value is <c>hidden</c>, so an absent value clips, and so
        /// does any spelling this cannot resolve: an unrecognised keyword
        /// falls back to the initial value the same way CSS treats it, rather
        /// than to the unpainted frame a wrong guess would emit.
        /// </summary>
        private static bool MarkerOverflowClipsViewport(SvgElement marker)
        {
            string value = marker.GetPresentationProperty("overflow")?.Trim();
            if (string.IsNullOrEmpty(value)) return true;
            return !value.Equals("visible", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("auto", StringComparison.OrdinalIgnoreCase);
        }

        private void DrawLinearPathMarkers(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style,
            List<SvgPathParser.MarkerSubpath> subpaths,
            string start,
            string middle,
            string end)
        {
            int firstSubpath = -1;
            int lastSubpath = -1;
            for (int i = 0; i < subpaths.Count; i++)
            {
                if (subpaths[i].Vertices.Count < 2) continue;
                if (firstSubpath < 0) firstSubpath = i;
                lastSubpath = i;
            }
            if (firstSubpath < 0) return;

            SvgPathParser.MarkerSubpath first = subpaths[firstSubpath];
            if (HasEffectValue(start))
                DrawMarkerInstance(start, element, canvas, viewport, style, first.Vertices[0].Point,
                    MarkerVertexDirection(first, 0), isStart: true);

            if (HasEffectValue(middle))
            {
                for (int subpathIndex = firstSubpath; subpathIndex <= lastSubpath; subpathIndex++)
                {
                    SvgPathParser.MarkerSubpath points = subpaths[subpathIndex];
                    if (points.Vertices.Count < 2) continue;
                    int firstMiddle = subpathIndex == firstSubpath ? 1 : 0;
                    int middleEnd = subpathIndex == lastSubpath
                        ? points.Vertices.Count - 1
                        : points.Vertices.Count;
                    for (int i = firstMiddle; i < middleEnd; i++)
                    {
                        DrawMarkerInstance(
                            middle, element, canvas, viewport, style, points.Vertices[i].Point,
                            MarkerVertexDirection(points, i), false);
                    }
                }
            }

            SvgPathParser.MarkerSubpath last = subpaths[lastSubpath];
            if (HasEffectValue(end))
            {
                int endIndex = last.Vertices.Count - 1;
                DrawMarkerInstance(end, element, canvas, viewport, style, last.Vertices[endIndex].Point,
                    MarkerVertexDirection(last, endIndex), isStart: false);
            }
        }

        private static SKPoint MarkerVertexDirection(
            SvgPathParser.MarkerSubpath subpath,
            int index)
        {
            var vertices = subpath.Vertices;
            SvgPathParser.MarkerVertex vertex = vertices[index];
            SKPoint incoming = vertex.Incoming;
            SKPoint outgoing = vertex.Outgoing;
            bool hasIncoming = vertex.HasIncoming;
            bool hasOutgoing = vertex.HasOutgoing;
            if (subpath.IsClosed && index == 0)
            {
                incoming = vertices[^1].Incoming;
                hasIncoming = vertices[^1].HasIncoming;
            }
            if (subpath.IsClosed && index == vertices.Count - 1)
            {
                outgoing = vertices[0].Outgoing;
                hasOutgoing = vertices[0].HasOutgoing;
            }
            if (!hasIncoming) return NormalizeDirection(outgoing);
            if (!hasOutgoing) return NormalizeDirection(incoming);
            return BisectDirections(
                NormalizeDirection(incoming),
                NormalizeDirection(outgoing));
        }

        private static SKPoint NormalizeDirection(SKPoint vector)
        {
            float length = MathF.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
            return length > 0f
                ? new SKPoint(vector.X / length, vector.Y / length)
                : new SKPoint(1f, 0f);
        }

        private static SKPoint BisectDirections(SKPoint incoming, SKPoint outgoing)
        {
            var tangent = new SKPoint(incoming.X + outgoing.X, incoming.Y + outgoing.Y);
            return tangent.X == 0f && tangent.Y == 0f
                ? new SKPoint(-outgoing.Y, outgoing.X)
                : tangent;
        }

        private InheritedStyle ResolveMarkerInheritedStyle(SvgElement marker)
        {
            _markerStyleCache ??= new Dictionary<SvgElement, InheritedStyle>();
            if (_markerStyleCache.TryGetValue(marker, out var cached)) return cached;

            var ancestry = new List<SvgElement>();
            for (SvgElement current = marker.Parent; current != null; current = current.Parent)
                ancestry.Add(current);
            var resolved = new InheritedStyle();
            for (int i = ancestry.Count - 1; i >= 0; i--)
                resolved = resolved.ResolveOverrides(ancestry[i], _report);
            _markerStyleCache[marker] = resolved;
            return resolved;
        }

        private List<MarkerVertex> ReadMarkerVertices(SvgElement element, SKPath path)
        {
            var result = new List<MarkerVertex>();
            if (element.Name == "polyline" || element.Name == "polygon")
            {
                var tokenizer = SvgValues.CreateTokenizer((element.GetAttribute("points") ?? string.Empty).AsSpan());
                while (result.Count <= MaxMarkersPerElement && tokenizer.Next(out var tx) && tokenizer.Next(out var ty))
                {
                    if (!SvgValues.TryParseNumber(tx, out float x) || !SvgValues.TryParseNumber(ty, out float y)) break;
                    result.Add(new MarkerVertex(new SKPoint(SvgValues.ClampCoord(x), SvgValues.ClampCoord(y)), default));
                }
                if (element.Name == "polygon" && result.Count > 1) result.Add(result[0]);
                for (int i = 0; i < result.Count; i++)
                {
                    SKPoint direction = i + 1 < result.Count
                        ? Direction(result[i].Point, result[i + 1].Point)
                        : (i > 0 ? Direction(result[i - 1].Point, result[i].Point) : new SKPoint(1f, 0f));
                    result[i] = result[i] with { Direction = direction };
                }
                return result;
            }

            using var measure = new SKPathMeasure(path, false);
            float length = measure.Length;
            if (!float.IsFinite(length) || length <= 0f) return result;

            if (!measure.GetPositionAndTangent(0f, out var start, out var startTangent) ||
                !TryNormalizeBearing(startTangent, out var startDirection))
            {
                _report.RequireFallback("SVG marker start bearing is unavailable");
                return result;
            }
            result.Add(new MarkerVertex(start, startDirection));

            float endDistance = MathF.Max(0f, length - .001f);
            if (!measure.GetPositionAndTangent(endDistance, out var end, out var endTangent) ||
                !TryNormalizeBearing(endTangent, out var endDirection))
            {
                _report.RequireFallback("SVG marker end bearing is unavailable");
                return result;
            }
            result.Add(new MarkerVertex(end, endDirection));
            return result;
        }

        private static bool TryNormalizeBearing(SKPoint tangent, out SKPoint direction)
        {
            direction = default;
            if (!float.IsFinite(tangent.X) || !float.IsFinite(tangent.Y)) return false;
            if (tangent.X == 0f && tangent.Y == 0f) return false;
            direction = NormalizeDirection(tangent);
            return SvgValues.IsFinite(direction.X) && SvgValues.IsFinite(direction.Y);
        }

        private static SKPoint Direction(SKPoint from, SKPoint to)
        {
            float x = to.X - from.X;
            float y = to.Y - from.Y;
            float length = MathF.Sqrt(x * x + y * y);
            return length > 0f ? new SKPoint(x / length, y / length) : new SKPoint(1f, 0f);
        }

        /// <summary>
        /// <c>orient</c> is a CSS/SVG <c>&lt;angle&gt;</c>, so <c>grad</c>,
        /// <c>rad</c> and <c>turn</c> are as legal as <c>deg</c>. Reading a bare
        /// number and stripping only a literal <c>deg</c> left those three
        /// spellings unparsed, and the caller then substituted the <c>auto</c>
        /// tangent - a rotation no browser paints for that document. An
        /// unparseable spelling now fails closed instead of borrowing the
        /// tangent, because the engine cannot verify the frame it would emit.
        /// </summary>
        private static bool TryResolveMarkerAngle(
            string raw,
            SKPoint tangent,
            bool isStart,
            out float angle)
        {
            float auto = MathF.Atan2(tangent.Y, tangent.X) * 180f / MathF.PI;
            if (string.IsNullOrWhiteSpace(raw))
            {
                angle = 0f;
                return true;
            }
            ReadOnlySpan<char> value = raw.AsSpan().Trim();
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                angle = auto;
                return true;
            }
            if (value.Equals("auto-start-reverse", StringComparison.OrdinalIgnoreCase))
            {
                angle = auto + (isStart ? 180f : 0f);
                return true;
            }
            return TryParseMarkerAngleDegrees(value, out angle);
        }

        private static bool TryParseMarkerAngleDegrees(ReadOnlySpan<char> raw, out float degrees)
        {
            degrees = 0f;
            ReadOnlySpan<char> value = raw.Trim();
            if (value.IsEmpty) return false;

            int cut = value.Length;
            while (cut > 0 && char.IsLetter(value[cut - 1])) cut--;
            float scale = 1f;
            if (cut != value.Length)
            {
                scale = MarkerAngleUnitScale(value, cut, out bool known);
                if (!known) return false;
            }
            if (!SvgValues.TryParseNumber(value.Slice(0, cut), out float magnitude)) return false;

            float resolved = magnitude * scale;
            if (!SvgValues.IsFinite(resolved)) return false;
            degrees = MathF.Abs(resolved) <= 360f ? resolved : resolved % 360f;
            return true;
        }

        private static float MarkerAngleUnitScale(ReadOnlySpan<char> value, int start, out bool known)
        {
            known = true;
            switch (value.Length - start)
            {
                case 0: return 1f;
                case 3 when MatchesAsciiUnit(value, start, "deg"): return 1f;
                case 4 when MatchesAsciiUnit(value, start, "grad"): return 0.9f;
                case 3 when MatchesAsciiUnit(value, start, "rad"): return 180f / (float)Math.PI;
                case 4 when MatchesAsciiUnit(value, start, "turn"): return 360f;
                default:
                    known = false;
                    return 0f;
            }
        }

        private static bool MatchesAsciiUnit(ReadOnlySpan<char> value, int start, string unit)
        {
            for (int i = 0; i < unit.Length; i++)
            {
                if ((char)(value[start + i] | 0x20) != unit[i]) return false;
            }
            return true;
        }

        private static bool TryMapMarkerReference(
            float x,
            float y,
            ViewportContext viewport,
            float vbX,
            float vbY,
            float vbW,
            float vbH,
            string preserveAspectRatio,
            out SKPoint mapped)
        {
            mapped = default;
            if (!SvgValues.IsFinite(x) || !SvgValues.IsFinite(y) ||
                !SvgValues.IsFinite(viewport.Width) || !SvgValues.IsFinite(viewport.Height) ||
                !SvgValues.IsFinite(vbX) || !SvgValues.IsFinite(vbY) ||
                !SvgValues.IsFinite(vbW) || !SvgValues.IsFinite(vbH) ||
                viewport.Width <= 0f || viewport.Height <= 0f || vbW <= 0f || vbH <= 0f ||
                System.Math.Abs(vbX) > SvgValues.CoordClamp ||
                System.Math.Abs(vbY) > SvgValues.CoordClamp)
            {
                return false;
            }

            float scaleX = viewport.Width / vbW;
            float scaleY = viewport.Height / vbH;
            if (!SvgValues.IsFinite(scaleX) || !SvgValues.IsFinite(scaleY) ||
                scaleX <= 0f || scaleY <= 0f ||
                System.Math.Abs(scaleX) > MaxViewportScale ||
                System.Math.Abs(scaleY) > MaxViewportScale)
            {
                return false;
            }

            ParsePreserveAspectRatio(preserveAspectRatio, out ParAlign align, out ParMeet meet);
            if (align == ParAlign.None)
            {
                mapped = new SKPoint(
                    (x - vbX) * scaleX,
                    (y - vbY) * scaleY);
                return SvgValues.IsFinite(mapped.X) && SvgValues.IsFinite(mapped.Y) &&
                    System.Math.Abs(mapped.X) <= SvgValues.CoordClamp &&
                    System.Math.Abs(mapped.Y) <= SvgValues.CoordClamp;
            }

            float scale = meet == ParMeet.Slice
                ? Math.Max(scaleX, scaleY)
                : Math.Min(scaleX, scaleY);
            float leftoverX = viewport.Width - vbW * scale;
            float leftoverY = viewport.Height - vbH * scale;
            if (!SvgValues.IsFinite(scale) || System.Math.Abs(scale) > MaxViewportScale ||
                !SvgValues.IsFinite(leftoverX) || !SvgValues.IsFinite(leftoverY) ||
                System.Math.Abs(leftoverX) > SvgValues.CoordClamp ||
                System.Math.Abs(leftoverY) > SvgValues.CoordClamp)
            {
                return false;
            }

            float tx = (align & ParAlign.XMid) != 0 ? leftoverX / 2f :
                (align & ParAlign.XMax) != 0 ? leftoverX : 0f;
            float ty = (align & ParAlign.YMid) != 0 ? leftoverY / 2f :
                (align & ParAlign.YMax) != 0 ? leftoverY : 0f;
            if (!SvgValues.IsFinite(tx) || !SvgValues.IsFinite(ty) ||
                System.Math.Abs(tx) > SvgValues.CoordClamp ||
                System.Math.Abs(ty) > SvgValues.CoordClamp)
            {
                return false;
            }
            mapped = new SKPoint(
                tx + (x - vbX) * scale,
                ty + (y - vbY) * scale);
            return SvgValues.IsFinite(mapped.X) && SvgValues.IsFinite(mapped.Y) &&
                System.Math.Abs(mapped.X) <= SvgValues.CoordClamp &&
                System.Math.Abs(mapped.Y) <= SvgValues.CoordClamp;
        }

        private static float ResolveMarkerLength(
            string raw,
            float fallback,
            float percentReference,
            float fontSize)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit)) return fallback;
            float resolved = SvgValues.ResolveUnits(
                value,
                unit,
                fontSize > 0f && float.IsFinite(fontSize) ? fontSize : DefaultFontSize,
                percentReference > 0f && float.IsFinite(percentReference) ? percentReference : 1f);
            return float.IsFinite(resolved) ? SvgValues.ClampCoord(resolved) : fallback;
        }

        /// <summary>
        /// Establishes the `overflow: hidden` marker viewport clip.
        /// </summary>
        private static void ClipMarkerViewport(SKCanvas canvas, float width, float height)
        {
            var viewport = new SKRect(0f, 0f, width, height);
            if (!MapsToAxisAlignedDeviceRect(canvas.TotalMatrix))
            {
                viewport = BiasClipForPixelSampling(canvas, viewport);
            }
            canvas.ClipRect(viewport);
        }

        /// <summary>
        /// Whether a rect in the current local space still lands on a device
        /// axis aligned rect. Skia resolves an axis aligned clip as a bounds
        /// test, which is exact; any other rect has to be sampled per pixel, and
        /// a sample that lands outside drops the pixel's own antialiased
        /// coverage. So the boundary only needs biasing in the sampled case:
        /// sampling the pixel's inner edge instead is the box-filter consistent
        /// reading of the same boundary, and it keeps content that lies on the
        /// viewport edge from losing coverage while still clipping content that
        /// spills further than half a pixel.
        /// </summary>
        private static SKRect BiasClipForPixelSampling(SKCanvas canvas, SKRect rect)
        {
            float bias = HalfDevicePixelInLocalUnits(canvas);
            if (!(bias > 0f)) return rect;
            float largest = MathF.Max(rect.Width, rect.Height);
            if (float.IsNaN(bias) || bias > largest) bias = largest;
            if (!float.IsFinite(bias) || !(bias > 0f)) return rect;
            return new SKRect(
                rect.Left - bias, rect.Top - bias, rect.Right + bias, rect.Bottom + bias);
        }

        private static bool MapsToAxisAlignedDeviceRect(SKMatrix matrix)
        {
            // Skia rows: the local x axis maps to (ScaleX, SkewY) and the local
            // y axis to (SkewX, ScaleY). A rect stays axis aligned only when
            // each of those lands on a device axis, exactly one component zero.
            bool xIsAxis = (matrix.ScaleX == 0f) != (matrix.SkewY == 0f);
            bool yIsAxis = (matrix.SkewX == 0f) != (matrix.ScaleY == 0f);
            return xIsAxis && yIsAxis;
        }

        private static float HalfDevicePixelInLocalUnits(SKCanvas canvas)
        {
            SKMatrix matrix = canvas.TotalMatrix;
            float x = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            float y = MathF.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);
            float scale = MathF.Max(x, y);
            return float.IsFinite(scale) && scale > 0f ? 0.5f / scale : 0f;
        }

        private readonly record struct MarkerVertex(SKPoint Point, SKPoint Direction);
    }
}
