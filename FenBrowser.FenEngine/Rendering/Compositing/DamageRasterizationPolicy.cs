using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Decides when damage-region rasterization is safe and worthwhile.
    /// </summary>
    public sealed class DamageRasterizationPolicy
    {
        private readonly float _maxAreaRatio;
        private readonly int _maxDamageRegions;

        public DamageRasterizationPolicy(float maxAreaRatio = 0.55f, int maxDamageRegions = 8)
        {
            _maxAreaRatio = Math.Clamp(maxAreaRatio, 0.01f, 1f);
            _maxDamageRegions = Math.Max(1, maxDamageRegions);
        }

        public bool ShouldUseDamageRasterization(
            bool hasBaseFrame,
            IReadOnlyList<SKRect> damageRegions,
            SKRect viewport,
            out float damageAreaRatio)
        {
            damageAreaRatio = 0f;
            if (!hasBaseFrame)
            {
                return false;
            }

            if (damageRegions == null || damageRegions.Count == 0 || damageRegions.Count > _maxDamageRegions)
            {
                return false;
            }

            var viewportArea = Math.Max(1f, viewport.Width * viewport.Height);
            var damageArea = 0f;

            for (var i = 0; i < damageRegions.Count; i++)
            {
                if (!TryIntersect(damageRegions[i], viewport, out var clipped))
                {
                    continue;
                }

                damageArea += clipped.Width * clipped.Height;
                if (damageArea >= viewportArea)
                {
                    damageArea = viewportArea;
                    break;
                }
            }

            if (damageArea <= 0f)
            {
                return false;
            }

            damageAreaRatio = Math.Clamp(damageArea / viewportArea, 0f, 1f);
            return damageAreaRatio < _maxAreaRatio;
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
