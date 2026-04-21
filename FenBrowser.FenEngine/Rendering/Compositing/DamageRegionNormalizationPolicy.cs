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
    /// </summary>
    public sealed class DamageRegionNormalizationPolicy
    {
        private readonly float _inflatePx;
        private readonly float _mergeGapPx;
        private readonly int _maxNormalizedRegions;

        public DamageRegionNormalizationPolicy(
            float inflatePx = 1f,
            float mergeGapPx = 1f,
            int maxNormalizedRegions = 16)
        {
            _inflatePx = Math.Max(0f, inflatePx);
            _mergeGapPx = Math.Max(0f, mergeGapPx);
            _maxNormalizedRegions = Math.Max(1, maxNormalizedRegions);
        }

        public IReadOnlyList<SKRect> Normalize(IReadOnlyList<SKRect> damageRegions, SKRect viewport)
        {
            if (damageRegions == null || damageRegions.Count == 0 || viewport.Width <= 0 || viewport.Height <= 0)
            {
                return Array.Empty<SKRect>();
            }

            var normalized = new List<SKRect>(damageRegions.Count);
            for (var i = 0; i < damageRegions.Count; i++)
            {
                if (!TryIntersect(damageRegions[i], viewport, out var clipped))
                {
                    continue;
                }

                var inflated = Inflate(clipped, _inflatePx);
                if (!TryIntersect(inflated, viewport, out var viewportClamped))
                {
                    continue;
                }

                normalized.Add(SnapOutward(viewportClamped));
            }

            if (normalized.Count == 0)
            {
                return Array.Empty<SKRect>();
            }

            MergeOverlappingRegions(normalized, _mergeGapPx);
            SortRegions(normalized);

            if (normalized.Count > _maxNormalizedRegions)
            {
                var union = normalized[0];
                for (var i = 1; i < normalized.Count; i++)
                {
                    union = Union(union, normalized[i]);
                }

                return new[] { ClampToViewport(SnapOutward(union), viewport) };
            }

            return normalized;
        }

        private static void SortRegions(List<SKRect> regions)
        {
            regions.Sort(static (left, right) =>
            {
                var top = left.Top.CompareTo(right.Top);
                if (top != 0)
                {
                    return top;
                }

                var leftEdge = left.Left.CompareTo(right.Left);
                if (leftEdge != 0)
                {
                    return leftEdge;
                }

                var width = left.Width.CompareTo(right.Width);
                if (width != 0)
                {
                    return width;
                }

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

        private static SKRect ClampToViewport(SKRect rect, SKRect clampBounds)
        {
            if (!TryIntersect(rect, clampBounds, out var clamped))
            {
                return SKRect.Empty;
            }

            return clamped;
        }

        private static void MergeOverlappingRegions(List<SKRect> regions, float mergeGapPx)
        {
            var i = 0;
            while (i < regions.Count)
            {
                var mergedAny = false;
                var j = i + 1;
                while (j < regions.Count)
                {
                    if (ShouldMerge(regions[i], regions[j], mergeGapPx))
                    {
                        regions[i] = Union(regions[i], regions[j]);
                        regions.RemoveAt(j);
                        mergedAny = true;
                        continue;
                    }

                    j++;
                }

                if (!mergedAny)
                {
                    i++;
                }
            }
        }

        private static bool ShouldMerge(SKRect a, SKRect b, float mergeGapPx)
        {
            if (TryIntersect(a, b, out _))
            {
                return true;
            }

            var expandedA = new SKRect(a.Left - mergeGapPx, a.Top - mergeGapPx, a.Right + mergeGapPx, a.Bottom + mergeGapPx);
            return TryIntersect(expandedA, b, out _);
        }

        private static bool TryIntersect(SKRect a, SKRect b, out SKRect intersection)
        {
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
    }
}
