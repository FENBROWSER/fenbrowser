// FenBrowser.Core.Css - External Style Cache
// Decouples CSS computed styles from DOM nodes

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// External cache for computed styles.
    /// Decouples CSS from DOM, following Servo's pattern.
    ///
    /// IMPORTANT: This replaces storing ComputedStyle directly on Node.
    /// Benefits:
    /// - Clean separation of concerns (DOM doesn't know about CSS)
    /// - Enables efficient style recalculation
    /// - Allows garbage collection of nodes without style reference cycles
    /// - Supports multiple style caches (e.g., for snapshots)
    /// </summary>
    public sealed class StyleCache
    {
        // Use ConditionalWeakTable to allow nodes to be GC'd without manual cleanup
        private readonly ConditionalWeakTable<Dom.V2.Node, CssComputed> _cache = new();
        private readonly object _styledElementsLock = new();

        // Optional: Track elements with styles for iteration
        private readonly HashSet<WeakReference<Dom.V2.Element>> _styledElements = new();

        /// <summary>
        /// Gets the computed style for a node.
        /// Returns null if no style has been computed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CssComputed Get(Dom.V2.Node node)
        {
            if (node == null) return null;
            _cache.TryGetValue(node, out var style);
            return style;
        }

        /// <summary>
        /// Sets the computed style for a node.
        /// </summary>
        public void Set(Dom.V2.Node node, CssComputed style)
        {
            if (node == null) return;

            // AddOrUpdate semantics
            if (_cache.TryGetValue(node, out _))
            {
                // Remove and re-add (ConditionalWeakTable doesn't have update)
                _cache.Remove(node);
            }
            _cache.Add(node, style);

            // Track styled elements
            if (node is Dom.V2.Element element)
            {
                lock (_styledElementsLock)
                {
                    _styledElements.Add(new WeakReference<Dom.V2.Element>(element));
                }
            }
        }

        /// <summary>
        /// Removes the computed style for a node.
        /// </summary>
        public void Remove(Dom.V2.Node node)
        {
            if (node == null) return;
            _cache.Remove(node);
        }

        /// <summary>
        /// Checks if a node has a computed style.
        /// </summary>
        public bool Has(Dom.V2.Node node)
        {
            if (node == null) return false;
            return _cache.TryGetValue(node, out _);
        }

        /// <summary>
        /// Clears all cached styles.
        /// Note: ConditionalWeakTable doesn't have Clear, so we create a new instance.
        /// </summary>
        public void Clear()
        {
            lock (_styledElementsLock)
            {
                _styledElements.Clear();
            }
            // ConditionalWeakTable entries are cleared when keys are GC'd
            // For immediate clear, caller should create a new StyleCache
        }

        /// <summary>
        /// Gets the number of cached styles (approximate, may include GC'd entries).
        /// </summary>
        public int Count
        {
            get
            {
                lock (_styledElementsLock)
                {
                    return _styledElements.Count;
                }
            }
        }

        /// <summary>
        /// Cleans up dead weak references.
        /// Call periodically to free memory.
        /// </summary>
        public void Cleanup()
        {
            lock (_styledElementsLock)
            {
                _styledElements.RemoveWhere(wr => !wr.TryGetTarget(out _));
            }
        }
    }

    /// <summary>
    /// Extension methods for accessing styles from nodes.
    /// Provides convenient API without polluting Node class.
    /// </summary>
    public static class NodeStyleExtensions
    {
        // Shared default cache for convenience.
        // CSS computation runs on worker threads while layout, paint, and JS bridges
        // may read styles from different threads, so the default cache cannot be thread-local.
        private static StyleCache _defaultCache = new StyleCache();

        private static StyleCache DefaultCache => _defaultCache ??= new StyleCache();

        /// <summary>
        /// Gets the computed style for this node from the default cache.
        /// </summary>
        public static CssComputed GetComputedStyle(this Dom.V2.Node node)
        {
            return DefaultCache.Get(node);
        }

        /// <summary>
        /// Sets the computed style for this node in the default cache.
        /// </summary>
        public static void SetComputedStyle(this Dom.V2.Node node, CssComputed style)
        {
            DefaultCache.Set(node, style);
        }

        /// <summary>
        /// Gets the computed style for this node from a specific cache.
        /// </summary>
        public static CssComputed GetComputedStyle(this Dom.V2.Node node, StyleCache cache)
        {
            return cache?.Get(node);
        }

        /// <summary>
        /// Sets the computed style for this node in a specific cache.
        /// </summary>
        public static void SetComputedStyle(this Dom.V2.Node node, CssComputed style, StyleCache cache)
        {
            cache?.Set(node, style);
        }

        /// <summary>
        /// Gets the default thread-local style cache.
        /// </summary>
        public static StyleCache GetDefaultStyleCache()
        {
            return DefaultCache;
        }

        /// <summary>
        /// Sets a new default style cache for this thread.
        /// </summary>
        public static void SetDefaultStyleCache(StyleCache cache)
        {
            _defaultCache = cache;
        }
    }

    /// <summary>
    /// Style context that holds both cache and related state.
    /// Used during style recalculation passes.
    /// </summary>
    public sealed class StyleContext
    {
        /// <summary>
        /// The style cache being populated.
        /// </summary>
        public StyleCache Cache { get; }

        /// <summary>
        /// Default styles to inherit from.
        /// </summary>
        public CssComputed DefaultStyle { get; set; }

        /// <summary>
        /// User-agent stylesheet rules.
        /// </summary>
        public object UserAgentStylesheet { get; set; }

        /// <summary>
        /// Document stylesheets.
        /// </summary>
        public IReadOnlyList<object> Stylesheets { get; set; }

        /// <summary>
        /// Media query evaluation context.
        /// </summary>
        public MediaContext Media { get; set; }

        /// <summary>
        /// Statistics about the style pass.
        /// </summary>
        public StyleStatistics Stats { get; } = new StyleStatistics();

        public StyleContext(StyleCache cache = null)
        {
            Cache = cache ?? new StyleCache();
        }

        /// <summary>
        /// Gets or computes the style for an element.
        /// </summary>
        public CssComputed GetOrCompute(Dom.V2.Element element, Func<Dom.V2.Element, CssComputed> compute)
        {
            var existing = Cache.Get(element);
            if (existing != null)
                return existing;

            Stats.ElementsStyled++;
            var style = compute(element);
            Cache.Set(element, style);
            return style;
        }
    }

    /// <summary>
    /// Media query evaluation context.
    /// </summary>
    public class MediaContext
    {
        public double ViewportWidth { get; set; }
        public double ViewportHeight { get; set; }
        public double DevicePixelRatio { get; set; } = 1.0;
        public string ColorScheme { get; set; } = "light";
        public bool PrefersReducedMotion { get; set; }
        public bool PrefersReducedTransparency { get; set; }
    }

    /// <summary>
    /// Statistics from a style recalculation pass.
    /// </summary>
    public class StyleStatistics
    {
        public int ElementsStyled { get; set; }
        public int RulesMatched { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public TimeSpan Duration { get; set; }

        public void Reset()
        {
            ElementsStyled = 0;
            RulesMatched = 0;
            CacheHits = 0;
            CacheMisses = 0;
            Duration = TimeSpan.Zero;
        }
    }
}
