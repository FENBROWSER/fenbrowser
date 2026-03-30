using System;
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
            float viewportEpsilon = 0.5f,
            float scrollEpsilon = 0.5f)
        {
            if (!hasBaseFrame)
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

            if (viewportEpsilon < 0 || scrollEpsilon < 0 || !float.IsFinite(viewportEpsilon) || !float.IsFinite(scrollEpsilon))
            {
                throw new ArgumentOutOfRangeException();
            }

            if (Math.Abs(previousViewport.Width - currentViewport.Width) > viewportEpsilon ||
                Math.Abs(previousViewport.Height - currentViewport.Height) > viewportEpsilon)
            {
                return false;
            }

            if (Math.Abs(previousScrollY - currentScrollY) > scrollEpsilon)
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
