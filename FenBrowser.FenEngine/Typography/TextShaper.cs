using SkiaSharp;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Concurrent;

namespace FenBrowser.FenEngine.Typography
{
    /// <summary>
    /// Text metrics returned by the TextShaper.
    /// </summary>
    public struct TextMetrics
    {
        /// <summary>Total width of the measured text.</summary>
        public float Width { get; set; }
        
        /// <summary>Height (typically line height).</summary>
        public float Height { get; set; }
        
        /// <summary>Distance from top to baseline.</summary>
        public float Ascent { get; set; }
        
        /// <summary>Distance from baseline to bottom.</summary>
        public float Descent { get; set; }
        
        /// <summary>Extra vertical space between lines (leading).</summary>
        public float Leading { get; set; }
        
        /// <summary>Computed line height (Ascent + Descent + Leading).</summary>
        public float LineHeight => Ascent + Descent + Leading;
    }

    /// <summary>
    /// Simplified font descriptor for text shaping.
    /// </summary>
    public struct FontDescriptor
    {
        public string Family { get; set; }
        public float Size { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(Family?.ToLowerInvariant() ?? "", Size, Bold, Italic);
        }
        
        public override bool Equals(object obj)
        {
            if (obj is FontDescriptor other)
            {
                return string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase) &&
                       Math.Abs(Size - other.Size) < 0.001f &&
                       Bold == other.Bold &&
                       Italic == other.Italic;
            }
            return false;
        }
    }

    /// <summary>
    /// Text shaping and measurement abstraction.
    /// Wraps Skia's text measurement with caching for performance.
    /// 
    /// Phase 3: Layout Truth - Accurate text metrics for layout.
    /// </summary>
    public sealed class TextShaper : IDisposable
    {
        // Font cache for reusing SKFont objects
        private readonly ConcurrentDictionary<FontDescriptor, SKFont> _fontCache = new();
        
        // Metrics cache for repeated measurements
        private readonly ConcurrentDictionary<(FontDescriptor, string), TextMetrics> _metricsCache = new();
        
        private const int MaxCacheSize = 1000;
        private bool _disposed;

        /// <summary>
        /// Measures the given text with the specified font.
        /// Uses caching for performance.
        /// </summary>
        public TextMetrics Measure(string text, FontDescriptor font)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new TextMetrics { Width = 0, Height = font.Size, Ascent = font.Size * 0.8f, Descent = font.Size * 0.2f };
            }

            var cacheKey = (font, text);
            
            // Check cache first
            if (_metricsCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var skFont = GetOrCreateFont(font);
            var metrics = MeasureCore(text, skFont);
            
            // Cache if not too large
            if (_metricsCache.Count < MaxCacheSize)
            {
                _metricsCache.TryAdd(cacheKey, metrics);
            }

            return metrics;
        }

        /// <summary>
        /// Measures text width only (faster than full Measure).
        /// </summary>
        public float MeasureWidth(string text, FontDescriptor font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            
            var skFont = GetOrCreateFont(font);
            using var paint = new SKPaint(skFont);
            return paint.MeasureText(text);
        }

        /// <summary>
        /// Gets font metrics without measuring specific text.
        /// </summary>
        public TextMetrics GetFontMetrics(FontDescriptor font)
        {
            var skFont = GetOrCreateFont(font);
            var skMetrics = skFont.Metrics;
            
            return new TextMetrics
            {
                Width = 0,
                Height = skMetrics.Descent - skMetrics.Ascent + skMetrics.Leading,
                Ascent = -skMetrics.Ascent, // Skia ascent is negative
                Descent = skMetrics.Descent,
                Leading = skMetrics.Leading
            };
        }

        private SKFont GetOrCreateFont(FontDescriptor descriptor)
        {
            if (_fontCache.TryGetValue(descriptor, out var cached))
            {
                return cached;
            }

            if (FenBrowser.Core.Logging.DebugConfig.LogTextShaping)
                 global::FenBrowser.Core.FenLogger.Log($"[Text] Resolving Font: '{descriptor.Family}' Size={descriptor.Size} B={descriptor.Bold} I={descriptor.Italic}", LogCategory.Text);

            // Find typeface
            var weight = descriptor.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            var slant = descriptor.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);

            SKTypeface typeface = null;
            
            // Try exact family match
            if (!string.IsNullOrEmpty(descriptor.Family))
            {
                typeface = SKTypeface.FromFamilyName(descriptor.Family, style);
            }
            
            // Fallback to default
            if (typeface == null)
            {
                typeface = SKTypeface.FromFamilyName("Arial", style) ??
                           SKTypeface.FromFamilyName("Segoe UI", style) ??
                           SKTypeface.Default;
            }

            var font = new SKFont(typeface, descriptor.Size);
            
            // Enable subpixel rendering for better measurement accuracy
            font.Subpixel = true;
            
            _fontCache.TryAdd(descriptor, font);
            return font;
        }

        private TextMetrics MeasureCore(string text, SKFont font)
        {
            using var paint = new SKPaint(font);
            var width = paint.MeasureText(text);
            var metrics = font.Metrics;

            if (FenBrowser.Core.Logging.DebugConfig.LogTextShaping)
            {
                 var shortText = text.Length > 20 ? text.Substring(0, 20) + "..." : text;
                 global::FenBrowser.Core.FenLogger.Log($"[Text] Method:Measure '{shortText}' (Length: {text.Length}) -> W={width:F2}", LogCategory.Text);
            }
            
            return new TextMetrics
            {
                Width = width,
                Height = metrics.Descent - metrics.Ascent + metrics.Leading,
                Ascent = -metrics.Ascent,
                Descent = metrics.Descent,
                Leading = metrics.Leading
            };
        }

        /// <summary>
        /// Clears all caches. Call when significant font changes occur.
        /// </summary>
        public void ClearCache()
        {
            _metricsCache.Clear();
            
            foreach (var font in _fontCache.Values)
            {
                font.Dispose();
            }
            _fontCache.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            ClearCache();
        }
    }
}
