using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Cache;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace FenBrowser.FenEngine.Typography
{
    public readonly record struct FontServiceCacheSnapshot(
        int MetricsEntries,
        int WidthEntries,
        int GlyphRunEntries,
        int TypefaceEntries,
        long ApproximateBytes,
        long HitCount,
        long MissCount,
        long EvictionCount);

    /// <summary>
    /// Skia-based implementation of IFontService.
    ///
    /// RULE 2: This provides Skia data, but NormalizedFontMetrics controls the final values.
    /// </summary>
    public class SkiaFontService : IFontService
    {
        private static readonly object s_instancesLock = new();
        private static readonly List<WeakReference<SkiaFontService>> s_instances = new();

        private readonly ConcurrentDictionary<string, SKTypeface> _typefaceCache = new();
        private readonly BoundedLruCache<MetricsCacheKey, NormalizedFontMetrics> _metricsCache;
        private readonly BoundedLruCache<MeasureCacheKey, float> _widthCache;
        private readonly BoundedLruCache<MeasureCacheKey, GlyphRun> _glyphRunCache;

        public SkiaFontService(RenderPerformanceConfiguration configuration = null)
        {
            var config = (configuration ?? RenderPerformanceConfiguration.Current).Clone();
            config.Normalize();

            _metricsCache = new BoundedLruCache<MetricsCacheKey, NormalizedFontMetrics>(
                config.FontMetricsCacheEntries,
                config.FontMetricsCacheBytes,
                static (_, _) => 96);

            _widthCache = new BoundedLruCache<MeasureCacheKey, float>(
                config.FontWidthCacheEntries,
                config.FontWidthCacheBytes,
                static (key, _) => 80 + (key.Text?.Length ?? 0) * 2 + (key.FontFamily?.Length ?? 0) * 2);

            _glyphRunCache = new BoundedLruCache<MeasureCacheKey, GlyphRun>(
                config.FontGlyphRunCacheEntries,
                config.FontGlyphRunCacheBytes,
                static (key, run) => 128 + (key.Text?.Length ?? 0) * 2 + (run?.Glyphs?.Length ?? 0) * 24);

            lock (s_instancesLock)
            {
                s_instances.Add(new WeakReference<SkiaFontService>(this));
            }
        }

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
            var normalized = NormalizedFontMetrics.FromSkia(metrics, fontSize, cssLineHeight);
            _metricsCache.Set(cacheKey, normalized);
            return normalized;
        }

        public float MeasureTextWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

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
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = false
            };

            var width = paint.MeasureText(text);
            if (text.Length <= 256)
            {
                _widthCache.Set(cacheKey, width);
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
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = false
            };

            if (!TryShapeWithHarfBuzz(text, paint, out var glyphs, out var width))
            {
                var glyphIds = paint.GetGlyphs(text);
                var widths = paint.GetGlyphWidths(text);
                glyphs = new PositionedGlyph[glyphIds.Length];
                width = 0;

                for (int i = 0; i < glyphIds.Length; i++)
                {
                    glyphs[i] = new PositionedGlyph
                    {
                        GlyphId = glyphIds[i],
                        X = width,
                        Y = 0,
                        AdvanceX = widths[i]
                    };
                    width += widths[i];
                }
            }

            var glyphRun = new GlyphRun
            {
                Glyphs = glyphs,
                Typeface = typeface,
                FontSize = fontSize,
                Width = width,
                Metrics = metrics,
                SourceText = text
            };

            if (text.Length <= 256)
            {
                _glyphRunCache.Set(cacheKey, glyphRun);
            }

            return glyphRun;
        }

        public SKTypeface ResolveTypeface(string fontFamily, int fontWeight = 400, SKFontStyleSlant fontStyle = SKFontStyleSlant.Upright)
        {
            string cacheKey = $"{fontFamily ?? "default"}|{fontWeight}|{fontStyle}";

            if (_typefaceCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var style = new SKFontStyle((SKFontStyleWeight)fontWeight, SKFontStyleWidth.Normal, fontStyle);
            SKTypeface typeface = null;
            foreach (var candidate in EnumerateFamilyCandidates(fontFamily))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                typeface = SKFontManager.Default.MatchFamily(candidate, style);
                if (typeface != null && !string.IsNullOrWhiteSpace(typeface.FamilyName))
                {
                    break;
                }
            }

            if (typeface == null)
            {
                typeface = TextLayoutHelper.ResolveTypeface(fontFamily, string.Empty, fontWeight, fontStyle);
            }

            _typefaceCache[cacheKey] = typeface;
            return typeface;
        }

        private static bool TryShapeWithHarfBuzz(
            string text,
            SKPaint paint,
            out PositionedGlyph[] glyphs,
            out float width)
        {
            glyphs = Array.Empty<PositionedGlyph>();
            width = 0;

            if (string.IsNullOrEmpty(text) || paint == null || paint.Typeface == null)
            {
                return false;
            }

            try
            {
                using var shaper = new SKShaper(paint.Typeface);
                var result = shaper.Shape(text, 0, 0, paint);
                if (result == null || result.Codepoints == null || result.Codepoints.Length == 0 || result.Points == null)
                {
                    return false;
                }

                var codepoints = result.Codepoints;
                var points = result.Points;
                var count = Math.Min(codepoints.Length, points.Length);
                if (count <= 0)
                {
                    return false;
                }

                width = float.IsFinite(result.Width) && result.Width > 0
                    ? result.Width
                    : paint.MeasureText(text);

                glyphs = new PositionedGlyph[count];
                for (var i = 0; i < count; i++)
                {
                    var currentX = points[i].X;
                    var nextX = i + 1 < count ? points[i + 1].X : width;
                    var advance = nextX - currentX;
                    if (!float.IsFinite(advance) || advance < 0f)
                    {
                        advance = 0f;
                    }

                    glyphs[i] = new PositionedGlyph
                    {
                        GlyphId = (ushort)(codepoints[i] & 0xFFFF),
                        X = currentX,
                        Y = points[i].Y,
                        AdvanceX = advance
                    };
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> EnumerateFamilyCandidates(string fontFamily)
        {
            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                var families = fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < families.Length; i++)
                {
                    var clean = families[i].Trim().Trim('\'', '"');
                    if (string.IsNullOrWhiteSpace(clean))
                    {
                        continue;
                    }

                    yield return MapGenericFamily(clean);
                }
            }

            yield return "Segoe UI";
            yield return "Arial";
            yield return "Helvetica";
            yield return "sans-serif";
        }

        private static string MapGenericFamily(string family)
        {
            if (family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
            {
                return "Segoe UI";
            }

            if (family.Equals("serif", StringComparison.OrdinalIgnoreCase))
            {
                return "Georgia";
            }

            if (family.Equals("monospace", StringComparison.OrdinalIgnoreCase))
            {
                return "Consolas";
            }

            return family;
        }

        public FontServiceCacheSnapshot GetCacheSnapshot()
        {
            var metrics = _metricsCache.GetSnapshot("metrics");
            var widths = _widthCache.GetSnapshot("widths");
            var glyphRuns = _glyphRunCache.GetSnapshot("glyph-runs");

            return new FontServiceCacheSnapshot(
                metrics.EntryCount,
                widths.EntryCount,
                glyphRuns.EntryCount,
                _typefaceCache.Count,
                metrics.ApproximateBytes + widths.ApproximateBytes + glyphRuns.ApproximateBytes,
                metrics.HitCount + widths.HitCount + glyphRuns.HitCount,
                metrics.MissCount + widths.MissCount + glyphRuns.MissCount,
                metrics.EvictionCount + widths.EvictionCount + glyphRuns.EvictionCount);
        }

        public static FontServiceCacheSnapshot GetGlobalCacheSnapshot()
        {
            var liveInstances = new List<SkiaFontService>();
            lock (s_instancesLock)
            {
                for (int i = s_instances.Count - 1; i >= 0; i--)
                {
                    if (s_instances[i].TryGetTarget(out var instance))
                    {
                        liveInstances.Add(instance);
                    }
                    else
                    {
                        s_instances.RemoveAt(i);
                    }
                }
            }

            int metricsEntries = 0;
            int widthEntries = 0;
            int glyphEntries = 0;
            int typefaceEntries = 0;
            long bytes = 0;
            long hits = 0;
            long misses = 0;
            long evictions = 0;

            foreach (var instance in liveInstances)
            {
                var snapshot = instance.GetCacheSnapshot();
                metricsEntries += snapshot.MetricsEntries;
                widthEntries += snapshot.WidthEntries;
                glyphEntries += snapshot.GlyphRunEntries;
                typefaceEntries += snapshot.TypefaceEntries;
                bytes += snapshot.ApproximateBytes;
                hits += snapshot.HitCount;
                misses += snapshot.MissCount;
                evictions += snapshot.EvictionCount;
            }

            return new FontServiceCacheSnapshot(
                metricsEntries,
                widthEntries,
                glyphEntries,
                typefaceEntries,
                bytes,
                hits,
                misses,
                evictions);
        }

        private readonly record struct MeasureCacheKey(string Text, string FontFamily, float FontSize, int FontWeight);

        private readonly record struct MetricsCacheKey(string FontFamily, float FontSize, int FontWeight, float? CssLineHeight);
    }
}
