using System;
using System.Collections.Generic;
using System.Linq;
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
                else if (part.Element?.Name == "textPath")
                {
                    LayoutTextPathElement(part.Element, viewport, paintStyle, textStyle, state, runs);
                }
            }
        }

        private void LayoutTextPathElement(
            SvgElement element,
            ViewportContext viewport,
            InheritedStyle inheritedPaint,
            TextStyle inheritedText,
            TextLayoutState state,
            List<TextPaintRun> runs)
        {
            string href = element.GetAttribute("href") ?? element.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href)) return;
            if (href[0] != '#')
            {
                _report.RejectResource("textPath external reference rejected by SVG resource policy");
                return;
            }
            if (!_doc.ElementsById.TryGetValue(href.Substring(1), out SvgElement target))
                return;
            if (target.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
            {
                _report.RequireFallback("SVG textPath target geometry requires compatibility fallback");
                return;
            }
            if (!string.IsNullOrWhiteSpace(target.GetAttribute("transform")) ||
                target.CascadedDeclarations?.ContainsKey("transform") == true)
            {
                _report.RequireFallback("transformed SVG textPath target requires compatibility fallback");
                return;
            }
            if (!string.IsNullOrWhiteSpace(element.GetAttribute("method")) ||
                !string.IsNullOrWhiteSpace(element.GetAttribute("spacing")) ||
                !string.IsNullOrWhiteSpace(element.GetAttribute("side")))
            {
                _report.RequireFallback("advanced SVG textPath layout requires compatibility fallback");
                return;
            }
            if (element.Content.Any(part => !part.IsText))
            {
                _report.RequireFallback("nested SVG textPath content requires compatibility fallback");
                return;
            }

            using var geometry = BuildGeometry(target, viewport);
            if (geometry == null) return;
            using var measure = new SKPathMeasure(geometry, false);
            float pathLength = measure.Length;
            if (!(pathLength > 0f) || !float.IsFinite(pathLength)) return;

            var paintStyle = inheritedPaint.ResolveOverrides(element, _report);
            if (!paintStyle.Visibility) return;
            var textStyle = ResolveTextStyle(element, inheritedText);
            string rawText = element.Content.Count == 0
                ? element.TextContent
                : string.Concat(element.Content.Where(part => part.IsText).Select(part => part.Text));
            int runIndex = runs.Count;
            TextPaintRun run = ShapeTextPart(
                rawText, element, paintStyle, textStyle, state, runs, applyOwnOpacity: true);
            if (run == null) return;

            float offset = ResolveTextPathOffset(element, target, pathLength, textStyle.FontSize);
            if (!float.IsFinite(offset))
            {
                runs.RemoveAt(runIndex);
                return;
            }
            offset += textStyle.Anchor == TextAnchor.Middle
                ? -run.GlyphRun.Width / 2f
                : textStyle.Anchor == TextAnchor.End ? -run.GlyphRun.Width : 0f;

            var glyphIds = new List<ushort>(run.GlyphRun.Count);
            var transforms = new List<SKRotationScaleMatrix>(run.GlyphRun.Count);
            for (int i = 0; i < run.GlyphRun.Count; i++)
            {
                if ((i & 255) == 0) CheckDeadline();
                PositionedGlyph glyph = run.GlyphRun.Glyphs[i];
                float advance = Math.Max(0f, glyph.AdvanceX);
                float centerDistance = offset + glyph.X + advance / 2f;
                if (centerDistance < 0f || centerDistance > pathLength) continue;
                if (!measure.GetPositionAndTangent(centerDistance, out SKPoint position, out SKPoint tangent))
                    continue;
                float degrees = MathF.Atan2(tangent.Y, tangent.X) * (180f / MathF.PI);
                glyphIds.Add(glyph.GlyphId);
                transforms.Add(SKRotationScaleMatrix.CreateDegrees(
                    1f, degrees, position.X, position.Y, advance / 2f, -glyph.Y));
            }
            if (glyphIds.Count == 0)
            {
                runs.RemoveAt(runIndex);
                return;
            }
            run.PathGlyphIds = glyphIds.ToArray();
            run.PathTransforms = transforms.ToArray();
            run.PathBounds = geometry.Bounds;
            run.Chunk = -1;
        }

        private static float ResolveTextPathOffset(
            SvgElement textPath,
            SvgElement target,
            float actualLength,
            float fontSize)
        {
            string raw = textPath.GetAttribute("startOffset");
            if (string.IsNullOrWhiteSpace(raw)) return 0f;
            raw = raw.Trim();

            bool percentage = raw.EndsWith("%", StringComparison.Ordinal);
            ReadOnlySpan<char> number = percentage ? raw.AsSpan(0, raw.Length - 1) : raw.AsSpan();
            if (!SvgValues.TryParseLength(number, out float declaredOffset, out var offsetUnit)) return 0f;

            string declaredPathLengthRaw = target.GetPresentationProperty("path-length") ??
                                           target.GetAttribute("pathLength");
            bool hasDeclaredPathLength = SvgValues.TryParseLength(
                declaredPathLengthRaw.AsSpan(), out float declaredPathLength, out var pathUnit);
            if (hasDeclaredPathLength)
                declaredPathLength = SvgValues.ResolveUnits(declaredPathLength, pathUnit, fontSize, actualLength);

            if (percentage)
            {
                float basis = hasDeclaredPathLength ? declaredPathLength : actualLength;
                declaredOffset = declaredOffset * 0.01f * basis;
                offsetUnit = SvgValues.SvgUnit.User;
            }
            else
            {
                declaredOffset = SvgValues.ResolveUnits(declaredOffset, offsetUnit, fontSize, actualLength);
            }

            if (!hasDeclaredPathLength) return declaredOffset;
            if (declaredPathLength == 0f)
                return declaredOffset == 0f ? 0f : MathF.CopySign(float.PositiveInfinity, declaredOffset);
            if (!(declaredPathLength > 0f) || !float.IsFinite(declaredPathLength)) return declaredOffset;
            return declaredOffset * (actualLength / declaredPathLength);
        }

        private TextPaintRun ShapeTextPart(
            string rawText,
            SvgElement element,
            InheritedStyle paintStyle,
            TextStyle textStyle,
            TextLayoutState state,
            List<TextPaintRun> runs,
            bool applyOwnOpacity)
        {
            string text = NormalizeText(rawText, textStyle.PreserveWhitespace, state);
            if (text.Length == 0) return null;

            var typeface = SvgTypefaceResolver.Resolve(textStyle.Family, text, textStyle.Weight, textStyle.Slant);
            if (typeface == null)
            {
                _report.RequireFallback("SVG text has no available typeface");
                return null;
            }

            var glyphRun = SkiaFontService.ShapeWithTypeface(text, typeface, textStyle.FontSize);
            if (glyphRun.Count == 0)
            {
                _report.RequireFallback("SVG text shaping produced no glyphs");
                return null;
            }
            if (!glyphRun.WasShaped && ContainsComplexText(text))
            {
                _report.RequireFallback("complex SVG text requires an available HarfBuzz shaper");
                return null;
            }
            if (state.GlyphCount + glyphRun.Count > MaxTextGlyphsPerDocument)
            {
                _report.RequireFallback("SVG text exceeds first-party glyph budget");
                return null;
            }

            if (textStyle.LetterSpacing != 0f && glyphRun.Count > 1)
            {
                for (int i = 1; i < glyphRun.Glyphs.Length; i++) glyphRun.Glyphs[i].X += textStyle.LetterSpacing * i;
                glyphRun.Width += textStyle.LetterSpacing * (glyphRun.Count - 1);
            }

            var run = new TextPaintRun(element, paintStyle, glyphRun, state.X, state.Y, state.CurrentChunk, applyOwnOpacity);
            runs.Add(run);
            state.X += glyphRun.Width;
            state.GlyphCount += glyphRun.Count;
            state.HasRenderedText = true;
            return run;
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
                boundsBuilder.AddRect(run.PathTransforms == null
                    ? new SKRect(run.X, run.Y + metrics.Ascent, run.X + Math.Max(1f, run.GlyphRun.Width), run.Y + metrics.Descent)
                    : run.PathBounds);
                using var boundsPath = boundsBuilder.Detach();
                using var fillPaint = BuildFillPaint(run.Element, run.PaintStyle, boundsPath);
                using var strokePaint = BuildStrokePaint(run.Element, run.PaintStyle);
                using var blobBuilder = new SKTextBlobBuilder();
                if (run.PathTransforms != null)
                {
                    var pathRun = blobBuilder.AllocateRotationScaleRun(font, run.PathGlyphIds.Length);
                    for (int i = 0; i < run.PathGlyphIds.Length; i++)
                    {
                        pathRun.Glyphs[i] = run.PathGlyphIds[i];
                        pathRun.Positions[i] = run.PathTransforms[i];
                    }
                }
                else
                {
                    var positioned = blobBuilder.AllocatePositionedRun(font, run.GlyphRun.Count);
                    for (int i = 0; i < run.GlyphRun.Count; i++)
                    {
                        positioned.Glyphs[i] = run.GlyphRun.Glyphs[i].GlyphId;
                        positioned.Positions[i] = new SKPoint(run.X + run.GlyphRun.Glyphs[i].X, run.Y + run.GlyphRun.Glyphs[i].Y);
                    }
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
            public int Chunk { get; set; }
            public bool ApplyOwnOpacity { get; }
            public ushort[] PathGlyphIds { get; set; }
            public SKRotationScaleMatrix[] PathTransforms { get; set; }
            public SKRect PathBounds { get; set; }
        }

        private readonly record struct TextChunk(float StartX, TextAnchor Anchor);
    }
}
