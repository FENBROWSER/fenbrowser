using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
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
        private readonly ConcurrentDictionary<Element, LayoutCache> _layoutCache;
        private readonly ConcurrentDictionary<Element, CssComputed> _styleCache;
        private readonly HashSet<Element> _dirtyElements;
        private readonly object _dirtyLock = new();
        private bool _fullLayoutRequired = true;

        public IncrementalLayoutManager()
        {
            _layoutCache = new ConcurrentDictionary<Element, LayoutCache>();
            _styleCache = new ConcurrentDictionary<Element, CssComputed>();
            _dirtyElements = new HashSet<Element>();
        }

        #region Dirty Tracking

        /// <summary>
        /// Mark an element and its ancestors as needing re-layout.
        /// </summary>
        public void MarkDirty(Element element)
        {
            if (element == null) return;

            lock (_dirtyLock)
            {
                _dirtyElements.Add(element);
                var parent = element.ParentElement;
                while (parent != null)
                {
                    _dirtyElements.Add(parent);
                    parent = parent.ParentElement;
                }
            }

            EngineLogCompat.Debug($"[IncrementalLayout] Marked dirty: {element.TagName}", LogCategory.Layout);
        }

        /// <summary>
        /// Mark entire tree as needing full re-layout.
        /// </summary>
        public void MarkFullLayoutRequired()
        {
            lock (_dirtyLock)
            {
                _fullLayoutRequired = true;
                _dirtyElements.Clear();
            }
        }

        /// <summary>
        /// Check if element needs re-layout.
        /// </summary>
        public bool IsDirty(Element element)
        {
            lock (_dirtyLock)
            {
                return _fullLayoutRequired || _dirtyElements.Contains(element);
            }
        }

        /// <summary>
        /// Clear dirty state after layout completes.
        /// </summary>
        public void ClearDirtyState()
        {
            // Full-layout state and the dirty set are one logical invalidation state.
            // Mutating them under separate synchronization allowed MarkDirty to race
            // between the old flag write and HashSet.Clear, silently losing a fresh
            // invalidation. Keep the transition atomic with MarkDirty/MarkFullLayout.
            lock (_dirtyLock)
            {
                _dirtyElements.Clear();
                _fullLayoutRequired = false;
            }
        }

        /// <summary>
        /// Check if any elements are dirty.
        /// </summary>
        public bool HasDirtyElements
        {
            get
            {
                lock (_dirtyLock)
                {
                    return _fullLayoutRequired || _dirtyElements.Count > 0;
                }
            }
        }

        #endregion

        #region Layout Caching

        /// <summary>
        /// Get cached layout for an element.
        /// </summary>
        public LayoutCache GetCachedLayout(Element element)
        {
            return element != null && _layoutCache.TryGetValue(element, out var cache) ? cache : null;
        }

        /// <summary>
        /// Cache layout result for an element.
        /// </summary>
        public void CacheLayout(Element element, SKRect box, float contentHeight)
        {
            if (element == null) return;

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
        public void InvalidateLayout(Element element)
        {
            InvalidateSubtree(element, _layoutCache);
        }

        #endregion

        #region Style Caching

        /// <summary>
        /// Get cached computed style.
        /// </summary>
        public CssComputed GetCachedStyle(Element element)
        {
            return element != null && _styleCache.TryGetValue(element, out var style) ? style : null;
        }

        /// <summary>
        /// Cache computed style.
        /// </summary>
        public void CacheStyle(Element element, CssComputed style)
        {
            if (element == null || style == null) return;
            _styleCache[element] = style;
        }

        /// <summary>
        /// Invalidate style cache for element and descendants.
        /// </summary>
        public void InvalidateStyle(Element element)
        {
            InvalidateSubtree(element, _styleCache);
        }

        private static void InvalidateSubtree<T>(
            Element root,
            ConcurrentDictionary<Element, T> cache)
        {
            if (root == null || cache == null) return;

            // Deep/generated DOMs can contain tens of thousands of nested elements.
            // Cache invalidation must not recurse on the native stack just because the
            // document is deep; use an explicit work stack instead.
            var stack = new Stack<Element>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                cache.TryRemove(current, out _);

                if (current.Children == null) continue;
                foreach (var child in current.Children)
                {
                    if (child is Element childElement)
                    {
                        stack.Push(childElement);
                    }
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
            int dirtyCount;
            bool fullLayoutRequired;
            lock (_dirtyLock)
            {
                dirtyCount = _dirtyElements.Count;
                fullLayoutRequired = _fullLayoutRequired;
            }

            return new CacheStats
            {
                LayoutCacheSize = _layoutCache.Count,
                StyleCacheSize = _styleCache.Count,
                DirtyElementCount = dirtyCount,
                FullLayoutRequired = fullLayoutRequired
            };
        }

        /// <summary>
        /// Removes cached layout and style entries for elements that are no longer in
        /// the live DOM. Called after full layout to prevent cache retention over a
        /// tab's lifetime.
        /// </summary>
        public void ClearOrphanedEntries(HashSet<Element> liveElements)
        {
            if (liveElements == null) return;

            // An empty live set means the document really has no live elements. The
            // previous early return retained every old element strongly forever after
            // navigating/clearing to an empty document.
            if (liveElements.Count == 0)
            {
                _layoutCache.Clear();
                _styleCache.Clear();
                return;
            }

            foreach (var key in _layoutCache.Keys)
            {
                if (!liveElements.Contains(key))
                {
                    _layoutCache.TryRemove(key, out _);
                }
            }

            foreach (var key in _styleCache.Keys)
            {
                if (!liveElements.Contains(key))
                {
                    _styleCache.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Clear all caches and force the next layout to rebuild.
        /// </summary>
        public void ClearAll()
        {
            _layoutCache.Clear();
            _styleCache.Clear();
            lock (_dirtyLock)
            {
                _dirtyElements.Clear();
                _fullLayoutRequired = true;
            }
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
