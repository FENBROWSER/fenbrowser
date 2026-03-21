using System;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout.Contexts // Namespace matching usage
{
    public static class LayoutBoxOps
    {
        public static void ComputeBoxModelFromContent(LayoutBox box, float contentW, float contentH)
        {
            var p = box.Geometry.Padding;
            var b = box.Geometry.Border;
            var m = box.Geometry.Margin;
            
            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            float right = left + contentW;
            float bottom = top + contentH;

            box.Geometry.ContentBox = new SKRect(left, top, right, bottom);
            
            box.Geometry.PaddingBox = new SKRect(
                left - (float)p.Left,
                top - (float)p.Top,
                right + (float)p.Right,
                bottom + (float)p.Bottom
            );
            
            box.Geometry.BorderBox = new SKRect(
                left - (float)(p.Left + b.Left),
                top - (float)(p.Top + b.Top),
                right + (float)(p.Right + b.Right),
                bottom + (float)(p.Bottom + b.Bottom)
            );
            
            box.Geometry.MarginBox = new SKRect(
                left - (float)(p.Left + b.Left + m.Left),
                top - (float)(p.Top + b.Top + m.Top),
                right + (float)(p.Right + b.Right + m.Right),
                bottom + (float)(p.Bottom + b.Bottom + m.Bottom)
            );
        }

        public static void SetPosition(LayoutBox box, float x, float y)
        {
             // Align MarginBox TopLeft to x,y
             float dx = x - box.Geometry.MarginBox.Left;
             float dy = y - box.Geometry.MarginBox.Top;
             
             ShiftBoxModel(box.Geometry, dx, dy);
        }

        public static void ResetSubtreeToOrigin(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            SetPosition(box, 0f, 0f);

            foreach (var child in box.Children)
            {
                ResetSubtreeToOrigin(child);
            }
        }

        public static void ShiftSubtree(LayoutBox box, float dx, float dy)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            ShiftBoxModel(box.Geometry, dx, dy);

            foreach (var child in box.Children)
            {
                ShiftSubtree(child, dx, dy);
            }
        }

        public static void PositionSubtree(LayoutBox box, float x, float y, LayoutState state)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            var relativeOffset = ResolveRelativeOffset(box.ComputedStyle, state);
            float targetX = x + relativeOffset.X;
            float targetY = y + relativeOffset.Y;
            float dx = targetX - box.Geometry.MarginBox.Left;
            float dy = targetY - box.Geometry.MarginBox.Top;
            ShiftSubtree(box, dx, dy);
        }

        public static void ShiftBoxModel(BoxModel model, float dx, float dy)
        {
            model.ContentBox = OffsetRect(model.ContentBox, dx, dy);
            model.PaddingBox = OffsetRect(model.PaddingBox, dx, dy);
            model.BorderBox = OffsetRect(model.BorderBox, dx, dy);
            model.MarginBox = OffsetRect(model.MarginBox, dx, dy);
        }
        
        private static SKRect OffsetRect(SKRect r, float dx, float dy)
        {
            return new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
        }

        private static SKPoint ResolveRelativeOffset(CssComputed style, LayoutState state)
        {
            if (style == null || !string.Equals(style.Position, "relative", StringComparison.OrdinalIgnoreCase))
            {
                return SKPoint.Empty;
            }

            float cbWidth = state.ContainingBlockWidth;
            if (!float.IsFinite(cbWidth) || cbWidth <= 0f)
            {
                cbWidth = state.AvailableSize.Width;
            }
            if (!float.IsFinite(cbWidth) || cbWidth <= 0f)
            {
                cbWidth = state.ViewportWidth;
            }

            float cbHeight = state.ContainingBlockHeight;
            if (!float.IsFinite(cbHeight) || cbHeight <= 0f)
            {
                cbHeight = state.AvailableSize.Height;
            }
            if (!float.IsFinite(cbHeight) || cbHeight <= 0f)
            {
                cbHeight = state.ViewportHeight;
            }

            float dx = 0f;
            if (style.Left.HasValue)
            {
                dx = (float)style.Left.Value;
            }
            else if (style.LeftPercent.HasValue && cbWidth > 0f)
            {
                dx = (float)(style.LeftPercent.Value / 100.0 * cbWidth);
            }
            else if (style.Right.HasValue)
            {
                dx = -(float)style.Right.Value;
            }
            else if (style.RightPercent.HasValue && cbWidth > 0f)
            {
                dx = -(float)(style.RightPercent.Value / 100.0 * cbWidth);
            }

            float dy = 0f;
            if (style.Top.HasValue)
            {
                dy = (float)style.Top.Value;
            }
            else if (style.TopPercent.HasValue && cbHeight > 0f)
            {
                dy = (float)(style.TopPercent.Value / 100.0 * cbHeight);
            }
            else if (style.Bottom.HasValue)
            {
                dy = -(float)style.Bottom.Value;
            }
            else if (style.BottomPercent.HasValue && cbHeight > 0f)
            {
                dy = -(float)(style.BottomPercent.Value / 100.0 * cbHeight);
            }

            return new SKPoint(dx, dy);
        }
    }
}
