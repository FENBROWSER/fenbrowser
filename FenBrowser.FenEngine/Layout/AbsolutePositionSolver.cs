// =============================================================================
// AbsolutePositionSolver.cs
// CSS 2.1 Absolute Positioning - The Equation of 7 Variables
// CSS Anchor Positioning Level 1 - anchor() / anchor-size() resolution
// 
// SPEC REFERENCE: CSS 2.1 §10.3, §10.6 - Width and Height of absolutely positioned elements
//                 https://www.w3.org/TR/CSS21/visudet.html#abs-non-replaced-width
//                 https://www.w3.org/TR/CSS21/visudet.html#abs-non-replaced-height
// 
// THE EQUATION (horizontal):
//   left + margin-left + border-left + padding-left + width + 
//   padding-right + border-right + margin-right + right = containing block width
// 
// THE EQUATION (vertical):
//   top + margin-top + border-top + padding-top + height + 
//   padding-bottom + border-bottom + margin-bottom + bottom = containing block height
// 
// STATUS: ✅ Fully Implemented
// =============================================================================

using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Represents resolved position and size for an absolutely positioned element.
    /// </summary>
    public struct AbsoluteLayoutResult
    {
        /// <summary>Resolved X position (relative to containing block).</summary>
        public float X;

        /// <summary>Resolved Y position (relative to containing block).</summary>
        public float Y;

        /// <summary>Resolved width (content box).</summary>
        public float Width;

        /// <summary>Resolved height (content box).</summary>
        public float Height;

        /// <summary>Resolved margin-left.</summary>
        public float MarginLeft;

        /// <summary>Resolved margin-right.</summary>
        public float MarginRight;

        /// <summary>Resolved margin-top.</summary>
        public float MarginTop;

        /// <summary>Resolved margin-bottom.</summary>
        public float MarginBottom;

        /// <summary>True if width was auto-resolved.</summary>
        public bool WidthWasAuto;

        /// <summary>True if height was auto-resolved.</summary>
        public bool HeightWasAuto;

        public override string ToString()
        {
            return $"AbsPos[{X},{Y} {Width}x{Height} m=({MarginLeft},{MarginTop},{MarginRight},{MarginBottom})]";
        }
    }

    /// <summary>
    /// Solves the CSS 2.1 "equation of 7 variables" for absolutely positioned elements.
    /// Plus CSS Anchor Positioning Level 1 anchor() and anchor-size() resolution.
    /// </summary>
    public static class AbsolutePositionSolver
    {
        /// <summary>
        /// Resolve anchor() expressions in inset/size styles given a resolved anchor box.
        /// Returns updated (left, top, right, bottom, width, height) absolute values.
        /// Anchor values are relative to the containing block's origin (the anchor box is in absolute coords).
        /// </summary>
        public static void ResolveAnchorOffsets(
            CssComputed style,
            SKRect anchorBox,
            ContainingBlock containingBlock,
            ref float? anchorLeft,
            ref float? anchorTop,
            ref float? anchorRight,
            ref float? anchorBottom,
            ref float? anchorWidth,
            ref float? anchorHeight)
        {
            string defaultAnchor = style.PositionAnchor;

            if (LayoutStyleResolver.IsAnchorFunction(style.LeftAnchorExpression))
            {
                var (name, side) = LayoutStyleResolver.ParseAnchorExpression(style.LeftAnchorExpression, defaultAnchor);
                if (name != null)
                {
                    float anchorVal = LayoutStyleResolver.ResolveAnchorSide(anchorBox, side);
                    anchorLeft = anchorVal - containingBlock.X;
                }
            }

            if (LayoutStyleResolver.IsAnchorFunction(style.RightAnchorExpression))
            {
                var (name, side) = LayoutStyleResolver.ParseAnchorExpression(style.RightAnchorExpression, defaultAnchor);
                if (name != null)
                {
                    float anchorVal = LayoutStyleResolver.ResolveAnchorSide(anchorBox, side);
                    anchorRight = (containingBlock.X + containingBlock.Width) - anchorVal;
                }
            }

            if (LayoutStyleResolver.IsAnchorFunction(style.TopAnchorExpression))
            {
                var (name, side) = LayoutStyleResolver.ParseAnchorExpression(style.TopAnchorExpression, defaultAnchor);
                if (name != null)
                {
                    float anchorVal = LayoutStyleResolver.ResolveAnchorSideVertical(anchorBox, side);
                    anchorTop = anchorVal - containingBlock.Y;
                }
            }

            if (LayoutStyleResolver.IsAnchorFunction(style.BottomAnchorExpression))
            {
                var (name, side) = LayoutStyleResolver.ParseAnchorExpression(style.BottomAnchorExpression, defaultAnchor);
                if (name != null)
                {
                    float anchorVal = LayoutStyleResolver.ResolveAnchorSideVertical(anchorBox, side);
                    anchorBottom = (containingBlock.Y + containingBlock.Height) - anchorVal;
                }
            }

            if (LayoutStyleResolver.IsAnchorSizeFunction(style.WidthAnchorExpression))
            {
                anchorWidth = anchorBox.Width;
            }

            if (LayoutStyleResolver.IsAnchorSizeFunction(style.HeightAnchorExpression))
            {
                anchorHeight = anchorBox.Height;
            }
        }

        /// <summary>
        /// Solve absolute positioning for an element.
        /// </summary>
        public static AbsoluteLayoutResult Solve(
            CssComputed style,
            ContainingBlock containingBlock,
            float intrinsicWidth = 0,
            float intrinsicHeight = 0,
            bool preserveIntrinsicAutoSize = false)
        {
            var result = new AbsoluteLayoutResult();

            // Solve horizontal axis
            SolveHorizontal(style, containingBlock.Width, intrinsicWidth, preserveIntrinsicAutoSize, ref result);

            // Solve vertical axis
            SolveVertical(style, containingBlock.Height, intrinsicHeight, preserveIntrinsicAutoSize, ref result);

            // Apply min/max constraints
            ApplyConstraints(style, containingBlock, ref result);

            return result;
        }

        /// <summary>
        /// Solve absolute positioning with anchor-based offset overrides.
        /// Called after anchor box has been resolved to override inset/size values.
        /// </summary>
        public static AbsoluteLayoutResult SolveWithAnchorOverrides(
            CssComputed style,
            ContainingBlock containingBlock,
            SKRect anchorBox,
            float intrinsicWidth = 0,
            float intrinsicHeight = 0,
            bool preserveIntrinsicAutoSize = false)
        {
            float? anchorLeft = null;
            float? anchorTop = null;
            float? anchorRight = null;
            float? anchorBottom = null;
            float? anchorWidth = null;
            float? anchorHeight = null;

            ResolveAnchorOffsets(style, anchorBox, containingBlock,
                ref anchorLeft, ref anchorTop, ref anchorRight, ref anchorBottom,
                ref anchorWidth, ref anchorHeight);

            var originalStyle = style;
            var overrideStyle = style.Clone();

            if (anchorLeft.HasValue) { overrideStyle.Left = anchorLeft.Value; overrideStyle.LeftPercent = null; }
            if (anchorTop.HasValue) { overrideStyle.Top = anchorTop.Value; overrideStyle.TopPercent = null; }
            if (anchorRight.HasValue) { overrideStyle.Right = anchorRight.Value; overrideStyle.RightPercent = null; }
            if (anchorBottom.HasValue) { overrideStyle.Bottom = anchorBottom.Value; overrideStyle.BottomPercent = null; }
            if (anchorWidth.HasValue) { overrideStyle.Width = anchorWidth.Value; overrideStyle.WidthPercent = null; }
            if (anchorHeight.HasValue) { overrideStyle.Height = anchorHeight.Value; overrideStyle.HeightPercent = null; }

            return Solve(overrideStyle, containingBlock, intrinsicWidth, intrinsicHeight, preserveIntrinsicAutoSize);
        }

        private static void SolveHorizontal(
            CssComputed style,
            float cbWidth,
            float intrinsicWidth,
            bool preserveIntrinsicAutoSize,
            ref AbsoluteLayoutResult result)
        {
            // Parse position values
            // 1. Try explicit pixel value
            // 2. Try percentage value
            // 3. Auto
            float? left = style.Left.HasValue ? (float)style.Left.Value : 
                          style.LeftPercent.HasValue ? (float)(style.LeftPercent.Value * cbWidth / 100f) : (float?)null;
            
            float? right = style.Right.HasValue ? (float)style.Right.Value : 
                           style.RightPercent.HasValue ? (float)(style.RightPercent.Value * cbWidth / 100f) : (float?)null;
            
            float? width = style.Width.HasValue ? (float)style.Width.Value : 
                           style.WidthPercent.HasValue ? (float)(style.WidthPercent.Value * cbWidth / 100f) : 
                           !string.IsNullOrEmpty(style.WidthExpression) ? LayoutHelper.EvaluateCssExpression(style.WidthExpression, cbWidth, (float)(CssParser.MediaViewportWidth ?? 0), (float)(CssParser.MediaViewportHeight ?? 0)) : (float?)null;
            
            // Use Thickness properties for margin, padding, border
            float? marginLeft = style.MarginLeftAuto ? null : (float?)(style.Margin.Left);
            float? marginRight = style.MarginRightAuto ? null : (float?)(style.Margin.Right);
            
            float borderLeft = (float)(style.BorderThickness.Left);
            float borderRight = (float)(style.BorderThickness.Right);
            float paddingLeft = (float)(style.Padding.Left);
            float paddingRight = (float)(style.Padding.Right);

            // Fixed parts (border + padding)
            float fixedSpace = borderLeft + paddingLeft + paddingRight + borderRight;

            // Count auto values
            int autoCount = 0;
            if (!left.HasValue) autoCount++;
            if (!right.HasValue) autoCount++;
            if (!width.HasValue) autoCount++;
            if (!marginLeft.HasValue) autoCount++;
            if (!marginRight.HasValue) autoCount++;

            // Default margins to 0 if auto
            float ml = marginLeft ?? 0;
            float mr = marginRight ?? 0;
            float l = left ?? 0;
            float r = right ?? 0;
            float w = width ?? intrinsicWidth;

            // The constraint equation: l + ml + fixedSpace + w + mr + r = cbWidth
            if (autoCount == 0)
            {
                // Over-constrained: ignore right (LTR)
                r = cbWidth - l - ml - fixedSpace - w - mr;
            }
            else if (!width.HasValue && !left.HasValue && !right.HasValue)
            {
                float availableWidth = Math.Max(0, cbWidth - fixedSpace - ml - mr);
                w = intrinsicWidth > 0
                    ? (preserveIntrinsicAutoSize ? intrinsicWidth : Math.Min(intrinsicWidth, availableWidth))
                    : availableWidth;
                l = 0;
                r = cbWidth - l - ml - fixedSpace - w - mr;
                result.WidthWasAuto = true;
            }
            else if (!width.HasValue && !left.HasValue)
            {
                float availableWidth = Math.Max(0, cbWidth - fixedSpace - ml - mr - r);
                w = intrinsicWidth > 0
                    ? (preserveIntrinsicAutoSize ? intrinsicWidth : Math.Min(intrinsicWidth, availableWidth))
                    : availableWidth;
                l = cbWidth - ml - fixedSpace - w - mr - r;
                result.WidthWasAuto = true;
            }
            else if (!width.HasValue && !right.HasValue)
            {
                float availableWidth = Math.Max(0, cbWidth - fixedSpace - l - ml - mr);
                w = intrinsicWidth > 0
                    ? (preserveIntrinsicAutoSize ? intrinsicWidth : Math.Min(intrinsicWidth, availableWidth))
                    : availableWidth;
                r = cbWidth - l - ml - fixedSpace - w - mr;
                result.WidthWasAuto = true;
            }
            else if (!left.HasValue && !right.HasValue)
            {
                l = 0;
                r = cbWidth - l - ml - fixedSpace - w - mr;
            }
            else if (!width.HasValue)
            {
                w = cbWidth - l - ml - fixedSpace - mr - r;
                result.WidthWasAuto = true;
            }
            else if (!left.HasValue)
            {
                l = cbWidth - ml - fixedSpace - w - mr - r;
            }
            else if (!right.HasValue)
            {
                r = cbWidth - l - ml - fixedSpace - w - mr;
            }
            else if (!marginLeft.HasValue && !marginRight.HasValue)
            {
                float remaining = cbWidth - l - fixedSpace - w - r;
                ml = mr = remaining / 2;
            }
            else if (!marginLeft.HasValue)
            {
                ml = cbWidth - l - fixedSpace - w - mr - r;
            }
            else if (!marginRight.HasValue)
            {
                mr = cbWidth - l - ml - fixedSpace - w - r;
            }

            w = Math.Max(0, w);

            result.X = l + ml + borderLeft + paddingLeft;
            result.Width = w;
            result.MarginLeft = ml;
            result.MarginRight = mr;
        }

        private static void SolveVertical(
            CssComputed style,
            float cbHeight,
            float intrinsicHeight,
            bool preserveIntrinsicAutoSize,
            ref AbsoluteLayoutResult result)
        {
            // Parse position values
            float? top = style.Top.HasValue ? (float)style.Top.Value : 
                         style.TopPercent.HasValue ? (float)(style.TopPercent.Value * cbHeight / 100f) : (float?)null;
                         
            float? bottom = style.Bottom.HasValue ? (float)style.Bottom.Value : 
                            style.BottomPercent.HasValue ? (float)(style.BottomPercent.Value * cbHeight / 100f) : (float?)null;
                            
            float? height = style.Height.HasValue ? (float)style.Height.Value : 
                            style.HeightPercent.HasValue ? (float)(style.HeightPercent.Value * cbHeight / 100f) : 
                            !string.IsNullOrEmpty(style.HeightExpression) ? LayoutHelper.EvaluateCssExpression(style.HeightExpression, cbHeight, (float)(CssParser.MediaViewportWidth ?? 0), (float)(CssParser.MediaViewportHeight ?? 0)) : (float?)null;
            
            // Use Thickness properties - margins are always explicit for top/bottom (no auto support in vertical)
            float marginTop = (float)(style.Margin.Top);
            float marginBottom = (float)(style.Margin.Bottom);
            
            float borderTop = (float)(style.BorderThickness.Top);
            float borderBottom = (float)(style.BorderThickness.Bottom);
            float paddingTop = (float)(style.Padding.Top);
            float paddingBottom = (float)(style.Padding.Bottom);

            float fixedSpace = borderTop + paddingTop + paddingBottom + borderBottom;

            float mt = marginTop;
            float mb = marginBottom;
            float t = top ?? 0;
            float b = bottom ?? 0;
            float h = height ?? intrinsicHeight;

            int autoCount = 0;
            if (!top.HasValue) autoCount++;
            if (!bottom.HasValue) autoCount++;
            if (!height.HasValue) autoCount++;

            if (autoCount == 0)
            {
                // All geometric properties constrained. Solve for margins if auto.
                bool mtAuto = style.MarginTopAuto;
                bool mbAuto = style.MarginBottomAuto;

                if (mtAuto && mbAuto)
                {
                    // Center vertically
                    float remaining = cbHeight - t - fixedSpace - h - b;
                    mt = mb = remaining / 2;
                }
                else if (mtAuto)
                {
                    mt = cbHeight - t - fixedSpace - h - mb - b;
                }
                else if (mbAuto)
                {
                    mb = cbHeight - t - mt - fixedSpace - h - b;
                }
                else
                {
                    // Over-constrained: ignore bottom
                    b = cbHeight - t - mt - fixedSpace - h - mb;
                }
            }
            else if (!height.HasValue && !top.HasValue && !bottom.HasValue)
            {
                h = intrinsicHeight > 0 ? intrinsicHeight : 0;
                t = 0;
                b = cbHeight - t - mt - fixedSpace - h - mb;
                result.HeightWasAuto = true;
            }
            else if (!height.HasValue && !top.HasValue)
            {
                float available = cbHeight - fixedSpace - mt - mb - b;
                h = ResolveAutoPositionedHeight(intrinsicHeight, available, preserveIntrinsicAutoSize);
                t = cbHeight - mt - fixedSpace - h - mb - b;
                result.HeightWasAuto = true;
            }
            else if (!height.HasValue && !bottom.HasValue)
            {
                float available = cbHeight - fixedSpace - t - mt - mb;
                h = ResolveAutoPositionedHeight(intrinsicHeight, available, preserveIntrinsicAutoSize);
                b = cbHeight - t - mt - fixedSpace - h - mb;
                result.HeightWasAuto = true;
            }
            else if (!top.HasValue && !bottom.HasValue)
            {
                t = 0;
                b = cbHeight - t - mt - fixedSpace - h - mb;
            }
            else if (!height.HasValue)
            {
                h = cbHeight - t - mt - fixedSpace - mb - b;
                result.HeightWasAuto = true;
            }
            else if (!top.HasValue)
            {
                t = cbHeight - mt - fixedSpace - h - mb - b;
            }
            else if (!bottom.HasValue)
            {
                b = cbHeight - t - mt - fixedSpace - h - mb;
            }

            h = Math.Max(0, h);

            result.Y = t + mt + borderTop + paddingTop;
            result.Height = h;
            result.MarginTop = mt;
            result.MarginBottom = mb;
        }

        private static float ResolveAutoPositionedHeight(
            float intrinsicHeight,
            float availableHeight,
            bool preserveIntrinsicAutoSize)
        {
            if (intrinsicHeight > 0f)
            {
                if (preserveIntrinsicAutoSize || !float.IsFinite(availableHeight) || availableHeight <= 0f)
                {
                    return intrinsicHeight;
                }

                return Math.Max(0f, Math.Min(intrinsicHeight, availableHeight));
            }

            return Math.Max(0f, availableHeight);
        }

        private static void ApplyConstraints(
            CssComputed style,
            ContainingBlock cb,
            ref AbsoluteLayoutResult result)
        {
            float minWidth = 0;
            if (style.MinWidth.HasValue) minWidth = (float)style.MinWidth.Value;
            else if (style.MinWidthExpression != null) 
                 minWidth = LayoutHelper.EvaluateCssExpression(style.MinWidthExpression, cb.Width, (float)(CssParser.MediaViewportWidth ?? 0), (float)(CssParser.MediaViewportHeight ?? 0));
                 
            float maxWidth = float.MaxValue;
            if (style.MaxWidth.HasValue) maxWidth = (float)style.MaxWidth.Value;
            else if (style.MaxWidthExpression != null)
                 maxWidth = LayoutHelper.EvaluateCssExpression(style.MaxWidthExpression, cb.Width, (float)(CssParser.MediaViewportWidth ?? 0), (float)(CssParser.MediaViewportHeight ?? 0));

            if (result.Width < minWidth)
                result.Width = minWidth;
            else if (maxWidth > 0 && result.Width > maxWidth)
                result.Width = maxWidth;

            float minHeight = 0;
            if (style.MinHeight.HasValue) minHeight = (float)style.MinHeight.Value;
            else if (style.MinHeightExpression != null)
                 minHeight = LayoutHelper.EvaluateCssExpression(style.MinHeightExpression, cb.Height, (float)(CssParser.MediaViewportWidth ?? 0), (float)(CssParser.MediaViewportHeight ?? 0));
                 
            float maxHeight = float.MaxValue;
            if (style.MaxHeight.HasValue) maxHeight = (float)style.MaxHeight.Value;
            else if (style.MaxHeightExpression != null)
                 maxHeight = LayoutHelper.EvaluateCssExpression(style.MaxHeightExpression, cb.Height, (float)(CssParser.MediaViewportWidth ?? 0), (float)(CssParser.MediaViewportHeight ?? 0));

            if (result.Height < minHeight)
                result.Height = minHeight;
            else if (maxHeight > 0 && result.Height > maxHeight)
                result.Height = maxHeight;
        }

        #region Parsing Helpers

        private static float? ParseLengthOrAuto(object value, float containerSize = 0)
        {
            if (value == null) return null;
            
            if (value is double d)
                return (float)d;
            
            var str = value.ToString().Trim().ToLowerInvariant();
            if (str == "auto" || string.IsNullOrEmpty(str)) return null;

            return ParseLengthValue(str, containerSize);
        }

        private static float? ParseLengthValue(string str, float containerSize)
        {
            if (str.EndsWith("%"))
            {
                if (float.TryParse(str.TrimEnd('%'), out var percent))
                    return containerSize * percent / 100f;
            }
            else if (str.EndsWith("px"))
            {
                if (float.TryParse(str.Substring(0, str.Length - 2), out var px))
                    return px;
            }
            else if (float.TryParse(str, out var val))
            {
                return val;
            }

            return null;
        }

        #endregion
    }
}

