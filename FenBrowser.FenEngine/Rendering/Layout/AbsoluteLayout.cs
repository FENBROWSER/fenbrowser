using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Layout
{
    /// <summary>
    /// Absolute and fixed positioning layout engine.
    /// Handles position: absolute, fixed, and sticky elements.
    /// </summary>
    public static class AbsoluteLayout
    {
        /// <summary>
        /// Layout an absolutely positioned element.
        /// </summary>
        public static SKRect Compute(
            ILayoutEngine engine,
            LiteElement element,
            CssComputed style,
            SKRect containingBlock,
            SKRect viewport)
        {
            if (style == null) return SKRect.Empty;

            var position = style.Position?.ToLowerInvariant() ?? "static";
            
            // Fixed uses viewport as containing block
            if (position == "fixed")
            {
                containingBlock = viewport;
            }

            // Calculate position
            float left = CalculateOffset(style.Left, style.LeftPercent, containingBlock.Width, containingBlock.Left);
            float top = CalculateOffset(style.Top, style.TopPercent, containingBlock.Height, containingBlock.Top);
            float right = CalculateOffset(style.Right, style.RightPercent, containingBlock.Width, 0);
            float bottom = CalculateOffset(style.Bottom, style.BottomPercent, containingBlock.Height, 0);

            // Calculate dimensions
            float width = CalculateDimension(style.Width, style.WidthPercent, containingBlock.Width);
            float height = CalculateDimension(style.Height, style.HeightPercent, containingBlock.Height);

            // If both left and right are set, calculate width
            if (style.Left.HasValue && style.Right.HasValue && width == 0)
            {
                width = containingBlock.Width - left - right;
            }

            // If both top and bottom are set, calculate height
            if (style.Top.HasValue && style.Bottom.HasValue && height == 0)
            {
                height = containingBlock.Height - top - bottom;
            }

            // Default dimensions if not specified
            if (width == 0) width = 100;  // Auto width
            if (height == 0) height = 50; // Auto height

            // Calculate final position
            float finalLeft, finalTop;

            if (style.Left.HasValue || style.LeftPercent.HasValue)
            {
                finalLeft = containingBlock.Left + left;
            }
            else if (style.Right.HasValue || style.RightPercent.HasValue)
            {
                finalLeft = containingBlock.Right - right - width;
            }
            else
            {
                finalLeft = containingBlock.Left; // Default to container left
            }

            if (style.Top.HasValue || style.TopPercent.HasValue)
            {
                finalTop = containingBlock.Top + top;
            }
            else if (style.Bottom.HasValue || style.BottomPercent.HasValue)
            {
                finalTop = containingBlock.Bottom - bottom - height;
            }
            else
            {
                finalTop = containingBlock.Top; // Default to container top
            }

            // Apply margins
            var margin = style.Margin;
            finalLeft += (float)margin.Left;
            finalTop += (float)margin.Top;
            width -= (float)(margin.Left + margin.Right);
            height -= (float)(margin.Top + margin.Bottom);

            // Ensure valid dimensions
            width = Math.Max(0, width);
            height = Math.Max(0, height);

            var rect = new SKRect(finalLeft, finalTop, finalLeft + width, finalTop + height);

            FenLogger.Debug($"[AbsoluteLayout] {element.Tag}: {rect} in {containingBlock}", LogCategory.Layout);

            return rect;
        }

        /// <summary>
        /// Find the containing block for an absolutely positioned element.
        /// </summary>
        public static SKRect FindContainingBlock(
            LiteElement element,
            ILayoutEngine engine,
            SKRect viewport)
        {
            var ctx = engine.Context;
            var parent = element.Parent;

            while (parent != null)
            {
                if (ctx.Styles != null && ctx.Styles.TryGetValue(parent, out var parentStyle))
                {
                    var pos = parentStyle.Position?.ToLowerInvariant() ?? "static";
                    
                    // Non-static positioned ancestor is the containing block
                    if (pos != "static")
                    {
                        if (ctx.Boxes.TryGetValue(parent, out var parentBox))
                        {
                            return parentBox.PaddingBox;
                        }
                    }
                }
                parent = parent.Parent;
            }

            // No positioned ancestor - use viewport
            return viewport;
        }

        /// <summary>
        /// Collect all absolutely positioned descendants.
        /// </summary>
        public static List<LiteElement> CollectAbsoluteElements(
            LiteElement root,
            Dictionary<LiteElement, CssComputed> styles)
        {
            var result = new List<LiteElement>();
            CollectRecursive(root, styles, result);
            return result;
        }

        private static void CollectRecursive(
            LiteElement node,
            Dictionary<LiteElement, CssComputed> styles,
            List<LiteElement> result)
        {
            if (node == null || node.Children == null) return;

            foreach (var child in node.Children)
            {
                if (child.IsText) continue;

                if (styles.TryGetValue(child, out var style))
                {
                    var pos = style.Position?.ToLowerInvariant() ?? "static";
                    if (pos == "absolute" || pos == "fixed")
                    {
                        result.Add(child);
                    }
                }

                CollectRecursive(child, styles, result);
            }
        }

        /// <summary>
        /// Check if element is out of normal flow.
        /// </summary>
        public static bool IsOutOfFlow(CssComputed style)
        {
            if (style == null) return false;
            var pos = style.Position?.ToLowerInvariant() ?? "static";
            return pos == "absolute" || pos == "fixed";
        }

        /// <summary>
        /// Check if element creates a new stacking context.
        /// </summary>
        public static bool CreatesStackingContext(CssComputed style)
        {
            if (style == null) return false;

            // Position with z-index
            var pos = style.Position?.ToLowerInvariant() ?? "static";
            if ((pos == "absolute" || pos == "relative" || pos == "fixed" || pos == "sticky") 
                && style.ZIndex.HasValue)
            {
                return true;
            }

            // Opacity < 1
            if (style.Opacity.HasValue && style.Opacity.Value < 1.0)
            {
                return true;
            }

            // Transform
            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none")
            {
                return true;
            }

            // Filter
            if (!string.IsNullOrEmpty(style.Filter) && style.Filter != "none")
            {
                return true;
            }

            return false;
        }

        private static float CalculateOffset(double? pixelValue, double? percentValue, float containerSize, float containerOffset)
        {
            if (pixelValue.HasValue)
                return (float)pixelValue.Value;
            if (percentValue.HasValue)
                return (float)(percentValue.Value / 100.0 * containerSize);
            return 0;
        }

        private static float CalculateDimension(double? pixelValue, double? percentValue, float containerSize)
        {
            if (pixelValue.HasValue)
                return (float)pixelValue.Value;
            if (percentValue.HasValue)
                return (float)(percentValue.Value / 100.0 * containerSize);
            return 0;
        }
    }
}
