using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2; // For consistency if needed

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// The paint tree - a hierarchy of PaintNodes ordered for correct rendering.
    /// Built from LayoutResult, used by the renderer for painting.
    /// </summary>
    public sealed class PaintTree
    {
        /// <summary>
        /// Root paint node of the tree.
        /// </summary>
        public PaintNode Root { get; }
        
        /// <summary>
        /// Total number of nodes in the tree.
        /// </summary>
        public int NodeCount { get; }
        
        /// <summary>
        /// Frame ID this paint tree was built for.
        /// </summary>
        public int FrameId { get; }
        
        /// <summary>
        /// Creates a new paint tree.
        /// </summary>
        public PaintTree(PaintNode root, int frameId = 0)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            FrameId = frameId;
            NodeCount = CountNodes(root);
        }
        
        /// <summary>
        /// Counts all nodes in the tree recursively.
        /// </summary>
        private static int CountNodes(PaintNode node)
        {
            if (node == null) return 0;
            
            int count = 1;
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    count += CountNodes(child);
                }
            }
            return count;
        }
        
        /// <summary>
        /// Creates an empty paint tree.
        /// </summary>
        public static PaintTree Empty => new PaintTree(new PaintNode());
        
        /// <summary>
        /// Traverses the tree in paint order (pre-order), invoking action for each node.
        /// </summary>
        public void Traverse(Action<PaintNode> action)
        {
            TraverseNode(Root, action);
        }
        
        /// <summary>
        /// Traverses a node and its children.
        /// </summary>
        private void TraverseNode(PaintNode node, Action<PaintNode> action)
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

