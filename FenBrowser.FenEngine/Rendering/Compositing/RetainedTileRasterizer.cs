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
    /// - Tiles are fixed 256px squares in document space, rasterized whole, so a tile
    ///   stays usable when the viewport scrolls over it.
    /// - A tile's pixels belong to the paint tree it was rasterized from. Scrolling with
    ///   the same tree reuses every cached tile and rasterizes only newly exposed ones.
    /// - A new tree keeps the visible tiles its damage regions do not touch; tiles off
    ///   screen are dropped, since damage is only computed around the viewport. Unknown
    ///   damage drops every tile.
    /// - The tree is recorded once into an R-tree indexed display list covering a band
    ///   around the viewport, re-recorded only when the tree changes or the viewport
    ///   leaves the band.
    /// - Prefers GPU-backed SKSurface allocation when a GRContext is supplied.
    /// </summary>
    internal sealed class RetainedTileRasterizer : IDisposable
    {
        private const int DefaultTileSizePx = 256;
        private const int DefaultMaxRetainedTiles = 256;
        private const int DefaultMaxVisibleTiles = 4096;
        private const int DefaultMaxDamageRegionsScanned = 256;
        private const int DefaultMaxDirtyTilesPerFrame = 16384;
        // The one-pass raster's surface limit (4096 x 4096).
        private const long MaxOnePassPixels = 4096L * 4096L;
        // Frames a tile may go unseen before it is dropped; long enough to scroll back.
        private const int OffscreenTileLifetimeFrames = 240;
        private readonly int _tileSizePx;
        private readonly int _maxRetainedTiles;
        private readonly int _maxVisibleTiles;
        private readonly int _maxDamageRegionsScanned;
        private readonly int _maxDirtyTilesPerFrame;
        private readonly Dictionary<TileKey, RetainedTile> _tiles = new Dictionary<TileKey, RetainedTile>();

        // The tree the cached tiles show, the colour they were cleared to, and the recording.
        private ImmutablePaintTree _contentTree;
        private SKColor _contentBackground;
        private SKPicture _displayList;
        private SKRect _displayListBand;
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
            _maxVisibleTiles = Math.Max(1, maxVisibleTiles);
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
            _contentTree = null;
            DropDisplayList();
            DropAllTiles();
            _rasterFrameSequence = 0;
        }

        /// <param name="contentDamage">
        /// Where <paramref name="paintTree"/> paints differently from
        /// <paramref name="contentDamageBase"/>, in document space and covering at least the
        /// visible tiles; null when unknown. Only used when the tree changed and the cached
        /// tiles were rasterized from exactly that base tree.
        /// </param>
        public RetainedTileRasterizationStats Rasterize(
            SKCanvas targetCanvas,
            SkiaRenderer renderer,
            ImmutablePaintTree paintTree,
            SKRect viewport,
            SKColor backgroundColor,
            IReadOnlyList<SKRect> contentDamage,
            ImmutablePaintTree contentDamageBase,
            bool preferGpuSurfaces,
            GRContext gpuContext)
        {
            if (_disposed || targetCanvas == null || renderer == null || paintTree == null || viewport.Width <= 0 || viewport.Height <= 0)
            {
                return default;
            }

            _rasterFrameSequence++;
            var visibleTiles = BuildVisibleTiles(viewport);
            if (visibleTiles.Count == 0)
            {
                return default;
            }

            // Tiles are cleared to the canvas background, which is not in the paint tree
            // and so not in its damage: a page's background loading with its stylesheet
            // left the undamaged tiles white.
            if (_contentBackground != backgroundColor)
            {
                DropAllTiles();
                _contentBackground = backgroundColor;
            }

            if (!ReferenceEquals(_contentTree, paintTree))
            {
                InvalidateForNewContent(
                    visibleTiles,
                    ReferenceEquals(_contentTree, contentDamageBase) ? contentDamage : null);
                _contentTree = paintTree;
                DropDisplayList();
            }

            int rasterizedTileCount = 0;
            int reusedTileCount = 0;
            bool usedGpuSurfaces = false;
            bool rebuiltDisplayList = false;

            int missingTileCount = 0;
            for (var i = 0; i < visibleTiles.Count; i++)
            {
                if (!_tiles.TryGetValue(visibleTiles[i].Key, out var cached) || cached.Image == null)
                {
                    missingTileCount++;
                }
            }

            // Mostly dirty (three quarters or more: a first frame, a page-wide change): one
            // render of the tile-aligned area and every visible tile cut from it. Replaying
            // the display list per tile redoes each layer and filter that spans several
            // tiles once per tile, with its full reach each time, which made a fresh frame
            // slower than no tiles at all.
            bool renderedInOnePass =
                missingTileCount * 4 >= visibleTiles.Count * 3 &&
                RasterizeVisibleTilesInOnePass(renderer, paintTree, visibleTiles, backgroundColor, preferGpuSurfaces, gpuContext, out usedGpuSurfaces);
            if (renderedInOnePass)
            {
                rasterizedTileCount = visibleTiles.Count;
            }

            for (var i = 0; i < visibleTiles.Count && !renderedInOnePass; i++)
            {
                var tile = visibleTiles[i];
                if (_tiles.TryGetValue(tile.Key, out var cachedTile) && cachedTile.Image != null)
                {
                    cachedTile.LastAccessFrame = _rasterFrameSequence;
                    _tiles[tile.Key] = cachedTile;
                    reusedTileCount++;
                    continue;
                }

                if (!EnsureDisplayListCovers(renderer, paintTree, viewport, tile.Bounds, ref rebuiltDisplayList))
                {
                    return default;
                }

                if (RasterizeTile(tile.Bounds, tile.Key, backgroundColor, preferGpuSurfaces, gpuContext, out bool usedGpuSurface))
                {
                    rasterizedTileCount++;
                    usedGpuSurfaces |= usedGpuSurface;
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
                    targetCanvas.DrawImage(cachedTile.Image, tile.Bounds.Left, tile.Bounds.Top, SKSamplingOptions.Default);
                }
            }

            targetCanvas.Restore();
            PruneRetainedTiles();

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

        private void InvalidateForNewContent(IReadOnlyList<VisibleTile> visibleTiles, IReadOnlyList<SKRect> contentDamage)
        {
            if (contentDamage == null || contentDamage.Count > _maxDamageRegionsScanned)
            {
                DropAllTiles();
                return;
            }

            var keep = new HashSet<TileKey>();
            int budget = _maxDirtyTilesPerFrame;
            for (var i = 0; i < visibleTiles.Count; i++)
            {
                var tile = visibleTiles[i];
                bool damaged = false;
                for (var d = 0; d < contentDamage.Count && !damaged; d++)
                {
                    if (--budget < 0)
                    {
                        DropAllTiles();
                        return;
                    }

                    damaged = contentDamage[d].IntersectsWith(tile.Bounds);
                }

                if (!damaged)
                {
                    keep.Add(tile.Key);
                }
            }

            var drop = new List<TileKey>();
            foreach (var key in _tiles.Keys)
            {
                if (!keep.Contains(key))
                {
                    drop.Add(key);
                }
            }

            foreach (var key in drop)
            {
                DropTile(key);
            }
        }

        private bool RasterizeVisibleTilesInOnePass(
            SkiaRenderer renderer,
            ImmutablePaintTree paintTree,
            IReadOnlyList<VisibleTile> visibleTiles,
            SKColor backgroundColor,
            bool preferGpuSurfaces,
            GRContext gpuContext,
            out bool usedGpuSurface)
        {
            usedGpuSurface = false;
            var area = visibleTiles[0].Bounds;
            for (var i = 1; i < visibleTiles.Count; i++)
            {
                area = SKRect.Union(area, visibleTiles[i].Bounds);
            }

            int width = (int)area.Width;
            int height = (int)area.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MaxOnePassPixels)
            {
                return false;
            }

            try
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                SKSurface surface = null;
                if (preferGpuSurfaces && gpuContext != null)
                {
                    surface = SKSurface.Create(gpuContext, true, info);
                    usedGpuSurface = surface != null;
                }

                surface ??= SKSurface.Create(info);
                if (surface == null)
                {
                    return false;
                }

                SKImage snapshot;
                using (surface)
                {
                    var canvas = surface.Canvas;
                    canvas.Save();
                    canvas.Translate(-area.Left, -area.Top);
                    renderer.Render(canvas, paintTree, area, backgroundColor, captureDebugScreenshot: false);
                    canvas.Restore();
                    canvas.Flush();
                    snapshot = surface.Snapshot();
                }

                if (snapshot == null)
                {
                    return false;
                }

                using (snapshot)
                {
                    for (var i = 0; i < visibleTiles.Count; i++)
                    {
                        var tile = visibleTiles[i];
                        var local = SKRectI.Round(new SKRect(
                            tile.Bounds.Left - area.Left,
                            tile.Bounds.Top - area.Top,
                            tile.Bounds.Right - area.Left,
                            tile.Bounds.Bottom - area.Top));
                        var image = snapshot.Subset(local);
                        if (image == null)
                        {
                            DropTile(tile.Key);
                            continue;
                        }

                        _tiles.TryGetValue(tile.Key, out var existing);
                        existing.Image?.Dispose();
                        existing.Image = image;
                        existing.LastAccessFrame = _rasterFrameSequence;
                        _tiles[tile.Key] = existing;
                    }
                }

                return true;
            }
            catch
            {
                usedGpuSurface = false;
                return false;
            }
        }

        private bool EnsureDisplayListCovers(
            SkiaRenderer renderer,
            ImmutablePaintTree paintTree,
            SKRect viewport,
            SKRect tileBounds,
            ref bool rebuilt)
        {
            if (_displayList != null && Contains(_displayListBand, tileBounds))
            {
                return true;
            }

            // A viewport's worth above and below, aligned to whole tiles, so small
            // scrolls stay inside one recording.
            var band = new SKRect(
                AlignDown(Math.Min(viewport.Left, tileBounds.Left)),
                AlignDown(Math.Min(viewport.Top - viewport.Height, tileBounds.Top)),
                AlignUp(Math.Max(viewport.Right, tileBounds.Right)),
                AlignUp(Math.Max(viewport.Bottom + viewport.Height, tileBounds.Bottom)));

            DropDisplayList();
            _displayList = renderer.RecordDisplayList(paintTree, band, useRTree: true);
            if (_displayList == null)
            {
                return false;
            }

            _displayListBand = band;
            rebuilt = true;
            return true;
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
                    tileCanvas.ClipRect(tileBounds);
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
                    existing.LastAccessFrame = _rasterFrameSequence;
                    _tiles[tileKey] = existing;
                    return true;
                }
            }
            catch
            {
                DropTile(tileKey);
                usedGpuSurface = false;
                return false;
            }
        }

        private void PruneRetainedTiles()
        {
            if (_tiles.Count == 0)
            {
                return;
            }

            var staleKeys = new List<TileKey>();
            foreach (var kv in _tiles)
            {
                if ((_rasterFrameSequence - kv.Value.LastAccessFrame) > OffscreenTileLifetimeFrames)
                {
                    staleKeys.Add(kv.Key);
                }
            }

            foreach (var key in staleKeys)
            {
                DropTile(key);
            }

            if (_tiles.Count > _maxRetainedTiles)
            {
                EvictLeastRecentlyUsed(_maxRetainedTiles);
            }
        }

        /// <summary>Whole document-space tiles overlapping the viewport.</summary>
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

                    visible.Add(new VisibleTile(new TileKey(tileX, tileY), tileRect));
                    if (visible.Count > _maxVisibleTiles)
                    {
                        // Fail closed: retained tile pass is optional; caller falls back to
                        // direct full/damage rasterization for oversized visible tile sets.
                        visible.Clear();
                        return visible;
                    }
                }
            }

            return visible;
        }

        private void DropDisplayList()
        {
            _displayList?.Dispose();
            _displayList = null;
            _displayListBand = SKRect.Empty;
        }

        private void DropAllTiles()
        {
            foreach (var entry in _tiles)
            {
                entry.Value.Image?.Dispose();
            }

            _tiles.Clear();
        }

        private void DropTile(TileKey key)
        {
            if (_tiles.TryGetValue(key, out var tile))
            {
                tile.Image?.Dispose();
                _tiles.Remove(key);
            }
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

                DropTile(lruKey);
            }
        }

        private float AlignDown(float value) => MathF.Floor(value / _tileSizePx) * _tileSizePx;

        private float AlignUp(float value) => MathF.Ceiling(value / _tileSizePx) * _tileSizePx;

        private static bool Contains(SKRect outer, SKRect inner) =>
            outer.Left <= inner.Left && outer.Top <= inner.Top &&
            outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

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
            public int LastAccessFrame;
        }
    }
}
