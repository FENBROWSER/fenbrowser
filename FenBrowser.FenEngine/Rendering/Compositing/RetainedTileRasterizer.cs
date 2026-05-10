using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    public readonly record struct RetainedTileRasterizationStats
    {
        public bool Enabled { get; init; }
        public bool UsedGpuSurfaces { get; init; }
        public bool RebuiltDisplayList { get; init; }
        public int VisibleTileCount { get; init; }
        public int RasterizedTileCount { get; init; }
        public int ReusedTileCount { get; init; }
        public int CachedTileCount { get; init; }
    }

    /// <summary>
    /// Tile-based retained rasterizer:
    /// - Records immutable paint tree into a retained SKPicture display list.
    /// - Rasterizes only dirty tiles into SKImage snapshots.
    /// - Reuses clean tiles across frames.
    /// - Prefers GPU-backed SKSurface allocation when a GRContext is supplied.
    /// </summary>
    internal sealed class RetainedTileRasterizer : IDisposable
    {
        private const int DefaultTileSizePx = 256;
        private const int DefaultMaxRetainedTiles = 2048;
        private const int DefaultMaxVisibleTiles = 4096;
        private const int DefaultMaxDamageRegionsScanned = 256;
        private const int DefaultMaxDirtyTilesPerFrame = 16384;
        private readonly int _tileSizePx;
        private readonly int _maxRetainedTiles;
        private readonly int _maxVisibleTiles;
        private readonly int _maxDamageRegionsScanned;
        private readonly int _maxDirtyTilesPerFrame;
        private readonly Dictionary<TileKey, RetainedTile> _tiles = new Dictionary<TileKey, RetainedTile>();

        private ImmutablePaintTree _displayListSourceTree;
        private SKPicture _displayList;
        private SKRect _displayListViewport;
        private int _displayListGeneration;
        private int _rasterFrameSequence;
        private bool _disposed;

        public RetainedTileRasterizer(
            int tileSizePx = DefaultTileSizePx,
            int maxRetainedTiles = DefaultMaxRetainedTiles,
            int maxVisibleTiles = DefaultMaxVisibleTiles,
            int maxDamageRegionsScanned = DefaultMaxDamageRegionsScanned,
            int maxDirtyTilesPerFrame = DefaultMaxDirtyTilesPerFrame)
        {
            _tileSizePx = Math.Clamp(tileSizePx, 64, 1024);
            _maxRetainedTiles = Math.Max(64, maxRetainedTiles);
            _maxVisibleTiles = Math.Max(64, maxVisibleTiles);
            _maxDamageRegionsScanned = Math.Max(16, maxDamageRegionsScanned);
            _maxDirtyTilesPerFrame = Math.Max(256, maxDirtyTilesPerFrame);
        }

        public bool HasRetainedContent => _displayList != null && _tiles.Count > 0;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Invalidate();
            _disposed = true;
        }

        public void Invalidate()
        {
            _displayListSourceTree = null;
            _displayListViewport = SKRect.Empty;

            _displayList?.Dispose();
            _displayList = null;

            foreach (var entry in _tiles)
            {
                entry.Value.Image?.Dispose();
            }

            _tiles.Clear();
            _displayListGeneration = 0;
            _rasterFrameSequence = 0;
        }

        public RetainedTileRasterizationStats Rasterize(
            SKCanvas targetCanvas,
            SkiaRenderer renderer,
            ImmutablePaintTree paintTree,
            SKRect viewport,
            SKColor backgroundColor,
            IReadOnlyList<SKRect> damageRegions,
            bool preferGpuSurfaces,
            GRContext gpuContext)
        {
            if (_disposed || targetCanvas == null || renderer == null || paintTree == null || viewport.Width <= 0 || viewport.Height <= 0)
            {
                return default;
            }

            _rasterFrameSequence++;
            bool rebuiltDisplayList = EnsureDisplayList(renderer, paintTree, viewport);
            if (_displayList == null)
            {
                return default;
            }

            var visibleTiles = BuildVisibleTiles(viewport);
            if (visibleTiles.Count == 0)
            {
                return default;
            }

            var dirtyTiles = CollectDirtyTiles(viewport, visibleTiles, damageRegions, rebuiltDisplayList);
            int rasterizedTileCount = 0;
            int reusedTileCount = 0;
            bool usedGpuSurfaces = false;

            for (var i = 0; i < visibleTiles.Count; i++)
            {
                var tile = visibleTiles[i];
                bool needsRaster = dirtyTiles.Contains(tile.Key) ||
                                   !_tiles.TryGetValue(tile.Key, out var cachedTile) ||
                                   cachedTile.Image == null ||
                                   cachedTile.DisplayListGeneration != _displayListGeneration;

                if (needsRaster)
                {
                    if (RasterizeTile(tile.Bounds, tile.Key, backgroundColor, preferGpuSurfaces, gpuContext, out bool usedGpuSurface))
                    {
                        rasterizedTileCount++;
                        usedGpuSurfaces |= usedGpuSurface;
                    }
                }
                else
                {
                    cachedTile.LastAccessFrame = _rasterFrameSequence;
                    reusedTileCount++;
                }
            }

            targetCanvas.Save();
            targetCanvas.ClipRect(viewport, SKClipOperation.Intersect, true);
            using (var paint = new SKPaint { Color = backgroundColor, Style = SKPaintStyle.Fill, IsAntialias = false })
            {
                targetCanvas.DrawRect(viewport, paint);
            }

            for (var i = 0; i < visibleTiles.Count; i++)
            {
                var tile = visibleTiles[i];
                if (_tiles.TryGetValue(tile.Key, out var cachedTile) && cachedTile.Image != null)
                {
                    targetCanvas.DrawImage(cachedTile.Image, tile.Bounds.Left, tile.Bounds.Top);
                }
            }

            targetCanvas.Restore();
            PruneRetainedTiles(visibleTiles);

            return new RetainedTileRasterizationStats
            {
                Enabled = true,
                UsedGpuSurfaces = usedGpuSurfaces,
                RebuiltDisplayList = rebuiltDisplayList,
                VisibleTileCount = visibleTiles.Count,
                RasterizedTileCount = rasterizedTileCount,
                ReusedTileCount = reusedTileCount,
                CachedTileCount = _tiles.Count
            };
        }

        private bool EnsureDisplayList(SkiaRenderer renderer, ImmutablePaintTree paintTree, SKRect viewport)
        {
            if (ReferenceEquals(_displayListSourceTree, paintTree) &&
                _displayList != null &&
                ApproximatelyEqual(_displayListViewport, viewport))
            {
                return false;
            }

            _displayList?.Dispose();
            _displayList = renderer.RecordDisplayList(paintTree, viewport);
            _displayListSourceTree = paintTree;
            _displayListViewport = viewport;
            _displayListGeneration++;

            if (_displayList == null)
            {
                foreach (var entry in _tiles)
                {
                    entry.Value.Image?.Dispose();
                }
                _tiles.Clear();
                return false;
            }

            return true;
        }

        private HashSet<TileKey> CollectDirtyTiles(
            SKRect viewport,
            IReadOnlyList<VisibleTile> visibleTiles,
            IReadOnlyList<SKRect> damageRegions,
            bool forceFullInvalidation)
        {
            var dirty = new HashSet<TileKey>();
            if (visibleTiles == null || visibleTiles.Count == 0)
            {
                return dirty;
            }

            if (forceFullInvalidation || damageRegions == null || damageRegions.Count == 0)
            {
                for (var i = 0; i < visibleTiles.Count; i++)
                {
                    dirty.Add(visibleTiles[i].Key);
                }

                return dirty;
            }

            int damageRegionBudget = Math.Min(damageRegions.Count, _maxDamageRegionsScanned);
            if (damageRegions.Count > damageRegionBudget)
            {
                return BuildDirtySetForAllVisibleTiles(visibleTiles);
            }

            int dirtyTileBudgetRemaining = _maxDirtyTilesPerFrame;
            for (var i = 0; i < damageRegionBudget; i++)
            {
                if (!TryIntersect(damageRegions[i], viewport, out var clippedDamage))
                {
                    continue;
                }

                int minTileX = (int)MathF.Floor(clippedDamage.Left / _tileSizePx);
                int maxTileX = (int)MathF.Floor((clippedDamage.Right - 1f) / _tileSizePx);
                int minTileY = (int)MathF.Floor(clippedDamage.Top / _tileSizePx);
                int maxTileY = (int)MathF.Floor((clippedDamage.Bottom - 1f) / _tileSizePx);

                for (int tileY = minTileY; tileY <= maxTileY; tileY++)
                {
                    for (int tileX = minTileX; tileX <= maxTileX; tileX++)
                    {
                        if (dirtyTileBudgetRemaining-- <= 0)
                        {
                            return BuildDirtySetForAllVisibleTiles(visibleTiles);
                        }

                        dirty.Add(new TileKey(tileX, tileY));
                    }
                }
            }

            for (var i = 0; i < visibleTiles.Count; i++)
            {
                var visibleTile = visibleTiles[i];
                if (!_tiles.TryGetValue(visibleTile.Key, out var cachedTile) ||
                    cachedTile.Image == null ||
                    cachedTile.DisplayListGeneration != _displayListGeneration)
                {
                    dirty.Add(visibleTile.Key);
                }
            }

            return dirty;
        }

        private bool RasterizeTile(
            SKRect tileBounds,
            TileKey tileKey,
            SKColor backgroundColor,
            bool preferGpuSurfaces,
            GRContext gpuContext,
            out bool usedGpuSurface)
        {
            usedGpuSurface = false;
            try
            {
                int width = Math.Max(1, (int)Math.Ceiling(tileBounds.Width));
                int height = Math.Max(1, (int)Math.Ceiling(tileBounds.Height));
                var tileInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

                SKSurface surface = null;
                if (preferGpuSurfaces && gpuContext != null)
                {
                    surface = SKSurface.Create(gpuContext, true, tileInfo);
                    usedGpuSurface = surface != null;
                }

                surface ??= SKSurface.Create(tileInfo);
                if (surface == null)
                {
                    return false;
                }

                using (surface)
                {
                    var tileCanvas = surface.Canvas;
                    tileCanvas.Clear(backgroundColor);
                    tileCanvas.Save();
                    tileCanvas.Translate(-tileBounds.Left, -tileBounds.Top);
                    tileCanvas.DrawPicture(_displayList);
                    tileCanvas.Restore();
                    tileCanvas.Flush();

                    var snapshot = surface.Snapshot();
                    if (snapshot == null)
                    {
                        return false;
                    }

                    if (!_tiles.ContainsKey(tileKey) && _tiles.Count >= _maxRetainedTiles)
                    {
                        EvictLeastRecentlyUsed(_maxRetainedTiles - 1);
                    }

                    _tiles.TryGetValue(tileKey, out var existing);
                    existing.Image?.Dispose();
                    existing.Image = snapshot;
                    existing.DisplayListGeneration = _displayListGeneration;
                    existing.LastAccessFrame = _rasterFrameSequence;
                    _tiles[tileKey] = existing;
                    return true;
                }
            }
            catch
            {
                if (_tiles.TryGetValue(tileKey, out var existing))
                {
                    existing.Image?.Dispose();
                    _tiles.Remove(tileKey);
                }

                usedGpuSurface = false;
                return false;
            }
        }

        private void PruneRetainedTiles(IReadOnlyList<VisibleTile> visibleTiles)
        {
            if (_tiles.Count == 0)
            {
                return;
            }

            var visibleSet = new HashSet<TileKey>();
            for (var i = 0; i < visibleTiles.Count; i++)
            {
                visibleSet.Add(visibleTiles[i].Key);
            }

            var staleKeys = new List<TileKey>();
            foreach (var kv in _tiles)
            {
                if (!visibleSet.Contains(kv.Key) && (_rasterFrameSequence - kv.Value.LastAccessFrame) > 2)
                {
                    staleKeys.Add(kv.Key);
                }
            }

            for (var i = 0; i < staleKeys.Count; i++)
            {
                if (_tiles.TryGetValue(staleKeys[i], out var staleTile))
                {
                    staleTile.Image?.Dispose();
                }

                _tiles.Remove(staleKeys[i]);
            }

            if (_tiles.Count > _maxRetainedTiles)
            {
                EvictLeastRecentlyUsed(_maxRetainedTiles);
            }
        }

        private List<VisibleTile> BuildVisibleTiles(SKRect viewport)
        {
            var visible = new List<VisibleTile>();
            int minTileX = (int)MathF.Floor(viewport.Left / _tileSizePx);
            int maxTileX = (int)MathF.Floor((viewport.Right - 1f) / _tileSizePx);
            int minTileY = (int)MathF.Floor(viewport.Top / _tileSizePx);
            int maxTileY = (int)MathF.Floor((viewport.Bottom - 1f) / _tileSizePx);

            for (int tileY = minTileY; tileY <= maxTileY; tileY++)
            {
                for (int tileX = minTileX; tileX <= maxTileX; tileX++)
                {
                    var tileRect = new SKRect(
                        tileX * _tileSizePx,
                        tileY * _tileSizePx,
                        (tileX + 1) * _tileSizePx,
                        (tileY + 1) * _tileSizePx);

                    if (TryIntersect(tileRect, viewport, out var clipped))
                    {
                        visible.Add(new VisibleTile(new TileKey(tileX, tileY), clipped));
                        if (visible.Count > _maxVisibleTiles)
                        {
                            // Fail closed: retained tile pass is optional; caller falls back to
                            // direct full/damage rasterization for oversized visible tile sets.
                            visible.Clear();
                            return visible;
                        }
                    }
                }
            }

            return visible;
        }

        private HashSet<TileKey> BuildDirtySetForAllVisibleTiles(IReadOnlyList<VisibleTile> visibleTiles)
        {
            var all = new HashSet<TileKey>();
            for (var i = 0; i < visibleTiles.Count; i++)
            {
                all.Add(visibleTiles[i].Key);
            }

            return all;
        }

        private void EvictLeastRecentlyUsed(int targetCount)
        {
            if (targetCount < 0)
            {
                targetCount = 0;
            }

            while (_tiles.Count > targetCount)
            {
                bool found = false;
                TileKey lruKey = default;
                int lruFrame = int.MaxValue;

                foreach (var kv in _tiles)
                {
                    if (!found || kv.Value.LastAccessFrame < lruFrame)
                    {
                        found = true;
                        lruKey = kv.Key;
                        lruFrame = kv.Value.LastAccessFrame;
                    }
                }

                if (!found)
                {
                    break;
                }

                if (_tiles.TryGetValue(lruKey, out var stale))
                {
                    stale.Image?.Dispose();
                }

                _tiles.Remove(lruKey);
            }
        }

        private static bool TryIntersect(SKRect a, SKRect b, out SKRect intersection)
        {
            var left = Math.Max(a.Left, b.Left);
            var top = Math.Max(a.Top, b.Top);
            var right = Math.Min(a.Right, b.Right);
            var bottom = Math.Min(a.Bottom, b.Bottom);

            if (right <= left || bottom <= top)
            {
                intersection = SKRect.Empty;
                return false;
            }

            intersection = new SKRect(left, top, right, bottom);
            return true;
        }

        private static bool ApproximatelyEqual(SKRect a, SKRect b)
        {
            const float epsilon = 0.01f;
            return Math.Abs(a.Left - b.Left) <= epsilon &&
                   Math.Abs(a.Top - b.Top) <= epsilon &&
                   Math.Abs(a.Right - b.Right) <= epsilon &&
                   Math.Abs(a.Bottom - b.Bottom) <= epsilon;
        }

        private readonly struct VisibleTile
        {
            public VisibleTile(TileKey key, SKRect bounds)
            {
                Key = key;
                Bounds = bounds;
            }

            public TileKey Key { get; }
            public SKRect Bounds { get; }
        }

        private readonly struct TileKey : IEquatable<TileKey>
        {
            public TileKey(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }
            public int Y { get; }

            public bool Equals(TileKey other) => X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is TileKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(X, Y);
        }

        private struct RetainedTile
        {
            public SKImage Image;
            public int DisplayListGeneration;
            public int LastAccessFrame;
        }
    }
}
