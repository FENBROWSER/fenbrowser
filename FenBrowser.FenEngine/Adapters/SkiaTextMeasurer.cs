using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Cache;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;

namespace FenBrowser.FenEngine.Adapters
{
    public readonly record struct TextMeasurerCacheSnapshot(
        int WidthEntries,
        int LineHeightEntries,
        long ApproximateBytes,
        long HitCount,
        long MissCount,
        long EvictionCount);

    /// <summary>
    /// SkiaSharp-based implementation of ITextMeasurer.
    /// This is a simple implementation that doesn't use RichTextKit.
    ///
    /// RULE 5: If we need RichTextKit, create RichTextKitMeasurer as an alternative.
    /// </summary>
    public class SkiaTextMeasurer : ITextMeasurer
    {
        private static readonly object s_instancesLock = new();
        private static readonly List<WeakReference<SkiaTextMeasurer>> s_instances = new();

        private readonly BoundedLruCache<TextMeasureCacheKey, float> _widthCache;
        private readonly BoundedLruCache<FontMetricsCacheKey, float> _lineHeightCache;

        public SkiaTextMeasurer(RenderPerformanceConfiguration configuration = null)
        {
            var config = (configuration ?? RenderPerformanceConfiguration.Current).Clone();
            config.Normalize();

            _widthCache = new BoundedLruCache<TextMeasureCacheKey, float>(
                config.TextWidthCacheEntries,
                config.TextWidthCacheBytes,
                static (key, _) => 80 + (key.Text?.Length ?? 0) * 2 + (key.FontFamily?.Length ?? 0) * 2);

            _lineHeightCache = new BoundedLruCache<FontMetricsCacheKey, float>(
                config.TextLineHeightCacheEntries,
                config.TextLineHeightCacheBytes,
                static (_, _) => 64);

            lock (s_instancesLock)
            {
                s_instances.Add(new WeakReference<SkiaTextMeasurer>(this));
            }
        }

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
                _widthCache.Set(widthKey, width);
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

            var typeface = TextLayoutHelper.ResolveTypeface(normalizedFamily, string.Empty, fontWeight, SKFontStyleSlant.Upright);

            using var font = new SKFont(typeface, fontSize);
            var metrics = font.Metrics;
            float resolved = fontSize * 1.2f;
            float metricsHeight = Math.Max(0, metrics.Descent - metrics.Ascent + metrics.Leading);
            float lineHeight = Math.Max(resolved, metricsHeight);
            _lineHeightCache.Set(metricsKey, lineHeight);
            return lineHeight;
        }

        public TextMeasurerCacheSnapshot GetCacheSnapshot()
        {
            var widths = _widthCache.GetSnapshot("widths");
            var lineHeights = _lineHeightCache.GetSnapshot("line-heights");
            return new TextMeasurerCacheSnapshot(
                widths.EntryCount,
                lineHeights.EntryCount,
                widths.ApproximateBytes + lineHeights.ApproximateBytes,
                widths.HitCount + lineHeights.HitCount,
                widths.MissCount + lineHeights.MissCount,
                widths.EvictionCount + lineHeights.EvictionCount);
        }

        public static TextMeasurerCacheSnapshot GetGlobalCacheSnapshot()
        {
            var liveInstances = new List<SkiaTextMeasurer>();
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

            int widthEntries = 0;
            int lineHeightEntries = 0;
            long bytes = 0;
            long hits = 0;
            long misses = 0;
            long evictions = 0;

            foreach (var instance in liveInstances)
            {
                var snapshot = instance.GetCacheSnapshot();
                widthEntries += snapshot.WidthEntries;
                lineHeightEntries += snapshot.LineHeightEntries;
                bytes += snapshot.ApproximateBytes;
                hits += snapshot.HitCount;
                misses += snapshot.MissCount;
                evictions += snapshot.EvictionCount;
            }

            return new TextMeasurerCacheSnapshot(widthEntries, lineHeightEntries, bytes, hits, misses, evictions);
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
