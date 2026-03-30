using SkiaSharp;
using FenBrowser.FenEngine.Layout;
using System.Collections.Concurrent;

namespace FenBrowser.FenEngine.Typography
{
    /// <summary>
    /// Skia-based implementation of IFontService.
    /// 
    /// RULE 2: This provides Skia data, but NormalizedFontMetrics controls the final values.
    /// </summary>
    public class SkiaFontService : IFontService
    {
        // Cache for resolved typefaces
        private readonly ConcurrentDictionary<string, SKTypeface> _typefaceCache = new();
        private readonly ConcurrentDictionary<MetricsCacheKey, NormalizedFontMetrics> _metricsCache = new();
        private readonly ConcurrentDictionary<MeasureCacheKey, float> _widthCache = new();
        private readonly ConcurrentDictionary<MeasureCacheKey, GlyphRun> _glyphRunCache = new();
        private const int MaxWidthCacheEntries = 4096;
        private const int MaxGlyphRunCacheEntries = 2048;
        private const int MaxMetricsCacheEntries = 512;
        
        public NormalizedFontMetrics GetMetrics(string fontFamily, float fontSize, int fontWeight = 400, float? cssLineHeight = null)
        {
            var cacheKey = new MetricsCacheKey(fontFamily ?? string.Empty, fontSize, fontWeight, cssLineHeight);
            if (_metricsCache.TryGetValue(cacheKey, out var cachedMetrics))
            {
                return cachedMetrics;
            }

            var typeface = ResolveTypeface(fontFamily, fontWeight);
            
            using var font = new SKFont(typeface, fontSize);
            var metrics = font.Metrics;
            
            // Convert raw Skia metrics to normalized CSS-semantic metrics
            // FenEngine controls line height, not Skia
            var normalized = NormalizedFontMetrics.FromSkia(metrics, fontSize, cssLineHeight);
            StoreMetrics(cacheKey, normalized);
            return normalized;
        }
        
        public float MeasureTextWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var cacheKey = new MeasureCacheKey(text, fontFamily ?? string.Empty, fontSize, fontWeight);
            if (_widthCache.TryGetValue(cacheKey, out var cachedWidth))
            {
                return cachedWidth;
            }
            
            var typeface = ResolveTypeface(fontFamily, fontWeight);
            
            using var paint = new SKPaint
            {
                TextSize = fontSize,
                Typeface = typeface,
                IsAntialias = true
            };
            
            var width = paint.MeasureText(text);
            if (text.Length <= 256)
            {
                StoreWidth(cacheKey, width);
            }

            return width;
        }
        
        public GlyphRun ShapeText(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new GlyphRun
                {
                    Glyphs = Array.Empty<PositionedGlyph>(),
                    Width = 0,
                    FontSize = fontSize,
                    SourceText = text
                };
            }

            var cacheKey = new MeasureCacheKey(text, fontFamily ?? string.Empty, fontSize, fontWeight);
            if (_glyphRunCache.TryGetValue(cacheKey, out var cachedRun))
            {
                return cachedRun;
            }
            
            var typeface = ResolveTypeface(fontFamily, fontWeight);
            var metrics = GetMetrics(fontFamily, fontSize, fontWeight);
            
            using var paint = new SKPaint
            {
                TextSize = fontSize,
                Typeface = typeface,
                IsAntialias = true
            };
            
            // Get glyph IDs
            var glyphIds = paint.GetGlyphs(text);
            var widths = paint.GetGlyphWidths(text);
            
            // Build positioned glyphs
            var glyphs = new PositionedGlyph[glyphIds.Length];
            float x = 0;
            
            for (int i = 0; i < glyphIds.Length; i++)
            {
                glyphs[i] = new PositionedGlyph
                {
                    GlyphId = glyphIds[i],
                    X = x,
                    Y = 0, // Baseline-relative
                    AdvanceX = widths[i]
                };
                x += widths[i];
            }
            
            var glyphRun = new GlyphRun
            {
                Glyphs = glyphs,
                Typeface = typeface,
                FontSize = fontSize,
                Width = x,
                Metrics = metrics,
                SourceText = text
            };

            if (text.Length <= 256)
            {
                StoreGlyphRun(cacheKey, glyphRun);
            }

            return glyphRun;
        }
        
        public SKTypeface ResolveTypeface(string fontFamily, int fontWeight = 400, SKFontStyleSlant fontStyle = SKFontStyleSlant.Upright)
        {
            // Create cache key
            string cacheKey = $"{fontFamily ?? "default"}|{fontWeight}|{fontStyle}";
            
            if (_typefaceCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
            
            // Use TextLayoutHelper's existing resolution logic
            var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, "", fontWeight, fontStyle);
            
            _typefaceCache[cacheKey] = typeface;
            return typeface;
        }

        private void StoreMetrics(MetricsCacheKey key, NormalizedFontMetrics metrics)
        {
            if (_metricsCache.Count >= MaxMetricsCacheEntries)
            {
                _metricsCache.Clear();
            }

            _metricsCache[key] = metrics;
        }

        private void StoreWidth(MeasureCacheKey key, float width)
        {
            if (_widthCache.Count >= MaxWidthCacheEntries)
            {
                _widthCache.Clear();
            }

            _widthCache[key] = width;
        }

        private void StoreGlyphRun(MeasureCacheKey key, GlyphRun run)
        {
            if (_glyphRunCache.Count >= MaxGlyphRunCacheEntries)
            {
                _glyphRunCache.Clear();
            }

            _glyphRunCache[key] = run;
        }

        private readonly record struct MeasureCacheKey(string Text, string FontFamily, float FontSize, int FontWeight);

        private readonly record struct MetricsCacheKey(string FontFamily, float FontSize, int FontWeight, float? CssLineHeight);
    }
}
