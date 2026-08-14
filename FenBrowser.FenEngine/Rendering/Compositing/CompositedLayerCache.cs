using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// GPU-backed surface cache for composited layers. Promoted layers
    /// (transform, opacity, will-change, scroll) get dedicated GPU surfaces
    /// so transform animations and opacity changes composite without
    /// re-rasterization. Surfaces are invalidated when the paint tree
    /// generation changes.
    /// </summary>
    internal sealed class CompositedLayerCache : IDisposable
    {
        private const int BytesPerPixel = 4;
        private const long MaxSingleLayerBytes = 64L * 1024 * 1024;
        private const int MaxRetainedLayers = 128;

        private readonly Dictionary<string, CachedSurface> _surfaces = new(StringComparer.Ordinal);
        private int _generation;
        private long _retainedBytes;
        private long _accessSequence;
        private bool _disposed;

        public int Count => _surfaces.Count;
        public int Generation => _generation;
        public long RetainedBytes => _retainedBytes;

        /// <summary>
        /// Advance the generation counter. Layers rasterized at older
        /// generations are considered stale and will be re-rasterized.
        /// </summary>
        public int NextGeneration()
        {
            if (_generation == int.MaxValue)
            {
                // Avoid generation wrap making ancient cached entries appear current.
                Clear();
                _generation = 1;
                return _generation;
            }

            return ++_generation;
        }

        /// <summary>
        /// Creates a GPU-backed surface sized to fit the layer bounds.
        /// Falls back to CPU when GRContext is unavailable. Oversized/invalid layers
        /// return null so the caller can use the normal paint path instead of allowing
        /// CSS promotion hints to allocate an unbounded surface.
        /// </summary>
        public SKSurface CreateLayerSurface(
            SKRect layerBounds,
            GRContext gpuContext,
            out SKImageInfo surfaceInfo)
        {
            surfaceInfo = default;
            if (!TryGetSurfaceDimensions(layerBounds, out var width, out var height, out _))
            {
                return null;
            }

            surfaceInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

            SKSurface surface = null;
            if (gpuContext != null)
            {
                try
                {
                    surface = SKSurface.Create(gpuContext, true, surfaceInfo);
                }
                catch
                {
                    // GPU allocation is an optimization. Fall through to a bounded
                    // CPU surface rather than surfacing a backend allocation failure.
                }
            }

            return surface ?? SKSurface.Create(surfaceInfo);
        }

        /// <summary>
        /// Stores a rasterized layer snapshot and returns the cached image.
        /// </summary>
        public bool TryStoreLayer(
            string layerKey,
            SKSurface surface,
            int paintGeneration)
        {
            if (_disposed || surface == null || string.IsNullOrEmpty(layerKey))
            {
                return false;
            }

            var info = surface.Canvas?.DeviceClipBounds;
            long estimatedBytes = 0;
            if (info.HasValue)
            {
                var bounds = info.Value;
                if (!TryComputeBytes(bounds.Width, bounds.Height, out estimatedBytes))
                {
                    return false;
                }
            }

            var snapshot = surface.Snapshot();
            if (snapshot == null)
            {
                return false;
            }

            // Snapshot dimensions are authoritative if the backend clip was not
            // available or differed from the actual image.
            if (!TryComputeBytes(snapshot.Width, snapshot.Height, out estimatedBytes))
            {
                snapshot.Dispose();
                return false;
            }

            if (_surfaces.TryGetValue(layerKey, out var old))
            {
                _retainedBytes = Math.Max(0, _retainedBytes - old.EstimatedBytes);
                old.Image?.Dispose();
            }
            else if (_surfaces.Count >= MaxRetainedLayers)
            {
                EvictLeastRecentlyUsed(MaxRetainedLayers - 1);
            }

            _surfaces[layerKey] = new CachedSurface
            {
                Image = snapshot,
                PaintGeneration = paintGeneration,
                EstimatedBytes = estimatedBytes,
                LastAccessSequence = ++_accessSequence
            };
            _retainedBytes += estimatedBytes;

            // A generation can transiently contain many promoted layers. Keep total
            // retained surface memory bounded independently of the entry-count cap.
            while (_retainedBytes > MaxSingleLayerBytes * 2 && _surfaces.Count > 1)
            {
                EvictLeastRecentlyUsed(_surfaces.Count - 1, layerKey);
            }

            return _surfaces.ContainsKey(layerKey);
        }

        /// <summary>
        /// Gets a cached layer image if it exists and is at the current generation.
        /// Returns null if the layer needs re-rasterization.
        /// </summary>
        public SKImage GetCachedLayer(string layerKey, int paintGeneration)
        {
            if (_disposed || string.IsNullOrEmpty(layerKey))
            {
                return null;
            }

            if (_surfaces.TryGetValue(layerKey, out var cached) &&
                cached.PaintGeneration == paintGeneration &&
                cached.Image != null)
            {
                cached.LastAccessSequence = ++_accessSequence;
                return cached.Image;
            }

            return null;
        }

        public void DrawLayer(
            SKCanvas targetCanvas,
            string layerKey,
            SKRect bounds,
            SKMatrix? transform,
            float opacity,
            int paintGeneration)
        {
            if (targetCanvas == null || !IsFiniteRect(bounds))
            {
                return;
            }

            var image = GetCachedLayer(layerKey, paintGeneration);
            if (image == null)
            {
                return;
            }

            targetCanvas.Save();
            try
            {
                if (transform.HasValue)
                {
                    var tx = transform.Value;
                    targetCanvas.Concat(in tx);
                }

                if (opacity < 0.999f)
                {
                    using var paint = new SKPaint
                    {
                        Color = new SKColor(255, 255, 255, (byte)Math.Clamp(opacity * 255f, 0f, 255f))
                    };
                    targetCanvas.DrawImage(image, bounds.Left, bounds.Top, SKSamplingOptions.Default, paint);
                }
                else
                {
                    targetCanvas.DrawImage(image, bounds.Left, bounds.Top, SKSamplingOptions.Default);
                }
            }
            finally
            {
                targetCanvas.Restore();
            }
        }

        public void InvalidateLayer(string layerKey)
        {
            if (string.IsNullOrEmpty(layerKey)) return;

            if (_surfaces.TryGetValue(layerKey, out var cached))
            {
                _retainedBytes = Math.Max(0, _retainedBytes - cached.EstimatedBytes);
                cached.Image?.Dispose();
                _surfaces.Remove(layerKey);
            }
        }

        public void Clear()
        {
            foreach (var kvp in _surfaces)
            {
                kvp.Value.Image?.Dispose();
            }

            _surfaces.Clear();
            _retainedBytes = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Clear();
            _disposed = true;
        }

        private void EvictLeastRecentlyUsed(int targetCount, string protectedKey = null)
        {
            targetCount = Math.Max(0, targetCount);
            while (_surfaces.Count > targetCount)
            {
                string oldestKey = null;
                long oldestSequence = long.MaxValue;

                foreach (var pair in _surfaces)
                {
                    if (string.Equals(pair.Key, protectedKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (pair.Value.LastAccessSequence < oldestSequence)
                    {
                        oldestSequence = pair.Value.LastAccessSequence;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey == null || !_surfaces.TryGetValue(oldestKey, out var oldest))
                {
                    break;
                }

                _retainedBytes = Math.Max(0, _retainedBytes - oldest.EstimatedBytes);
                oldest.Image?.Dispose();
                _surfaces.Remove(oldestKey);
            }
        }

        private static bool TryGetSurfaceDimensions(
            SKRect layerBounds,
            out int width,
            out int height,
            out long estimatedBytes)
        {
            width = 0;
            height = 0;
            estimatedBytes = 0;

            if (!IsFiniteRect(layerBounds) || layerBounds.Width <= 0f || layerBounds.Height <= 0f)
            {
                return false;
            }

            var widthDouble = Math.Ceiling((double)layerBounds.Width);
            var heightDouble = Math.Ceiling((double)layerBounds.Height);
            if (widthDouble < 1 || heightDouble < 1 ||
                widthDouble > int.MaxValue || heightDouble > int.MaxValue)
            {
                return false;
            }

            width = (int)widthDouble;
            height = (int)heightDouble;
            return TryComputeBytes(width, height, out estimatedBytes);
        }

        private static bool TryComputeBytes(int width, int height, out long bytes)
        {
            bytes = 0;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var pixels = (long)width * height;
            if (pixels > MaxSingleLayerBytes / BytesPerPixel)
            {
                return false;
            }

            bytes = pixels * BytesPerPixel;
            return true;
        }

        private static bool IsFiniteRect(SKRect rect) =>
            float.IsFinite(rect.Left) &&
            float.IsFinite(rect.Top) &&
            float.IsFinite(rect.Right) &&
            float.IsFinite(rect.Bottom);

        private sealed class CachedSurface
        {
            public SKImage Image;
            public int PaintGeneration;
            public long EstimatedBytes;
            public long LastAccessSequence;
        }
    }
}
