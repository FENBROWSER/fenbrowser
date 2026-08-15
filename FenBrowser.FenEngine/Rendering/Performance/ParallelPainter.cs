using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Performance
{
    /// <summary>
    /// Painter helpers and hot-path caches used by rendering.
    /// </summary>
    public class ParallelPainter
    {
        private readonly object _metricsLock = new();
        private readonly TextMeasurementCache _textCache;
        private long _totalPaintTimeMs;
        private long _paintCount;

        public ParallelPainter()
        {
            _textCache = new TextMeasurementCache();
        }

        public SKSize MeasureText(string text, SKFont font)
        {
            if (string.IsNullOrEmpty(text)) return SKSize.Empty;
            ArgumentNullException.ThrowIfNull(font);

            var key = new TextCacheKey(text, font.Size, font.Typeface?.FamilyName ?? "default");

            if (_textCache.TryGet(key, out var cached))
            {
                return cached;
            }

            float width = font.MeasureText(text);
            var metrics = font.Metrics;
            float height = metrics.Descent - metrics.Ascent;

            var size = new SKSize(width, height);
            _textCache.Set(key, size);
            return size;
        }

        public float GetBaseline(SKFont font)
        {
            ArgumentNullException.ThrowIfNull(font);
            var metrics = font.Metrics;
            return -metrics.Ascent;
        }

        public List<(Element element, SKRect box)> SortByZIndex(
            IEnumerable<(Element element, SKRect box, int zIndex)> boxes)
        {
            ArgumentNullException.ThrowIfNull(boxes);
            var list = new List<(Element element, SKRect box, int zIndex)>(boxes);
            list.Sort(static (a, b) => a.zIndex.CompareTo(b.zIndex));

            var result = new List<(Element, SKRect)>(list.Count);
            foreach (var item in list)
            {
                result.Add((item.element, item.box));
            }
            return result;
        }

        public void DrawRectangles(SKCanvas canvas, IReadOnlyList<SKRect> rects, SKPaint paint)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            ArgumentNullException.ThrowIfNull(rects);
            ArgumentNullException.ThrowIfNull(paint);

            foreach (var rect in rects)
            {
                canvas.DrawRect(rect, paint);
            }
        }

        public void DrawRoundedRect(SKCanvas canvas, SKRect rect, float radiusX, float radiusY, SKPaint paint)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            ArgumentNullException.ThrowIfNull(paint);
            using var rrect = new SKRoundRect(rect, radiusX, radiusY);
            canvas.DrawRoundRect(rrect, paint);
        }

        public void DrawBoxShadow(SKCanvas canvas, SKRect rect, float blur, float offsetX, float offsetY, SKColor color)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            blur = Math.Max(0, blur);

            var shadowRect = new SKRect(
                rect.Left + offsetX - blur,
                rect.Top + offsetY - blur,
                rect.Right + offsetX + blur,
                rect.Bottom + offsetY + blur);

            using var shadowPaint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                MaskFilter = blur > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur / 2) : null
            };

            canvas.DrawRect(shadowRect, shadowPaint);
        }

        public void RecordPaintTime(long elapsedMs)
        {
            if (elapsedMs < 0)
                return;

            lock (_metricsLock)
            {
                _totalPaintTimeMs = SaturatingAdd(_totalPaintTimeMs, elapsedMs);
                if (_paintCount != long.MaxValue)
                    _paintCount++;
            }
        }

        public double GetAveragePaintTimeMs()
        {
            lock (_metricsLock)
            {
                return _paintCount > 0 ? (double)_totalPaintTimeMs / _paintCount : 0;
            }
        }

        public void ResetMetrics()
        {
            lock (_metricsLock)
            {
                _totalPaintTimeMs = 0;
                _paintCount = 0;
            }
            _textCache.Clear();
        }

        public (int textCacheSize, double avgPaintMs, long paintCount) GetStats()
        {
            lock (_metricsLock)
            {
                var avg = _paintCount > 0 ? (double)_totalPaintTimeMs / _paintCount : 0;
                return (_textCache.Count, avg, _paintCount);
            }
        }

        private static long SaturatingAdd(long left, long right) =>
            right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    /// Bounded text-measurement cache.
    /// </summary>
    public class TextMeasurementCache
    {
        private readonly ConcurrentDictionary<TextCacheKey, SKSize> _cache = new();
        private const int MaxCacheSize = 10000;
        private int _clearInProgress;

        public bool TryGet(TextCacheKey key, out SKSize size) => _cache.TryGetValue(key, out size);

        public void Set(TextCacheKey key, SKSize size)
        {
            if (_cache.Count >= MaxCacheSize &&
                Interlocked.CompareExchange(ref _clearInProgress, 1, 0) == 0)
            {
                try
                {
                    if (_cache.Count >= MaxCacheSize)
                        _cache.Clear();
                }
                finally
                {
                    Volatile.Write(ref _clearInProgress, 0);
                }
            }

            _cache[key] = size;
        }

        public void Clear() => _cache.Clear();
        public int Count => _cache.Count;
    }

    /// <summary>
    /// Cache key for text measurement. Equality and hashing deliberately use exactly
    /// the same fields/semantics; approximate float equality with a raw-float hash used
    /// to violate the dictionary requirement that equal keys have equal hash codes.
    /// </summary>
    public readonly struct TextCacheKey : IEquatable<TextCacheKey>
    {
        public readonly string Text;
        public readonly float FontSize;
        public readonly string FontFamily;

        public TextCacheKey(string text, float fontSize, string fontFamily)
        {
            Text = text ?? string.Empty;
            FontSize = fontSize;
            FontFamily = fontFamily ?? string.Empty;
        }

        public bool Equals(TextCacheKey other)
        {
            return string.Equals(Text, other.Text, StringComparison.Ordinal) &&
                   FontSize.Equals(other.FontSize) &&
                   string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is TextCacheKey key && Equals(key);
        public override int GetHashCode() => HashCode.Combine(Text, FontSize, FontFamily);
    }

    /// <summary>
    /// Render timer for performance profiling.
    /// </summary>
    public class RenderProfiler
    {
        private readonly ConcurrentDictionary<string, TimingData> _timings = new(StringComparer.Ordinal);

        public IDisposable Time(string phaseName)
        {
            return new PhaseTimer(this, NormalizePhaseName(phaseName));
        }

        internal void Record(string phaseName, long elapsedMs)
        {
            if (elapsedMs < 0)
                return;

            _timings.AddOrUpdate(
                NormalizePhaseName(phaseName),
                _ => new TimingData(elapsedMs, 1),
                (_, existing) => existing.Add(elapsedMs));
        }

        public Dictionary<string, double> GetAverages()
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kvp in _timings)
            {
                result[kvp.Key] = kvp.Value.Count > 0
                    ? (double)kvp.Value.TotalMs / kvp.Value.Count
                    : 0;
            }
            return result;
        }

        public void Reset() => _timings.Clear();

        private static string NormalizePhaseName(string phaseName)
        {
            if (string.IsNullOrWhiteSpace(phaseName))
                return "unknown";
            return phaseName.Length <= 128 ? phaseName : phaseName.Substring(0, 128);
        }

        private readonly record struct TimingData(long TotalMs, int Count)
        {
            public TimingData Add(long elapsedMs)
            {
                var total = elapsedMs > 0 && TotalMs > long.MaxValue - elapsedMs
                    ? long.MaxValue
                    : TotalMs + elapsedMs;
                var count = Count == int.MaxValue ? int.MaxValue : Count + 1;
                return new TimingData(total, count);
            }
        }

        private sealed class PhaseTimer : IDisposable
        {
            private readonly RenderProfiler _profiler;
            private readonly string _phase;
            private readonly Stopwatch _sw;
            private int _disposed;

            public PhaseTimer(RenderProfiler profiler, string phase)
            {
                _profiler = profiler;
                _phase = phase;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _sw.Stop();
                _profiler.Record(_phase, _sw.ElapsedMilliseconds);
            }
        }
    }
}
