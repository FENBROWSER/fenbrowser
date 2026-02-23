using FenBrowser.Core.Css;
using System;
using System.Globalization;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Painting
{
    /// <summary>
    /// Paints text content with proper styling, wrapping, and decoration.
    /// </summary>
    public class TextPainter
    {
        /// <summary>
        /// Paint text within a box.
        /// </summary>
        public void PaintText(SKCanvas canvas, string text, SKRect box, CssComputed style)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = ApplyTextTransform(text, style);

            using var paint = CreateTextPaint(style);

            // Get text metrics
            var metrics = paint.FontMetrics;
            float baseline = -metrics.Ascent;

            float letterSpacing = (float)(style?.LetterSpacing ?? 0);
            float wordSpacing = (float)(style?.WordSpacing ?? 0);

            // Apply text alignment
            float x = box.Left;
            float y = box.Top + baseline;

            var textAlign = style?.TextAlign;
            if (textAlign.HasValue)
            {
                float textWidth = MeasureTextWithSpacing(text, paint, letterSpacing, wordSpacing);
                switch (textAlign.Value)
                {
                    case SKTextAlign.Center:
                        x = box.Left + (box.Width - textWidth) / 2;
                        break;
                    case SKTextAlign.Right:
                        x = box.Right - textWidth;
                        break;
                }
            }

            // Draw text
            if (FenBrowser.Core.Logging.DebugConfig.LogPaintCommands)
            {
                 if (text.Length < 100 && (text.Contains("Google") || text.Contains("Wiki") || text.Contains("Reddit")))
                     Console.WriteLine($"[SKIA-DRAW-TEXT] '{text}' at ({x}, {y}) Color={paint.Color} Alpha={paint.Color.Alpha}");
            }

            if (letterSpacing != 0 || wordSpacing != 0)
            {
                DrawTextWithSpacing(canvas, text, x, y, paint, letterSpacing, wordSpacing);
            }
            else
            {
                canvas.DrawText(text, x, y, paint);
            }

            // Draw text decoration
            PaintTextDecoration(canvas, text, x, y, paint, style);
        }

        /// <summary>
        /// Paint wrapped text within a box.
        /// </summary>
        public void PaintWrappedText(SKCanvas canvas, string text, SKRect box, CssComputed style)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = ApplyTextTransform(text, style);

            using var paint = CreateTextPaint(style);

            var metrics = paint.FontMetrics;
            float lineHeight = GetLineHeight(style, metrics);
            float y = box.Top - metrics.Ascent;

            float letterSpacing = (float)(style?.LetterSpacing ?? 0);
            float wordSpacing = (float)(style?.WordSpacing ?? 0);

            // Improved word wrap
            var words = text.Split(' ');
            var line = "";
            float x = box.Left;

            foreach (var word in words)
            {
                var testLine = string.IsNullOrEmpty(line) ? word : line + " " + word;
                float testWidth = MeasureTextWithSpacing(testLine, paint, letterSpacing, wordSpacing);

                if (testWidth > box.Width && !string.IsNullOrEmpty(line))
                {
                    // Draw current line
                    if (letterSpacing != 0 || wordSpacing != 0)
                        DrawTextWithSpacing(canvas, line, x, y, paint, letterSpacing, wordSpacing);
                    else
                        canvas.DrawText(line, x, y, paint);
                    y += lineHeight;
                    line = word;

                    if (y > box.Bottom) break; // Overflow
                }
                else
                {
                    line = testLine;
                }
            }

            // Draw remaining
            if (!string.IsNullOrEmpty(line) && y <= box.Bottom)
            {
                if (letterSpacing != 0 || wordSpacing != 0)
                    DrawTextWithSpacing(canvas, line, x, y, paint, letterSpacing, wordSpacing);
                else
                    canvas.DrawText(line, x, y, paint);
            }
        }

        /// <summary>
        /// Measure text dimensions.
        /// </summary>
        public SKSize MeasureText(string text, CssComputed style)
        {
            if (string.IsNullOrEmpty(text)) return SKSize.Empty;
            text = ApplyTextTransform(text, style);

            using var paint = CreateTextPaint(style);
            
            float letterSpacing = (float)(style?.LetterSpacing ?? 0);
            float wordSpacing = (float)(style?.WordSpacing ?? 0);
            float width = MeasureTextWithSpacing(text, paint, letterSpacing, wordSpacing);
            
            var metrics = paint.FontMetrics;
            float height = metrics.Descent - metrics.Ascent;

            return new SKSize(width, height);
        }

        /// <summary>
        /// Get baseline offset for text.
        /// </summary>
        public float GetBaseline(CssComputed style)
        {
            using var paint = CreateTextPaint(style);
            return -paint.FontMetrics.Ascent;
        }

        /// <summary>
        /// Create SKPaint for text rendering.
        /// </summary>
        private SKPaint CreateTextPaint(CssComputed style)
        {
            var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                TextSize = (float)(style?.FontSize ?? 16)
            };

            // Color
            if (style?.ForegroundColor.HasValue == true)
            {
                var c = style.ForegroundColor.Value;
                paint.Color = new SKColor(c.Red, c.Green, c.Blue, c.Alpha);
            }
            else
            {
                paint.Color = SKColors.Black;
            }

            // Font weight
            var weight = SKFontStyleWeight.Normal;
            if (style?.FontWeight.HasValue == true)
            {
                weight = (SKFontStyleWeight)(int)style.FontWeight.Value;
            }

            // Font style
            var slant = SKFontStyleSlant.Upright;
            if (style?.FontStyle == SKFontStyleSlant.Italic)
            {
                slant = SKFontStyleSlant.Italic;
            }

            // Font family
            string fontFamily = style?.FontFamilyName ?? "Segoe UI";
            paint.Typeface = SKTypeface.FromFamilyName(fontFamily, weight, SKFontStyleWidth.Normal, slant);

            // Letter spacing
            if (style?.LetterSpacing.HasValue == true)
            {
                // Handled in DrawText overrides
            }

            // Text shadow
            if (!string.IsNullOrEmpty(style?.TextShadow) && style.TextShadow != "none")
            {
                var shadow = ParseTextShadow(style.TextShadow);
                if (shadow != null)
                {
                    paint.ImageFilter = SKImageFilter.CreateDropShadow(
                        shadow.OffsetX,
                        shadow.OffsetY,
                        shadow.Blur > 0 ? shadow.Blur / 2 : 0,
                        shadow.Blur > 0 ? shadow.Blur / 2 : 0,
                        shadow.Color,
                        SKDropShadowImageFilterShadowMode.DrawShadowAndForeground);
                }
            }

            return paint;
        }

        /// <summary>
        /// Paint text decoration (underline, line-through, overline).
        /// </summary>
        private void PaintTextDecoration(SKCanvas canvas, string text, float x, float y, SKPaint textPaint, CssComputed style)
        {
            var decoration = style?.TextDecoration?.ToLowerInvariant();
            if (string.IsNullOrEmpty(decoration) || decoration == "none") return;

            float textWidth = textPaint.MeasureText(text);
            var metrics = textPaint.FontMetrics;

            using var linePaint = new SKPaint
            {
                Color = textPaint.Color,
                StrokeWidth = Math.Max(1, textPaint.TextSize / 16),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            if (decoration.Contains("underline"))
            {
                float underlineY = y + metrics.Descent / 2;
                canvas.DrawLine(x, underlineY, x + textWidth, underlineY, linePaint);
            }

            if (decoration.Contains("line-through"))
            {
                float strikeY = y - metrics.Ascent / 3;
                canvas.DrawLine(x, strikeY, x + textWidth, strikeY, linePaint);
            }

            if (decoration.Contains("overline"))
            {
                float overlineY = y + metrics.Ascent;
                canvas.DrawLine(x, overlineY, x + textWidth, overlineY, linePaint);
            }
        }

        private float GetLineHeight(CssComputed style, SKFontMetrics metrics)
        {
            if (style?.LineHeight.HasValue == true)
            {
                return (float)style.LineHeight.Value;
            }
            return (metrics.Descent - metrics.Ascent) * 1.2f;
        }

        private string ApplyTextTransform(string text, CssComputed style)
        {
            if (string.IsNullOrEmpty(text) || style == null) return text;
            var transform = style.TextTransform?.ToLowerInvariant();
            if (string.IsNullOrEmpty(transform) || transform == "none") return text;

            if (transform == "uppercase") return text.ToUpperInvariant();
            if (transform == "lowercase") return text.ToLowerInvariant();
            if (transform == "capitalize") 
            {
                if (text.Length == 0) return text;
                var chars = text.ToCharArray();
                bool newWord = true;
                for (int i = 0; i < chars.Length; i++)
                {
                    if (char.IsWhiteSpace(chars[i])) newWord = true;
                    else if (newWord) { chars[i] = char.ToUpperInvariant(chars[i]); newWord = false; }
                    else chars[i] = char.ToLowerInvariant(chars[i]);
                }
                return new string(chars);
            }
            return text;
        }

        private float MeasureTextWithSpacing(string text, SKPaint paint, float letterSpacing, float wordSpacing)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (letterSpacing == 0 && wordSpacing == 0) return paint.MeasureText(text);

            float width = 0;
            foreach (char c in text)
            {
                width += paint.MeasureText(c.ToString()) + letterSpacing;
                if (char.IsWhiteSpace(c)) width += wordSpacing;
            }
            return width - letterSpacing;
        }

        private void DrawTextWithSpacing(SKCanvas canvas, string text, float x, float y, SKPaint paint, float letterSpacing, float wordSpacing)
        {
            float currentX = x;
            foreach (char c in text)
            {
                string s = c.ToString();
                canvas.DrawText(s, currentX, y, paint);
                float advance = paint.MeasureText(s);
                currentX += advance + letterSpacing;
                if (char.IsWhiteSpace(c)) currentX += wordSpacing;
            }
        }

        private class TextShadowInfo
        {
            public float OffsetX;
            public float OffsetY;
            public float Blur;
            public SKColor Color = new SKColor(0, 0, 0, 128);
        }

        private TextShadowInfo ParseTextShadow(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "none") return null;
            try
            {
                var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var info = new TextShadowInfo();
                int i = 0;
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float ox)) { info.OffsetX = ox; i++; }
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float oy)) { info.OffsetY = oy; i++; }
                if (i < parts.Length && float.TryParse(parts[i].Replace("px", ""), out float blur)) { info.Blur = blur; i++; }
                
                if (i < parts.Length)
                {
                    string colorStr = string.Join(" ", parts, i, parts.Length - i).Trim().ToLowerInvariant();
                    if (colorStr.StartsWith("#"))
                    {
                        var hex = colorStr.Substring(1);
                        if (hex.Length == 6)
                        {
                            info.Color = new SKColor(
                                Convert.ToByte(hex.Substring(0, 2), 16),
                                Convert.ToByte(hex.Substring(2, 2), 16),
                                Convert.ToByte(hex.Substring(4, 2), 16));
                        }
                    }
                }
                return info;
            }
            catch { return null; }
        }
    }
}

