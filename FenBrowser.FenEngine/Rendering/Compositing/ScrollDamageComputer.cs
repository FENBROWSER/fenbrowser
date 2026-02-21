using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Computes damage regions that arise from scroll-position changes between frames.
    ///
    /// When the scroll offset changes, portions of the viewport that were previously
    /// covered by content are now exposed (or the opposite) and must be repainted.
    /// This is separate from paint-tree delta damage and must be merged with it.
    ///
    /// Strategy:
    ///   - No scroll change → no damage.
    ///   - Small delta (≤ stripThresholdPx) → two pixel-aligned strips:
    ///       one for the newly-exposed edge, one for the outgoing edge.
    ///   - Large delta or viewport size change → full-viewport damage (cheapest
    ///       correct option when most content has changed).
    /// </summary>
    public sealed class ScrollDamageComputer
    {
        private readonly float _stripThresholdPx;
        private readonly float _scrollEpsilon;
        private readonly float _viewportEpsilon;

        /// <param name="stripThresholdPx">
        /// Delta (px) below which two strips are emitted instead of full viewport.
        /// Default 120 px — roughly two full lines of text.
        /// </param>
        public ScrollDamageComputer(
            float stripThresholdPx = 120f,
            float scrollEpsilon = 0.5f,
            float viewportEpsilon = 0.5f)
        {
            _stripThresholdPx = Math.Max(1f, stripThresholdPx);
            _scrollEpsilon = Math.Max(0f, scrollEpsilon);
            _viewportEpsilon = Math.Max(0f, viewportEpsilon);
        }

        /// <summary>
        /// Compute scroll-induced damage regions.
        /// </summary>
        /// <param name="previousScrollY">Scroll offset of the previous frame.</param>
        /// <param name="currentScrollY">Scroll offset of the current frame.</param>
        /// <param name="previousViewport">Viewport rectangle of the previous frame.</param>
        /// <param name="currentViewport">Viewport rectangle of the current frame (origin is always 0,0).</param>
        /// <returns>
        /// Empty list = no scroll damage.
        /// One element = full viewport damage.
        /// Two elements = exposed-edge strip + outgoing-edge strip.
        /// </returns>
        public IReadOnlyList<SKRect> ComputeScrollDamage(
            float previousScrollY,
            float currentScrollY,
            SKSize previousViewport,
            SKRect currentViewport)
        {
            if (currentViewport.Width <= 0 || currentViewport.Height <= 0)
            {
                return Array.Empty<SKRect>();
            }

            // Viewport size change → full repaint.
            bool viewportChanged =
                Math.Abs(previousViewport.Width - currentViewport.Width) > _viewportEpsilon ||
                Math.Abs(previousViewport.Height - currentViewport.Height) > _viewportEpsilon;

            if (viewportChanged)
            {
                return new[] { currentViewport };
            }

            float delta = currentScrollY - previousScrollY;
            if (Math.Abs(delta) <= _scrollEpsilon)
            {
                // No meaningful scroll change.
                return Array.Empty<SKRect>();
            }

            float absDelta = Math.Abs(delta);
            if (absDelta >= _stripThresholdPx)
            {
                // Large scroll jump — full viewport.
                return new[] { currentViewport };
            }

            // Small delta: two strips clamped to the viewport.
            // scrolledDown (delta > 0): expose bottom, vacate top.
            // scrolledUp   (delta < 0): expose top, vacate bottom.
            bool scrolledDown = delta > 0;

            SKRect exposedStrip, outgoingStrip;

            if (scrolledDown)
            {
                // Newly exposed area at the bottom.
                exposedStrip = new SKRect(
                    currentViewport.Left,
                    currentViewport.Bottom - absDelta,
                    currentViewport.Right,
                    currentViewport.Bottom);

                // Area that scrolled off the top.
                outgoingStrip = new SKRect(
                    currentViewport.Left,
                    currentViewport.Top,
                    currentViewport.Right,
                    currentViewport.Top + absDelta);
            }
            else
            {
                // Newly exposed area at the top.
                exposedStrip = new SKRect(
                    currentViewport.Left,
                    currentViewport.Top,
                    currentViewport.Right,
                    currentViewport.Top + absDelta);

                // Area that scrolled off the bottom.
                outgoingStrip = new SKRect(
                    currentViewport.Left,
                    currentViewport.Bottom - absDelta,
                    currentViewport.Right,
                    currentViewport.Bottom);
            }

            // Clamp both strips to viewport bounds.
            exposedStrip = ClampToViewport(exposedStrip, currentViewport);
            outgoingStrip = ClampToViewport(outgoingStrip, currentViewport);

            var result = new List<SKRect>(2);
            if (IsNonEmpty(exposedStrip)) result.Add(exposedStrip);
            if (IsNonEmpty(outgoingStrip)) result.Add(outgoingStrip);

            // If both strips collapsed, fall back to full viewport.
            if (result.Count == 0)
            {
                return new[] { currentViewport };
            }

            return result;
        }

        private static SKRect ClampToViewport(SKRect rect, SKRect viewport)
        {
            var left = Math.Max(rect.Left, viewport.Left);
            var top = Math.Max(rect.Top, viewport.Top);
            var right = Math.Min(rect.Right, viewport.Right);
            var bottom = Math.Min(rect.Bottom, viewport.Bottom);

            if (right <= left || bottom <= top)
            {
                return SKRect.Empty;
            }

            return new SKRect(left, top, right, bottom);
        }

        private static bool IsNonEmpty(SKRect rect)
        {
            return rect.Width > 0 && rect.Height > 0;
        }
    }
}
