using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Computes viewport-clamped damage regions from paint-tree deltas, in canvas space.
    /// Every changed, added or removed node contributes its whole paint extent
    /// (<see cref="PaintExtent"/>: descendants, shadows, filter reach) mapped through its
    /// ancestors' transforms and scroll/sticky offsets. Raw node bounds are not enough:
    /// a node under a transform, a scroller or a sticky offset paints somewhere else,
    /// and a retained raster trusting that rectangle kept stale pixels.
    /// Applies a bounded online merge policy so large DOM changes cannot turn damage
    /// normalization into an unbounded quadratic pass.
    /// </summary>
    public sealed class PaintDamageTracker
    {
        private readonly int _maxDamageRegions;
        private readonly int _mergeWorkingSetLimit;
        private readonly float _mergeTolerancePx;

        public PaintDamageTracker(int maxDamageRegions = 32, float mergeTolerancePx = 1.0f)
        {
            _maxDamageRegions = Math.Max(1, maxDamageRegions);
            _mergeWorkingSetLimit = Math.Max(_maxDamageRegions + 1, _maxDamageRegions * 2);
            _mergeTolerancePx = Math.Max(0.0f, mergeTolerancePx);
        }

        /// <summary>
        /// Damage between two trees, clamped to <paramref name="viewport"/>. Empty when
        /// nothing visible changed; the whole viewport when the change cannot be bounded.
        /// </summary>
        public IReadOnlyList<SKRect> ComputeDamageRegions(
            ImmutablePaintTree previousTree,
            ImmutablePaintTree currentTree,
            SKRect viewport)
        {
            if (!IsUsableRect(viewport))
            {
                return Array.Empty<SKRect>();
            }

            if (previousTree == null || currentTree == null)
            {
                return new[] { viewport };
            }

            if (ReferenceEquals(previousTree, currentTree))
            {
                return Array.Empty<SKRect>();
            }

            var regions = new List<SKRect>(Math.Min(_maxDamageRegions, 16));
            var work = new Stack<Level>();
            work.Push(new Level(currentTree.Roots, previousTree.Roots, SKMatrix.Identity));

            while (work.Count > 0)
            {
                var level = work.Pop();
                if (!DiffLevel(level, work, regions, viewport))
                {
                    return new[] { viewport };
                }
            }

            if (regions.Count == 0)
            {
                return Array.Empty<SKRect>();
            }

            if (regions.Count > _maxDamageRegions)
            {
                return new[] { UnionAll(regions) };
            }

            return regions;
        }

        private readonly record struct Level(
            IReadOnlyList<PaintNodeBase> Current,
            IReadOnlyList<PaintNodeBase> Previous,
            SKMatrix ToCanvas);

        /// <summary>
        /// Pairs one level's nodes and records damage; false when some change cannot be bounded.
        /// </summary>
        private bool DiffLevel(Level level, Stack<Level> work, List<SKRect> regions, SKRect viewport)
        {
            var current = level.Current ?? Array.Empty<PaintNodeBase>();
            var previous = level.Previous ?? Array.Empty<PaintNodeBase>();

            // Nodes pair by (type, stable id or source node); nodes with neither pair by
            // type and order among their unkeyed siblings. Several nodes can share a key
            // (an element's text fragments), so each key holds a queue in sibling order.
            var previousByKey = new Dictionary<NodeKey, Queue<PaintNodeBase>>();
            var unkeyedOrdinal = new Dictionary<Type, int>();
            foreach (var node in previous)
            {
                if (node == null) continue;
                var key = KeyOf(node, unkeyedOrdinal);
                if (!previousByKey.TryGetValue(key, out var bucket))
                {
                    bucket = new Queue<PaintNodeBase>();
                    previousByKey.Add(key, bucket);
                }
                bucket.Enqueue(node);
            }

            unkeyedOrdinal.Clear();
            foreach (var node in current)
            {
                if (node == null) continue;
                var key = KeyOf(node, unkeyedOrdinal);
                if (!previousByKey.TryGetValue(key, out var bucket) || bucket.Count == 0)
                {
                    if (!AddNodeDamage(regions, node, level.ToCanvas, viewport)) return false;
                    continue;
                }

                var old = bucket.Dequeue();
                if (bucket.Count == 0) previousByKey.Remove(key);

                // Subtrees are shared between trees wherever nothing changed.
                if (ReferenceEquals(old, node)) continue;

                if (!SameOwnPaint(old, node))
                {
                    if (!AddNodeDamage(regions, old, level.ToCanvas, viewport)) return false;
                    if (!AddNodeDamage(regions, node, level.ToCanvas, viewport)) return false;
                    continue;
                }

                // Same transform and offsets on both sides, so the children share a space.
                work.Push(new Level(node.Children, old.Children, ChildSpace(node, level.ToCanvas)));
            }

            foreach (var bucket in previousByKey.Values)
            {
                while (bucket.Count > 0)
                {
                    if (!AddNodeDamage(regions, bucket.Dequeue(), level.ToCanvas, viewport)) return false;
                }
            }

            return true;
        }

        private static bool SameOwnPaint(PaintNodeBase previous, PaintNodeBase current)
        {
            return previous.GetType() == current.GetType() &&
                   previous.Bounds == current.Bounds &&
                   previous.Transform == current.Transform &&
                   previous.Opacity == current.Opacity &&
                   previous.ClipRect == current.ClipRect &&
                   previous.IsHovered == current.IsHovered &&
                   previous.IsFocused == current.IsFocused &&
                   ImmutablePaintTree.HasEquivalentVisualState(previous, current);
        }

        /// <summary>The space a node's children paint in: its own transform, then its scroll or sticky offset.</summary>
        private static SKMatrix ChildSpace(PaintNodeBase node, SKMatrix toCanvas)
        {
            var space = node.Transform.HasValue ? toCanvas.PreConcat(node.Transform.Value) : toCanvas;
            if (node is ScrollPaintNode scroll && (scroll.ScrollX != 0 || scroll.ScrollY != 0))
            {
                space = space.PreConcat(SKMatrix.CreateTranslation(-scroll.ScrollX, -scroll.ScrollY));
            }
            else if (node is StickyPaintNode sticky && (sticky.StickyOffset.X != 0 || sticky.StickyOffset.Y != 0))
            {
                space = space.PreConcat(SKMatrix.CreateTranslation(sticky.StickyOffset.X, sticky.StickyOffset.Y));
            }

            return space;
        }

        private bool AddNodeDamage(List<SKRect> regions, PaintNodeBase node, SKMatrix toCanvas, SKRect viewport)
        {
            if (!PaintExtent.TryGet(node, out var extent))
            {
                return false;
            }

            if (extent.IsEmpty)
            {
                return true;
            }

            extent.Inflate(PaintExtent.InkMargin, PaintExtent.InkMargin);
            var nodeToCanvas = node.Transform.HasValue ? toCanvas.PreConcat(node.Transform.Value) : toCanvas;
            AddMergedDamageRect(regions, nodeToCanvas.MapRect(extent), viewport);
            return true;
        }

        private static NodeKey KeyOf(PaintNodeBase node, Dictionary<Type, int> unkeyedOrdinal)
        {
            var type = node.GetType();
            if (node.StableNodeId != 0) return new NodeKey(type, node.StableNodeId, null, -1);
            if (node.SourceNode != null) return new NodeKey(type, 0, node.SourceNode, -1);

            unkeyedOrdinal.TryGetValue(type, out var ordinal);
            unkeyedOrdinal[type] = ordinal + 1;
            return new NodeKey(type, 0, null, ordinal);
        }

        private readonly record struct NodeKey(Type Type, ulong StableId, Node Source, int UnkeyedOrdinal)
        {
            public bool Equals(NodeKey other) =>
                ReferenceEquals(Type, other.Type) &&
                StableId == other.StableId &&
                ReferenceEquals(Source, other.Source) &&
                UnkeyedOrdinal == other.UnkeyedOrdinal;

            public override int GetHashCode() => HashCode.Combine(
                Type,
                StableId,
                Source == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Source),
                UnkeyedOrdinal);
        }

        private void AddMergedDamageRect(List<SKRect> regions, SKRect candidate, SKRect viewport)
        {
            var clipped = Intersect(candidate, viewport);
            if (!IsUsableRect(clipped))
            {
                return;
            }

            // Merge transitively. The old one-pass implementation stopped after the
            // first match, leaving regions that overlapped the newly enlarged union.
            var combined = clipped;
            var index = 0;
            while (index < regions.Count)
            {
                if (IntersectsOrNear(regions[index], combined))
                {
                    combined = UnionRects(regions[index], combined);
                    regions.RemoveAt(index);
                    index = 0;
                    continue;
                }

                index++;
            }

            regions.Add(combined);

            // Keep the online merge working set bounded. Once there are substantially
            // more independent regions than the public output policy permits, collapse
            // conservatively rather than spending O(changedNodes * changedNodes) CPU.
            if (regions.Count > _mergeWorkingSetLimit)
            {
                var union = UnionAll(regions);
                regions.Clear();
                regions.Add(union);
            }
        }

        private bool IntersectsOrNear(SKRect a, SKRect b)
        {
            var expanded = new SKRect(
                a.Left - _mergeTolerancePx,
                a.Top - _mergeTolerancePx,
                a.Right + _mergeTolerancePx,
                a.Bottom + _mergeTolerancePx);
            return expanded.IntersectsWith(b);
        }

        private static SKRect Intersect(SKRect a, SKRect b)
        {
            if (!IsFiniteRect(a) || !IsFiniteRect(b))
            {
                return SKRect.Empty;
            }

            var left = Math.Max(a.Left, b.Left);
            var top = Math.Max(a.Top, b.Top);
            var right = Math.Min(a.Right, b.Right);
            var bottom = Math.Min(a.Bottom, b.Bottom);
            if (right <= left || bottom <= top)
            {
                return SKRect.Empty;
            }

            return new SKRect(left, top, right, bottom);
        }

        private static bool IsUsableRect(SKRect rect) =>
            IsFiniteRect(rect) && rect.Width > 0f && rect.Height > 0f;

        private static bool IsFiniteRect(SKRect rect) =>
            float.IsFinite(rect.Left) &&
            float.IsFinite(rect.Top) &&
            float.IsFinite(rect.Right) &&
            float.IsFinite(rect.Bottom);

        private static SKRect UnionRects(SKRect a, SKRect b)
        {
            return new SKRect(
                Math.Min(a.Left, b.Left),
                Math.Min(a.Top, b.Top),
                Math.Max(a.Right, b.Right),
                Math.Max(a.Bottom, b.Bottom));
        }

        private static SKRect UnionAll(IReadOnlyList<SKRect> rects)
        {
            var union = rects[0];
            for (var i = 1; i < rects.Count; i++)
            {
                union = UnionRects(union, rects[i]);
            }

            return union;
        }
    }
}
