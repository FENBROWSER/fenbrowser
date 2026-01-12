using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
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
        private readonly Dictionary<Element, ScrollState> _scrollStates;
        private readonly object _lock = new();

        public ScrollManager()
        {
            _scrollStates = new Dictionary<Element, ScrollState>();
        }

        #region Scroll State Management

        /// <summary>
        /// Get or create scroll state for an element.
        /// </summary>
        public ScrollState GetScrollState(Element element)
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
        public void SetScrollPosition(Element element, float scrollX, float scrollY)
        {
            var state = GetScrollState(element);
            state.ScrollX = Math.Max(0, Math.Min(scrollX, state.MaxScrollX));
            state.ScrollY = Math.Max(0, Math.Min(scrollY, state.MaxScrollY));

            FenLogger.Debug($"[ScrollManager] {element.Tag} scroll: ({state.ScrollX}, {state.ScrollY})", LogCategory.Rendering);
        }

        /// <summary>
        /// Update scroll bounds based on content size.
        /// </summary>
        public void SetScrollBounds(Element element, float contentWidth, float contentHeight, float viewportWidth, float viewportHeight)
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
        public void Scroll(Element element, float deltaX, float deltaY)
        {
            var state = GetScrollState(element);
            SetScrollPosition(element, state.ScrollX + deltaX, state.ScrollY + deltaY);
        }

        /// <summary>
        /// Clear scroll state for an element.
        /// </summary>
        public void ClearScrollState(Element element)
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
        public bool IsScrollable(Element element, CssComputed style)
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
        public bool HasVerticalScrollbar(Element element)
        {
            var state = GetScrollState(element);
            return state.ContentHeight > state.ViewportHeight;
        }

        /// <summary>
        /// Check if element has horizontal scrollbar.
        /// </summary>
        public bool HasHorizontalScrollbar(Element element)
        {
            var state = GetScrollState(element);
            return state.ContentWidth > state.ViewportWidth;
        }

        /// <summary>
        /// Get scroll offset for rendering.
        /// </summary>
        public (float x, float y) GetScrollOffset(Element element)
        {
            var state = GetScrollState(element);
            return (state.ScrollX, state.ScrollY);
        }

        #endregion

        #region Smooth Scrolling

        /// <summary>
        /// Start smooth scroll animation to target position.
        /// </summary>
        public void SmoothScrollTo(Element element, float targetX, float targetY, int durationMs = 300)
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
        public bool UpdateSmoothScroll(Element element)
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

        /// <summary>
        /// Update all smooth scroll animations.
        /// Returns true if any animation is active (requests repaint).
        /// </summary>
        public bool OnFrame()
        {
            bool active = false;
            lock (_lock)
            {
                foreach (var kvp in _scrollStates)
                {
                    if (UpdateSmoothScroll(kvp.Key))
                    {
                        active = true;
                    }
                }
            }
            return active;
        }

        #endregion

        #region Scroll Snapping

        public void ApplyScrollSnap(Element element, CssComputed style, List<float> snapPoints)
        {
            if (style?.ScrollSnapType == null || snapPoints == null || snapPoints.Count == 0)
                return;

            var state = GetScrollState(element);
            var snapType = style.ScrollSnapType.ToLowerInvariant();

            // Simple Y-axis snap for now
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

                // Snap if mandatory or close enough (proximity)
                // Default threshold for proximity can be e.g. 50px
                if (snapType.Contains("mandatory") || minDist < 50)
                {
                    // Trigger smooth scroll
                    SmoothScrollTo(element, state.ScrollX, nearestY);
                }
            }
            // TODO: X-axis snap
        }

        public void PerformSnap(Element element)
        {
             // 1. Get computed style (need to fetch from Layout/Renderer or pass it in)
            // For now, assuming we can get it via helper or cached
            // We'll pass null to IsScrollable and check if we can retrieve it from layout engine?
            // Actually, we need the style. 
            // MinimalLayoutComputer keeps styles.
            // But ScrollManager is in Rendering, separation of concerns?
            // Let's rely on passed style or look it up if possible.
            // For now, allow passing style?
            // Or assume `element.ComputedStyle` if we add it?
        }
        
        public void PerformSnap(Element element, CssComputed style, Func<Element, SKRect> getBox)
        {
            if (style == null || style.ScrollSnapType == null || style.ScrollSnapType == "none") return;
            
            var snapPoints = CalculateSnapPoints(element, style, getBox);
            ApplyScrollSnap(element, style, snapPoints);
        }

        private List<float> CalculateSnapPoints(Element container, CssComputed containerStyle, Func<Element, SKRect> getBox)
        {
            var points = new List<float>();
            if (container.Children == null) return points;
            
            var containerBox = getBox(container);
            if (containerBox.IsEmpty) return points;

            bool vertical = containerStyle.ScrollSnapType.Contains("y") || containerStyle.ScrollSnapType.Contains("block") || containerStyle.ScrollSnapType.Contains("both");
            
            foreach (var child in container.Children) 
            {
                if (child is Element childEl)
                {
                    // effectively: if (childStyle.ScrollSnapAlign != "none")
                    // But we don't have child style here easily unless we pass a style provider too.
                    // For MVP, assume all children are snap targets if container has snap-type.
                    // Or rely on default "none" which means we should look it up.
                    
                    var childBox = getBox(childEl);
                    if (childBox.IsEmpty) continue;

                    // Naive implementation: Snap to start
                    // TODO: Parse scroll-snap-align (start, center, end)
                    
                    float snapPos = 0;
                    if (vertical)
                    {
                        // Snap-align: start
                        // The scroll position where child.Top aligns with container.Top
                        // Offset = child.Top - container.Top (relative to current scroll?)
                        // Wait, boxes are likely in *document* coordinates (including scroll offset?).
                        // If boxes are static layout (Scrolled), then child.Top changes as we scroll?
                        // Usually LayoutResult is "Layout coordinates" (0,0 is top of content).
                        // So child.Top is fixed relative to content.
                        // Container.Top is fixed relative to content (if it's the scroll container itself).
                        
                        // ScrollPosition = Child.Top - Container.PaddingTop?
                        // Let's assume layout coordinates are relative to the container's content origin (0,0).
                        
                        // If container is checking its own children:
                        // Child.Y relative to Container Content.
                        
                        // Let's assume getBox returns rects in global/document space.
                        // child.Top - container.Top gives offset within container content?
                        // No, if container is scrolled, its "Top" might be visually shifted?
                        // LayoutEngine usually produces coordinates relative to the Document (0,0).
                        
                        // Target ScrollY = Child.Top (in document) - Container.Top (in document).
                        snapPos = childBox.Top - containerBox.Top;
                    }
                    else
                    {
                        snapPos = childBox.Left - containerBox.Left;
                    }
                    
                    points.Add(snapPos);
                }
            }
            return points;
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


