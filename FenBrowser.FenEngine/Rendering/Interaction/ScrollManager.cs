using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
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
        private readonly ScrollState _nullScrollState = new();
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
            if (element == null) return _nullScrollState;

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
            if (element == null) return;

            var state = GetScrollState(element);
            state.ScrollX = Math.Max(0, Math.Min(scrollX, state.MaxScrollX));
            state.ScrollY = Math.Max(0, Math.Min(scrollY, state.MaxScrollY));

            FenLogger.Debug($"[ScrollManager] {element.TagName} scroll: ({state.ScrollX}, {state.ScrollY})", LogCategory.Rendering);
        }

        /// <summary>
        /// Update scroll bounds based on content size.
        /// </summary>
        public void SetScrollBounds(Element element, float contentWidth, float contentHeight, float viewportWidth, float viewportHeight)
        {
            if (element == null) return;

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
            if (element == null) return;

            var state = GetScrollState(element);
            SetScrollPosition(element, state.ScrollX + deltaX, state.ScrollY + deltaY);
        }

        /// <summary>
        /// Clear scroll state for an element.
        /// </summary>
        public void ClearScrollState(Element element)
        {
            if (element == null) return;

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
            if (element == null) return false;

            var state = GetScrollState(element);
            return state.ContentHeight > state.ViewportHeight;
        }

        /// <summary>
        /// Check if element has horizontal scrollbar.
        /// </summary>
        public bool HasHorizontalScrollbar(Element element)
        {
            if (element == null) return false;

            var state = GetScrollState(element);
            return state.ContentWidth > state.ViewportWidth;
        }

        /// <summary>
        /// Get scroll offset for rendering.
        /// </summary>
        public (float x, float y) GetScrollOffset(Element element)
        {
            if (element == null) return (0, 0);

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
            if (snapType.Contains("x") || snapType.Contains("both") || snapType.Contains("inline"))
            {
                float nearestX = state.ScrollX;
                float minDistX = float.MaxValue;
                foreach (var point in snapPoints)
                {
                    var dist = Math.Abs(state.ScrollX - point);
                    if (dist < minDistX)
                    {
                        minDistX = dist;
                        nearestX = point;
                    }
                }

                if (snapType.Contains("mandatory") || minDistX < 50)
                {
                    SmoothScrollTo(element, nearestX, state.ScrollY);
                }
            }
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
        
        public void PerformSnap(Element element, CssComputed style, Func<Element, SKRect> getBox, Func<Element, CssComputed> getStyle = null)
        {
            if (style == null || style.ScrollSnapType == null || style.ScrollSnapType == "none") return;
            
            var snapPoints = CalculateSnapPoints(element, style, getBox, getStyle);
            ApplyScrollSnap(element, style, snapPoints);
        }

        private List<float> CalculateSnapPoints(Element container, CssComputed containerStyle, Func<Element, SKRect> getBox, Func<Element, CssComputed> getStyle)
        {
            var points = new List<float>();
            if (container.Children == null) return points;
            
            var containerBox = getBox(container);
            if (containerBox.IsEmpty) return points;

            bool vertical = containerStyle.ScrollSnapType.Contains("y") || containerStyle.ScrollSnapType.Contains("block") || containerStyle.ScrollSnapType.Contains("both");
            bool horizontal = containerStyle.ScrollSnapType.Contains("x") || containerStyle.ScrollSnapType.Contains("inline") || containerStyle.ScrollSnapType.Contains("both");
            float padTop = (float)(containerStyle.Padding.Top);
            float padLeft = (float)(containerStyle.Padding.Left);
            
            foreach (var child in container.Children) 
            {
                if (child is Element childEl)
                {
                    var childBox = getBox(childEl);
                    if (childBox.IsEmpty) continue;

                    var childStyle = getStyle?.Invoke(childEl);
                    string align = childStyle?.ScrollSnapAlign ?? "start";

                    if (vertical)
                    {
                        float snapPos = childBox.Top - containerBox.Top - padTop; // start
                        if (align.Contains("center"))
                            snapPos = childBox.Top - containerBox.Top - ((containerBox.Height - childBox.Height) / 2f);
                        else if (align.Contains("end"))
                            snapPos = childBox.Bottom - containerBox.Bottom + padTop;
                        points.Add(snapPos);
                    }

                    if (horizontal)
                    {
                        float snapPosX = childBox.Left - containerBox.Left - padLeft; // start
                        if (align.Contains("center"))
                            snapPosX = childBox.Left - containerBox.Left - ((containerBox.Width - childBox.Width) / 2f);
                        else if (align.Contains("end"))
                            snapPosX = childBox.Right - containerBox.Right + padLeft;
                        points.Add(snapPosX);
                    }
                }
            }
            return points;
        }

        #endregion

        #region Scroll Anchoring

        private class AnchorData
        {
            public Node Node { get; set; }
            public float OffsetY { get; set; } // Distance from Scroll Top to Node Top
        }

        private readonly Dictionary<Element, AnchorData> _anchors = new();

        /// <summary>
        /// Selects a candidate node to anchor to before layout changes.
        /// </summary>
        public void SelectAnchor(Element container, Node rootContent, Func<Node, SKRect> getBox)
        {
            var state = GetScrollState(container);
            // Viewport in Content Coordinates: [ScrollY, ScrollY + ViewportHeight]
            var visibleRect = new SKRect(0, state.ScrollY, state.ViewportWidth, state.ScrollY + state.ViewportHeight);

            // Find valid anchor: The first visible block-level element
            var candidate = FindAnchorRecursive(rootContent, visibleRect, getBox);

            if (candidate != null)
            {
                var box = getBox(candidate);
                // Offset = Box.Top - ScrollY (Distance from visual top)
                float offset = box.Top - state.ScrollY;
                
                _anchors[container] = new AnchorData { Node = candidate, OffsetY = offset };
                FenLogger.Debug($"[ScrollManager] Selected Anchor: {candidate.GetType().Name} (Tag: {(candidate as Element)?.TagName}) @ Offset {offset}", LogCategory.Rendering);
            }
            else
            {
                _anchors.Remove(container);
            }
        }

        private Node FindAnchorRecursive(Node node, SKRect visibleRect, Func<Node, SKRect> getBox)
        {
            if (node == null) return null;

            // Check self
            var box = getBox(node);
            if (!box.IsEmpty)
            {
                // Must be partially visible
                if (box.Bottom > visibleRect.Top && box.Top < visibleRect.Bottom)
                {
                    // Heuristic: Prefer Elements / Text with actual content
                    if (node is Element || (node is Text t && !string.IsNullOrWhiteSpace(t.Data)))
                    {
                        // Found a candidate? 
                        // TODO: Refine heuristic (e.g. skip massive containers, prefer leaf nodes or headers)
                        // For now, take first match (DFS Pre-order). 
                        // Actually, we want the top-most visible one. DFS pre-order does that efficiently.
                        if (node is Element) return node;
                        // If text, return parent? Or text itself if getBox supports it.
                        return node;
                    }
                }
            }

            // Recurse
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    var result = FindAnchorRecursive(child, visibleRect, getBox);
                    if (result != null) return result;
                }
            }
            return null;
        }

        /// <summary>
        /// Adjusts scroll position after layout to keep the anchor node stable.
        /// </summary>
        public void AdjustScroll(Element container, Func<Node, SKRect> getBox)
        {
            if (!_anchors.TryGetValue(container, out var anchorRef)) return;
            
            // Validate anchor still exists
            // (Node references persist across layout passes)
            
            var newBox = getBox(anchorRef.Node);
            if (newBox.IsEmpty)
            {
                // Anchor lost or became invisible - clear it
                _anchors.Remove(container);
                return;
            }

            // Calculate expected scroll position to maintain visual offset
            // NewScrollY = NewBox.Top - OldOffset
            float targetScrollY = newBox.Top - anchorRef.OffsetY;
            
            var state = GetScrollState(container);
            float currentScrollY = state.ScrollY;

            // If diff is significant, adjust
            if (Math.Abs(targetScrollY - currentScrollY) > 0.5f)
            {
                float diff = targetScrollY - currentScrollY;
                FenLogger.Debug($"[ScrollManager] Adjusting Scroll by {diff}px (Anchor moved from {anchorRef.Node.GetHashCode()})", LogCategory.Rendering);
                
                // Update state directly without clamping immediately? 
                // Clamping happens in SetScrollPosition.
                SetScrollPosition(container, state.ScrollX, targetScrollY);
            }
            
            // Clean up
            _anchors.Remove(container);
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




