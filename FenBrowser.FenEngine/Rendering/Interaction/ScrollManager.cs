using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Interaction
{
    /// <summary>
    /// Manages scroll state and behavior for scrollable containers.
    /// Handles overflow scrolling, smooth scroll, and scroll snapping.
    /// </summary>
    public class ScrollManager
    {
        private readonly Dictionary<LiteElement, ScrollState> _scrollStates;
        private readonly object _lock = new();

        public ScrollManager()
        {
            _scrollStates = new Dictionary<LiteElement, ScrollState>();
        }

        #region Scroll State Management

        /// <summary>
        /// Get or create scroll state for an element.
        /// </summary>
        public ScrollState GetScrollState(LiteElement element)
        {
            lock (_lock)
            {
                if (!_scrollStates.TryGetValue(element, out var state))
                {
                    state = new ScrollState();
                    _scrollStates[element] = state;
                }
                return state;
            }
        }

        /// <summary>
        /// Update scroll position for an element.
        /// </summary>
        public void SetScrollPosition(LiteElement element, float scrollX, float scrollY)
        {
            var state = GetScrollState(element);
            state.ScrollX = Math.Max(0, Math.Min(scrollX, state.MaxScrollX));
            state.ScrollY = Math.Max(0, Math.Min(scrollY, state.MaxScrollY));

            FenLogger.Debug($"[ScrollManager] {element.Tag} scroll: ({state.ScrollX}, {state.ScrollY})", LogCategory.Rendering);
        }

        /// <summary>
        /// Update scroll bounds based on content size.
        /// </summary>
        public void SetScrollBounds(LiteElement element, float contentWidth, float contentHeight, float viewportWidth, float viewportHeight)
        {
            var state = GetScrollState(element);
            state.ContentWidth = contentWidth;
            state.ContentHeight = contentHeight;
            state.ViewportWidth = viewportWidth;
            state.ViewportHeight = viewportHeight;
            state.MaxScrollX = Math.Max(0, contentWidth - viewportWidth);
            state.MaxScrollY = Math.Max(0, contentHeight - viewportHeight);

            // Clamp current scroll to new bounds
            state.ScrollX = Math.Min(state.ScrollX, state.MaxScrollX);
            state.ScrollY = Math.Min(state.ScrollY, state.MaxScrollY);
        }

        /// <summary>
        /// Apply scroll delta (e.g., from mouse wheel).
        /// </summary>
        public void Scroll(LiteElement element, float deltaX, float deltaY)
        {
            var state = GetScrollState(element);
            SetScrollPosition(element, state.ScrollX + deltaX, state.ScrollY + deltaY);
        }

        /// <summary>
        /// Clear scroll state for an element.
        /// </summary>
        public void ClearScrollState(LiteElement element)
        {
            lock (_lock)
            {
                _scrollStates.Remove(element);
            }
        }

        /// <summary>
        /// Clear all scroll states.
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                _scrollStates.Clear();
            }
        }

        #endregion

        #region Scroll Queries

        /// <summary>
        /// Check if element is scrollable.
        /// </summary>
        public bool IsScrollable(LiteElement element, CssComputed style)
        {
            if (style == null) return false;

            var overflowX = style.OverflowX?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";
            var overflowY = style.OverflowY?.ToLowerInvariant() ?? style.Overflow?.ToLowerInvariant() ?? "visible";

            return overflowX == "scroll" || overflowX == "auto" ||
                   overflowY == "scroll" || overflowY == "auto";
        }

        /// <summary>
        /// Check if element has vertical scrollbar.
        /// </summary>
        public bool HasVerticalScrollbar(LiteElement element)
        {
            var state = GetScrollState(element);
            return state.ContentHeight > state.ViewportHeight;
        }

        /// <summary>
        /// Check if element has horizontal scrollbar.
        /// </summary>
        public bool HasHorizontalScrollbar(LiteElement element)
        {
            var state = GetScrollState(element);
            return state.ContentWidth > state.ViewportWidth;
        }

        /// <summary>
        /// Get scroll offset for rendering.
        /// </summary>
        public (float x, float y) GetScrollOffset(LiteElement element)
        {
            var state = GetScrollState(element);
            return (state.ScrollX, state.ScrollY);
        }

        #endregion

        #region Smooth Scrolling

        /// <summary>
        /// Start smooth scroll animation to target position.
        /// </summary>
        public void SmoothScrollTo(LiteElement element, float targetX, float targetY, int durationMs = 300)
        {
            var state = GetScrollState(element);
            state.SmoothScrollTarget = (targetX, targetY);
            state.SmoothScrollStartTime = DateTime.UtcNow;
            state.SmoothScrollDurationMs = durationMs;
            state.SmoothScrollStart = (state.ScrollX, state.ScrollY);
        }

        /// <summary>
        /// Update smooth scroll animation. Call each frame.
        /// </summary>
        public bool UpdateSmoothScroll(LiteElement element)
        {
            var state = GetScrollState(element);
            if (!state.SmoothScrollStartTime.HasValue) return false;

            var elapsed = (DateTime.UtcNow - state.SmoothScrollStartTime.Value).TotalMilliseconds;
            var progress = Math.Min(1.0, elapsed / state.SmoothScrollDurationMs);

            // Ease out cubic
            var eased = 1 - Math.Pow(1 - progress, 3);

            var (startX, startY) = state.SmoothScrollStart;
            var (targetX, targetY) = state.SmoothScrollTarget;

            state.ScrollX = (float)(startX + (targetX - startX) * eased);
            state.ScrollY = (float)(startY + (targetY - startY) * eased);

            if (progress >= 1.0)
            {
                state.SmoothScrollStartTime = null;
                return false; // Animation complete
            }

            return true; // Animation in progress
        }

        #endregion

        #region Scroll Snapping

        /// <summary>
        /// Apply scroll snap if configured.
        /// </summary>
        public void ApplyScrollSnap(LiteElement element, CssComputed style, List<float> snapPoints)
        {
            if (style?.ScrollSnapType == null || snapPoints == null || snapPoints.Count == 0)
                return;

            var state = GetScrollState(element);
            var snapType = style.ScrollSnapType.ToLowerInvariant();

            if (snapType.Contains("y") || snapType.Contains("both") || snapType.Contains("block"))
            {
                // Find nearest snap point
                float nearestY = snapPoints[0];
                float minDist = Math.Abs(state.ScrollY - nearestY);

                foreach (var point in snapPoints)
                {
                    var dist = Math.Abs(state.ScrollY - point);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestY = point;
                    }
                }

                if (snapType.Contains("mandatory") || minDist < 50)
                {
                    SmoothScrollTo(element, state.ScrollX, nearestY);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Scroll state for a scrollable element.
    /// </summary>
    public class ScrollState
    {
        public float ScrollX { get; set; }
        public float ScrollY { get; set; }
        public float MaxScrollX { get; set; }
        public float MaxScrollY { get; set; }
        public float ContentWidth { get; set; }
        public float ContentHeight { get; set; }
        public float ViewportWidth { get; set; }
        public float ViewportHeight { get; set; }

        // Smooth scroll animation
        public DateTime? SmoothScrollStartTime { get; set; }
        public int SmoothScrollDurationMs { get; set; }
        public (float x, float y) SmoothScrollStart { get; set; }
        public (float x, float y) SmoothScrollTarget { get; set; }

        /// <summary>
        /// Check if currently animating.
        /// </summary>
        public bool IsAnimating => SmoothScrollStartTime.HasValue;
    }
}
