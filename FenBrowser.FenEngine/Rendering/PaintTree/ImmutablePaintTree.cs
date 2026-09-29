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

        /// <summary>
        /// Some node's paint was computed from the viewport scroll offset (position:fixed
        /// counter-translation, sticky offsets, background-attachment:fixed, top-layer
        /// backdrops). The tree is only valid at the offset it was built for.
        /// </summary>
        public bool ReadsViewportScroll { get; init; }

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

        /// <summary>
        /// Splices a freshly built paint subtree for <paramref name="sourceNode"/> in place
        /// of everything the previous build produced for that DOM subtree, or returns null
        /// when that cannot be done exactly and the caller must rebuild the whole tree.
        /// A DOM subtree rarely paints as one node: an element emits sibling nodes for its
        /// background, border and content (a video's frame), and positioned descendants
        /// are hoisted into an ancestor's stacking context. The splice is exact only when
        /// every node the subtree owns sits in one contiguous run under a single parent;
        /// replacing just the first match left the rest behind, and the tree grew by a
        /// copy of the subtree on every paint-only frame.
        /// </summary>
        public ImmutablePaintTree WithReplacedSubtree(
            Node sourceNode,
            IReadOnlyList<PaintNodeBase> newSubtreeNodes,
            bool subtreeReadsViewportScroll = false)
        {
            if (sourceNode == null || newSubtreeNodes == null || Roots.Count == 0)
                return null;

            var parentLinks = new Dictionary<PaintNodeBase, ParentLink>();
            var ownership = new Dictionary<Node, bool>();

            // A new top-level node the next splice could not attribute to this subtree
            // (no source, or a source outside it) would never be replaced again.
            for (var i = 0; i < newSubtreeNodes.Count; i++)
            {
                if (newSubtreeNodes[i] == null || !IsOwnedBy(newSubtreeNodes[i].SourceNode, sourceNode, ownership))
                    return null;
            }

            var search = new Stack<PaintNodeBase>();
            PaintNodeBase ownedParent = null;
            int firstOwned = int.MaxValue;
            int lastOwned = -1;
            int ownedCount = 0;
            bool foundSourceNode = false;

            for (var i = Roots.Count - 1; i >= 0; i--)
            {
                var root = Roots[i];
                if (root == null) continue;
                parentLinks[root] = new ParentLink(null, i);
                search.Push(root);
            }

            while (search.Count > 0)
            {
                var node = search.Pop();
                var link = parentLinks[node];
                if (IsOwnedBy(node.SourceNode, sourceNode, ownership))
                {
                    // Everything below an owned node is replaced with it.
                    if (ownedCount > 0 && !ReferenceEquals(ownedParent, link.Parent))
                        return null;

                    ownedParent = link.Parent;
                    firstOwned = Math.Min(firstOwned, link.Index);
                    lastOwned = Math.Max(lastOwned, link.Index);
                    ownedCount++;
                    foundSourceNode |= ReferenceEquals(node.SourceNode, sourceNode);
                    continue;
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

            if (!foundSourceNode || lastOwned - firstOwned + 1 != ownedCount)
                return null;

            IReadOnlyList<PaintNodeBase> replacement = newSubtreeNodes;
            int replaceIndex = firstOwned;
            int replaceCount = ownedCount;
            var current = ownedParent;

            while (current != null)
            {
                var newChildren = ReplaceRange(current.Children, replaceIndex, replaceCount, replacement);
                replacement = new[] { current.CloneWithChildren(newChildren) };
                var link = parentLinks[current];
                replaceIndex = link.Index;
                replaceCount = 1;
                current = link.Parent;
            }

            var newRoots = ReplaceRange(Roots, replaceIndex, replaceCount, replacement);
            return new ImmutablePaintTree(newRoots, FrameId, CountNodes(newRoots))
            {
                ReadsViewportScroll = ReadsViewportScroll || subtreeReadsViewportScroll
            };
        }

        /// <summary>Whether <paramref name="node"/> is <paramref name="subtreeRoot"/> or inside it, across shadow and pseudo-element boundaries.</summary>
        private static bool IsOwnedBy(Node node, Node subtreeRoot, Dictionary<Node, bool> memo)
        {
            if (node == null) return false;
            if (memo.TryGetValue(node, out var known)) return known;

            bool owned = false;
            for (var current = node; current != null; current = OwnerOf(current))
            {
                if (ReferenceEquals(current, subtreeRoot))
                {
                    owned = true;
                    break;
                }

                if (!ReferenceEquals(current, node) && memo.TryGetValue(current, out var ancestorOwned))
                {
                    owned = ancestorOwned;
                    break;
                }
            }

            memo[node] = owned;
            return owned;
        }

        private static Node OwnerOf(Node node) => node switch
        {
            ShadowRoot shadowRoot => shadowRoot.Host,
            PseudoElement { ParentNode: null } pseudo => pseudo.OriginatingElement,
            _ => node.ParentNode
        };

        private static IReadOnlyList<PaintNodeBase> ReplaceRange(
            IReadOnlyList<PaintNodeBase> nodes,
            int index,
            int count,
            IReadOnlyList<PaintNodeBase> replacement)
        {
            var replacementCount = replacement?.Count ?? 0;
            var result = new List<PaintNodeBase>(Math.Max(0, nodes.Count - count + replacementCount));
            for (var i = 0; i < index; i++) result.Add(nodes[i]);
            if (replacement != null)
            {
                for (var i = 0; i < replacement.Count; i++) result.Add(replacement[i]);
            }
            for (var i = index + count; i < nodes.Count; i++) result.Add(nodes[i]);
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
                (VideoPaintNode a, VideoPaintNode b) => ReferenceEquals(a.Presenter, b.Presenter)
                    && a.Sequence == b.Sequence
                    && string.Equals(a.ObjectFit, b.ObjectFit, StringComparison.Ordinal)
                    && string.Equals(a.ObjectPosition, b.ObjectPosition, StringComparison.Ordinal),
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
