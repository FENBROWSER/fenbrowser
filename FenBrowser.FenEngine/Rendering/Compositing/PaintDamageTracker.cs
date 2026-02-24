using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Computes viewport-clamped damage regions from paint-tree deltas.
    /// Applies bounded region policy to keep compositing contracts stable.
    /// </summary>
    public sealed class PaintDamageTracker
    {
        private readonly int _maxDamageRegions;
        private readonly float _mergeTolerancePx;

        public PaintDamageTracker(int maxDamageRegions = 32, float mergeTolerancePx = 1.0f)
        {
            _maxDamageRegions = Math.Max(1, maxDamageRegions);
            _mergeTolerancePx = Math.Max(0.0f, mergeTolerancePx);
        }

        public IReadOnlyList<SKRect> ComputeDamageRegions(ImmutablePaintTree previousTree, ImmutablePaintTree currentTree, SKRect viewport)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0)
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

            var raw = new List<SKRect>();

            foreach (var node in diff.AddedNodes)
            {
                AddDamageRect(raw, node.Bounds, viewport);
            }

            foreach (var node in diff.RemovedNodes)
            {
                AddDamageRect(raw, node.Bounds, viewport);
            }

            foreach (var change in diff.ModifiedNodes)
            {
                var union = UnionRects(change.OldNode.Bounds, change.NewNode.Bounds);
                AddDamageRect(raw, union, viewport);
            }

            if (raw.Count == 0)
            {
                return Array.Empty<SKRect>();
            }

            var merged = MergeRects(raw);
            if (merged.Count > _maxDamageRegions)
            {
                return new[] { UnionAll(merged) };
            }

            return merged;
        }

        private static void AddDamageRect(List<SKRect> regions, SKRect candidate, SKRect viewport)
        {
            var clipped = Intersect(candidate, viewport);
            if (clipped.Width > 0 && clipped.Height > 0)
            {
                regions.Add(clipped);
            }
        }

        private List<SKRect> MergeRects(List<SKRect> input)
        {
            var merged = new List<SKRect>();
            foreach (var rect in input)
            {
                bool mergedIntoExisting = false;
                for (int i = 0; i < merged.Count; i++)
                {
                    if (IntersectsOrNear(merged[i], rect))
                    {
                        merged[i] = UnionRects(merged[i], rect);
                        mergedIntoExisting = true;
                        break;
                    }
                }

                if (!mergedIntoExisting)
                {
                    merged.Add(rect);
                }
            }

            return merged;
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

        private static SKRect UnionRects(SKRect a, SKRect b)
        {
            return new SKRect(
                Math.Min(a.Left, b.Left),
                Math.Min(a.Top, b.Top),
                Math.Max(a.Right, b.Right),
                Math.Max(a.Bottom, b.Bottom));
        }

        private static SKRect UnionAll(List<SKRect> rects)
        {
            var union = rects[0];
            for (int i = 1; i < rects.Count; i++)
            {
                union = UnionRects(union, rects[i]);
            }

            return union;
        }
    }
}
