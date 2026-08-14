using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Immutable paint tree - fully ordered description of everything to draw.
    /// </summary>
    public sealed class ImmutablePaintTree
    {
        public IReadOnlyList<PaintNodeBase> Roots { get; }
        public int FrameId { get; }
        public int NodeCount { get; }
        public long BuildTimestamp { get; }

        public ImmutablePaintTree(IReadOnlyList<PaintNodeBase> roots, int frameId = 0, int? nodeCount = null)
        {
            Roots = roots ?? throw new ArgumentNullException(nameof(roots));
            FrameId = frameId;
            NodeCount = nodeCount ?? CountNodes(roots);
            BuildTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static ImmutablePaintTree Empty { get; } =
            new ImmutablePaintTree(Array.Empty<PaintNodeBase>());

        private static int CountNodes(IReadOnlyList<PaintNodeBase> nodes)
        {
            if (nodes == null || nodes.Count == 0) return 0;

            var count = 0;
            var stack = new Stack<PaintNodeBase>();
            for (var i = nodes.Count - 1; i >= 0; i--)
            {
                if (nodes[i] != null) stack.Push(nodes[i]);
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                count++;
                var children = node.Children;
                if (children == null) continue;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i] != null) stack.Push(children[i]);
                }
            }

            return count;
        }

        public ImmutablePaintTree WithReplacedSubtree(
            Node sourceNode,
            IReadOnlyList<PaintNodeBase> newSubtreeNodes)
        {
            if (sourceNode == null || newSubtreeNodes == null || Roots.Count == 0)
                return this;

            var parentLinks = new Dictionary<PaintNodeBase, ParentLink>();
            var search = new Stack<PaintNodeBase>();

            for (var i = Roots.Count - 1; i >= 0; i--)
            {
                var root = Roots[i];
                if (root == null) continue;
                parentLinks[root] = new ParentLink(null, i);
                search.Push(root);
            }

            PaintNodeBase found = null;
            while (search.Count > 0)
            {
                var node = search.Pop();
                if (ReferenceEquals(node.SourceNode, sourceNode))
                {
                    found = node;
                    break;
                }

                var children = node.Children;
                if (children == null) continue;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    if (child == null) continue;
                    parentLinks[child] = new ParentLink(node, i);
                    search.Push(child);
                }
            }

            if (found == null)
                return this;

            IReadOnlyList<PaintNodeBase> replacement = newSubtreeNodes;
            var current = found;

            while (parentLinks.TryGetValue(current, out var link) && link.Parent != null)
            {
                var parent = link.Parent;
                var newChildren = ReplaceAt(parent.Children, link.Index, replacement);
                var clonedParent = parent.CloneWithChildren(newChildren);
                replacement = new[] { clonedParent };
                current = parent;
            }

            if (!parentLinks.TryGetValue(current, out var rootLink) || rootLink.Parent != null)
                return this;

            var newRoots = ReplaceAt(Roots, rootLink.Index, replacement);
            return new ImmutablePaintTree(newRoots, FrameId, CountNodes(newRoots));
        }

        private static IReadOnlyList<PaintNodeBase> ReplaceAt(
            IReadOnlyList<PaintNodeBase> nodes,
            int index,
            IReadOnlyList<PaintNodeBase> replacement)
        {
            if (nodes == null || index < 0 || index >= nodes.Count)
                return nodes;

            var replacementCount = replacement?.Count ?? 0;
            var result = new List<PaintNodeBase>(Math.Max(0, nodes.Count - 1 + replacementCount));
            for (var i = 0; i < index; i++) result.Add(nodes[i]);
            if (replacement != null)
            {
                for (var i = 0; i < replacement.Count; i++) result.Add(replacement[i]);
            }
            for (var i = index + 1; i < nodes.Count; i++) result.Add(nodes[i]);
            return result;
        }

        private readonly struct ParentLink
        {
            public ParentLink(PaintNodeBase parent, int index)
            {
                Parent = parent;
                Index = index;
            }

            public PaintNodeBase Parent { get; }
            public int Index { get; }
        }

        public void Traverse(Action<PaintNodeBase> action)
        {
            if (action == null || Roots.Count == 0) return;

            var stack = new Stack<PaintNodeBase>();
            for (var i = Roots.Count - 1; i >= 0; i--)
            {
                if (Roots[i] != null) stack.Push(Roots[i]);
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                action(node);

                var children = node.Children;
                if (children == null) continue;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i] != null) stack.Push(children[i]);
                }
            }
        }

        public PaintTreeDiff Diff(ImmutablePaintTree other)
        {
            if (other == null)
            {
                return new PaintTreeDiff { AddedNodes = new List<PaintNodeBase>(Roots) };
            }

            var added = new List<PaintNodeBase>();
            var removed = new List<PaintNodeBase>();
            var modified = new List<NodeChange>();

            DiffIterative(Roots, other.Roots, added, removed, modified);

            return new PaintTreeDiff
            {
                AddedNodes = added,
                RemovedNodes = removed,
                ModifiedNodes = modified
            };
        }

        private static void DiffIterative(
            IReadOnlyList<PaintNodeBase> currentRoots,
            IReadOnlyList<PaintNodeBase> previousRoots,
            List<PaintNodeBase> added,
            List<PaintNodeBase> removed,
            List<NodeChange> modified)
        {
            var work = new Stack<(IReadOnlyList<PaintNodeBase> Current, IReadOnlyList<PaintNodeBase> Previous)>();
            work.Push((currentRoots ?? Array.Empty<PaintNodeBase>(), previousRoots ?? Array.Empty<PaintNodeBase>()));

            while (work.Count > 0)
            {
                var pair = work.Pop();
                var previousByKey = new Dictionary<PaintNodeKey, Queue<PaintNodeBase>>();

                // StableNodeId is assigned per DOM source, not per paint fragment. A
                // source can therefore emit several nodes of the same subtype (e.g.
                // multiple text fragments). Preserve sibling order within each key
                // instead of overwriting all but the last previous node.
                foreach (var node in pair.Previous)
                {
                    if (!TryGetNodeKey(node, out var key)) continue;
                    if (!previousByKey.TryGetValue(key, out var bucket))
                    {
                        bucket = new Queue<PaintNodeBase>();
                        previousByKey.Add(key, bucket);
                    }
                    bucket.Enqueue(node);
                }

                foreach (var current in pair.Current)
                {
                    if (!TryGetNodeKey(current, out var key) ||
                        !previousByKey.TryGetValue(key, out var bucket) ||
                        bucket.Count == 0)
                    {
                        if (current != null) added.Add(current);
                        continue;
                    }

                    var previous = bucket.Dequeue();
                    if (bucket.Count == 0) previousByKey.Remove(key);

                    var geomChanged = previous.Bounds != current.Bounds || previous.Transform != current.Transform;
                    var styleChanged = previous.Opacity != current.Opacity
                        || previous.ClipRect != current.ClipRect
                        || previous.IsHovered != current.IsHovered
                        || previous.IsFocused != current.IsFocused
                        || !HasEquivalentVisualState(previous, current);

                    if (geomChanged)
                    {
                        modified.Add(new NodeChange(previous, current, ChangeType.Geometry));
                    }
                    else if (styleChanged)
                    {
                        modified.Add(new NodeChange(previous, current, ChangeType.Style));
                    }

                    work.Push((
                        current.Children ?? Array.Empty<PaintNodeBase>(),
                        previous.Children ?? Array.Empty<PaintNodeBase>()));
                }

                foreach (var bucket in previousByKey.Values)
                {
                    while (bucket.Count > 0)
                    {
                        removed.Add(bucket.Dequeue());
                    }
                }
            }
        }

        private readonly struct PaintNodeKey : IEquatable<PaintNodeKey>
        {
            private readonly ulong _stableNodeId;
            private readonly Node _sourceNode;
            private readonly Type _nodeType;

            public PaintNodeKey(PaintNodeBase node)
            {
                _stableNodeId = node.StableNodeId;
                _sourceNode = node.SourceNode;
                _nodeType = node.GetType();
            }

            public bool Equals(PaintNodeKey other)
            {
                if (!ReferenceEquals(_nodeType, other._nodeType)) return false;

                if (_stableNodeId != 0 || other._stableNodeId != 0)
                {
                    return _stableNodeId != 0 && _stableNodeId == other._stableNodeId;
                }

                return ReferenceEquals(_sourceNode, other._sourceNode);
            }

            public override bool Equals(object obj) => obj is PaintNodeKey other && Equals(other);

            public override int GetHashCode()
            {
                var identityHash = _stableNodeId != 0
                    ? _stableNodeId.GetHashCode()
                    : (_sourceNode == null ? 0 : RuntimeHelpers.GetHashCode(_sourceNode));
                return HashCode.Combine(_nodeType, identityHash);
            }
        }

        private static bool TryGetNodeKey(PaintNodeBase node, out PaintNodeKey key)
        {
            if (node == null || (node.StableNodeId == 0 && node.SourceNode == null))
            {
                key = default;
                return false;
            }

            key = new PaintNodeKey(node);
            return true;
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
                    && ReferenceEquals(a.Gradient, b.Gradient)
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
                    && HaveEqualStrings(a.TextDecorations, b.TextDecorations)
                    && HaveEqualGlyphs(a.Glyphs, b.Glyphs),
                (ImagePaintNode a, ImagePaintNode b) => ReferenceEquals(a.Bitmap, b.Bitmap)
                    && Nullable.Equals(a.SourceRect, b.SourceRect)
                    && string.Equals(a.ObjectFit, b.ObjectFit, StringComparison.Ordinal)
                    && string.Equals(a.ObjectPosition, b.ObjectPosition, StringComparison.Ordinal)
                    && a.IsBackgroundImage == b.IsBackgroundImage
                    && a.TileModeX == b.TileModeX
                    && a.TileModeY == b.TileModeY
                    && a.BackgroundPosition == b.BackgroundPosition
                    && Nullable.Equals(a.BackgroundImageSize, b.BackgroundImageSize)
                    && a.BackgroundOrigin == b.BackgroundOrigin
                    && a.BackgroundAttachmentFixed == b.BackgroundAttachmentFixed
                    && a.FixedViewportOrigin == b.FixedViewportOrigin,
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
                (ClipPaintNode a, ClipPaintNode b) => ReferenceEquals(a.ClipPath, b.ClipPath),
                (CustomPaintNode a, CustomPaintNode b) => ReferenceEquals(a.PaintAction, b.PaintAction),
                _ => true
            };
        }

        private static bool HaveEqualGlyphs(IReadOnlyList<PositionedGlyph> left, IReadOnlyList<PositionedGlyph> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;

            for (var i = 0; i < left.Count; i++)
            {
                var a = left[i];
                var b = right[i];
                if (a.GlyphId != b.GlyphId || !a.X.Equals(b.X) || !a.Y.Equals(b.Y)) return false;
            }
            return true;
        }

        private static bool HaveEqualFloats(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (!left[i].Equals(right[i])) return false;
            }
            return true;
        }

        private static bool HaveEqualPoints(IReadOnlyList<SKPoint> left, IReadOnlyList<SKPoint> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static bool HaveEqualColors(IReadOnlyList<SKColor> left, IReadOnlyList<SKColor> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static bool HaveEqualStrings(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}
