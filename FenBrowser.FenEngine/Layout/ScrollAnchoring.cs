using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Implements Scroll Anchoring to prevent unexpected layout shifts (CLS).
    /// CSS Overflow 3 §4 / WHATWG HTML §7.1.5.
    ///
    /// Before layout, selects the best candidate anchor node in the current
    /// viewport and records its viewport-relative Y position. After layout,
    /// computes the delta between the anchor's old and new position and returns
    /// an adjustment to compensate for layout-induced movement above the anchor.
    /// </summary>
    public class ScrollAnchoring
    {
        private Node _anchorNode;
        private float _anchorViewportY;
        private float _captureScrollY;

        /// <summary>
        /// Select the best anchor node from the current (pre-layout) box tree.
        /// The anchor is the deepest visible node within the viewport that is
        /// most likely to be stable across layout changes.
        /// </summary>
        /// <param name="layoutBoxes">Node → BoxModel mapping from the previous layout pass.</param>
        /// <param name="scrollY">Current scroll position (document-relative Y of viewport top).</param>
        /// <param name="viewportHeight">Viewport height in pixels.</param>
        public void SelectAnchor(
            IReadOnlyDictionary<Node, BoxModel> layoutBoxes,
            float scrollY,
            float viewportHeight)
        {
            _anchorNode = null;
            _anchorViewportY = 0f;
            _captureScrollY = scrollY;

            if (layoutBoxes == null || layoutBoxes.Count == 0 || viewportHeight <= 0f)
            {
                return;
            }

            float viewportTop = scrollY;
            float viewportBottom = scrollY + viewportHeight;

            Node bestNode = null;
            float bestViewportY = 0f;
            float bestScore = float.MaxValue;

            foreach (var kvp in layoutBoxes)
            {
                if (kvp.Key is not Element && kvp.Key is not Text)
                {
                    continue;
                }

                var box = kvp.Value;
                if (box == null)
                {
                    continue;
                }

                float boxTop = box.MarginBox.Top;
                float boxBottom = box.MarginBox.Bottom;

                // Must intersect with the viewport
                if (boxBottom <= viewportTop || boxTop >= viewportBottom)
                {
                    continue;
                }

                // Skip boxes that are too small to be useful anchors
                float boxHeight = box.MarginBox.Height;
                if (boxHeight < 1f)
                {
                    continue;
                }

                // Scoring: prefer nodes deeper in the DOM tree (more specific),
                // and prefer nodes that are not at the extreme top of the viewport
                // (headers tend to change size more than body content).
                float depthBonus = GetDepth(kvp.Key);
                float distanceFromViewportTop = Math.Abs(boxTop - viewportTop);

                // Lower score is better. Deeper nodes get a bonus.
                // Nodes very close to the top get a slight penalty.
                float score = distanceFromViewportTop - depthBonus * 2f;

                // Penalty for being at the very top of the viewport (likely a header)
                if (distanceFromViewportTop < 5f)
                {
                    score += 100f;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestNode = kvp.Key;
                    bestViewportY = boxTop - scrollY;
                }
            }

            if (bestNode != null)
            {
                _anchorNode = bestNode;
                _anchorViewportY = bestViewportY;

                FenBrowser.Core.EngineLogCompat.Debug(
                    $"[ScrollAnchoring] Anchor selected: {(bestNode as Element)?.TagName ?? "#text"} " +
                    $"at viewportY={bestViewportY:F1} depth={GetDepth(bestNode)} score={bestScore:F1}",
                    LogCategory.Layout);
            }
        }

        /// <summary>
        /// Computes the scroll adjustment needed to keep the anchor node stable
        /// after layout. Returns the delta to add to the current scroll position.
        /// </summary>
        /// <param name="newLayoutBoxes">Node → BoxModel mapping from the current (post-layout) pass.</param>
        /// <returns>Scroll adjustment delta in pixels. Positive = scroll down, negative = scroll up.</returns>
        public float CalculateAdjustment(IReadOnlyDictionary<Node, BoxModel> newLayoutBoxes)
        {
            if (_anchorNode == null || newLayoutBoxes == null)
            {
                return 0f;
            }

            if (!newLayoutBoxes.TryGetValue(_anchorNode, out var newBox) || newBox == null)
            {
                FenBrowser.Core.EngineLogCompat.Debug(
                    "[ScrollAnchoring] Anchor node no longer has a layout box — suppressing adjustment.",
                    LogCategory.Layout);
                _anchorNode = null;
                return 0f;
            }

            float newViewportY = newBox.MarginBox.Top - _captureScrollY;
            float delta = newViewportY - _anchorViewportY;

            // Suppress tiny adjustments (sub-pixel noise) and implausibly large ones
            if (Math.Abs(delta) < 0.5f)
            {
                return 0f;
            }

            const float maxAdjustment = 500f;
            if (Math.Abs(delta) > maxAdjustment)
            {
                FenBrowser.Core.EngineLogCompat.Debug(
                    $"[ScrollAnchoring] Delta {delta:F1} exceeds max {maxAdjustment} — capping.",
                    LogCategory.Layout);
                delta = Math.Sign(delta) * maxAdjustment;
            }

            FenBrowser.Core.EngineLogCompat.Debug(
                $"[ScrollAnchoring] Adjusting scroll by {delta:F1}px " +
                $"(anchor viewportY: {_anchorViewportY:F1} → {newViewportY:F1})",
                LogCategory.Layout);

            return delta;
        }

        /// <summary>
        /// Reset anchor state (call on navigation or explicit scroll).
        /// </summary>
        public void Reset()
        {
            _anchorNode = null;
            _anchorViewportY = 0f;
            _captureScrollY = 0f;
        }

        private static int GetDepth(Node node)
        {
            int depth = 0;
            var current = node;
            while (current?.ParentNode != null)
            {
                depth++;
                current = current.ParentNode;
                if (depth > 100) break; // safety cap
            }

            return depth;
        }
    }
}
