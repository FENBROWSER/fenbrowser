using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private const int MaxTextRenderCharsPerElement = 4096;
        private const int MaxTextGlyphsPerDocument = 16384;

        private void DrawTextElement(SvgElement el, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
            var state = new TextLayoutState();
            var runs = new List<TextPaintRun>();
            var chunks = new List<TextChunk>();
            LayoutTextElement(el, viewport, inherited, ResolveAncestorTextStyle(el), state, runs, chunks, true);
            ApplyTextAnchors(runs, chunks);
            if (runs.Count == 0) return;

            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                foreach (var run in runs)
                {
                    CheckDeadline();
                    PaintGlyphRun(canvas, run);
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
        }

        private void LayoutTextElement(
            SvgElement element,
            ViewportContext viewport,
            InheritedStyle inheritedPaint,
            TextStyle inheritedText,
            TextLayoutState state,
            List<TextPaintRun> runs,
            List<TextChunk> chunks,
            bool isRoot)
        {
            CheckDeadline();
            if (IsDisplayNone(element)) return;
            if (!isRoot &&
                (!string.IsNullOrWhiteSpace(element.GetAttribute("transform")) ||
                 !string.IsNullOrWhiteSpace(element.GetPresentationProperty("clip-path"))))
            {
                _report.RequireFallback("transformed or clipped tspan requires compatibility fallback");
            }
            if (!isRoot)
            {
                string opacity = element.GetPresentationProperty("opacity");
                if (!string.IsNullOrWhiteSpace(opacity) &&
                    (!SvgValues.TryParseNumber(opacity.AsSpan(), out float parsedOpacity) || parsedOpacity < 1f))
                {
                    _report.RequireFallback("isolated tspan opacity requires compatibility fallback");
                }
            }
            if ((element.TextContent?.Length ?? 0) > MaxTextRenderCharsPerElement)
            {
                _report.RequireFallback("SVG text exceeds first-party render length budget");
                return;
            }

            var paintStyle = inheritedPaint.ResolveOverrides(element, _report);
            if (!paintStyle.Visibility) return;
            var textStyle = ResolveTextStyle(element, inheritedText);

            bool hasX = TryResolveSingleTextLength(element.GetAttribute("x"), state.X, viewport.Width, textStyle.FontSize, out float x);
            bool hasY = TryResolveSingleTextLength(element.GetAttribute("y"), state.Y, viewport.Height, textStyle.FontSize, out float y);
            if (hasX) state.X = x;
            if (hasY) state.Y = y;
            if (TryResolveSingleTextLength(element.GetAttribute("dx"), 0f, viewport.Width, textStyle.FontSize, out float dx)) state.X += dx;
            if (TryResolveSingleTextLength(element.GetAttribute("dy"), 0f, viewport.Height, textStyle.FontSize, out float dy)) state.Y += dy;

            if (isRoot || hasX || hasY || state.CurrentChunk < 0)
            {
                state.CurrentChunk = chunks.Count;
                chunks.Add(new TextChunk(state.X, textStyle.Anchor));
            }

            if (element.Content.Count == 0 && !string.IsNullOrEmpty(element.TextContent))
            {
                ShapeTextPart(element.TextContent, element, paintStyle, textStyle, state, runs, !isRoot);
                return;
            }

            foreach (var part in element.Content)
            {
                if (part.IsText)
                {
                    ShapeTextPart(part.Text, element, paintStyle, textStyle, state, runs, !isRoot);
                }
                else if (part.Element?.Name == "tspan")
                {
                    LayoutTextElement(part.Element, viewport, paintStyle, textStyle, state, runs, chunks, false);
                }
            }
        }

        private void ShapeTextPart(
            string rawText,
            SvgElement element,
            InheritedStyle paintStyle,
            TextStyle textStyle,
            TextLayoutState state,
            List<TextPaintRun> runs,
            bool applyOwnOpacity)
        {
            string text = NormalizeText(rawText, textStyle.PreserveWhitespace, state);
            if (text.Length == 0) return;

            var typeface = SvgTypefaceResolver.Resolve(textStyle.Family, text, textStyle.Weight, textStyle.Slant);
            if (typeface == null)
            {
                _report.RequireFallback("SVG text has no available typeface");
                return;
            }

            var glyphRun = SkiaFontService.ShapeWithTypeface(text, typeface, textStyle.FontSize);
            if (glyphRun.Count == 0)
            {
                _report.RequireFallback("SVG text shaping produced no glyphs");
                return;
            }
            if (!glyphRun.WasShaped && ContainsComplexText(text))
            {
                _report.RequireFallback("complex SVG text requires an available HarfBuzz shaper");
                return;
            }
            if (state.GlyphCount + glyphRun.Count > MaxTextGlyphsPerDocument)
            {
                _report.RequireFallback("SVG text exceeds first-party glyph budget");
                return;
            }

            if (textStyle.LetterSpacing != 0f && glyphRun.Count > 1)
            {
                for (int i = 1; i < glyphRun.Glyphs.Length; i++) glyphRun.Glyphs[i].X += textStyle.LetterSpacing * i;
                glyphRun.Width += textStyle.LetterSpacing * (glyphRun.Count - 1);
            }

            runs.Add(new TextPaintRun(element, paintStyle, glyphRun, state.X, state.Y, state.CurrentChunk, applyOwnOpacity));
            state.X += glyphRun.Width;
            state.GlyphCount += glyphRun.Count;
            state.HasRenderedText = true;
        }

        private void PaintGlyphRun(SKCanvas canvas, TextPaintRun run)
        {
            SKPaint layerPaint = null;
            bool layered = run.ApplyOwnOpacity && TryBeginGroupOpacity(run.Element, canvas, out layerPaint);
            try
            {
                using var font = new SKFont(run.GlyphRun.Typeface, run.GlyphRun.FontSize)
                {
                    Edging = SKFontEdging.Antialias,
                    Subpixel = true
                };
                var metrics = font.Metrics;
                using var boundsBuilder = new SKPathBuilder();
                boundsBuilder.AddRect(new SKRect(run.X, run.Y + metrics.Ascent, run.X + Math.Max(1f, run.GlyphRun.Width), run.Y + metrics.Descent));
                using var boundsPath = boundsBuilder.Detach();
                using var fillPaint = BuildFillPaint(run.Element, run.PaintStyle, boundsPath);
                using var strokePaint = BuildStrokePaint(run.Element, run.PaintStyle);
                using var blobBuilder = new SKTextBlobBuilder();
                var positioned = blobBuilder.AllocatePositionedRun(font, run.GlyphRun.Count);
                for (int i = 0; i < run.GlyphRun.Count; i++)
                {
                    positioned.Glyphs[i] = run.GlyphRun.Glyphs[i].GlyphId;
                    positioned.Positions[i] = new SKPoint(run.X + run.GlyphRun.Glyphs[i].X, run.Y + run.GlyphRun.Glyphs[i].Y);
                }
                using var blob = blobBuilder.Build();
                if (fillPaint != null) canvas.DrawText(blob, 0f, 0f, fillPaint);
                if (strokePaint != null) canvas.DrawText(blob, 0f, 0f, strokePaint);
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

        private static void ApplyTextAnchors(List<TextPaintRun> runs, List<TextChunk> chunks)
        {
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                float right = chunk.StartX;
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].Chunk == chunkIndex) right = Math.Max(right, runs[i].X + runs[i].GlyphRun.Width);
                }
                float width = Math.Max(0f, right - chunk.StartX);
                float shift = chunk.Anchor == TextAnchor.Middle ? -width / 2f : chunk.Anchor == TextAnchor.End ? -width : 0f;
                if (shift == 0f) continue;
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].Chunk == chunkIndex) runs[i].X += shift;
                }
            }
        }

        private TextStyle ResolveTextStyle(SvgElement element, TextStyle inherited)
        {
            string family = element.GetPresentationProperty("font-family");
            string sizeRaw = element.GetPresentationProperty("font-size");
            string weightRaw = element.GetPresentationProperty("font-weight");
            string styleRaw = element.GetPresentationProperty("font-style");
            string anchorRaw = element.GetPresentationProperty("text-anchor");
            string spacingRaw = element.GetPresentationProperty("letter-spacing");
            string xmlSpace = element.GetAttribute("xml:space");

            float fontSize = ResolveFontSize(sizeRaw, inherited.FontSize);
            float letterSpacing = inherited.LetterSpacing;
            if (!string.IsNullOrWhiteSpace(spacingRaw) && !spacingRaw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                SvgValues.TryParseLength(spacingRaw.AsSpan().Trim(), out float spacing, out var spacingUnit))
            {
                letterSpacing = SvgValues.ClampCoord(SvgValues.ResolveUnits(spacing, spacingUnit, fontSize, fontSize));
            }

            return new TextStyle(
                string.IsNullOrWhiteSpace(family) ? inherited.Family : family,
                fontSize,
                ParseFontWeight(weightRaw, inherited.Weight),
                ParseFontSlant(styleRaw, inherited.Slant),
                ParseTextAnchor(anchorRaw, inherited.Anchor),
                letterSpacing,
                string.IsNullOrWhiteSpace(xmlSpace) ? inherited.PreserveWhitespace : xmlSpace.Trim().Equals("preserve", StringComparison.OrdinalIgnoreCase));
        }

        private TextStyle ResolveAncestorTextStyle(SvgElement element)
        {
            var ancestors = new Stack<SvgElement>();
            for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                ancestors.Push(ancestor);
            }
            var style = TextStyle.Default;
            while (ancestors.Count > 0)
            {
                style = ResolveTextStyle(ancestors.Pop(), style);
            }
            return style;
        }

        private bool TryResolveSingleTextLength(string raw, float fallback, float percentReference, float fontSize, out float value)
        {
            value = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            if (!tokenizer.Next(out var token) || !SvgValues.TryParseLength(token, out float parsed, out var unit)) return false;
            if (tokenizer.Next(out _)) _report.RequireFallback("per-glyph SVG text positioning requires compatibility fallback");
            value = SvgValues.ClampCoord(SvgValues.ResolveUnits(parsed, unit, fontSize, percentReference));
            return true;
        }

        private static float ResolveFontSize(string raw, float inherited)
        {
            if (string.IsNullOrWhiteSpace(raw) || !SvgValues.TryParseLength(raw.AsSpan().Trim(), out float value, out var unit)) return inherited;
            float resolved = SvgValues.ResolveUnits(value, unit, inherited, inherited);
            return float.IsFinite(resolved) && resolved > 0f ? Math.Min(resolved, 4096f) : inherited;
        }

        private static string NormalizeText(string raw, bool preserve, TextLayoutState state)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (preserve)
            {
                state.PendingSpace = false;
                return raw.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            }
            var result = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (state.HasRenderedText || result.Length > 0) state.PendingSpace = true;
                }
                else
                {
                    if (state.PendingSpace) { result.Append(' '); state.PendingSpace = false; }
                    result.Append(c);
                }
            }
            return result.ToString();
        }

        private static bool ContainsComplexText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > 0x7f) return true;
            }
            return false;
        }

        private static int ParseFontWeight(string raw, int inherited)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inherited;
            if (raw.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase)) return 700;
            if (raw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase)) return 400;
            if (raw.Trim().Equals("bolder", StringComparison.OrdinalIgnoreCase)) return Math.Min(900, inherited + 300);
            if (raw.Trim().Equals("lighter", StringComparison.OrdinalIgnoreCase)) return Math.Max(100, inherited - 300);
            return int.TryParse(raw, out int weight) ? Math.Clamp(weight, 100, 900) : inherited;
        }

        private static SKFontStyleSlant ParseFontSlant(string raw, SKFontStyleSlant inherited)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inherited;
            if (raw.Trim().Equals("italic", StringComparison.OrdinalIgnoreCase)) return SKFontStyleSlant.Italic;
            if (raw.Trim().Equals("oblique", StringComparison.OrdinalIgnoreCase)) return SKFontStyleSlant.Oblique;
            return raw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase) ? SKFontStyleSlant.Upright : inherited;
        }

        private static TextAnchor ParseTextAnchor(string raw, TextAnchor inherited)
        {
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            return raw.Trim().ToLowerInvariant() switch
            {
                "start" => TextAnchor.Start,
                "middle" => TextAnchor.Middle,
                "end" => TextAnchor.End,
                _ => inherited
            };
        }

        private enum TextAnchor { Start, Middle, End }

        private sealed class TextLayoutState
        {
            public float X;
            public float Y;
            public int CurrentChunk = -1;
            public int GlyphCount;
            public bool HasRenderedText;
            public bool PendingSpace;
        }

        private readonly record struct TextStyle(string Family, float FontSize, int Weight, SKFontStyleSlant Slant, TextAnchor Anchor, float LetterSpacing, bool PreserveWhitespace)
        {
            public static TextStyle Default => new(null, DefaultFontSize, 400, SKFontStyleSlant.Upright, TextAnchor.Start, 0f, false);
        }

        private sealed class TextPaintRun
        {
            public TextPaintRun(SvgElement element, InheritedStyle paintStyle, GlyphRun glyphRun, float x, float y, int chunk, bool applyOwnOpacity)
            {
                Element = element;
                PaintStyle = paintStyle;
                GlyphRun = glyphRun;
                X = x;
                Y = y;
                Chunk = chunk;
                ApplyOwnOpacity = applyOwnOpacity;
            }
            public SvgElement Element { get; }
            public InheritedStyle PaintStyle { get; }
            public GlyphRun GlyphRun { get; }
            public float X { get; set; }
            public float Y { get; }
            public int Chunk { get; }
            public bool ApplyOwnOpacity { get; }
        }

        private readonly record struct TextChunk(float StartX, TextAnchor Anchor);
    }
}
