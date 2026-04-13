using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        public void SetScrollPosition(Element element, float scrollX, float scrollY, bool fromUserInput = false)
        {
            var state = element == null ? _nullScrollState : GetScrollState(element);
            float previousX = state.ScrollX;
            float previousY = state.ScrollY;
            var now = DateTime.UtcNow;

            state.ScrollX = ClampToKnownBounds(scrollX, state.MaxScrollX);
            state.ScrollY = ClampToKnownBounds(scrollY, state.MaxScrollY);

            var dtSeconds = (now - state.LastScrollUpdateUtc).TotalSeconds;
            if (dtSeconds > 0.0001 && dtSeconds < 0.5)
            {
                state.LastVelocityX = (state.ScrollX - previousX) / (float)dtSeconds;
                state.LastVelocityY = (state.ScrollY - previousY) / (float)dtSeconds;
            }
            else if (fromUserInput)
            {
                state.LastVelocityX = 0;
                state.LastVelocityY = 0;
            }

            if (fromUserInput)
            {
                state.LastInputDeltaX = state.ScrollX - previousX;
                state.LastInputDeltaY = state.ScrollY - previousY;
            }

            state.LastScrollUpdateUtc = now;

            FenLogger.Debug($"[ScrollManager] {(element?.TagName ?? "#viewport")} scroll: ({state.ScrollX}, {state.ScrollY})", LogCategory.Rendering);
        }

        /// <summary>
        /// Update scroll bounds based on content size.
        /// </summary>
        public void SetScrollBounds(Element element, float contentWidth, float contentHeight, float viewportWidth, float viewportHeight)
        {
            var state = element == null ? _nullScrollState : GetScrollState(element);
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
            var state = element == null ? _nullScrollState : GetScrollState(element);
            SetScrollPosition(element, state.ScrollX + deltaX, state.ScrollY + deltaY, fromUserInput: true);
        }

        /// <summary>
        /// Clear scroll state for an element.
        /// </summary>
        public void ClearScrollState(Element element)
        {
            if (element == null)
            {
                _nullScrollState.ScrollX = 0;
                _nullScrollState.ScrollY = 0;
                _nullScrollState.MaxScrollX = 0;
                _nullScrollState.MaxScrollY = 0;
                _nullScrollState.ContentWidth = 0;
                _nullScrollState.ContentHeight = 0;
                _nullScrollState.ViewportWidth = 0;
                _nullScrollState.ViewportHeight = 0;
                return;
            }

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
            var state = element == null ? _nullScrollState : GetScrollState(element);
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
            targetX = ClampToKnownBounds(targetX, state.MaxScrollX);
            targetY = ClampToKnownBounds(targetY, state.MaxScrollY);

            if (Math.Abs(targetX - state.ScrollX) < 0.5f && Math.Abs(targetY - state.ScrollY) < 0.5f)
            {
                return;
            }

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

            state.ScrollX = ClampToKnownBounds((float)(startX + (targetX - startX) * eased), state.MaxScrollX);
            state.ScrollY = ClampToKnownBounds((float)(startY + (targetY - startY) * eased), state.MaxScrollY);

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
            ApplyScrollSnap(element, style, snapPoints, snapPoints);
        }

        public void ApplyScrollSnap(Element element, CssComputed style, IReadOnlyList<float> snapPointsX, IReadOnlyList<float> snapPointsY)
        {
            if (style?.ScrollSnapType == null)
                return;

            var state = GetScrollState(element);
            var snapType = style.ScrollSnapType.ToLowerInvariant();
            bool mandatory = snapType.Contains("mandatory", StringComparison.Ordinal);
            bool snapped = false;

            float targetX = state.ScrollX;
            float targetY = state.ScrollY;

            if ((snapType.Contains("y", StringComparison.Ordinal) || snapType.Contains("both", StringComparison.Ordinal) || snapType.Contains("block", StringComparison.Ordinal)) &&
                snapPointsY != null && snapPointsY.Count > 0)
            {
                float directionHintY = ResolveDirectionHint(state.LastInputDeltaY, state.LastVelocityY);
                float nearestY = FindBestSnapPoint(state.ScrollY, snapPointsY, directionHintY);
                float clampedY = ClampToKnownBounds(nearestY, state.MaxScrollY);
                float minDist = Math.Abs(state.ScrollY - clampedY);
                float thresholdY = Math.Max(24f, Math.Min(96f, state.ViewportHeight > 0 ? state.ViewportHeight * 0.08f : 50f));

                if (mandatory || minDist < thresholdY)
                {
                    targetY = clampedY;
                    snapped = true;
                }
            }

            if ((snapType.Contains("x", StringComparison.Ordinal) || snapType.Contains("both", StringComparison.Ordinal) || snapType.Contains("inline", StringComparison.Ordinal)) &&
                snapPointsX != null && snapPointsX.Count > 0)
            {
                float directionHintX = ResolveDirectionHint(state.LastInputDeltaX, state.LastVelocityX);
                float nearestX = FindBestSnapPoint(state.ScrollX, snapPointsX, directionHintX);
                float clampedX = ClampToKnownBounds(nearestX, state.MaxScrollX);
                float minDistX = Math.Abs(state.ScrollX - clampedX);
                float thresholdX = Math.Max(24f, Math.Min(96f, state.ViewportWidth > 0 ? state.ViewportWidth * 0.08f : 50f));

                if (mandatory || minDistX < thresholdX)
                {
                    targetX = clampedX;
                    snapped = true;
                }
            }

            if (snapped)
            {
                SmoothScrollTo(element, targetX, targetY);
                // Consume user-input hint once snap has been scheduled.
                state.LastInputDeltaX = 0;
                state.LastInputDeltaY = 0;
                state.LastVelocityX = 0;
                state.LastVelocityY = 0;
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
            if (style == null || string.IsNullOrWhiteSpace(style.ScrollSnapType) || style.ScrollSnapType.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
            if (element == null || getBox == null) return;

            var state = GetScrollState(element);
            if (state == null || state.IsAnimating) return;
            
            var snapPoints = CalculateSnapPoints(element, style, getBox, getStyle);
            ApplyScrollSnap(element, style, snapPoints.Horizontal, snapPoints.Vertical);
        }

        private (List<float> Horizontal, List<float> Vertical) CalculateSnapPoints(Element container, CssComputed containerStyle, Func<Element, SKRect> getBox, Func<Element, CssComputed> getStyle)
        {
            var pointsX = new List<float>();
            var pointsY = new List<float>();
            if (container.Children == null) return (pointsX, pointsY);
            
            var containerBox = getBox(container);
            if (containerBox.IsEmpty) return (pointsX, pointsY);

            var snapType = containerStyle.ScrollSnapType?.ToLowerInvariant() ?? string.Empty;
            bool vertical = snapType.Contains("y", StringComparison.Ordinal) || snapType.Contains("block", StringComparison.Ordinal) || snapType.Contains("both", StringComparison.Ordinal);
            bool horizontal = snapType.Contains("x", StringComparison.Ordinal) || snapType.Contains("inline", StringComparison.Ordinal) || snapType.Contains("both", StringComparison.Ordinal);
            var (padTop, padRight, padBottom, padLeft) = ResolveScrollPadding(containerStyle);
            
            foreach (var child in container.Children) 
            {
                if (child is Element childEl)
                {
                    var childBox = getBox(childEl);
                    if (childBox.IsEmpty) continue;

                    var childStyle = getStyle?.Invoke(childEl);
                    string align = (childStyle?.ScrollSnapAlign ?? "start").ToLowerInvariant();
                    var (marginTop, marginRight, marginBottom, marginLeft) = ResolveScrollMargin(childStyle);

                    if (vertical)
                    {
                        float snapPos = childBox.Top - containerBox.Top - padTop - marginTop; // start
                        if (align.Contains("center", StringComparison.Ordinal))
                        {
                            float centerBias = (marginBottom - marginTop) * 0.5f;
                            snapPos = childBox.Top - containerBox.Top - ((containerBox.Height - childBox.Height) / 2f) + centerBias;
                        }
                        else if (align.Contains("end", StringComparison.Ordinal))
                        {
                            snapPos = childBox.Bottom - containerBox.Bottom + padBottom + marginBottom;
                        }
                        pointsY.Add(snapPos);
                    }

                    if (horizontal)
                    {
                        float snapPosX = childBox.Left - containerBox.Left - padLeft - marginLeft; // start
                        if (align.Contains("center", StringComparison.Ordinal))
                        {
                            float centerBiasX = (marginRight - marginLeft) * 0.5f;
                            snapPosX = childBox.Left - containerBox.Left - ((containerBox.Width - childBox.Width) / 2f) + centerBiasX;
                        }
                        else if (align.Contains("end", StringComparison.Ordinal))
                        {
                            snapPosX = childBox.Right - containerBox.Right + padRight + marginRight;
                        }
                        pointsX.Add(snapPosX);
                    }
                }
            }
            return (pointsX, pointsY);
        }

        private static float ClampToKnownBounds(float value, float max)
        {
            if (max > 0.001f)
            {
                return Math.Max(0, Math.Min(value, max));
            }
            return Math.Max(0, value);
        }

        private static float ResolveDirectionHint(float lastInputDelta, float lastVelocity)
        {
            if (Math.Abs(lastInputDelta) > 0.001f) return lastInputDelta;
            if (Math.Abs(lastVelocity) > 0.001f) return lastVelocity;
            return 0f;
        }

        private static float FindBestSnapPoint(float current, IReadOnlyList<float> points, float directionHint)
        {
            if (points == null || points.Count == 0) return current;

            float nearest = points[0];
            float nearestDist = Math.Abs(current - nearest);

            for (int i = 1; i < points.Count; i++)
            {
                float dist = Math.Abs(current - points[i]);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = points[i];
                }
            }

            if (directionHint > 0.001f)
            {
                float forward = float.MaxValue;
                bool foundForward = false;
                for (int i = 0; i < points.Count; i++)
                {
                    float p = points[i];
                    if (p >= current + 0.5f && p < forward)
                    {
                        forward = p;
                        foundForward = true;
                    }
                }
                if (foundForward) return forward;
            }
            else if (directionHint < -0.001f)
            {
                float backward = float.MinValue;
                bool foundBackward = false;
                for (int i = 0; i < points.Count; i++)
                {
                    float p = points[i];
                    if (p <= current - 0.5f && p > backward)
                    {
                        backward = p;
                        foundBackward = true;
                    }
                }
                if (foundBackward) return backward;
            }

            return nearest;
        }

        private static (float Top, float Right, float Bottom, float Left) ResolveScrollPadding(CssComputed style)
        {
            if (style == null) return (0, 0, 0, 0);

            bool hasTop = TryGetLengthPx(style, "scroll-padding-top", out float top);
            bool hasRight = TryGetLengthPx(style, "scroll-padding-right", out float right);
            bool hasBottom = TryGetLengthPx(style, "scroll-padding-bottom", out float bottom);
            bool hasLeft = TryGetLengthPx(style, "scroll-padding-left", out float left);

            if (TryGetShorthandInsets(style, "scroll-padding", out var shorthand))
            {
                if (!hasTop) top = shorthand.Top;
                if (!hasRight) right = shorthand.Right;
                if (!hasBottom) bottom = shorthand.Bottom;
                if (!hasLeft) left = shorthand.Left;
            }

            return (top, right, bottom, left);
        }

        private static (float Top, float Right, float Bottom, float Left) ResolveScrollMargin(CssComputed style)
        {
            if (style == null) return (0, 0, 0, 0);

            bool hasTop = TryGetLengthPx(style, "scroll-margin-top", out float top);
            bool hasRight = TryGetLengthPx(style, "scroll-margin-right", out float right);
            bool hasBottom = TryGetLengthPx(style, "scroll-margin-bottom", out float bottom);
            bool hasLeft = TryGetLengthPx(style, "scroll-margin-left", out float left);

            if (TryGetShorthandInsets(style, "scroll-margin", out var shorthand))
            {
                if (!hasTop) top = shorthand.Top;
                if (!hasRight) right = shorthand.Right;
                if (!hasBottom) bottom = shorthand.Bottom;
                if (!hasLeft) left = shorthand.Left;
            }

            return (top, right, bottom, left);
        }

        private static bool TryGetLengthPx(CssComputed style, string key, out float value)
        {
            value = 0;
            if (style?.Map == null) return false;
            if (!style.Map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;
            return TryParseLengthPx(raw, out value);
        }

        private static bool TryGetShorthandInsets(CssComputed style, string key, out (float Top, float Right, float Bottom, float Left) insets)
        {
            insets = (0, 0, 0, 0);
            if (style?.Map == null) return false;
            if (!style.Map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;

            var parts = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 4) return false;

            var values = new float[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryParseLengthPx(parts[i], out values[i])) return false;
            }

            switch (parts.Length)
            {
                case 1:
                    insets = (values[0], values[0], values[0], values[0]);
                    break;
                case 2:
                    insets = (values[0], values[1], values[0], values[1]);
                    break;
                case 3:
                    insets = (values[0], values[1], values[2], values[1]);
                    break;
                default:
                    insets = (values[0], values[1], values[2], values[3]);
                    break;
            }

            return true;
        }

        private static bool TryParseLengthPx(string raw, out float value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string text = raw.Trim().ToLowerInvariant();
            if (string.Equals(text, "auto", StringComparison.Ordinal)) return false;

            float multiplier = 1f;
            if (text.EndsWith("px", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 2);
            }
            else if (text.EndsWith("rem", StringComparison.Ordinal) || text.EndsWith("em", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 2);
                multiplier = 16f;
            }
            else if (text.EndsWith("%", StringComparison.Ordinal))
            {
                // Percent insets are currently unsupported in snap offset math.
                return false;
            }

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            value = parsed * multiplier;
            return true;
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
        public DateTime LastScrollUpdateUtc { get; set; } = DateTime.UtcNow;
        public float LastVelocityX { get; set; }
        public float LastVelocityY { get; set; }
        public float LastInputDeltaX { get; set; }
        public float LastInputDeltaY { get; set; }

        /// <summary>
        /// Check if currently animating.
        /// </summary>
        public bool IsAnimating => SmoothScrollStartTime.HasValue;
    }
}




