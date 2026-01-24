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
        
        /// <summary>
        /// Compares this paint tree with another to identify changes.
        /// </summary>
        public PaintTreeDiff Diff(ImmutablePaintTree other)
        {
            if (other == null) return new PaintTreeDiff { AddedNodes = new List<PaintNodeBase>(Roots) };
            
            var added = new List<PaintNodeBase>();
            var removed = new List<PaintNodeBase>();
            var modified = new List<NodeChange>();

            DiffRecursive(Roots, other.Roots, added, removed, modified);

            return new PaintTreeDiff
            {
                AddedNodes = added,
                RemovedNodes = removed,
                ModifiedNodes = modified
            };
        }

        private void DiffRecursive(
            IReadOnlyList<PaintNodeBase> current, 
            IReadOnlyList<PaintNodeBase> other,
            List<PaintNodeBase> added,
            List<PaintNodeBase> removed,
            List<NodeChange> modified)
        {
            // Keyed Matching: Use SourceNode + GetType() as a stable key.
            // This allows us to detect moved nodes and stable updates even if order changes slightly.
            var otherNodesByKey = new Dictionary<string, PaintNodeBase>();
            foreach (var node in other)
            {
                string key = GetNodeKey(node);
                if (!string.IsNullOrEmpty(key)) otherNodesByKey[key] = node;
            }

            foreach (var nodeB in current)
            {
                string key = GetNodeKey(nodeB);
                if (string.IsNullOrEmpty(key) || !otherNodesByKey.TryGetValue(key, out var nodeA))
                {
                    added.Add(nodeB);
                    continue;
                }

                // Node exists in both, check for modifications
                bool geomChanged = nodeA.Bounds != nodeB.Bounds || nodeA.Transform != nodeB.Transform;
                bool styleChanged = nodeA.Opacity != nodeB.Opacity || nodeA.ClipRect != nodeB.ClipRect;
                
                if (geomChanged) modified.Add(new NodeChange(nodeA, nodeB, ChangeType.Geometry));
                else if (styleChanged) modified.Add(new NodeChange(nodeA, nodeB, ChangeType.Style));

                // Recurse into children
                DiffRecursive(nodeB.Children, nodeA.Children, added, removed, modified);
                
                // Mark as processed
                otherNodesByKey.Remove(key);
            }

            // Remaining nodes in 'other' were removed
            foreach (var node in otherNodesByKey.Values)
            {
                removed.Add(node);
            }
        }

        private string GetNodeKey(PaintNodeBase node)
        {
            if (node.SourceNode == null) return null;
            // Key is composed of the DOM node hash and the paint node type (role)
            return $"{node.SourceNode.GetHashCode()}_{node.GetType().Name}";
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
