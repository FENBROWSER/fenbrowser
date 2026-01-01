using SkiaSharp;
using FenBrowser.FenEngine.Layout;

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
        private readonly Dictionary<string, SKTypeface> _typefaceCache = new();
        
        public NormalizedFontMetrics GetMetrics(string fontFamily, float fontSize, int fontWeight = 400, float? cssLineHeight = null)
        {
            var typeface = ResolveTypeface(fontFamily, fontWeight);
            
            using var font = new SKFont(typeface, fontSize);
            var metrics = font.Metrics;
            
            // Convert raw Skia metrics to normalized CSS-semantic metrics
            // FenEngine controls line height, not Skia
            return NormalizedFontMetrics.FromSkia(metrics, fontSize, cssLineHeight);
        }
        
        public float MeasureTextWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            
            var typeface = ResolveTypeface(fontFamily, fontWeight);
            
            using var paint = new SKPaint
            {
                TextSize = fontSize,
                Typeface = typeface,
                IsAntialias = true
            };
            
            return paint.MeasureText(text);
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
            
            return new GlyphRun
            {
                Glyphs = glyphs,
                Typeface = typeface,
                FontSize = fontSize,
                Width = x,
                Metrics = metrics,
                SourceText = text
            };
        }
        
        public SKTypeface ResolveTypeface(string fontFamily, int fontWeight = 400, SKFontStyleSlant fontStyle = SKFontStyleSlant.Upright)
        {
            // Create cache key
            string cacheKey = $"{fontFamily ?? "default"}|{fontWeight}|{fontStyle}";
            
            if (_typefaceCache.TryGetValue(cacheKey, out var cached))
                return cached;
            
            // Use TextLayoutHelper's existing resolution logic
            var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, "", fontWeight, fontStyle);
            
            _typefaceCache[cacheKey] = typeface;
            return typeface;
        }
    }
}
