using System;
using System.Collections.Generic;
using SkiaSharp;

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
                bool styleChanged = nodeA.Opacity != nodeB.Opacity
                    || nodeA.ClipRect != nodeB.ClipRect
                    || nodeA.IsHovered != nodeB.IsHovered
                    || nodeA.IsFocused != nodeB.IsFocused
                    || !HasEquivalentVisualState(nodeA, nodeB);
                
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

        private static bool HasEquivalentVisualState(PaintNodeBase previous, PaintNodeBase current)
        {
            if (previous == null || current == null)
            {
                return previous == current;
            }

            if (previous.GetType() != current.GetType())
            {
                return false;
            }

            return (previous, current) switch
            {
                (BackgroundPaintNode a, BackgroundPaintNode b) => Nullable.Equals(a.Color, b.Color)
                    && (a.Gradient == null) == (b.Gradient == null)
                    && HaveEqualPoints(a.BorderRadius, b.BorderRadius),
                (BorderPaintNode a, BorderPaintNode b) => HaveEqualFloats(a.Widths, b.Widths)
                    && HaveEqualColors(a.Colors, b.Colors)
                    && HaveEqualStrings(a.Styles, b.Styles)
                    && HaveEqualPoints(a.BorderRadius, b.BorderRadius),
                (TextPaintNode a, TextPaintNode b) => a.Color == b.Color
                    && a.FontSize.Equals(b.FontSize)
                    && a.TextOrigin == b.TextOrigin
                    && string.Equals(a.FallbackText, b.FallbackText, StringComparison.Ordinal)
                    && string.Equals(a.WritingMode, b.WritingMode, StringComparison.Ordinal)
                    && string.Equals(a.Typeface?.FamilyName, b.Typeface?.FamilyName, StringComparison.Ordinal)
                    && HaveEqualStrings(a.TextDecorations, b.TextDecorations),
                (ImagePaintNode a, ImagePaintNode b) => ReferenceEquals(a.Bitmap, b.Bitmap)
                    && Nullable.Equals(a.SourceRect, b.SourceRect)
                    && string.Equals(a.ObjectFit, b.ObjectFit, StringComparison.Ordinal),
                (BoxShadowPaintNode a, BoxShadowPaintNode b) => a.Blur.Equals(b.Blur)
                    && a.Spread.Equals(b.Spread)
                    && a.Offset == b.Offset
                    && a.Color == b.Color
                    && a.Inset == b.Inset
                    && HaveEqualPoints(a.BorderRadius, b.BorderRadius),
                (StackingContextPaintNode a, StackingContextPaintNode b) => a.ZIndex == b.ZIndex
                    && string.Equals(a.Filter, b.Filter, StringComparison.Ordinal)
                    && string.Equals(a.BackdropFilter, b.BackdropFilter, StringComparison.Ordinal),
                (ScrollPaintNode a, ScrollPaintNode b) => a.ScrollX.Equals(b.ScrollX)
                    && a.ScrollY.Equals(b.ScrollY),
                (StickyPaintNode a, StickyPaintNode b) => a.StickyOffset == b.StickyOffset,
                (MaskPaintNode a, MaskPaintNode b) => ReferenceEquals(a.MaskBitmap, b.MaskBitmap)
                    && string.Equals(a.MaskSize, b.MaskSize, StringComparison.Ordinal),
                _ => true
            };
        }

        private static bool HaveEqualFloats(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HaveEqualPoints(IReadOnlyList<SKPoint> left, IReadOnlyList<SKPoint> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HaveEqualColors(IReadOnlyList<SKColor> left, IReadOnlyList<SKColor> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HaveEqualStrings(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
