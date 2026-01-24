using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Manages floating elements within a container.
    /// Implements the BFC (Block Formatting Context) float intrusion logic.
    /// </summary>
    public class FloatManager
    {
        private List<SKRect> _leftFloats = new List<SKRect>();
        private List<SKRect> _rightFloats = new List<SKRect>();

        public void AddFloat(LayoutBox floatBox, bool isLeft)
        {
            var rect = floatBox.Geometry.MarginBox;
            if (isLeft) _leftFloats.Add(rect);
            else _rightFloats.Add(rect);
        }

        /// <summary>
        /// Calculates the available width at a given Y coordinate, accounting for floats.
        /// </summary>
        public (float LeftOffset, float RightOffset, float AvailableWidth) GetAvailableSpace(float y, float height, float containerWidth)
        {
            float occupiedLeft = 0;
            float occupiedRight = 0;

            // Check intersection with Y range [y, y+height]
            float bottom = y + height;

            foreach (var rect in _leftFloats)
            {
                if (rect.Bottom > y && rect.Top < bottom)
                {
                    if (rect.Right > occupiedLeft) occupiedLeft = rect.Right;
                }
            }

            foreach (var rect in _rightFloats)
            {
                if (rect.Bottom > y && rect.Top < bottom)
                {
                    // Right floats are from right edge
                    // Logic: distance from left edge?
                    // rect.Left is absolute relative to container
                    float dist = containerWidth - rect.Left;
                    if (dist > occupiedRight) occupiedRight = dist;
                }
            }

            return (occupiedLeft, occupiedRight, containerWidth - occupiedLeft - occupiedRight);
        }

        public float GetClearanceY(string clearMode, float currentY)
        {
            float maxY = currentY;
            if (clearMode == "left" || clearMode == "both")
            {
                foreach (var rect in _leftFloats) if (rect.Bottom > maxY) maxY = rect.Bottom;
            }
            if (clearMode == "right" || clearMode == "both")
            {
                foreach (var rect in _rightFloats) if (rect.Bottom > maxY) maxY = rect.Bottom;
            }
            return maxY;
        }
    }
}
