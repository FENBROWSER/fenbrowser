using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Normalizes damage regions for safer partial raster:
    /// - viewport clamp
    /// - outward pixel snapping
    /// - edge inflation to avoid seam artifacts
    /// - overlap/nearby merge
    /// - bounded normalization work under invalidation storms
    /// </summary>
    public sealed class DamageRegionNormalizationPolicy
    {
        private readonly float _inflatePx;
        private readonly float _mergeGapPx;
        private readonly int _maxNormalizedRegions;
        private readonly int _workingSetLimit;

        public DamageRegionNormalizationPolicy(
            float inflatePx = 1f,
            float mergeGapPx = 1f,
            int maxNormalizedRegions = 16)
        {
            _inflatePx = Math.Max(0f, inflatePx);
            _mergeGapPx = Math.Max(0f, mergeGapPx);
            _maxNormalizedRegions = Math.Max(1, maxNormalizedRegions);
            _workingSetLimit = Math.Max(_maxNormalizedRegions + 1, _maxNormalizedRegions * 2);
        }

        public IReadOnlyList<SKRect> Normalize(IReadOnlyList<SKRect> damageRegions, SKRect viewport)
        {
            if (damageRegions == null || damageRegions.Count == 0 || !IsUsableRect(viewport))
            {
                return Array.Empty<SKRect>();
            }

            var normalized = new List<SKRect>(Math.Min(damageRegions.Count, _workingSetLimit));
            for (var i = 0; i < damageRegions.Count; i++)
            {
                var candidate = damageRegions[i];
                if (!IsFiniteRect(candidate) || !TryIntersect(candidate, viewport, out var clipped))
                {
                    continue;
                }

                var inflated = Inflate(clipped, _inflatePx);
                if (!TryIntersect(inflated, viewport, out var viewportClamped))
                {
                    continue;
                }

                var snapped = ClampToViewport(SnapOutward(viewportClamped), viewport);
                if (!IsUsableRect(snapped))
                {
                    continue;
                }

                AddMergedRegion(normalized, snapped);

                // Once the working set grows materially beyond what the caller is
                // allowed to consume, collapse conservatively. This turns adversarial
                // thousands-of-tiny-invalidations input into bounded work instead of
                // an O(N^2) merge pass followed by the same full-union result anyway.
                if (normalized.Count > _workingSetLimit)
                {
                    var union = UnionAll(normalized);
                    normalized.Clear();
                    normalized.Add(ClampToViewport(SnapOutward(union), viewport));
                }
            }

            if (normalized.Count == 0)
            {
                return Array.Empty<SKRect>();
            }

            SortRegions(normalized);

            if (normalized.Count > _maxNormalizedRegions)
            {
                return new[] { ClampToViewport(SnapOutward(UnionAll(normalized)), viewport) };
            }

            return normalized;
        }

        private void AddMergedRegion(List<SKRect> regions, SKRect candidate)
        {
            var combined = candidate;
            var index = 0;
            while (index < regions.Count)
            {
                if (ShouldMerge(regions[index], combined, _mergeGapPx))
                {
                    combined = Union(regions[index], combined);
                    regions.RemoveAt(index);
                    // The enlarged union can now touch a region checked earlier.
                    // Restart to make merging transitively complete.
                    index = 0;
                    continue;
                }

                index++;
            }

            regions.Add(combined);
        }

        private static void SortRegions(List<SKRect> regions)
        {
            regions.Sort(static (left, right) =>
            {
                var top = left.Top.CompareTo(right.Top);
                if (top != 0) return top;

                var leftEdge = left.Left.CompareTo(right.Left);
                if (leftEdge != 0) return leftEdge;

                var width = left.Width.CompareTo(right.Width);
                if (width != 0) return width;

                return left.Height.CompareTo(right.Height);
            });
        }

        private static SKRect Inflate(SKRect rect, float px)
        {
            return new SKRect(rect.Left - px, rect.Top - px, rect.Right + px, rect.Bottom + px);
        }

        private static SKRect SnapOutward(SKRect rect)
        {
            return new SKRect(
                (float)Math.Floor(rect.Left),
                (float)Math.Floor(rect.Top),
                (float)Math.Ceiling(rect.Right),
                (float)Math.Ceiling(rect.Bottom));
        }

        private static SKRect Union(SKRect a, SKRect b)
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
                union = Union(union, rects[i]);
            }
            return union;
        }

        private static SKRect ClampToViewport(SKRect rect, SKRect clampBounds)
        {
            return TryIntersect(rect, clampBounds, out var clamped)
                ? clamped
                : SKRect.Empty;
        }

        private static bool ShouldMerge(SKRect a, SKRect b, float mergeGapPx)
        {
            if (TryIntersect(a, b, out _))
            {
                return true;
            }

            var expandedA = new SKRect(
                a.Left - mergeGapPx,
                a.Top - mergeGapPx,
                a.Right + mergeGapPx,
                a.Bottom + mergeGapPx);
            return TryIntersect(expandedA, b, out _);
        }

        private static bool TryIntersect(SKRect a, SKRect b, out SKRect intersection)
        {
            if (!IsFiniteRect(a) || !IsFiniteRect(b))
            {
                intersection = SKRect.Empty;
                return false;
            }

            var left = Math.Max(a.Left, b.Left);
            var top = Math.Max(a.Top, b.Top);
            var right = Math.Min(a.Right, b.Right);
            var bottom = Math.Min(a.Bottom, b.Bottom);

            if (right <= left || bottom <= top)
            {
                intersection = SKRect.Empty;
                return false;
            }

            intersection = new SKRect(left, top, right, bottom);
            return true;
        }

        private static bool IsUsableRect(SKRect rect) =>
            IsFiniteRect(rect) && rect.Width > 0f && rect.Height > 0f;

        private static bool IsFiniteRect(SKRect rect) =>
            float.IsFinite(rect.Left) &&
            float.IsFinite(rect.Top) &&
            float.IsFinite(rect.Right) &&
            float.IsFinite(rect.Bottom);
    }
}
