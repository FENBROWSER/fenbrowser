using SkiaSharp;
using FenBrowser.FenEngine.Layout;
using System;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// SkiaSharp-based implementation of ITextMeasurer.
    /// This is a simple implementation that doesn't use RichTextKit.
    /// 
    /// RULE 5: If we need RichTextKit, create RichTextKitMeasurer as an alternative.
    /// </summary>
    public class SkiaTextMeasurer : ITextMeasurer
    {
        public float MeasureWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text) || !IsUsableFontSize(fontSize))
            {
                return 0;
            }
            
            var typeface = TextLayoutHelper.ResolveTypeface(NormalizeFontFamily(fontFamily), text, fontWeight, SKFontStyleSlant.Upright);
            
            using var paint = new SKPaint
            {
                TextSize = fontSize,
                Typeface = typeface,
                IsAntialias = true
            };
            
            return paint.MeasureText(text);
        }
        
        public float GetLineHeight(string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (!IsUsableFontSize(fontSize))
            {
                return 0;
            }

            var typeface = TextLayoutHelper.ResolveTypeface(NormalizeFontFamily(fontFamily), "", fontWeight, SKFontStyleSlant.Upright);
            
            using var font = new SKFont(typeface, fontSize);
            var metrics = font.Metrics;
            
            // FenEngine's standard line-height: 1.2x font size
            // This is WE decide, not what Skia suggests
            float resolved = fontSize * 1.2f;
            float metricsHeight = Math.Max(0, metrics.Descent - metrics.Ascent + metrics.Leading);
            return Math.Max(resolved, metricsHeight);
        }

        private static bool IsUsableFontSize(float fontSize)
        {
            return float.IsFinite(fontSize) && fontSize > 0;
        }

        private static string NormalizeFontFamily(string fontFamily)
        {
            return string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily.Trim();
        }
    }
}
