using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {

        // ---------------------------------------------------------------- paint

        private bool TryApplyFillRule(SvgElement el, SKPath path)
        {
            var rule = el.GetPresentationProperty("fill-rule") ?? el.GetPresentationProperty("clip-rule");
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
            if (spec.Kind == SvgValues.PaintKind.None)
            {
                return null;
            }

            float opacity = ReadClampedOpacity(el, "fill-opacity", 1f);
            if (opacity <= 0f)
            {
                return null;
            }

            var paint = new SKPaint { IsStroke = false, IsAntialias = true };

            if (spec.Kind == SvgValues.PaintKind.Color)
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

        private SKPaint BuildStrokePaint(
            SvgElement el,
            InheritedStyle style,
            SKPath geometry = null)
        {
            var spec = style.Stroke;
            if (spec.Kind == SvgValues.PaintKind.None)
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
                    float calibration = ResolvePathLengthCalibration(el, geometry);
                    if (float.IsPositiveInfinity(calibration))
                    {
                        // A zero pathLength maps every dash interval to infinity;
                        // the observable result is one solid stroke.
                    }
                    else if (calibration != 1f)
                    {
                        var scaled = new float[style.Dash.Length];
                        for (int i = 0; i < scaled.Length; i++)
                            scaled[i] = style.Dash[i] * calibration;
                        paint.PathEffect = SKPathEffect.CreateDash(
                            scaled, style.DashOffset * calibration);
                    }
                    else
                    {
                        paint.PathEffect = SKPathEffect.CreateDash(style.Dash, style.DashOffset);
                    }
                }
            }

            if (spec.Kind == SvgValues.PaintKind.Color)
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

        private static float ResolvePathLengthCalibration(SvgElement element, SKPath geometry)
        {
            if (geometry == null) return 1f;
            string raw = element.GetPresentationProperty("path-length") ??
                         element.GetAttribute("pathLength");
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float declared, out var unit))
                return 1f;
            declared = SvgValues.ResolveUnits(declared, unit, DefaultFontSize, 1f);
            if (declared == 0f) return float.PositiveInfinity;
            if (!(declared > 0f) || !float.IsFinite(declared)) return 1f;

            float actual = 0f;
            using var measure = new SKPathMeasure(geometry, false);
            do { actual += measure.Length; } while (measure.NextContour());
            if (!(actual > 0f) || !float.IsFinite(actual)) return 1f;
            float calibration = actual / declared;
            return float.IsFinite(calibration) && calibration > 0f ? calibration : 1f;
        }

        private float ReadClampedOpacity(SvgElement el, string name, float defaultValue)
        {
            var raw = el.GetPresentationProperty(name);
            if (string.IsNullOrWhiteSpace(raw) || !SvgValues.TryParseNumber(raw.AsSpan(), out float v))
            {
                return defaultValue;
            }
            if (!SvgValues.IsFinite(v)) return defaultValue;
            return System.Math.Clamp(v, 0f, 1f);
        }

        /// <summary>Per-render memoized paint-server state (P0.3).</summary>
        internal sealed class CachedGradient
        {
            public bool IsValid;
            public bool IsRadial;
            public SKShaderTileMode Mode;
            public float[] Positions;
            public SKColor[] Colors;
            // Linear (unit space for oBB; user space for userSpaceOnUse).
            public SKPoint P1, P2;
            // Radial.
            public SKPoint Center, Focus;
            public float Radius;
            // Fully-built shader for userSpaceOnUse servers (extra baked in).
            public SKShader UserSpaceShader;
        }

        private CachedGradient GetCachedGradient(SvgElement server, InheritedStyle style)
        {
            var key = server;
            if (ShaderCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var entry = new CachedGradient();
            ShaderCache[key] = entry;

            var stops = CollectStops(server, style, visited: null);
            if (stops == null || stops.Length == 0)
            {
                return entry;
            }

            entry.Positions = StopsPositions(stops);
            entry.Colors = StopsColors(stops);
            entry.Mode = TileModeOf(server);
            entry.IsRadial = server.Name == "radialGradient";

            bool isObjectBoundingBox =
                !string.Equals(server.GetAttribute("gradientUnits"), "userSpaceOnUse", System.StringComparison.Ordinal);

            SKMatrix extra = SKMatrix.Identity;
            var gt = server.GetAttribute("gradientTransform");
            if (!string.IsNullOrWhiteSpace(gt) &&
                !SvgValues.TryParseTransformList(gt.AsSpan(), out extra))
            {
                return entry; // Malformed transform disables the paint server.
            }

            if (!isObjectBoundingBox)
            {
                // User-space coordinates are shape-independent: build the whole
                // shader once and reuse it for every referencing shape.
                if (!entry.IsRadial)
                {
                    entry.P1 = new SKPoint(
                        GradientCoord(server, "x1", 0f),
                        GradientCoord(server, "y1", 0f));
                    entry.P2 = new SKPoint(
                        GradientCoord(server, "x2", 1f),
                        GradientCoord(server, "y2", 0f));
                    entry.UserSpaceShader = SKShader.CreateLinearGradient(
                        entry.P1, entry.P2, entry.Colors, entry.Positions, entry.Mode);
                }
                else
                {
                    entry.Center = new SKPoint(
                        GradientCoord(server, "cx", 0.5f),
                        GradientCoord(server, "cy", 0.5f));
                    entry.Radius = GradientRadius(server);
                    entry.Focus = ResolveFocus(server, entry.Center);
                    if (entry.Radius > 0f)
                    {
                        entry.UserSpaceShader = SKShader.CreateTwoPointConicalGradient(
                            entry.Focus, 0f, entry.Center, entry.Radius,
                            entry.Colors, entry.Positions, entry.Mode);
                    }
                }
                if (entry.UserSpaceShader != null && !extra.IsIdentity)
                {
                    entry.UserSpaceShader = entry.UserSpaceShader.WithLocalMatrix(extra);
                }
                entry.IsValid = entry.UserSpaceShader != null;
            }
            else
            {
                // objectBoundingBox: geometry resolves in the 0..1 unit square
                // so only stops/mode are cached; the per-shape bbox matrix is
                // applied via WithLocalMatrix at use time.
                if (!entry.IsRadial)
                {
                    entry.P1 = new SKPoint(
                        GradientCoord(server, "x1", 0f),
                        GradientCoord(server, "y1", 0f));
                    entry.P2 = new SKPoint(
                        GradientCoord(server, "x2", 1f),
                        GradientCoord(server, "y2", 0f));
                }
                else
                {
                    entry.Center = new SKPoint(
                        GradientCoord(server, "cx", 0.5f),
                        GradientCoord(server, "cy", 0.5f));
                    entry.Radius = GradientRadius(server);
                }
                entry.IsValid = !entry.IsRadial || entry.Radius > 0f;
            }

            return entry;
        }

        private SKPoint ResolveFocus(SvgElement server, SKPoint center)
        {
            var focus = center;
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
            return focus;
        }

        private SKShader BuildServerShader(string fragment, SKPath path, string fallbackText, InheritedStyle style)
        {
            CachedGradient g = null;
            if (fragment != null && _doc.ElementsById.TryGetValue(fragment, out var server))
            {
                if (server.Name == "linearGradient" || server.Name == "radialGradient")
                {
                    g = GetCachedGradient(server, style);
                }
                else
                {
                    _report.RequireFallback(
                        $"paint server '{server.Name}' requires compatibility fallback");
                }
            }

            if (g == null || !g.IsValid)
            {
                // Invalid/degenerate reference: spec fallback color, else none.
                if (!string.IsNullOrEmpty(fallbackText) &&
                    SvgValues.TryParseColor(fallbackText.AsSpan(), out var fc))
                {
                    return SKShader.CreateColor(fc);
                }
                return null;
            }

            if (g.UserSpaceShader != null)
            {
                return g.UserSpaceShader; // shared, owned by cache for this render
            }

            var (matrix, degenerate) = ObjectBoundingBoxMatrix(path, SKMatrix.Identity);
            if (degenerate)
            {
                if (!string.IsNullOrEmpty(fallbackText) &&
                    SvgValues.TryParseColor(fallbackText.AsSpan(), out var fc2))
                {
                    return SKShader.CreateColor(fc2);
                }
                return null; // Zero-area bbox disables gradient (spec).
            }

            SKShader baseShader = g.IsRadial
                ? SKShader.CreateRadialGradient(g.Center, g.Radius, g.Colors, g.Positions, g.Mode)
                : SKShader.CreateLinearGradient(g.P1, g.P2, g.Colors, g.Positions, g.Mode);

            var gtText = fragment != null && _doc.ElementsById.TryGetValue(fragment, out var s2)
                ? s2.GetAttribute("gradientTransform")
                : null;
            SKMatrix extra = SKMatrix.Identity;
            if (!string.IsNullOrWhiteSpace(gtText) &&
                !SvgValues.TryParseTransformList(gtText.AsSpan(), out extra))
            {
                baseShader.Dispose();
                return null;
            }

            var finalMatrix = SKMatrix.Concat(matrix, extra);
            return baseShader.WithLocalMatrix(finalMatrix);
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

        private float GradientCoord(SvgElement server, string name, float defaultValue)
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
            if (visited.Contains(server) || visited.Count >= _maxReferenceDepth)
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
                    var span = offRaw.AsSpan().Trim();
                    bool wasPercent = span.EndsWith("%");
                    if (wasPercent)
                    {
                        span = span.Slice(0, span.Length - 1);
                    }
                    if (SvgValues.TryParseNumber(span, out float ov) && SvgValues.IsFinite(ov))
                    {
                        offset = wasPercent ? ov / 100f : ov;
                    }
                }
                offset = System.Math.Clamp(offset, 0f, 1f);

                var colorRaw = stop.GetPresentationProperty("stop-color") ?? "black";
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
                var soRaw = stop.GetPresentationProperty("stop-opacity");
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

    }
}
