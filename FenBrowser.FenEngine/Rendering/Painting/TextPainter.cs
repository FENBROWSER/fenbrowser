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

            using var paint = CreateTextPaint(style);

            // Get text metrics
            var metrics = paint.FontMetrics;
            float baseline = -metrics.Ascent;

            // Apply text alignment
            float x = box.Left;
            float y = box.Top + baseline;

            var textAlign = style?.TextAlign;
            if (textAlign.HasValue)
            {
                float textWidth = paint.MeasureText(text);
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
            canvas.DrawText(text, x, y, paint);

            // Draw text decoration
            PaintTextDecoration(canvas, text, x, y, paint, style);
        }

        /// <summary>
        /// Paint wrapped text within a box.
        /// </summary>
        public void PaintWrappedText(SKCanvas canvas, string text, SKRect box, CssComputed style)
        {
            if (string.IsNullOrEmpty(text)) return;

            using var paint = CreateTextPaint(style);

            var metrics = paint.FontMetrics;
            float lineHeight = GetLineHeight(style, metrics);
            float y = box.Top - metrics.Ascent;

            // Simple word wrap
            var words = text.Split(' ');
            var line = "";
            float x = box.Left;

            foreach (var word in words)
            {
                var testLine = string.IsNullOrEmpty(line) ? word : line + " " + word;
                float testWidth = paint.MeasureText(testLine);

                if (testWidth > box.Width && !string.IsNullOrEmpty(line))
                {
                    // Draw current line
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
                canvas.DrawText(line, x, y, paint);
            }
        }

        /// <summary>
        /// Measure text dimensions.
        /// </summary>
        public SKSize MeasureText(string text, CssComputed style)
        {
            if (string.IsNullOrEmpty(text)) return SKSize.Empty;

            using var paint = CreateTextPaint(style);
            float width = paint.MeasureText(text);
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
                // SkiaSharp doesn't directly support letter spacing in SKPaint
                // Would need to draw char by char for exact spacing
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
    }
}

