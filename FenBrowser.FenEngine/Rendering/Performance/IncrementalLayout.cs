using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Performance
{
    /// <summary>
    /// Incremental layout system for performance optimization.
    /// Only recomputes dirty subtrees, caches computed styles.
    /// </summary>
    public class IncrementalLayoutManager
    {
        private readonly ConcurrentDictionary<LiteElement, LayoutCache> _layoutCache;
        private readonly ConcurrentDictionary<LiteElement, CssComputed> _styleCache;
        private readonly HashSet<LiteElement> _dirtyElements;
        private readonly object _dirtyLock = new();
        private bool _fullLayoutRequired = true;

        public IncrementalLayoutManager()
        {
            _layoutCache = new ConcurrentDictionary<LiteElement, LayoutCache>();
            _styleCache = new ConcurrentDictionary<LiteElement, CssComputed>();
            _dirtyElements = new HashSet<LiteElement>();
        }

        #region Dirty Tracking

        /// <summary>
        /// Mark an element as needing re-layout.
        /// </summary>
        public void MarkDirty(LiteElement element)
        {
            if (element == null) return;

            lock (_dirtyLock)
            {
                _dirtyElements.Add(element);
                // Also mark ancestors as needing partial re-layout
                var parent = element.Parent;
                while (parent != null)
                {
                    _dirtyElements.Add(parent);
                    parent = parent.Parent;
                }
            }

            FenLogger.Debug($"[IncrementalLayout] Marked dirty: {element.Tag}", LogCategory.Layout);
        }

        /// <summary>
        /// Mark entire tree as needing full re-layout.
        /// </summary>
        public void MarkFullLayoutRequired()
        {
            _fullLayoutRequired = true;
            lock (_dirtyLock)
            {
                _dirtyElements.Clear();
            }
        }

        /// <summary>
        /// Check if element needs re-layout.
        /// </summary>
        public bool IsDirty(LiteElement element)
        {
            if (_fullLayoutRequired) return true;
            lock (_dirtyLock)
            {
                return _dirtyElements.Contains(element);
            }
        }

        /// <summary>
        /// Clear dirty state after layout completes.
        /// </summary>
        public void ClearDirtyState()
        {
            _fullLayoutRequired = false;
            lock (_dirtyLock)
            {
                _dirtyElements.Clear();
            }
        }

        /// <summary>
        /// Check if any elements are dirty.
        /// </summary>
        public bool HasDirtyElements => _fullLayoutRequired || _dirtyElements.Count > 0;

        #endregion

        #region Layout Caching

        /// <summary>
        /// Get cached layout for an element.
        /// </summary>
        public LayoutCache GetCachedLayout(LiteElement element)
        {
            return _layoutCache.TryGetValue(element, out var cache) ? cache : null;
        }

        /// <summary>
        /// Cache layout result for an element.
        /// </summary>
        public void CacheLayout(LiteElement element, SKRect box, float contentHeight)
        {
            var cache = new LayoutCache
            {
                Box = box,
                ContentHeight = contentHeight,
                CachedAt = DateTime.UtcNow
            };
            _layoutCache[element] = cache;
        }

        /// <summary>
        /// Invalidate cached layout for element and descendants.
        /// </summary>
        public void InvalidateLayout(LiteElement element)
        {
            if (element == null) return;

            _layoutCache.TryRemove(element, out _);
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    InvalidateLayout(child);
                }
            }
        }

        #endregion

        #region Style Caching

        /// <summary>
        /// Get cached computed style.
        /// </summary>
        public CssComputed GetCachedStyle(LiteElement element)
        {
            return _styleCache.TryGetValue(element, out var style) ? style : null;
        }

        /// <summary>
        /// Cache computed style.
        /// </summary>
        public void CacheStyle(LiteElement element, CssComputed style)
        {
            _styleCache[element] = style;
        }

        /// <summary>
        /// Invalidate style cache for element and descendants.
        /// </summary>
        public void InvalidateStyle(LiteElement element)
        {
            if (element == null) return;

            _styleCache.TryRemove(element, out _);
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    InvalidateStyle(child);
                }
            }
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public CacheStats GetStats()
        {
            return new CacheStats
            {
                LayoutCacheSize = _layoutCache.Count,
                StyleCacheSize = _styleCache.Count,
                DirtyElementCount = _dirtyElements.Count,
                FullLayoutRequired = _fullLayoutRequired
            };
        }

        /// <summary>
        /// Clear all caches.
        /// </summary>
        public void ClearAll()
        {
            _layoutCache.Clear();
            _styleCache.Clear();
            _dirtyElements.Clear();
            _fullLayoutRequired = true;
        }

        #endregion
    }

    /// <summary>
    /// Cached layout data for an element.
    /// </summary>
    public class LayoutCache
    {
        public SKRect Box { get; set; }
        public float ContentHeight { get; set; }
        public DateTime CachedAt { get; set; }
    }

    /// <summary>
    /// Cache statistics for monitoring.
    /// </summary>
    public struct CacheStats
    {
        public int LayoutCacheSize;
        public int StyleCacheSize;
        public int DirtyElementCount;
        public bool FullLayoutRequired;

        public override string ToString() =>
            $"Layout: {LayoutCacheSize}, Style: {StyleCacheSize}, Dirty: {DirtyElementCount}, Full: {FullLayoutRequired}";
    }
}
