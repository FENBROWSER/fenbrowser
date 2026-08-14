using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Computes viewport-clamped damage regions from paint-tree deltas.
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

            var diff = currentTree.Diff(previousTree);
            if (!diff.HasChanges)
            {
                return Array.Empty<SKRect>();
            }

            var regions = new List<SKRect>(Math.Min(_maxDamageRegions, 16));

            foreach (var node in diff.AddedNodes)
            {
                AddSubtreeDamage(regions, node, viewport);
            }

            foreach (var node in diff.RemovedNodes)
            {
                AddSubtreeDamage(regions, node, viewport);
            }

            foreach (var change in diff.ModifiedNodes)
            {
                // A parent transform/filter/opacity/style change can alter descendants
                // that paint outside the parent's nominal box. Damage both old and new
                // subtrees instead of assuming the root bounds contain all visual ink.
                AddSubtreeDamage(regions, change.OldNode, viewport);
                AddSubtreeDamage(regions, change.NewNode, viewport);
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

        private void AddSubtreeDamage(List<SKRect> regions, PaintNodeBase root, SKRect viewport)
        {
            if (root == null) return;

            var stack = new Stack<PaintNodeBase>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                AddMergedDamageRect(regions, node.Bounds, viewport);

                var children = node.Children;
                if (children == null) continue;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i] != null) stack.Push(children[i]);
                }
            }
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
