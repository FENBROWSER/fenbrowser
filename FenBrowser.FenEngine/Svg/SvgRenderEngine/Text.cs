using System;
using System.Text;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private const int MaxBasicTextRenderChars = 4096;

        private void DrawTextElement(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            if ((el.TextContent?.Length ?? 0) > MaxBasicTextRenderChars)
            {
                _report.RequireFallback("SVG text exceeds first-party render length budget");
                return;
            }
            string text = NormalizeText(el.TextContent);
            if (text.Length == 0) return;

            var style = inherited.ResolveOverrides(el, _report);
            if (!style.Visibility) return;

            float fontSize = ResolveTextLength(
                TextProperty(el, "font-size"), DefaultFontSize, DefaultFontSize);
            if (!(fontSize > 0f) || !float.IsFinite(fontSize)) fontSize = DefaultFontSize;
            fontSize = Math.Min(fontSize, 4096f);

            string family = TextProperty(el, "font-family");
            int weight = ParseFontWeight(TextProperty(el, "font-weight"));
            var slant = ParseFontSlant(TextProperty(el, "font-style"));
            var typeface = SvgTypefaceResolver.Resolve(family, text, weight, slant);
            if (typeface == null)
            {
                _report.RequireFallback("SVG text has no available typeface");
                return;
            }
            using var font = new SKFont(typeface, fontSize)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
                Embolden = weight >= 600,
                SkewX = slant == SKFontStyleSlant.Upright ? 0f : -0.25f
            };

            float x = ResolveTextLength(el.GetAttribute("x"), 0f, viewport.Width) +
                      ResolveTextLength(el.GetAttribute("dx"), 0f, viewport.Width);
            float y = ResolveTextLength(el.GetAttribute("y"), 0f, viewport.Height) +
                      ResolveTextLength(el.GetAttribute("dy"), 0f, viewport.Height);
            float measuredWidth = font.MeasureText(text);
            string anchor = TextProperty(el, "text-anchor")?.Trim();
            if (string.Equals(anchor, "middle", StringComparison.OrdinalIgnoreCase)) x -= measuredWidth / 2f;
            else if (string.Equals(anchor, "end", StringComparison.OrdinalIgnoreCase)) x -= measuredWidth;

            font.GetFontMetrics(out var metrics);
            using var boundsBuilder = new SKPathBuilder();
            boundsBuilder.AddRect(new SKRect(
                x,
                y + metrics.Ascent,
                x + Math.Max(1f, measuredWidth),
                y + metrics.Descent));
            using var boundsPath = boundsBuilder.Detach();

            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                using var fillPaint = BuildFillPaint(el, style, boundsPath);
                using var strokePaint = BuildStrokePaint(el, style);
                if (fillPaint != null) canvas.DrawText(text, x, y, SKTextAlign.Left, font, fillPaint);
                if (strokePaint != null) canvas.DrawText(text, x, y, SKTextAlign.Left, font, strokePaint);
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
        }

        private static string NormalizeText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var result = new StringBuilder(raw.Length);
            bool pendingSpace = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsWhiteSpace(c)) pendingSpace = result.Length > 0;
                else
                {
                    if (pendingSpace) { result.Append(' '); pendingSpace = false; }
                    result.Append(c);
                }
            }
            return result.ToString();
        }

        private static float ResolveTextLength(string raw, float fallback, float percentReference)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            if (!tokenizer.Next(out var token) ||
                !SvgValues.TryParseLength(token, out float value, out var unit)) return fallback;
            return SvgValues.ClampCoord(
                SvgValues.ResolveUnits(value, unit, DefaultFontSize, percentReference));
        }

        private static string TextProperty(SvgElement el, string name)
        {
            string style = el.GetAttribute("style");
            string matched = null;
            if (!string.IsNullOrWhiteSpace(style))
            {
                var remaining = style.AsSpan();
                int declarations = 0;
                while (!remaining.IsEmpty && declarations++ < 64)
                {
                    int semi = remaining.IndexOf(';');
                    var declaration = semi < 0 ? remaining : remaining.Slice(0, semi);
                    remaining = semi < 0 ? default : remaining.Slice(semi + 1);
                    int colon = declaration.IndexOf(':');
                    if (colon > 0 && declaration.Slice(0, colon).Trim().Equals(
                        name.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        matched = declaration.Slice(colon + 1).Trim().ToString();
                    }
                }
            }
            return matched ?? el.GetAttribute(name);
        }

        private static int ParseFontWeight(string raw)
        {
            if (string.Equals(raw?.Trim(), "bold", StringComparison.OrdinalIgnoreCase)) return 700;
            if (string.Equals(raw?.Trim(), "normal", StringComparison.OrdinalIgnoreCase)) return 400;
            return int.TryParse(raw, out int weight) ? Math.Clamp(weight, 100, 900) : 400;
        }

        private static SKFontStyleSlant ParseFontSlant(string raw)
        {
            if (string.Equals(raw?.Trim(), "italic", StringComparison.OrdinalIgnoreCase)) return SKFontStyleSlant.Italic;
            if (string.Equals(raw?.Trim(), "oblique", StringComparison.OrdinalIgnoreCase)) return SKFontStyleSlant.Oblique;
            return SKFontStyleSlant.Upright;
        }
    }
}
