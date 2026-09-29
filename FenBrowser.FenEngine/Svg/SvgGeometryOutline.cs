using System;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// The outline of each SVG basic shape and of path data (SVG 2 §9.2 "The 'path'
    /// element" equivalent paths for §10 basic shapes), from already-resolved
    /// geometry lengths. The renderer and the SVG DOM geometry methods
    /// (SVGGeometryElement.getTotalLength / getPointAtLength) both build outlines
    /// here, so a script measures exactly the path that is painted.
    /// </summary>
    public static class SvgGeometryOutline
    {
        private const float QuarterConicWeight = 0.70710678f;

        /// <summary>
        /// Curve flattening resolution for measuring. At Skia's default (1) a small
        /// circle measures about 2.5% short and at 16 still 0.16% short; 1024 matches
        /// 2πr to within 0.02%. Skia bounds the subdivision depth per curve, so the
        /// cost stays bounded by the path's segment budget.
        /// </summary>
        private const float MeasureResolution = 1024f;

        /// <summary>What a percentage geometry length is resolved against.</summary>
        public enum PercentBasis
        {
            Width,
            Height,
            /// <summary>sqrt((w² + h²) / 2), for r.</summary>
            Diagonal
        }

        /// <summary>
        /// Resolves one geometry property to user units. False when it is unspecified,
        /// auto or invalid, which each shape treats per its own rules.
        /// </summary>
        public delegate bool LengthResolver(string property, PercentBasis basis, out float value);

        /// <summary>The SVGGeometryElement names: the shapes with an outline.</summary>
        public static bool IsGeometryElement(string localName) =>
            localName is "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon";

        /// <summary>
        /// Builds the outline of a geometry element, or null when it has none (a
        /// zero-sized shape, missing points, or empty path data).
        /// </summary>
        /// <param name="rawValue">The raw points or d value for polyline, polygon and path.</param>
        public static SKPath TryBuild(string localName, LengthResolver length, Func<string, string> rawValue)
        {
            if (localName == "path")
            {
                string d = rawValue("d");
                if (string.IsNullOrWhiteSpace(d) ||
                    d.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                SvgPathParser.TryBuildPath(d.AsSpan(), out var parsed, new SvgParseReport());
                if (parsed.IsEmpty)
                {
                    parsed.Dispose();
                    return null;
                }
                return parsed;
            }

            using var builder = new SKPathBuilder();
            float Length(string name, PercentBasis basis) =>
                length(name, basis, out float value) ? value : 0f;

            bool ok;
            switch (localName)
            {
                case "rect":
                {
                    float w = Length("width", PercentBasis.Width);
                    float h = Length("height", PercentBasis.Height);
                    ok = w > 0f && h > 0f;
                    if (ok)
                    {
                        bool hasRx = length("rx", PercentBasis.Width, out float rx) && rx > 0f;
                        bool hasRy = length("ry", PercentBasis.Height, out float ry) && ry > 0f;
                        AppendRect(builder, Length("x", PercentBasis.Width), Length("y", PercentBasis.Height),
                            w, h, hasRx ? rx : null, hasRy ? ry : null);
                    }
                    break;
                }
                case "circle":
                {
                    float r = Length("r", PercentBasis.Diagonal);
                    ok = r > 0f;
                    if (ok)
                    {
                        builder.AddCircle(Length("cx", PercentBasis.Width), Length("cy", PercentBasis.Height), r);
                    }
                    break;
                }
                case "ellipse":
                {
                    float? rx = length("rx", PercentBasis.Width, out float x) && x >= 0f ? x : null;
                    float? ry = length("ry", PercentBasis.Height, out float y) && y >= 0f ? y : null;
                    float radiusX = rx ?? ry ?? 0f;
                    float radiusY = ry ?? rx ?? 0f;
                    ok = radiusX > 0f && radiusY > 0f;
                    if (ok)
                    {
                        float cx = Length("cx", PercentBasis.Width);
                        float cy = Length("cy", PercentBasis.Height);
                        builder.AddOval(new SKRect(cx - radiusX, cy - radiusY, cx + radiusX, cy + radiusY));
                    }
                    break;
                }
                case "line":
                    builder.MoveTo(Length("x1", PercentBasis.Width), Length("y1", PercentBasis.Height));
                    builder.LineTo(Length("x2", PercentBasis.Width), Length("y2", PercentBasis.Height));
                    ok = true;
                    break;
                case "polyline":
                case "polygon":
                    ok = AppendPoints(builder, rawValue("points"), localName == "polygon", warn: null);
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

        /// <summary>
        /// A rect with the SVG corner rules: a missing radius takes the other one,
        /// and both are clamped to half the side they round.
        /// </summary>
        public static void AppendRect(SKPathBuilder path, float x, float y, float w, float h, float? rx, float? ry)
        {
            float radiusX = rx ?? ry ?? 0f;
            float radiusY = ry ?? rx ?? 0f;
            radiusX = Math.Min(Math.Max(radiusX, 0f), w / 2f);
            radiusY = Math.Min(Math.Max(radiusY, 0f), h / 2f);

            if (radiusX <= 0f || radiusY <= 0f)
            {
                path.AddRect(new SKRect(x, y, x + w, y + h));
                return;
            }

            // The equivalent path starts at (x + rx, y) and runs clockwise.
            float right = x + w;
            float bottom = y + h;
            path.MoveTo(x + radiusX, y);
            path.LineTo(right - radiusX, y);
            path.ConicTo(right, y, right, y + radiusY, QuarterConicWeight);
            path.LineTo(right, bottom - radiusY);
            path.ConicTo(right, bottom, right - radiusX, bottom, QuarterConicWeight);
            path.LineTo(x + radiusX, bottom);
            path.ConicTo(x, bottom, x, bottom - radiusY, QuarterConicWeight);
            path.LineTo(x, y + radiusY);
            path.ConicTo(x, y, x + radiusX, y, QuarterConicWeight);
            path.Close();
        }

        /// <summary>
        /// Appends a points list. Pairs are consumed as a stream, so no token list is
        /// materialized and the segment budget bounds the output for any input length.
        /// The valid prefix is kept and an odd trailing coordinate ignored (browser-style).
        /// </summary>
        public static bool AppendPoints(SKPathBuilder path, string raw, bool closePolygon, Action<string> warn)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var tok = SvgValues.CreateTokenizer(raw.AsSpan());
            int emitted = 0;
            while (tok.Next(out var tx) && tok.Next(out var ty))
            {
                if (emitted >= SvgPathParser.MaxSegments)
                {
                    warn?.Invoke("points list truncated at segment budget");
                    break;
                }
                if (!SvgValues.TryParseNumber(tx, out float x) ||
                    !SvgValues.TryParseNumber(ty, out float y))
                {
                    break;
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

        /// <summary>The total length of every contour, in user units.</summary>
        public static float MeasureLength(SKPath path)
        {
            if (path == null)
            {
                return 0f;
            }

            using var measure = new SKPathMeasure(path, false, MeasureResolution);
            double total = 0;
            do
            {
                total += measure.Length;
            }
            while (measure.NextContour());
            return (float)total;
        }

        /// <summary>
        /// The point at a distance along the outline, clamped to [0, length]; the
        /// path's start for an empty outline.
        /// </summary>
        public static SKPoint PointAtLength(SKPath path, float distance)
        {
            if (path == null)
            {
                return SKPoint.Empty;
            }

            using var measure = new SKPathMeasure(path, false, MeasureResolution);
            float remaining = Math.Max(distance, 0f);
            SKPoint last = path.PointCount > 0 ? path.GetPoint(0) : SKPoint.Empty;
            do
            {
                float contour = measure.Length;
                if (contour <= 0f)
                {
                    continue;
                }
                if (remaining <= contour)
                {
                    return measure.GetPosition(remaining, out SKPoint position) ? position : last;
                }
                remaining -= contour;
                if (measure.GetPosition(contour, out SKPoint end))
                {
                    last = end;
                }
            }
            while (measure.NextContour());
            return last;
        }
    }
}
