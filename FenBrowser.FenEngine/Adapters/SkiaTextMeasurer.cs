using SkiaSharp;
using FenBrowser.FenEngine.Layout;
using System;
using System.Collections.Concurrent;

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
        private readonly ConcurrentDictionary<TextMeasureCacheKey, float> _widthCache = new();
        private readonly ConcurrentDictionary<FontMetricsCacheKey, float> _lineHeightCache = new();
        private const int MaxWidthCacheEntries = 4096;
        private const int MaxLineHeightCacheEntries = 512;

        public float MeasureWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text) || !IsUsableFontSize(fontSize))
            {
                return 0;
            }

            var normalizedFamily = NormalizeFontFamily(fontFamily);
            var widthKey = new TextMeasureCacheKey(text, normalizedFamily, fontSize, fontWeight);
            if (_widthCache.TryGetValue(widthKey, out var cachedWidth))
            {
                return cachedWidth;
            }
            
            var typeface = TextLayoutHelper.ResolveTypeface(normalizedFamily, text, fontWeight, SKFontStyleSlant.Upright);
            
            using var paint = new SKPaint
            {
                TextSize = fontSize,
                Typeface = typeface,
                IsAntialias = true
            };
            
            var width = paint.MeasureText(text);
            if (text.Length <= 256)
            {
                StoreWidth(widthKey, width);
            }

            return width;
        }
        
        public float GetLineHeight(string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (!IsUsableFontSize(fontSize))
            {
                return 0;
            }

            var normalizedFamily = NormalizeFontFamily(fontFamily);
            var metricsKey = new FontMetricsCacheKey(normalizedFamily, fontSize, fontWeight);
            if (_lineHeightCache.TryGetValue(metricsKey, out var cachedLineHeight))
            {
                return cachedLineHeight;
            }

            var typeface = TextLayoutHelper.ResolveTypeface(normalizedFamily, "", fontWeight, SKFontStyleSlant.Upright);
            
            using var font = new SKFont(typeface, fontSize);
            var metrics = font.Metrics;
            
            // FenEngine's standard line-height: 1.2x font size
            // This is WE decide, not what Skia suggests
            float resolved = fontSize * 1.2f;
            float metricsHeight = Math.Max(0, metrics.Descent - metrics.Ascent + metrics.Leading);
            float lineHeight = Math.Max(resolved, metricsHeight);
            StoreLineHeight(metricsKey, lineHeight);
            return lineHeight;
        }

        private void StoreWidth(TextMeasureCacheKey key, float width)
        {
            if (_widthCache.Count >= MaxWidthCacheEntries)
            {
                _widthCache.Clear();
            }

            _widthCache[key] = width;
        }

        private void StoreLineHeight(FontMetricsCacheKey key, float lineHeight)
        {
            if (_lineHeightCache.Count >= MaxLineHeightCacheEntries)
            {
                _lineHeightCache.Clear();
            }

            _lineHeightCache[key] = lineHeight;
        }

        private static bool IsUsableFontSize(float fontSize)
        {
            return float.IsFinite(fontSize) && fontSize > 0;
        }

        private static string NormalizeFontFamily(string fontFamily)
        {
            return string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily.Trim();
        }

        private readonly record struct TextMeasureCacheKey(string Text, string FontFamily, float FontSize, int FontWeight);

        private readonly record struct FontMetricsCacheKey(string FontFamily, float FontSize, int FontWeight);
    }
}
