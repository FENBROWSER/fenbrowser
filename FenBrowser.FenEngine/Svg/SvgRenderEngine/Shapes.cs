using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {

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

            // P0.6: parse rx/ry once (previously Attr + HasPositiveAttr each
            // re-parsed the same attribute string).
            bool hasRx = TryParsePositive(el, "rx", out float rx);
            bool hasRy = TryParsePositive(el, "ry", out float ry);
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
        private static bool TryParsePositive(SvgElement el, string name, out float value)
        {
            var raw = el.GetAttribute(name);
            if (!string.IsNullOrWhiteSpace(raw) &&
                SvgValues.TryParseLength(raw.AsSpan(), out value, out _) &&
                value > 0f)
            {
                return true;
            }
            value = 0f;
            return false;
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
            // P0.6: parse each attribute exactly once.
            float cx = Attr(el, "cx");
            float cy = Attr(el, "cy");
            float rx = Attr(el, "rx");
            float ry = Attr(el, "ry");
            if (rx <= 0f || ry <= 0f)
            {
                return false;
            }
            var oval = new SKRect(cx - rx, cy - ry, cx + rx, cy + ry);
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

    }
}
