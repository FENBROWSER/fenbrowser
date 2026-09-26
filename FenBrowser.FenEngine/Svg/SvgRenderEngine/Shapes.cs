using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {

        private void DrawShape(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            var style = inherited.ResolveOverrides(el, _report);
            if (!style.Visibility)
            {
                return; // visibility:hidden suppresses this shape (children may override).
            }

            using var path = BuildGeometry(el, viewport, style.FontSize, style.RootFontSize);
            if (path == null || path.IsEmpty)
            {
                return;
            }

            TryApplyFillRule(el, path);

            DrawWithEffects(el, canvas, viewport, () =>
            {
                bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
                try
                {
                    using var fillPaint = ApplyInheritedFillOpacity(
                        BuildFillPaint(el, style, path, viewport), style);
                    using var strokePaint = ApplyInheritedStrokeOpacity(
                        BuildStrokePaint(el, style, path, viewport), style);
                    bool nonScalingStroke =
                        SvgFeatureSupport.SupportsNonScalingStroke(
                            el, el.GetPresentationProperty("vector-effect"));
                    SKPath deviceStrokePath = null;
                    if (nonScalingStroke && style.Stroke.Kind == SvgValues.PaintKind.ServerRef)
                    {
                        _report.RequireFallback(
                            "SVG non-scaling paint-server stroke requires compatibility fallback");
                        nonScalingStroke = false;
                    }
                    if (nonScalingStroke && strokePaint != null)
                    {
                        deviceStrokePath = new SKPath();
                        path.Transform(canvas.TotalMatrix, deviceStrokePath);
                    }

                    try
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            switch (style.PaintOrder.At(i))
                            {
                                case PaintPhase.Fill when fillPaint != null:
                                    canvas.DrawPath(path, fillPaint);
                                    break;
                                case PaintPhase.Stroke when strokePaint != null:
                                    if (nonScalingStroke)
                                    {
                                        using var strokeScope = new CanvasState(canvas);
                                        canvas.ResetMatrix();
                                        canvas.DrawPath(deviceStrokePath, strokePaint);
                                    }
                                    else
                                    {
                                        canvas.DrawPath(path, strokePaint);
                                    }
                                    break;
                                case PaintPhase.Markers:
                                    DrawMarkers(el, canvas, viewport, style, path);
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        deviceStrokePath?.Dispose();
                    }
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

        private SKPath BuildGeometry(SvgElement element, ViewportContext viewport)
        {
            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize, _report);
            return BuildGeometry(element, viewport, fontSize, rootFontSize);
        }

        private static void ResolveGeometryFontContext(
            SvgElement element,
            out float fontSize,
            out float rootFontSize,
            SvgParseReport report = null)
        {
            var ancestry = new List<SvgElement>();
            for (SvgElement current = element; current != null; current = current.Parent)
                ancestry.Add(current);
            fontSize = DefaultFontSize;
            rootFontSize = DefaultFontSize;
            for (int i = ancestry.Count - 1; i >= 0; i--)
            {
                fontSize = ResolveFontSize(
                    ancestry[i].GetPresentationProperty("font-size"),
                    fontSize,
                    report);
                if (ancestry[i].Parent == null) rootFontSize = fontSize;
            }
        }

        private SKPath BuildGeometry(
            SvgElement el,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize)
        {
            if (el.Name == "path")
            {
                string d = ResolvePathData(el);
                if (string.IsNullOrEmpty(d))
                {
                    return null;
                }
                if (!SvgPathParser.TryBuildPath(d.AsSpan(), out var parsed, _report, CheckTime))
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
                    ok = AppendRect(el, builder, viewport, fontSize, rootFontSize);
                    break;
                case "circle":
                    ok = AppendCircle(el, builder, viewport, fontSize, rootFontSize);
                    break;
                case "ellipse":
                    ok = AppendEllipse(el, builder, viewport, fontSize, rootFontSize);
                    break;
                case "line":
                    ok = AppendLine(el, builder, viewport, fontSize, rootFontSize);
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

        private float Attr(
            SvgElement el,
            string name,
            ViewportContext viewport,
            float percentReference,
            float fontSize,
            float rootFontSize)
        {
            return TryResolveGeometryLength(
                    el, name, viewport, percentReference, fontSize, rootFontSize, out float value)
                ? value
                : 0f;
        }

        private bool TryResolveGeometryLength(
            SvgElement element,
            string name,
            ViewportContext viewport,
            float percentReference,
            float fontSize,
            float rootFontSize,
            out float value)
        {
            value = 0f;
            string raw = element.GetPresentationProperty(name);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (SvgValues.TryParseLength(raw.AsSpan(), out float parsed, out var unit))
            {
                if (element.UsesCssPropertySyntax(name) &&
                    unit == SvgValues.SvgUnit.User && parsed != 0f)
                    return false;
                value = SvgValues.ResolveUnits(parsed, unit, fontSize, percentReference);
            }
            else if (!SvgCssLengthEvaluator.TryEvaluate(
                         raw,
                         percentReference,
                         fontSize,
                         rootFontSize,
                         viewport.Width,
                         viewport.Height,
                         out value))
            {
                RequireFallbackForUnsupportedGeometryLength(element, name, raw);
                return false;
            }
            value = NormalizeGeometryValue(SvgValues.ClampCoord(value));
            return true;
        }

        private static float NormalizeGeometryValue(float value)
        {
            float nearestInteger = System.MathF.Round(value);
            return System.MathF.Abs(value - nearestInteger) <= 0.0001f
                ? nearestInteger
                : value;
        }

        private void RequireFallbackForUnsupportedGeometryLength(
            SvgElement element,
            string name,
            string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string value = raw.Trim();
            bool supportedCssSyntaxNotImplemented =
                (name is "width" or "height" &&
                 value.Equals("auto", System.StringComparison.OrdinalIgnoreCase)) ||
                SvgCssLengthEvaluator.RequiresUnsupportedUnitSupport(value);
            if (supportedCssSyntaxNotImplemented)
                _report.RequireFallback(
                    $"SVG CSS geometry value '{name}: {value}' requires compatibility fallback");
        }

        private static string ResolvePathData(SvgElement element)
        {
            string raw = element.GetPresentationProperty("d");
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Trim().Equals("none", System.StringComparison.OrdinalIgnoreCase))
                return null;
            string value = raw.Trim();
            if (!value.StartsWith("path(", System.StringComparison.OrdinalIgnoreCase)) return value;

            var tokenizer = new CssTokenizer(value);
            CssToken token;
            do { token = tokenizer.Consume(); } while (token.Type == CssTokenType.Whitespace);
            if (token.Type != CssTokenType.Function ||
                !token.Value.Equals("path", System.StringComparison.OrdinalIgnoreCase)) return null;
            do { token = tokenizer.Consume(); } while (token.Type == CssTokenType.Whitespace);
            if (token.Type != CssTokenType.String) return null;
            string data = token.Value;
            do { token = tokenizer.Consume(); } while (token.Type == CssTokenType.Whitespace);
            if (token.Type != CssTokenType.RightParen) return null;
            do { token = tokenizer.Consume(); } while (token.Type == CssTokenType.Whitespace);
            return token.Type == CssTokenType.EOF ? data : null;
        }

        private float ResolveCoord(string raw, float percentReference = 0f)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit))
                return 0f;
            float resolved = SvgValues.ResolveUnits(value, unit, DefaultFontSize, percentReference);
            return SvgValues.ClampCoord(resolved);
        }

        private bool AppendRect(
            SvgElement el,
            SKPathBuilder path,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize)
        {
            float x = Attr(el, "x", viewport, viewport.Width, fontSize, rootFontSize);
            float y = Attr(el, "y", viewport, viewport.Height, fontSize, rootFontSize);
            float w = Attr(el, "width", viewport, viewport.Width, fontSize, rootFontSize);
            float h = Attr(el, "height", viewport, viewport.Height, fontSize, rootFontSize);
            if (w <= 0f || h <= 0f)
            {
                return false; // Zero/negative extents disable rendering (spec).
            }

            // P0.6: parse rx/ry once (previously Attr + HasPositiveAttr each
            // re-parsed the same attribute string).
            bool hasRx = TryParsePositive(
                el, "rx", viewport, viewport.Width, fontSize, rootFontSize, out float rx);
            bool hasRy = TryParsePositive(
                el, "ry", viewport, viewport.Height, fontSize, rootFontSize, out float ry);
            if (!hasRx) rx = 0f;
            if (!hasRy) ry = 0f;

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

        /// <summary>Parses once; true only for a specified positive value.</summary>
        private bool TryParsePositive(
            SvgElement el,
            string name,
            ViewportContext viewport,
            float percentReference,
            float fontSize,
            float rootFontSize,
            out float value)
        {
            return TryResolveGeometryLength(
                       el, name, viewport, percentReference, fontSize, rootFontSize, out value) &&
                   value > 0f;
        }

        private bool AppendCircle(
            SvgElement el,
            SKPathBuilder path,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize)
        {
            float diagonal = MathF.Sqrt(
                (viewport.Width * viewport.Width + viewport.Height * viewport.Height) / 2f);
            float r = Attr(el, "r", viewport, diagonal, fontSize, rootFontSize);
            if (r <= 0f)
            {
                return false;
            }
            path.AddCircle(
                Attr(el, "cx", viewport, viewport.Width, fontSize, rootFontSize),
                Attr(el, "cy", viewport, viewport.Height, fontSize, rootFontSize),
                r);
            return true;
        }

        private bool AppendEllipse(
            SvgElement el,
            SKPathBuilder path,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize)
        {
            float cx = Attr(el, "cx", viewport, viewport.Width, fontSize, rootFontSize);
            float cy = Attr(el, "cy", viewport, viewport.Height, fontSize, rootFontSize);
            float? rxValue = ResolveAutoRadius(
                el, "rx", viewport, viewport.Width, fontSize, rootFontSize);
            float? ryValue = ResolveAutoRadius(
                el, "ry", viewport, viewport.Height, fontSize, rootFontSize);
            if (!rxValue.HasValue && !ryValue.HasValue) return false;
            float rx = rxValue ?? ryValue.Value;
            float ry = ryValue ?? rxValue.Value;
            if (rx <= 0f || ry <= 0f)
            {
                return false;
            }
            var oval = new SKRect(cx - rx, cy - ry, cx + rx, cy + ry);
            path.AddOval(oval);
            return true;
        }

        private float? ResolveAutoRadius(
            SvgElement element,
            string name,
            ViewportContext viewport,
            float percentReference,
            float fontSize,
            float rootFontSize)
        {
            string raw = element.GetPresentationProperty(name);
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Trim().Equals("auto", System.StringComparison.OrdinalIgnoreCase))
                return null;
            if (!TryResolveGeometryLength(
                    element, name, viewport, percentReference, fontSize, rootFontSize, out float value))
                return null;
            return value < 0f ? null : value;
        }

        private bool AppendLine(
            SvgElement el,
            SKPathBuilder path,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize)
        {
            path.MoveTo(
                Attr(el, "x1", viewport, viewport.Width, fontSize, rootFontSize),
                Attr(el, "y1", viewport, viewport.Height, fontSize, rootFontSize));
            path.LineTo(
                Attr(el, "x2", viewport, viewport.Width, fontSize, rootFontSize),
                Attr(el, "y2", viewport, viewport.Height, fontSize, rootFontSize));
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

    }
}
