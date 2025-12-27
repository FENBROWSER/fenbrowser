using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Immutable paint tree - fully ordered description of everything to draw.
    /// 
    /// INVARIANTS:
    /// - Fully ordered (no runtime sorting)
    /// - No hidden dependencies
    /// - No DOM references
    /// - No layout math
    /// - Safe to reuse across frames
    /// </summary>
    public sealed class ImmutablePaintTree
    {
        /// <summary>
        /// Root paint nodes of the tree (multiple roots for multiple stacking contexts).
        /// </summary>
        public IReadOnlyList<PaintNodeBase> Roots { get; }
        
        /// <summary>
        /// Frame ID this paint tree was built for.
        /// Used for caching and invalidation.
        /// </summary>
        public int FrameId { get; }
        
        /// <summary>
        /// Total number of nodes in the tree.
        /// </summary>
        public int NodeCount { get; }
        
        /// <summary>
        /// Timestamp when this tree was built.
        /// </summary>
        public long BuildTimestamp { get; }
        
        /// <summary>
        /// Creates a new immutable paint tree.
        /// </summary>
        public ImmutablePaintTree(IReadOnlyList<PaintNodeBase> roots, int frameId = 0)
        {
            Roots = roots ?? throw new ArgumentNullException(nameof(roots));
            FrameId = frameId;
            NodeCount = CountNodes(roots);
            BuildTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        
        /// <summary>
        /// Creates an empty paint tree.
        /// </summary>
        public static ImmutablePaintTree Empty => new ImmutablePaintTree(Array.Empty<PaintNodeBase>());
        
        /// <summary>
        /// Counts all nodes in the tree recursively.
        /// </summary>
        private static int CountNodes(IReadOnlyList<PaintNodeBase> nodes)
        {
            if (nodes == null || nodes.Count == 0) return 0;
            
            int count = 0;
            foreach (var node in nodes)
            {
                count += 1 + CountNodes(node.Children);
            }
            return count;
        }
        
        /// <summary>
        /// Traverses the tree in paint order, invoking action for each node.
        /// </summary>
        public void Traverse(Action<PaintNodeBase> action)
        {
            if (action == null) return;
            
            foreach (var root in Roots)
            {
                TraverseNode(root, action);
            }
        }
        
        private static void TraverseNode(PaintNodeBase node, Action<PaintNodeBase> action)
        {
            if (node == null) return;
            
            action(node);
            
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    TraverseNode(child, action);
                }
            }
        }
    }
}
