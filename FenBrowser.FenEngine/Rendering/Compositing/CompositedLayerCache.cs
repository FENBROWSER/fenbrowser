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
        private readonly Dictionary<string, CachedSurface> _surfaces = new();
        private readonly Dictionary<string, int> _invalidationCounts = new();
        private int _generation;
        private bool _disposed;

        public int Count => _surfaces.Count;
        public int Generation => _generation;

        /// <summary>
        /// Advance the generation counter. Layers rasterized at older
        /// generations are considered stale and will be re-rasterized.
        /// </summary>
        public int NextGeneration()
        {
            return ++_generation;
        }

        /// <summary>
        /// Creates a GPU-backed surface sized to fit the layer bounds.
        /// Falls back to CPU when GRContext is unavailable.
        /// </summary>
        public SKSurface CreateLayerSurface(
            SKRect layerBounds,
            GRContext gpuContext,
            out SKImageInfo surfaceInfo)
        {
            int width = Math.Max(1, (int)Math.Ceiling(layerBounds.Width));
            int height = Math.Max(1, (int)Math.Ceiling(layerBounds.Height));
            surfaceInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

            SKSurface surface = null;
            if (gpuContext != null)
            {
                surface = SKSurface.Create(gpuContext, true, surfaceInfo);
            }

            return surface ?? SKSurface.Create(surfaceInfo);
        }

        /// <summary>
        /// Stores a rasterized layer snapshot and returns the cached image.
        /// The key should uniquely identify the layer (e.g., node identity + transform state).
        /// </summary>
        public bool TryStoreLayer(
            string layerKey,
            SKSurface surface,
            int paintGeneration)
        {
            if (surface == null || string.IsNullOrEmpty(layerKey))
            {
                return false;
            }

            // Evict old surface if present
            if (_surfaces.TryGetValue(layerKey, out var old))
            {
                old.Image?.Dispose();
            }

            var snapshot = surface.Snapshot();
            if (snapshot == null)
            {
                return false;
            }

            _surfaces[layerKey] = new CachedSurface
            {
                Image = snapshot,
                PaintGeneration = paintGeneration
            };

            return true;
        }

        /// <summary>
        /// Gets a cached layer image if it exists and is at the current generation.
        /// Returns null if the layer needs re-rasterization.
        /// </summary>
        public SKImage GetCachedLayer(string layerKey, int paintGeneration)
        {
            if (_surfaces.TryGetValue(layerKey, out var cached) &&
                cached.PaintGeneration == paintGeneration &&
                cached.Image != null)
            {
                return cached.Image;
            }

            return null;
        }

        /// <summary>
        /// Composite a cached layer onto the target canvas, applying
        /// its transform and opacity.
        /// </summary>
        public void DrawLayer(
            SKCanvas targetCanvas,
            string layerKey,
            SKRect bounds,
            SKMatrix? transform,
            float opacity,
            int paintGeneration)
        {
            var image = GetCachedLayer(layerKey, paintGeneration);
            if (image == null)
            {
                return;
            }

            targetCanvas.Save();

            if (transform.HasValue)
            {
                var tx = transform.Value;
                targetCanvas.Concat(ref tx);
            }

            if (opacity < 0.999f)
            {
                using var paint = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, (byte)Math.Clamp(opacity * 255f, 0f, 255f))
                };
                targetCanvas.DrawImage(image, bounds.Left, bounds.Top, paint);
            }
            else
            {
                targetCanvas.DrawImage(image, bounds.Left, bounds.Top);
            }

            targetCanvas.Restore();
        }

        /// <summary>
        /// Invalidate and remove a specific layer.
        /// </summary>
        public void InvalidateLayer(string layerKey)
        {
            if (_surfaces.TryGetValue(layerKey, out var cached))
            {
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

        private sealed class CachedSurface
        {
            public SKImage Image;
            public int PaintGeneration;
        }
    }
}
