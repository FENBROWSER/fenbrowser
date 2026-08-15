using SkiaSharp;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FenBrowser.FenEngine.Typography
{
    public struct TextMetrics
    {
        public float Width { get; set; }
        public float Height { get; set; }
        public float Ascent { get; set; }
        public float Descent { get; set; }
        public float Leading { get; set; }
        public float LineHeight => Ascent + Descent + Leading;
    }

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
    /// Wraps Skia text measurement with caching for performance.
    /// </summary>
    public sealed class TextShaper : IDisposable
    {
        private readonly ConcurrentDictionary<FontDescriptor, SKFont> _fontCache = new();
        private readonly ConcurrentDictionary<(FontDescriptor, string), TextMetrics> _metricsCache = new();

        private const int MaxCacheSize = 1000;
        private int _disposed;

        public TextMetrics Measure(string text, FontDescriptor font)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(text))
            {
                return new TextMetrics { Width = 0, Height = font.Size, Ascent = font.Size * 0.8f, Descent = font.Size * 0.2f };
            }

            var cacheKey = (font, text);
            if (_metricsCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var skFont = GetOrCreateFont(font);
            var metrics = MeasureCore(text, skFont);

            if (_metricsCache.Count < MaxCacheSize)
            {
                _metricsCache.TryAdd(cacheKey, metrics);
            }

            return metrics;
        }

        public float MeasureWidth(string text, FontDescriptor font)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(text)) return 0;

            var skFont = GetOrCreateFont(font);
            return skFont.MeasureText(text);
        }

        public TextMetrics GetFontMetrics(FontDescriptor font)
        {
            ThrowIfDisposed();
            var skFont = GetOrCreateFont(font);
            var skMetrics = skFont.Metrics;

            return new TextMetrics
            {
                Width = 0,
                Height = skMetrics.Descent - skMetrics.Ascent + skMetrics.Leading,
                Ascent = -skMetrics.Ascent,
                Descent = skMetrics.Descent,
                Leading = skMetrics.Leading
            };
        }

        private SKFont GetOrCreateFont(FontDescriptor descriptor)
        {
            ThrowIfDisposed();
            if (_fontCache.TryGetValue(descriptor, out var cached))
            {
                return cached;
            }

            if (DebugConfig.LogTextShaping)
            {
                global::FenBrowser.Core.EngineLogCompat.Log(
                    $"[Text] Resolving Font: '{descriptor.Family}' Size={descriptor.Size} B={descriptor.Bold} I={descriptor.Italic}",
                    LogCategory.Text);
            }

            var weight = descriptor.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            var slant = descriptor.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);

            SKTypeface typeface = null;
            if (!string.IsNullOrEmpty(descriptor.Family))
            {
                typeface = SKTypeface.FromFamilyName(descriptor.Family, style);
            }

            if (typeface == null)
            {
                typeface = SKTypeface.FromFamilyName("Arial", style) ??
                           SKTypeface.FromFamilyName("Segoe UI", style) ??
                           SKTypeface.Default;
            }

            var candidate = new SKFont(typeface, descriptor.Size)
            {
                Subpixel = true
            };

            // ConcurrentDictionary.TryAdd followed by `return candidate` leaked a
            // native SKFont whenever another thread won the same descriptor race.
            // Publish one winner and deterministically dispose every losing candidate.
            var winner = _fontCache.GetOrAdd(descriptor, candidate);
            if (!ReferenceEquals(winner, candidate))
            {
                candidate.Dispose();
            }

            // Dispose can race font construction. Never return a newly published native
            // resource after this shaper has transitioned to disposed state.
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_fontCache.TryRemove(new KeyValuePair<FontDescriptor, SKFont>(descriptor, winner)))
                {
                    winner.Dispose();
                }
                throw new ObjectDisposedException(nameof(TextShaper));
            }

            return winner;
        }

        private TextMetrics MeasureCore(string text, SKFont font)
        {
            var width = font.MeasureText(text);
            var metrics = font.Metrics;

            if (DebugConfig.LogTextShaping)
            {
                var shortText = text.Length > 20 ? text.Substring(0, 20) + "..." : text;
                global::FenBrowser.Core.EngineLogCompat.Log(
                    $"[Text] Method:Measure '{shortText}' (Length: {text.Length}) -> W={width:F2}",
                    LogCategory.Text);
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

        public void ClearCache()
        {
            ThrowIfDisposed();
            ClearCacheCore();
        }

        private void ClearCacheCore()
        {
            _metricsCache.Clear();

            foreach (var pair in _fontCache)
            {
                if (_fontCache.TryRemove(pair.Key, out var font))
                {
                    font.Dispose();
                }
            }
        }

        public FontDescriptor FindFallbackFont(string text, FontDescriptor primaryFont)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(text)) return primaryFont;

            if (FontSupportsText(primaryFont, text))
                return primaryFont;

            var fallbacks = new[] { "Arial", "Segoe UI", "Segoe UI Symbol", "Microsoft Sans Serif" };

            foreach (var fallbackFamily in fallbacks)
            {
                var fallbackFont = new FontDescriptor
                {
                    Family = fallbackFamily,
                    Size = primaryFont.Size,
                    Bold = primaryFont.Bold,
                    Italic = primaryFont.Italic
                };

                if (FontSupportsText(fallbackFont, text))
                {
                    if (DebugConfig.LogTextShaping)
                    {
                        global::FenBrowser.Core.EngineLogCompat.Debug(
                            $"[Text] Font fallback: '{primaryFont.Family}' → '{fallbackFamily}' for text '{text.Substring(0, Math.Min(20, text.Length))}'",
                            LogCategory.Text);
                    }
                    return fallbackFont;
                }
            }

            return primaryFont;
        }

        private bool FontSupportsText(FontDescriptor font, string text)
        {
            try
            {
                var skFont = GetOrCreateFont(font);
                using var skFontInstance = new SKFont(skFont.Typeface);
                return skFontInstance.ContainsGlyphs(text);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(TextShaper));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            ClearCacheCore();
        }
    }
}
