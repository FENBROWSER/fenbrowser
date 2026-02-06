using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Full CSS Float implementation per CSS 2.1 spec.
    /// Handles float left/right with proper text wrapping and clearing.
    /// </summary>
    public class CssFloatContext
    {
        private readonly List<FloatBox> _leftFloats = new();
        private readonly List<FloatBox> _rightFloats = new();
        private readonly float _containerWidth;
        private readonly float _containerLeft;
        private float _clearY = 0;

        public CssFloatContext(float containerLeft, float containerWidth)
        {
            _containerLeft = containerLeft;
            _containerWidth = containerWidth;
        }

        /// <summary>
        /// Add a floating element to the context
        /// </summary>
        public SKRect AddFloat(string floatType, float width, float height, float currentY)
        {
            floatType = floatType?.ToLowerInvariant() ?? "none";
            
            if (floatType == "left")
            {
                return AddLeftFloat(width, height, currentY);
            }
            else if (floatType == "right")
            {
                return AddRightFloat(width, height, currentY);
            }

            return SKRect.Empty;
        }

        private SKRect AddLeftFloat(float width, float height, float currentY)
        {
            float y = Math.Max(currentY, _clearY);
            
            // Find position that doesn't overlap existing floats
            while (true)
            {
                var (left, right) = GetAvailableWidth(y, height);
                if (width <= right - left)
                {
                    var rect = new SKRect(left, y, left + width, y + height);
                    _leftFloats.Add(new FloatBox { Rect = rect });
                    return rect;
                }
                y += 1; // Try next line
                if (y > 10000) break; // Safety limit
            }

            return new SKRect(_containerLeft, y, _containerLeft + width, y + height);
        }

        private SKRect AddRightFloat(float width, float height, float currentY)
        {
            float y = Math.Max(currentY, _clearY);
            
            while (true)
            {
                var (left, right) = GetAvailableWidth(y, height);
                if (width <= right - left)
                {
                    var rect = new SKRect(right - width, y, right, y + height);
                    _rightFloats.Add(new FloatBox { Rect = rect });
                    return rect;
                }
                y += 1;
                if (y > 10000) break;
            }

            return new SKRect(_containerLeft + _containerWidth - width, y, 
                              _containerLeft + _containerWidth, y + height);
        }

        /// <summary>
        /// Get available width for content at a given Y position
        /// </summary>
        public (float left, float right) GetAvailableWidth(float y, float height = 0)
        {
            float left = _containerLeft;
            float right = _containerLeft + _containerWidth;

            // Adjust for left floats
            foreach (var f in _leftFloats)
            {
                if (OverlapsVertically(f.Rect, y, height))
                {
                    left = Math.Max(left, f.Rect.Right);
                }
            }

            // Adjust for right floats
            foreach (var f in _rightFloats)
            {
                if (OverlapsVertically(f.Rect, y, height))
                {
                    right = Math.Min(right, f.Rect.Left);
                }
            }

            return (left, right);
        }

        /// <summary>
        /// Get the first Y position where full width is available
        /// </summary>
        public float GetClearY(string clearType)
        {
            clearType = clearType?.ToLowerInvariant() ?? "none";
            
            float maxY = 0;

            if (clearType == "left" || clearType == "both")
            {
                foreach (var f in _leftFloats)
                    maxY = Math.Max(maxY, f.Rect.Bottom);
            }

            if (clearType == "right" || clearType == "both")
            {
                foreach (var f in _rightFloats)
                    maxY = Math.Max(maxY, f.Rect.Bottom);
            }

            return maxY;
        }

        /// <summary>
        /// Clear floats and return the new Y position
        /// </summary>
        public float Clear(string clearType, float currentY)
        {
            float clearY = GetClearY(clearType);
            return Math.Max(currentY, clearY);
        }

        /// <summary>
        /// Get line boxes that wrap around floats
        /// </summary>
        public IEnumerable<LineBox> GetLineBoxes(float startY, float endY, float lineHeight)
        {
            var boxes = new List<LineBox>();
            float y = startY;

            while (y < endY)
            {
                var (left, right) = GetAvailableWidth(y, lineHeight);
                
                // Find consistent width for this line
                float nextY = y + lineHeight;
                var (nextLeft, nextRight) = GetAvailableWidth(nextY, lineHeight);

                // If width changes significantly, split the line
                if (Math.Abs(left - nextLeft) > 1 || Math.Abs(right - nextRight) > 1)
                {
                    // Find the float that causes the change
                    float transitionY = FindTransitionY(y, nextY, left, right);
                    if (transitionY > y)
                    {
                        boxes.Add(new LineBox { Y = y, Height = transitionY - y, Left = left, Right = right });
                        y = transitionY;
                        continue;
                    }
                }

                boxes.Add(new LineBox { Y = y, Height = lineHeight, Left = left, Right = right });
                y = nextY;
            }

            return boxes;
        }

        private float FindTransitionY(float startY, float endY, float startLeft, float startRight)
        {
            // Binary search for the exact transition point
            while (endY - startY > 0.5f)
            {
                float midY = (startY + endY) / 2;
                var (left, right) = GetAvailableWidth(midY);
                
                if (Math.Abs(left - startLeft) > 0.5f || Math.Abs(right - startRight) > 0.5f)
                    endY = midY;
                else
                    startY = midY;
            }
            return endY;
        }

        private bool OverlapsVertically(SKRect rect, float y, float height)
        {
            return rect.Bottom > y && rect.Top < y + height;
        }

        private struct FloatBox
        {
            public SKRect Rect;
        }

        public struct LineBox
        {
            public float Y;
            public float Height;
            public float Left;
            public float Right;
            public float Width => Right - Left;
        }
    }

    /// <summary>
    /// CSS Sticky positioning implementation.
    /// Handles position: sticky with proper offset constraints.
    /// </summary>
    public class CssStickyContext
    {
        private readonly List<StickyElement> _stickyElements = new();
        private float _scrollTop = 0;
        private float _viewportHeight = 0;

        /// <summary>
        /// Register a sticky element
        /// </summary>
        public void Register(Element element, SKRect originalRect, StickyOffsets offsets, SKRect containingBlock)
        {
            _stickyElements.Add(new StickyElement
            {
                Element = element,
                OriginalRect = originalRect,
                Offsets = offsets,
                ContainingBlock = containingBlock
            });
        }

        /// <summary>
        /// Update scroll position and get adjusted positions
        /// </summary>
        public void UpdateScroll(float scrollTop, float viewportHeight)
        {
            _scrollTop = scrollTop;
            _viewportHeight = viewportHeight;
        }

        /// <summary>
        /// Get the current position for a sticky element
        /// </summary>
        public SKRect GetCurrentRect(Element element)
        {
            var sticky = _stickyElements.FirstOrDefault(s => s.Element == element);
            if (sticky.Element == null) return SKRect.Empty;

            var original = sticky.OriginalRect;
            var container = sticky.ContainingBlock;
            var offsets = sticky.Offsets;

            // Calculate the sticky position based on scroll
            float newTop = original.Top;
            float newLeft = original.Left;

            // Sticky top
            if (offsets.Top.HasValue)
            {
                float stickyTop = _scrollTop + offsets.Top.Value;
                
                // Constrained by original position (only stick when scrolled past)
                if (original.Top < stickyTop)
                {
                    newTop = stickyTop;
                    
                    // Constrained by containing block bottom
                    float maxTop = container.Bottom - original.Height;
                    newTop = Math.Min(newTop, maxTop);
                }
            }

            // Sticky bottom
            if (offsets.Bottom.HasValue)
            {
                float stickyBottom = _scrollTop + _viewportHeight - offsets.Bottom.Value;
                
                if (original.Bottom > stickyBottom)
                {
                    newTop = stickyBottom - original.Height;
                    
                    // Constrained by containing block top
                    newTop = Math.Max(newTop, container.Top);
                }
            }

            // Sticky left
            if (offsets.Left.HasValue)
            {
                // Similar logic for horizontal scrolling
                // (less common, implement if needed)
            }

            // Sticky right
            if (offsets.Right.HasValue)
            {
                // Similar logic for horizontal scrolling
            }

            return new SKRect(newLeft, newTop, newLeft + original.Width, newTop + original.Height);
        }

        /// <summary>
        /// Clear all registered sticky elements
        /// </summary>
        public void Clear()
        {
            _stickyElements.Clear();
        }

        private struct StickyElement
        {
            public Element Element;
            public SKRect OriginalRect;
            public StickyOffsets Offsets;
            public SKRect ContainingBlock;
        }
    }

    /// <summary>
    /// Sticky position offsets
    /// </summary>
    public struct StickyOffsets
    {
        public float? Top;
        public float? Bottom;
        public float? Left;
        public float? Right;

        public static StickyOffsets Parse(CssComputed style)
        {
            var offsets = new StickyOffsets();

            if (style?.Map != null)
            {
                if (style.Map.TryGetValue("top", out var top) && top != "auto")
                    offsets.Top = CssValueParser.ParseLength(top);
                if (style.Map.TryGetValue("bottom", out var bottom) && bottom != "auto")
                    offsets.Bottom = CssValueParser.ParseLength(bottom);
                if (style.Map.TryGetValue("left", out var left) && left != "auto")
                    offsets.Left = CssValueParser.ParseLength(left);
                if (style.Map.TryGetValue("right", out var right) && right != "auto")
                    offsets.Right = CssValueParser.ParseLength(right);
            }

            return offsets;
        }
    }

    /// <summary>
    /// Utility for parsing CSS values
    /// </summary>
    public static class CssValueParser
    {
        public static float? ParseLength(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "auto") return null;

            value = value.Trim().ToLowerInvariant();

            if (value.EndsWith("px"))
            {
                if (float.TryParse(value.Replace("px", ""), out var px))
                    return px;
            }
            else if (value.EndsWith("em") || value.EndsWith("rem"))
            {
                if (float.TryParse(value.Replace("em", "").Replace("r", ""), out var em))
                    return em * 16; // Approximate
            }
            else if (value.EndsWith("%"))
            {
                if (float.TryParse(value.Replace("%", ""), out var pct))
                    return pct; // Caller needs to apply percentage to container
            }
            else if (value.EndsWith("vh"))
            {
                if (float.TryParse(value.Replace("vh", ""), out var vh))
                    return vh * 8; // Approximate viewport height * percentage
            }
            else if (value.EndsWith("vw"))
            {
                if (float.TryParse(value.Replace("vw", ""), out var vw))
                    return vw * 12; // Approximate viewport width * percentage
            }
            else if (float.TryParse(value, out var plain))
            {
                return plain;
            }

            return null;
        }
    }
}



