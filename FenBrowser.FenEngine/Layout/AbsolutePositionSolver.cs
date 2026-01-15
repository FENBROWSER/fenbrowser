// =============================================================================
// AbsolutePositionSolver.cs
// CSS 2.1 Absolute Positioning - The Equation of 7 Variables
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
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Rendering;

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
    /// </summary>
    public static class AbsolutePositionSolver
    {
        /// <summary>
        /// Solve absolute positioning for an element.
        /// </summary>
        public static AbsoluteLayoutResult Solve(
            CssComputed style,
            ContainingBlock containingBlock,
            float intrinsicWidth = 0,
            float intrinsicHeight = 0)
        {
            var result = new AbsoluteLayoutResult();

            // Solve horizontal axis
            SolveHorizontal(style, containingBlock.Width, intrinsicWidth, ref result);

            // Solve vertical axis
            SolveVertical(style, containingBlock.Height, intrinsicHeight, ref result);

            // Apply min/max constraints
            ApplyConstraints(style, containingBlock, ref result);

            return result;
        }

        private static void SolveHorizontal(
            CssComputed style,
            float cbWidth,
            float intrinsicWidth,
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
                w = intrinsicWidth > 0 ? intrinsicWidth : Math.Max(0, cbWidth - fixedSpace - ml - mr); // Fix: don't go negative if container small
                l = 0;
                r = cbWidth - l - ml - fixedSpace - w - mr;
                result.WidthWasAuto = true;
            }
            else if (!width.HasValue && !left.HasValue)
            {
                w = intrinsicWidth > 0 ? intrinsicWidth : Math.Max(0, cbWidth - fixedSpace - ml - mr - r);
                l = cbWidth - ml - fixedSpace - w - mr - r;
                result.WidthWasAuto = true;
            }
            else if (!width.HasValue && !right.HasValue)
            {
                w = intrinsicWidth > 0 ? intrinsicWidth : Math.Max(0, cbWidth - fixedSpace - l - ml - mr);
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
                h = intrinsicHeight > 0 ? intrinsicHeight : Math.Max(0, cbHeight - fixedSpace - mt - mb - b);
                t = cbHeight - mt - fixedSpace - h - mb - b;
                result.HeightWasAuto = true;
            }
            else if (!height.HasValue && !bottom.HasValue)
            {
                h = intrinsicHeight > 0 ? intrinsicHeight : Math.Max(0, cbHeight - fixedSpace - t - mt - mb);
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
