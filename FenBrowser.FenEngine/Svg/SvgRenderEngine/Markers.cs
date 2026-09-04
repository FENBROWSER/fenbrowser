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
            string shorthand = element.GetPresentationProperty("marker");
            string start = element.GetPresentationProperty("marker-start") ?? shorthand;
            string middle = element.GetPresentationProperty("marker-mid") ?? shorthand;
            string end = element.GetPresentationProperty("marker-end") ?? shorthand;
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
                    Direction(vertices[0].Point, vertices[1].Point), isStart: true);

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
                        var tangent = new SKPoint(incoming.X + outgoing.X, incoming.Y + outgoing.Y);
                        if (tangent.X == 0f && tangent.Y == 0f) tangent = outgoing;
                        DrawMarkerInstance(middle, element, canvas, viewport, style, vertices[i].Point, tangent, false);
                    }
                    if (vertices.Count > MaxMarkersPerElement)
                        _report.RequireFallback("SVG marker instance budget exceeded");
                }
            }

            if (HasEffectValue(end))
            {
                int last = vertices.Count - 1;
                DrawMarkerInstance(end, element, canvas, viewport, style, vertices[last].Point,
                    Direction(vertices[last - 1].Point, vertices[last].Point), isStart: false);
            }
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

            float markerWidth = ResolveMarkerLength(marker.GetAttribute("markerWidth"), 3f);
            float markerHeight = ResolveMarkerLength(marker.GetAttribute("markerHeight"), 3f);
            if (markerWidth <= 0f || markerHeight <= 0f) return;
            float refX = ResolveMarkerLength(marker.GetAttribute("refX"), 0f);
            float refY = ResolveMarkerLength(marker.GetAttribute("refY"), 0f);
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
            float angle = ResolveMarkerAngle(marker.GetAttribute("orient"), markerTangent, isStart);

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
                SKPoint mappedRef = hasViewBox
                    ? MapMarkerReference(refX, refY, markerViewport, vbX, vbY, vbW, vbH,
                        marker.GetAttribute("preserveAspectRatio"))
                    : new SKPoint(refX, refY);
                canvas.Translate(-mappedRef.X, -mappedRef.Y);
                if (!string.Equals(
                        marker.GetPresentationProperty("overflow")?.Trim(),
                        "visible",
                        StringComparison.OrdinalIgnoreCase))
                {
                    canvas.ClipRect(new SKRect(0f, 0f, markerWidth, markerHeight));
                }
                if (hasViewBox)
                    ApplyViewportTransform(canvas, markerViewport, true, vbX, vbY, vbW, vbH,
                        marker.GetAttribute("preserveAspectRatio"));

                var markerStyle = ResolveMarkerStyle(marker);
                DrawChildren(marker, canvas,
                    hasViewBox ? new ViewportContext(vbW, vbH) : markerViewport,
                    markerStyle);
            }
            finally
            {
                _activeMarkerIds.Remove(id);
            }
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
            return tangent.X == 0f && tangent.Y == 0f ? outgoing : tangent;
        }

        private InheritedStyle ResolveMarkerStyle(SvgElement marker)
        {
            _markerStyleCache ??= new Dictionary<SvgElement, InheritedStyle>();
            if (_markerStyleCache.TryGetValue(marker, out var cached)) return cached;

            var ancestry = new List<SvgElement>();
            for (SvgElement current = marker; current != null; current = current.Parent)
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
                    result.Add(new MarkerVertex(new SKPoint(SvgValues.ClampCoord(x), SvgValues.ClampCoord(y))));
                }
                if (element.Name == "polygon" && result.Count > 1) result.Add(result[0]);
                return result;
            }

            using var measure = new SKPathMeasure(path, false);
            if (measure.Length <= 0f) return result;
            if (measure.GetPositionAndTangent(0f, out var start, out _)) result.Add(new MarkerVertex(start));
            float endDistance = MathF.Max(0f, measure.Length - .001f);
            if (measure.GetPositionAndTangent(endDistance, out var end, out _)) result.Add(new MarkerVertex(end));
            return result;
        }

        private static SKPoint Direction(SKPoint from, SKPoint to)
        {
            float x = to.X - from.X;
            float y = to.Y - from.Y;
            float length = MathF.Sqrt(x * x + y * y);
            return length > 0f ? new SKPoint(x / length, y / length) : new SKPoint(1f, 0f);
        }

        private static float ResolveMarkerAngle(string raw, SKPoint tangent, bool isStart)
        {
            float auto = MathF.Atan2(tangent.Y, tangent.X) * 180f / MathF.PI;
            if (string.IsNullOrWhiteSpace(raw)) return 0f;
            if (raw.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return auto;
            if (raw.Trim().Equals("auto-start-reverse", StringComparison.OrdinalIgnoreCase)) return auto + (isStart ? 180f : 0f);
            string value = raw.Trim();
            if (value.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) value = value[..^3].Trim();
            return SvgValues.TryParseNumber(value.AsSpan(), out float angle) && float.IsFinite(angle) ? angle : auto;
        }

        private static SKPoint MapMarkerReference(
            float x, float y, ViewportContext viewport,
            float vbX, float vbY, float vbW, float vbH, string preserveAspectRatio)
        {
            ParsePreserveAspectRatio(preserveAspectRatio, out ParAlign align, out ParMeet meet);
            if (align == ParAlign.None)
            {
                return new SKPoint(
                    (x - vbX) * viewport.Width / vbW,
                    (y - vbY) * viewport.Height / vbH);
            }
            float scale = meet == ParMeet.Slice
                ? Math.Max(viewport.Width / vbW, viewport.Height / vbH)
                : Math.Min(viewport.Width / vbW, viewport.Height / vbH);
            float leftoverX = viewport.Width - vbW * scale;
            float leftoverY = viewport.Height - vbH * scale;
            float tx = (align & ParAlign.XMid) != 0 ? leftoverX / 2f :
                (align & ParAlign.XMax) != 0 ? leftoverX : 0f;
            float ty = (align & ParAlign.YMid) != 0 ? leftoverY / 2f :
                (align & ParAlign.YMax) != 0 ? leftoverY : 0f;
            return new SKPoint(tx + (x - vbX) * scale, ty + (y - vbY) * scale);
        }

        private static float ResolveMarkerLength(string raw, float fallback)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit)) return fallback;
            float resolved = SvgValues.ResolveUnits(value, unit, DefaultFontSize, 1f);
            return float.IsFinite(resolved) ? SvgValues.ClampCoord(resolved) : fallback;
        }

        private readonly record struct MarkerVertex(SKPoint Point);
    }
}
