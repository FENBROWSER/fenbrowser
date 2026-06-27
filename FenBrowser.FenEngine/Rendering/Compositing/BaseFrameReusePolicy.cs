using System;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Guards reuse of previous recorded frame as damage-raster base.
    /// </summary>
    public static class BaseFrameReusePolicy
    {
        public static bool CanReuseBaseFrame(
            bool hasBaseFrame,
            SKSize previousViewport,
            SKSize currentViewport,
            float previousScrollY,
            float currentScrollY,
            RenderFrameInvalidationReason invalidationReasons = RenderFrameInvalidationReason.None,
            int consecutiveReuseCount = 0,
            int maxConsecutiveReuseCount = 120,
            double baseFrameAgeMs = 0,
            double maxBaseFrameAgeMs = 2000,
            float viewportEpsilon = 0.5f,
            float scrollEpsilon = 0.5f)
        {
            if (!hasBaseFrame)
            {
                return false;
            }

            if ((invalidationReasons & RenderFrameInvalidationReason.Navigation) != 0)
            {
                return false;
            }

            if (!IsFinitePositive(previousViewport.Width) ||
                !IsFinitePositive(previousViewport.Height) ||
                !IsFinitePositive(currentViewport.Width) ||
                !IsFinitePositive(currentViewport.Height))
            {
                return false;
            }

            if (!float.IsFinite(previousScrollY) || !float.IsFinite(currentScrollY))
            {
                return false;
            }

            if (consecutiveReuseCount < 0 || maxConsecutiveReuseCount < 1)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (!double.IsFinite(baseFrameAgeMs) || baseFrameAgeMs < 0)
            {
                return false;
            }

            if (!double.IsFinite(maxBaseFrameAgeMs) || maxBaseFrameAgeMs < 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (viewportEpsilon < 0 || scrollEpsilon < 0 || !float.IsFinite(viewportEpsilon) || !float.IsFinite(scrollEpsilon))
            {
                throw new ArgumentOutOfRangeException();
            }

            if (consecutiveReuseCount >= maxConsecutiveReuseCount)
            {
                return false;
            }

            if (baseFrameAgeMs > maxBaseFrameAgeMs)
            {
                return false;
            }

            if (Math.Abs(previousViewport.Width - currentViewport.Width) > viewportEpsilon ||
                Math.Abs(previousViewport.Height - currentViewport.Height) > viewportEpsilon)
            {
                return false;
            }

            // Allow scroll-only frames to reuse the base frame even when scrollY
            // changed — that's the whole point of damage rasterisation: translate
            // the base image and only re-render the exposed band.
            bool isScrollOnly = (invalidationReasons & ~RenderFrameInvalidationReason.Scroll) == 0;
            if (!isScrollOnly &&
                Math.Abs(previousScrollY - currentScrollY) > scrollEpsilon)
            {
                return false;
            }

            // But don't allow unlimited reuse under scroll — force a fresh frame
            // periodically so newly exposed content actually gets rendered instead
            // of showing as white.
            if (isScrollOnly && consecutiveReuseCount >= 3)
            {
                return false;
            }

            return true;
        }

        private static bool IsFinitePositive(float value)
        {
            return float.IsFinite(value) && value > 0f;
        }
    }
}
