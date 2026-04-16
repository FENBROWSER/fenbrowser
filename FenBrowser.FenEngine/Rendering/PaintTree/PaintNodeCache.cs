using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// High-performance side-table cache for PaintNode fragments.
    /// Enables O(N_dirty) paint tree construction by reusing static subtrees.
    /// </summary>
    internal static class PaintNodeCache
    {
        private sealed class CacheMetadata
        {
            public SKRect Bounds;
            public bool InStackingContext;
        }

        // Use ConditionalWeakTable to ensure we don't leak memory when nodes are GC'd.
        private static readonly ConditionalWeakTable<Node, List<PaintNodeBase>> _nodeCache = new();
        private static readonly ConditionalWeakTable<Node, CacheMetadata> _cacheMetadata = new();

        /// <summary>
        /// Retrieves cached paint nodes for a DOM node.
        /// Returns null if no cache exists.
        /// </summary>
        public static List<PaintNodeBase>? Get(Node node)
        {
            if (node == null) return null;
            _nodeCache.TryGetValue(node, out var nodes);
            return nodes;
        }

        /// <summary>
        /// Updates the cache for a specific DOM node.
        /// Also updates the node's cached geometry to detect shifts.
        /// </summary>
        public static void Update(Node node, List<PaintNodeBase> nodes, SKRect currentBounds, bool inStackingContext)
        {
            if (node == null || nodes == null) return;

            _nodeCache.Remove(node);
            _nodeCache.Add(node, nodes);

            _cacheMetadata.Remove(node);
            _cacheMetadata.Add(node, new CacheMetadata
            {
                Bounds = currentBounds,
                InStackingContext = inStackingContext
            });
        }

        /// <summary>
        /// Verifies if the cached fragment for a node is still valid.
        /// Validation criteria:
        /// 1. Node is NOT Paint-Dirty or Child-Paint-Dirty.
        /// 2. Node's absolute Document bounds have not changed.
        /// 3. Node's stacking context membership is identical.
        /// </summary>
        public static bool IsCacheValid(Node node, SKRect currentBounds, bool inStackingContext)
        {
            if (node == null) return false;

            // 1. Check Dirty Flags
            if (node.PaintDirty || node.ChildPaintDirty) return false;

            if (!_cacheMetadata.TryGetValue(node, out var meta)) return false;

            // 2. Verifying geometry stability (The "Stable-Box" constraint)
            if (meta.Bounds != currentBounds) return false;

            // 3. Stacking Context stability
            if (meta.InStackingContext != inStackingContext) return false;

            // Check if we actually have data
            return _nodeCache.TryGetValue(node, out _);
        }

        /// <summary>
        /// Clears the cache for a specific node (invalidation).
        /// </summary>
        public static void Invalidate(Node node)
        {
            if (node == null) return;
            _nodeCache.Remove(node);
            _cacheMetadata.Remove(node);
        }
    }
}
