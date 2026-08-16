using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
        private readonly ConditionalWeakTable<Element, ScrollState> _scrollStates = new();
        private readonly ScrollState _nullScrollState = new();
        private readonly object _lock = new();

        #region Scroll State Management

        /// <summary>
        /// Get or create scroll state for an element.
        /// </summary>
        public ScrollState GetScrollState(Element element)
        {
            if (element == null) return _nullScrollState;

            lock (_lock)
            {
                return _scrollStates.GetValue(element, static _ => new ScrollState());
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
            var nowUtc = DateTime.UtcNow;
            var nowTimestamp = Stopwatch.GetTimestamp();

            state.ScrollX = ClampToKnownBounds(scrollX, state.MaxScrollX);
            state.ScrollY = ClampToKnownBounds(scrollY, state.MaxScrollY);

            var dtSeconds = state.LastScrollUpdateTimestamp != 0
                ? Stopwatch.GetElapsedTime(state.LastScrollUpdateTimestamp, nowTimestamp).TotalSeconds
                : (nowUtc - state.LastScrollUpdateUtc).TotalSeconds;
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

            state.LastScrollUpdateUtc = nowUtc;
            state.LastScrollUpdateTimestamp = nowTimestamp;
        }

        /// <summary>
        /// Update scroll bounds based on content size.
        /// </summary>
        public void SetScrollBounds(Element element, float contentWidth, float contentHeight, float viewportWidth, float viewportHeight)
        {
            var state = element == null ? _nullScrollState : GetScrollState(element);
            contentWidth = NormalizeDimension(contentWidth);
            contentHeight = NormalizeDimension(contentHeight);
            viewportWidth = NormalizeDimension(viewportWidth);
            viewportHeight = NormalizeDimension(viewportHeight);

            state.ContentWidth = contentWidth;
            state.ContentHeight = contentHeight;
            state.ViewportWidth = viewportWidth;
            state.ViewportHeight = viewportHeight;
            state.MaxScrollX = Math.Max(0f, contentWidth - viewportWidth);
            state.MaxScrollY = Math.Max(0f, contentHeight - viewportHeight);

            // Bounds are known here, so a zero maximum means the axis cannot scroll.
            state.ScrollX = Math.Clamp(float.IsFinite(state.ScrollX) ? state.ScrollX : 0f, 0f, state.MaxScrollX);
            state.ScrollY = Math.Clamp(float.IsFinite(state.ScrollY) ? state.ScrollY : 0f, 0f, state.MaxScrollY);
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
                ResetScrollState(_nullScrollState);
                return;
            }

            lock (_lock)
            {
                _scrollStates.Remove(element);
                _anchors.Remove(element);
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
                _anchors.Clear();
                ResetScrollState(_nullScrollState);
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

        public IReadOnlyDictionary<Element, SKPoint> SnapshotElementScrollOffsets()
        {
            lock (_lock)
            {
                var snapshot = new Dictionary<Element, SKPoint>();
                foreach (var pair in _scrollStates)
                {
                    snapshot[pair.Key] = new SKPoint(pair.Value.ScrollX, pair.Value.ScrollY);
                }

                return snapshot;
            }
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
            durationMs = Math.Clamp(durationMs, 1, 60_000);

            if (Math.Abs(targetX - state.ScrollX) < 0.5f && Math.Abs(targetY - state.ScrollY) < 0.5f)
            {
                state.SmoothScrollStartTime = null;
                state.SmoothScrollStartTimestamp = 0;
                return;
            }

            state.SmoothScrollTarget = (targetX, targetY);
            state.SmoothScrollStartTime = DateTime.UtcNow;
            state.SmoothScrollStartTimestamp = Stopwatch.GetTimestamp();
            state.SmoothScrollDurationMs = durationMs;
            state.SmoothScrollStart = (state.ScrollX, state.ScrollY);
        }

        /// <summary>
        /// Update smooth scroll animation. Call each frame.
        /// </summary>
        public bool UpdateSmoothScroll(Element element)
        {
            var state = GetScrollState(element);
            if (!state.IsAnimating) return false;

            var elapsed = state.SmoothScrollStartTimestamp != 0
                ? Stopwatch.GetElapsedTime(state.SmoothScrollStartTimestamp, Stopwatch.GetTimestamp()).TotalMilliseconds
                : state.SmoothScrollStartTime.HasValue
                    ? Math.Max(0d, (DateTime.UtcNow - state.SmoothScrollStartTime.Value).TotalMilliseconds)
                    : 0d;
            var duration = Math.Max(1, state.SmoothScrollDurationMs);
            var progress = Math.Clamp(elapsed / duration, 0d, 1d);

            // Ease out cubic
            var eased = 1 - Math.Pow(1 - progress, 3);

            var (startX, startY) = state.SmoothScrollStart;
            var (targetX, targetY) = state.SmoothScrollTarget;

            state.ScrollX = ClampToKnownBounds((float)(startX + (targetX - startX) * eased), state.MaxScrollX);
            state.ScrollY = ClampToKnownBounds((float)(startY + (targetY - startY) * eased), state.MaxScrollY);

            if (progress >= 1.0)
            {
                state.SmoothScrollStartTime = null;
                state.SmoothScrollStartTimestamp = 0;
                return false;
            }

            return true;
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
            if (!float.IsFinite(value))
                return 0f;

            max = NormalizeDimension(max);
            if (max > 0.001f)
                return Math.Clamp(value, 0f, max);

            // A zero max can also mean bounds have not been measured yet; preserve the
            // previous behavior in that case while refusing negative/non-finite offsets.
            return Math.Max(0f, value);
        }

        private static float NormalizeDimension(float value)
        {
            return float.IsFinite(value) && value > 0f ? value : 0f;
        }

        private static void ResetScrollState(ScrollState state)
        {
            if (state == null) return;
            state.ScrollX = 0f;
            state.ScrollY = 0f;
            state.MaxScrollX = 0f;
            state.MaxScrollY = 0f;
            state.ContentWidth = 0f;
            state.ContentHeight = 0f;
            state.ViewportWidth = 0f;
            state.ViewportHeight = 0f;
            state.SmoothScrollStartTime = null;
            state.SmoothScrollStartTimestamp = 0;
            state.SmoothScrollDurationMs = 0;
            state.SmoothScrollStart = (0f, 0f);
            state.SmoothScrollTarget = (0f, 0f);
            state.LastVelocityX = 0f;
            state.LastVelocityY = 0f;
            state.LastInputDeltaX = 0f;
            state.LastInputDeltaY = 0f;
            state.LastScrollUpdateUtc = DateTime.UtcNow;
            state.LastScrollUpdateTimestamp = Stopwatch.GetTimestamp();
        }

        private static float ResolveDirectionHint(float lastInputDelta, float lastVelocity)
        {
            if (Math.Abs(lastInputDelta) > 0.001f) return lastInputDelta;
            if (Math.Abs(lastVelocity) > 0.001f) return lastVelocity;
            return 0f;
        }

        private static float FindBestSnapPoint(float current, IReadOnlyList<float> points, float directionHint)
        {
            if (points == null || points.Count == 0 || !float.IsFinite(current)) return current;

            float nearest = current;
            float nearestDist = float.MaxValue;
            float forward = float.MaxValue;
            float backward = float.MinValue;
            bool foundForward = false;
            bool foundBackward = false;

            for (int i = 0; i < points.Count; i++)
            {
                float point = points[i];
                if (!float.IsFinite(point)) continue;

                float dist = Math.Abs(current - point);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = point;
                }

                if (point >= current + 0.5f && point < forward)
                {
                    forward = point;
                    foundForward = true;
                }
                if (point <= current - 0.5f && point > backward)
                {
                    backward = point;
                    foundBackward = true;
                }
            }

            if (directionHint > 0.001f && foundForward) return forward;
            if (directionHint < -0.001f && foundBackward) return backward;
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
            if (!float.IsFinite(value))
            {
                value = 0f;
                return false;
            }
            return true;
        }

        #endregion

        #region Scroll Anchoring

        private class AnchorData
        {
            public Node Node { get; set; }
            public float OffsetY { get; set; } // Distance from Scroll Top to Node Top
        }

        private readonly ConditionalWeakTable<Element, AnchorData> _anchors = new();

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
                
                _anchors.AddOrUpdate(container, new AnchorData { Node = candidate, OffsetY = offset });
                EngineLogCompat.Debug($"[ScrollManager] Selected Anchor: {candidate.GetType().Name} (Tag: {(candidate as Element)?.TagName}) @ Offset {offset}", LogCategory.Rendering);
            }
            else
            {
                _anchors.Remove(container);
            }
        }

        private Node FindAnchorRecursive(Node node, SKRect visibleRect, Func<Node, SKRect> getBox)
        {
            if (node == null || getBox == null) return null;

            // Preserve the old DFS pre-order semantics without consuming native stack
            // proportional to adversarial DOM depth.
            var pending = new Stack<Node>();
            pending.Push(node);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var box = getBox(current);
                if (!box.IsEmpty && box.Bottom > visibleRect.Top && box.Top < visibleRect.Bottom)
                {
                    if (current is Element ||
                        (current is Text text && !string.IsNullOrWhiteSpace(text.Data)))
                    {
                        return current;
                    }
                }

                var children = current.ChildNodes;
                if (children == null) continue;
                for (int i = children.Length - 1; i >= 0; i--)
                {
                    if (children[i] != null)
                        pending.Push(children[i]);
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
                EngineLogCompat.Debug($"[ScrollManager] Adjusting Scroll by {diff}px (Anchor moved from {anchorRef.Node.GetHashCode()})", LogCategory.Rendering);
                
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
        public long SmoothScrollStartTimestamp { get; set; }
        public int SmoothScrollDurationMs { get; set; }
        public (float x, float y) SmoothScrollStart { get; set; }
        public (float x, float y) SmoothScrollTarget { get; set; }
        public DateTime LastScrollUpdateUtc { get; set; } = DateTime.UtcNow;
        public long LastScrollUpdateTimestamp { get; set; } = Stopwatch.GetTimestamp();
        public float LastVelocityX { get; set; }
        public float LastVelocityY { get; set; }
        public float LastInputDeltaX { get; set; }
        public float LastInputDeltaY { get; set; }

        /// <summary>
        /// Check if currently animating.
        /// </summary>
        public bool IsAnimating => SmoothScrollStartTimestamp != 0 || SmoothScrollStartTime.HasValue;
    }
}
