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

        /// <param name="paintedArea">
        /// When known, a user-space rect that contains every pixel this paint can
        /// touch; lets an oversized pattern tile be recorded only where it shows.
        /// </param>
        private SKPaint BuildFillPaint(
            SvgElement el,
            InheritedStyle style,
            SKPath path,
            ViewportContext viewport,
            SKRect? paintedArea = null)
        {
            var spec = style.Fill;
            if (spec.Kind == SvgValues.PaintKind.None)
            {
                return null;
            }

            float opacity = ReadOwnPaintOpacity(el, "fill-opacity");
            if (opacity <= 0f)
            {
                return null;
            }

            var paint = new SKPaint { IsStroke = false, IsAntialias = true };

            if (spec.Kind == SvgValues.PaintKind.Color ||
                spec.Kind == SvgValues.PaintKind.CurrentColor)
            {
                var c = spec.Kind == SvgValues.PaintKind.CurrentColor
                    ? style.CurrentColor
                    : spec.Color;
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
            var shader = BuildServerShader(
                spec.Fragment, path, fallbackText: spec.Fallback, style, viewport,
                ResolveContextPaintFrame(spec.ContextSource, el, viewport), paintedArea,
                out var fallbackColor, out bool disposeShaderAfterAssignment);
            if (fallbackColor.HasValue)
            {
                var c = fallbackColor.Value;
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, (byte)(c.Alpha * opacity));
                return paint;
            }
            if (shader == null)
            {
                paint.Dispose();
                return null;
            }
            try
            {
                paint.Shader = shader;
            }
            finally
            {
                if (disposeShaderAfterAssignment)
                {
                    // SKPaint retains its own native reference. Release the managed
                    // shader handle created for this geometry immediately.
                    shader.Dispose();
                }
            }
            paint.Color = SKColors.White.WithAlpha((byte)(255 * opacity));
            return paint;
        }

        private SKPaint BuildStrokePaint(
            SvgElement el,
            InheritedStyle style,
            SKPath geometry,
            ViewportContext viewport,
            SKRect? paintedArea = null)
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

            float opacity = ReadOwnPaintOpacity(el, "stroke-opacity");
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

            if (spec.Kind == SvgValues.PaintKind.Color ||
                spec.Kind == SvgValues.PaintKind.CurrentColor)
            {
                var c = spec.Kind == SvgValues.PaintKind.CurrentColor
                    ? style.CurrentColor
                    : spec.Color;
                byte alpha = (byte)(c.Alpha * opacity);
                if (alpha == 0)
                {
                    paint.Dispose();
                    return null;
                }
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, alpha);
                return paint;
            }

            var shader = BuildServerShader(
                spec.Fragment, geometry, fallbackText: spec.Fallback, style, viewport,
                ResolveContextPaintFrame(spec.ContextSource, el, viewport), paintedArea,
                out var fallbackColor, out bool disposeShaderAfterAssignment);
            if (fallbackColor.HasValue)
            {
                var c = fallbackColor.Value;
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, (byte)(c.Alpha * opacity));
                return paint;
            }
            if (shader == null)
            {
                paint.Dispose();
                return null;
            }
            try
            {
                paint.Shader = shader;
            }
            finally
            {
                if (disposeShaderAfterAssignment)
                {
                    shader.Dispose();
                }
            }
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

        private float ReadOwnPaintOpacity(SvgElement el, string name) =>
            TryParseOpacity(el.GetPresentationProperty(name), out float opacity) ? opacity : 1f;

        internal sealed class CachedGradient
        {
            public bool IsValid;
            public bool IsRadial;
            public bool IsUserSpace;
            public SKShaderTileMode Mode;
            public float[] Positions;
            public SKColor[] Colors;
            public SKPoint P1, P2;
            public SKPoint Center, Focus;
            public float Radius;
            public float FocalRadius;
            public SKMatrix Transform;
        }

        private sealed class GradientDefinition
        {
            public string X1;
            public string Y1;
            public string X2;
            public string Y2;
            public string Cx;
            public string Cy;
            public string R;
            public string Fx;
            public string Fy;
            public string Fr;
            public string GradientUnits;
            public string SpreadMethod;
            public SvgElement TransformOwner;
            public SKMatrix Transform = SKMatrix.Identity;
        }

        private readonly Dictionary<GradientCacheKey, CachedGradient> _gradientCache = new();
        private readonly record struct GradientCacheKey(
            SvgElement Server,
            SKColor CurrentColor,
            float FontSize,
            float ViewportWidth,
            float ViewportHeight);

        private CachedGradient GetCachedGradient(
            SvgElement server,
            InheritedStyle style,
            ViewportContext viewport)
        {
            var key = new GradientCacheKey(
                server,
                style.CurrentColor,
                style.FontSize,
                viewport.Width,
                viewport.Height);
            if (_gradientCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var entry = new CachedGradient();
            _gradientCache[key] = entry;

            if (!TryResolveGradientDefinition(server, style, viewport, out var definition))
            {
                return entry;
            }

            var stops = CollectStops(server, style, visited: null);
            if (stops == null || stops.Length == 0)
            {
                return entry;
            }

            entry.Positions = StopsPositions(stops);
            entry.Colors = StopsColors(stops);
            entry.Mode = TileModeOf(definition.SpreadMethod);
            entry.IsRadial = server.Name == "radialGradient";
            entry.IsUserSpace = string.Equals(
                definition.GradientUnits, "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(definition.GradientUnits) &&
                !entry.IsUserSpace &&
                !string.Equals(
                    definition.GradientUnits, "objectBoundingBox", StringComparison.OrdinalIgnoreCase))
            {
                _report.RequireFallback(
                    $"SVG gradientUnits '{definition.GradientUnits}' requires compatibility fallback");
                return entry;
            }

            SKMatrix transform = definition.Transform;
            if (!transform.TryInvert(out _))
            {
                return entry;
            }
            entry.Transform = transform;

            if (entry.IsUserSpace &&
                (!SvgValues.IsFinite(viewport.Width) || !SvgValues.IsFinite(viewport.Height) ||
                 viewport.Width <= 0f || viewport.Height <= 0f))
            {
                _report.RequireFallback("SVG userSpaceOnUse gradient requires a finite viewport");
                return entry;
            }

            float xReference = entry.IsUserSpace ? viewport.Width : 1f;
            float yReference = entry.IsUserSpace ? viewport.Height : 1f;
            float radiusReference = entry.IsUserSpace
                ? MathF.Sqrt((viewport.Width * viewport.Width + viewport.Height * viewport.Height) / 2f)
                : 1f;
            if (entry.IsUserSpace && !SvgValues.IsFinite(radiusReference))
            {
                _report.RequireFallback("SVG userSpaceOnUse gradient requires a finite normalized diagonal");
                return entry;
            }
            float x1Default = 0f;
            float y1Default = 0f;
            float x2Default = entry.IsUserSpace ? xReference : 1f;
            float y2Default = 0f;
            float cxDefault = entry.IsUserSpace ? xReference * 0.5f : 0.5f;
            float cyDefault = entry.IsUserSpace ? yReference * 0.5f : 0.5f;
            float radiusDefault = entry.IsUserSpace ? radiusReference * 0.5f : 0.5f;
            if (entry.IsRadial)
            {
                bool hasFx = !string.IsNullOrWhiteSpace(definition.Fx);
                bool hasFy = !string.IsNullOrWhiteSpace(definition.Fy);
                if (!TryGradientLength(definition.Cx, cxDefault, xReference, style.FontSize, out float cx) ||
                    !TryGradientLength(definition.Cy, cyDefault, yReference, style.FontSize, out float cy) ||
                    !TryGradientLength(definition.R, radiusDefault, radiusReference, style.FontSize, out float radius) ||
                    !TryGradientLength(definition.Fx, cx, xReference, style.FontSize, out float fx) ||
                    !TryGradientLength(definition.Fy, cy, yReference, style.FontSize, out float fy) ||
                    !TryGradientLength(definition.Fr, 0f, radiusReference, style.FontSize, out float focalRadius) ||
                    radius <= 0f || !SvgValues.IsFinite(radius) ||
                    !(focalRadius >= 0f) || !SvgValues.IsFinite(focalRadius))
                {
                    return entry;
                }
                entry.Center = new SKPoint(cx, cy);
                entry.Focus = entry.IsUserSpace
                    ? new SKPoint(fx, fy)
                    : ClampObjectBoundingBoxFocus(fx, fy, hasFx, hasFy);
                entry.Radius = radius;
                entry.FocalRadius = focalRadius;
            }
            else if (!TryGradientLength(definition.X1, x1Default, xReference, style.FontSize, out float x1) ||
                     !TryGradientLength(definition.Y1, y1Default, yReference, style.FontSize, out float y1) ||
                     !TryGradientLength(definition.X2, x2Default, xReference, style.FontSize, out float x2) ||
                     !TryGradientLength(definition.Y2, y2Default, yReference, style.FontSize, out float y2))
            {
                return entry;
            }
            else
            {
                entry.P1 = new SKPoint(x1, y1);
                entry.P2 = new SKPoint(x2, y2);
            }

            entry.IsValid = !entry.IsRadial || (entry.Radius > 0f && SvgValues.IsFinite(entry.Radius));
            return entry;
        }

        private bool TryResolveGradientDefinition(
            SvgElement server,
            InheritedStyle style,
            ViewportContext viewport,
            out GradientDefinition definition)
        {
            definition = new GradientDefinition();
            var visited = new HashSet<SvgElement>();
            SvgElement current = server;
            while (current != null)
            {
                CheckDeadline();
                if (current.Name is not ("linearGradient" or "radialGradient") ||
                    !visited.Add(current) || visited.Count > _maxReferenceDepth)
                {
                    _report.RequireFallback("SVG paint server gradient template requires compatibility fallback");
                    return false;
                }

                definition.X1 ??= InheritedAttribute(current, "x1");
                definition.Y1 ??= InheritedAttribute(current, "y1");
                definition.X2 ??= InheritedAttribute(current, "x2");
                definition.Y2 ??= InheritedAttribute(current, "y2");
                definition.Cx ??= InheritedAttribute(current, "cx");
                definition.Cy ??= InheritedAttribute(current, "cy");
                definition.R ??= InheritedAttribute(current, "r");
                definition.Fx ??= InheritedAttribute(current, "fx");
                definition.Fy ??= InheritedAttribute(current, "fy");
                definition.Fr ??= InheritedAttribute(current, "fr");
                definition.GradientUnits ??= InheritedAttribute(current, "gradientUnits");
                definition.SpreadMethod ??= InheritedAttribute(current, "spreadMethod");
                if (definition.TransformOwner == null)
                {
                    switch (ResolveServerTransform(current, "gradientTransform", style, viewport, out var candidate))
                    {
                        case ServerTransformStatus.NotSpecified:
                            break;
                        case ServerTransformStatus.Resolved:
                            definition.TransformOwner = current;
                            definition.Transform = candidate;
                            break;
                        default:
                            _report.RequireFallback(
                                "SVG gradientTransform requires compatibility fallback");
                            return false;
                    }
                }

                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (string.IsNullOrWhiteSpace(href))
                {
                    break;
                }
                if (!SvgValues.TryParseLocalReference(href, out string id))
                {
                    _report.RejectResource("SVG gradient external template reference rejected");
                    return false;
                }
                if (!_doc.ElementsById.TryGetValue(id, out var template) ||
                    template.Name != server.Name)
                {
                    _report.RequireFallback("SVG paint server gradient template reference is invalid or unsupported");
                    return false;
                }
                current = template;
            }
            return true;
        }

        private static string InheritedAttribute(SvgElement element, string name)
        {
            string value = element.GetAttribute(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private enum ServerTransformStatus
        {
            NotSpecified,
            Resolved,
            Unresolved
        }

        /// <summary>
        /// A paint server's transform. gradientTransform and patternTransform are
        /// presentation attributes for the transform property (CSS Transforms 1 §6,
        /// SVG 2 §14.2.2), so a CSS declaration wins and uses CSS syntax (units,
        /// transform-origin); only the attribute uses the SVG list grammar.
        /// </summary>
        private static ServerTransformStatus ResolveServerTransform(
            SvgElement element,
            string attributeName,
            InheritedStyle style,
            ViewportContext viewport,
            out SKMatrix transform)
        {
            transform = SKMatrix.Identity;
            bool fromCss = element.CascadedDeclarations != null &&
                           element.CascadedDeclarations.ContainsKey("transform");
            string raw = fromCss
                ? element.GetPresentationProperty("transform")
                : InheritedAttribute(element, attributeName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return ServerTransformStatus.NotSpecified;
            }
            if (fromCss)
            {
                // Paint servers have no CSS layout box: the reference box is the
                // nearest viewport (view-box) and transform-origin defaults to 0 0.
                return SvgCssTransform.TryResolve(
                        raw,
                        element.GetPresentationProperty("transform-origin"),
                        element.GetPresentationProperty("transform-box"),
                        viewport.Width,
                        viewport.Height,
                        style.FontSize,
                        style.RootFontSize,
                        fillBox: null,
                        out transform,
                        out _) switch
                    {
                        SvgCssTransformStatus.None => ServerTransformStatus.NotSpecified,
                        SvgCssTransformStatus.Identity or SvgCssTransformStatus.Matrix => ServerTransformStatus.Resolved,
                        _ => ServerTransformStatus.Unresolved
                    };
            }
            if (string.Equals(raw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return ServerTransformStatus.Resolved;
            }
            if (!fromCss && SvgCssCascade.IsDefinitelyInvalid("transform", raw))
            {
                return ServerTransformStatus.NotSpecified;
            }
            return SvgValues.TryParseTransformList(raw.AsSpan(), out transform)
                ? ServerTransformStatus.Resolved
                : ServerTransformStatus.Unresolved;
        }

        private bool TryGradientLength(
            string raw,
            float fallback,
            float percentReference,
            float fontSize,
            out float value)
        {
            value = fallback;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float parsed, out var unit))
            {
                _report.RequireFallback("SVG paint server gradient coordinate requires compatibility fallback");
                return false;
            }
            float resolved = SvgValues.ResolveUnits(parsed, unit, fontSize, percentReference);
            if (!SvgValues.IsFinite(resolved))
            {
                _report.RequireFallback("SVG paint server gradient coordinate is non-finite");
                return false;
            }
            value = SvgValues.ClampCoord(resolved);
            return true;
        }

        private static SKPoint ClampObjectBoundingBoxFocus(
            float fx,
            float fy,
            bool hasFx,
            bool hasFy)
        {
            return new SKPoint(
                hasFx ? System.Math.Clamp(fx, 0f, 1f) : fx,
                hasFy ? System.Math.Clamp(fy, 0f, 1f) : fy);
        }

        private SKShader BuildServerShader(
            string fragment,
            SKPath path,
            string fallbackText,
            InheritedStyle style,
            ViewportContext viewport,
            ContextPaintFrame context,
            SKRect? paintedArea,
            out SKColor? fallbackColor,
            out bool disposeShaderAfterAssignment)
        {
            fallbackColor = null;
            disposeShaderAfterAssignment = false;
            CachedGradient g = null;
            if (fragment != null && _doc.ElementsById.TryGetValue(fragment, out var server))
            {
                if (server.Name == "linearGradient" || server.Name == "radialGradient")
                {
                    g = GetCachedGradient(server, style, viewport);
                }
                else if (server.Name == "pattern")
                {
                    var patternShader = BuildPatternShader(
                        server, path, context, style, viewport, paintedArea);
                    if (patternShader != null)
                    {
                        disposeShaderAfterAssignment = true;
                        return patternShader;
                    }
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
                if (TryResolvePaintFallbackColor(fallbackText, style, out var color))
                {
                    fallbackColor = color;
                }
                return null;
            }

            SKShader baseShader;
            if (g.IsUserSpace)
            {
                baseShader = g.IsRadial
                    ? SKShader.CreateTwoPointConicalGradient(
                        g.Focus, FocalRadiusOf(g), g.Center, g.Radius, g.Colors, g.Positions, g.Mode)
                    : SKShader.CreateLinearGradient(
                        g.P1, g.P2, g.Colors, g.Positions, g.Mode);
            }
            else
            {
                var (matrix, degenerate) = ObjectBoundingBoxMatrix(
                    ResolveObjectBoundingBox(context, path), SKMatrix.Identity);
                if (degenerate)
                {
                    if (TryResolvePaintFallbackColor(fallbackText, style, out var color))
                    {
                        fallbackColor = color;
                    }
                    return null;
                }

                baseShader = g.IsRadial
                    ? (g.Focus == g.Center && FocalRadiusOf(g) == 0f
                        ? SKShader.CreateRadialGradient(
                            g.Center, g.Radius, g.Colors, g.Positions, g.Mode)
                        : SKShader.CreateTwoPointConicalGradient(
                            g.Focus, FocalRadiusOf(g), g.Center, g.Radius,
                            g.Colors, g.Positions, g.Mode))
                    : SKShader.CreateLinearGradient(
                        g.P1, g.P2, g.Colors, g.Positions, g.Mode);
                if (baseShader == null)
                {
                    return null;
                }
                var finalMatrix = context == null
                    ? SKMatrix.Concat(matrix, g.Transform)
                    : SKMatrix.Concat(
                        SKMatrix.Concat(context.ToElementSpace, matrix), g.Transform);
                var shader = baseShader.WithLocalMatrix(finalMatrix);
                if (!ReferenceEquals(shader, baseShader))
                {
                    baseShader.Dispose();
                }
                disposeShaderAfterAssignment = shader != null;
                return shader;
            }

            if (baseShader == null)
            {
                return null;
            }
            var userSpaceMatrix = ServerSpaceMatrix(g.Transform, context);
            if (!userSpaceMatrix.IsIdentity)
            {
                var transformed = baseShader.WithLocalMatrix(userSpaceMatrix);
                if (!ReferenceEquals(transformed, baseShader))
                {
                    baseShader.Dispose();
                }
                baseShader = transformed;
            }
            disposeShaderAfterAssignment = baseShader != null;
            return baseShader;
        }

        private static SKMatrix ServerSpaceMatrix(SKMatrix transform, ContextPaintFrame context) =>
            context == null ? transform : SKMatrix.Concat(transform, context.ToElementSpace);

        private static float FocalRadiusOf(CachedGradient g) =>
            g.FocalRadius > 0f && g.FocalRadius < g.Radius ? g.FocalRadius : 0f;

        private static bool TryResolvePaintFallbackColor(
            string fallbackText,
            InheritedStyle style,
            out SKColor color)
        {
            color = default;
            if (string.Equals(fallbackText, "currentColor", System.StringComparison.OrdinalIgnoreCase))
            {
                color = style.CurrentColor;
                return true;
            }
            return !string.IsNullOrEmpty(fallbackText) &&
                   SvgValues.TryParseColor(fallbackText.AsSpan(), out color);
        }

        private (SKMatrix matrix, bool degenerate) ObjectBoundingBoxMatrix(SKRect? bounds, SKMatrix extra)
        {
            if (!bounds.HasValue)
            {
                // Server refs without resolvable geometry cannot resolve oBB space.
                return (SKMatrix.Identity, true);
            }

            var box = bounds.Value;
            if (!IsPaintableBounds(box))
            {
                return (SKMatrix.Identity, true);
            }

            // unit square -> bbox: translate(bx,by) * scale(bw,bh)
            var m = SKMatrix.Concat(
                SKMatrix.CreateTranslation(box.Left, box.Top),
                SKMatrix.CreateScale(box.Width, box.Height));
            m = SKMatrix.Concat(m, extra);
            return (m, false);
        }

        private static bool IsPaintableBounds(SKRect bounds) =>
            bounds.Width > 0f && bounds.Height > 0f && IsFinite(bounds);

        private static SKRect? ResolveObjectBoundingBox(
            ContextPaintFrame context,
            SKPath geometry)
        {
            if (context != null && IsPaintableBounds(context.Bounds))
            {
                return context.Bounds;
            }
            SKRect local = geometry?.TightBounds ?? default;
            return geometry != null && IsPaintableBounds(local) ? local : null;
        }

        private const int MaxContextPaintFrames = 128;

        private sealed class ContextPaintFrame
        {
            public SKRect Bounds;
            public SKMatrix ToElementSpace;
        }

        private readonly record struct ContextPaintKey(
            SvgElement Target,
            SvgElement Element,
            float ViewportWidth,
            float ViewportHeight);

        private readonly Dictionary<ContextPaintKey, ContextPaintFrame> _contextPaintFrames = new();

        /// <summary>
        /// Coordinate frame a context paint server is resolved in. A `use`
        /// context paints on the instantiated content, so the server belongs to
        /// the referenced element's object bounding box, not to the box of the
        /// single element being painted, and that box has to be mapped into the
        /// painted element's own user space - the space the element's transform
        /// is applied on top of, exactly as for a directly referenced server.
        /// A context source that is not a `use`, a nested viewport target, or an
        /// unresolvable frame yields null, and the caller falls back to the
        /// painted geometry exactly like a direct paint server.
        /// </summary>
        private ContextPaintFrame ResolveContextPaintFrame(
            SvgElement source,
            SvgElement el,
            ViewportContext viewport)
        {
            SvgElement target = ContextPaintTarget(source);
            if (target == null || el == null) return null;
            if (target.Name is "svg" or "symbol") return null;

            var key = new ContextPaintKey(target, el, viewport.Width, viewport.Height);
            if (_contextPaintFrames.TryGetValue(key, out var memoized)) return memoized;

            ContextPaintFrame frame = null;
            if (TryResolveObjectBounds(target, viewport, out var bounds) &&
                TryResolveContextToElementSpace(el, target, viewport, out var toElementSpace))
            {
                frame = new ContextPaintFrame
                {
                    Bounds = bounds,
                    ToElementSpace = toElementSpace
                };
            }
            if (_contextPaintFrames.Count < MaxContextPaintFrames)
            {
                _contextPaintFrames[key] = frame;
            }
            return frame;
        }

        private SvgElement ContextPaintTarget(SvgElement source)
        {
            if (source == null || source.Name != "use") return null;
            string href = source.GetAttribute("href") ?? source.GetLookup("xlink:href");
            if (!SvgValues.TryParseLocalReference(href, out string id)) return null;
            return _doc.ElementsById.TryGetValue(id, out var target) ? target : null;
        }

        private bool TryResolveContextToElementSpace(
            SvgElement el,
            SvgElement target,
            ViewportContext viewport,
            out SKMatrix toElementSpace)
        {
            toElementSpace = SKMatrix.Identity;
            SKMatrix toTargetSpace = SKMatrix.Identity;
            SvgElement current = el;
            for (int hops = 0; current != null; hops++)
            {
                if (hops > _maxReferenceDepth) return false;
                CheckDeadline();
                if (!TryResolveObjectBoundsTransform(current, viewport, out var step))
                {
                    return false;
                }
                toTargetSpace = toTargetSpace.IsIdentity
                    ? step
                    : SKMatrix.Concat(step, toTargetSpace);
                if (ReferenceEquals(current, target)) break;
                current = current.Parent;
            }
            if (!ReferenceEquals(current, target)) return false;
            if (toTargetSpace.IsIdentity) return true;
            return toTargetSpace.TryInvert(out toElementSpace) &&
                SvgValues.IsFinite(toElementSpace);
        }

        private static SKShaderTileMode TileModeOf(string spread)
        {
            if (string.Equals(spread, "repeat", System.StringComparison.OrdinalIgnoreCase))
            {
                return SKShaderTileMode.Repeat;
            }
            if (string.Equals(spread, "reflect", System.StringComparison.OrdinalIgnoreCase))
            {
                return SKShaderTileMode.Mirror;
            }
            return SKShaderTileMode.Clamp;
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
            CheckDeadline();
            if (server.Name is not ("linearGradient" or "radialGradient") ||
                visited.Contains(server) || visited.Count >= _maxReferenceDepth)
            {
                _report.RequireFallback("SVG paint server gradient stop template requires compatibility fallback");
                return System.Array.Empty<(float, SKColor)>();
            }
            visited.Add(server);

            var own = new List<(float offset, SKColor color)>();
            var children = server.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if ((i & 0xF) == 0) CheckDeadline();
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
                if (colorRaw.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
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

            string href = server.GetAttribute("href") ?? server.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href))
            {
                return System.Array.Empty<(float, SKColor)>();
            }
            if (!SvgValues.TryParseLocalReference(href, out string id))
            {
                _report.RejectResource("SVG gradient external stop template rejected");
                return System.Array.Empty<(float, SKColor)>();
            }
            if (!_doc.ElementsById.TryGetValue(id, out var template) ||
                template.Name is not ("linearGradient" or "radialGradient"))
            {
                _report.RequireFallback("SVG paint server gradient stop template is invalid or unsupported");
                return System.Array.Empty<(float, SKColor)>();
            }

            return CollectStops(template, style, visited);
        }

    }
}
