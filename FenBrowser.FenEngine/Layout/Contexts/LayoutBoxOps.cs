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
             // Align the outermost resolved box top-left to x,y.
             // Some layout paths populate ContentBox/BorderBox before MarginBox is
             // synchronized; anchoring exclusively on MarginBox then shifts descendants
             // too far to the right/bottom when the outer boxes are still empty.
             var anchor = GetPositionAnchor(box.Geometry);
             float dx = x - anchor.X;
             float dy = y - anchor.Y;
             
             ShiftBoxModel(box.Geometry, dx, dy);
        }

        public static void ResetSubtreeToOrigin(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            float dx = -box.Geometry.ContentBox.Left;
            float dy = -box.Geometry.ContentBox.Top;
            ShiftBoxModel(box.Geometry, dx, dy);

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
                var childPosition = LayoutStyleResolver.GetEffectivePosition(child?.ComputedStyle);
                if (string.Equals(childPosition, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ShiftSubtree(child, dx, dy);
            }
        }

        public static void ShiftDescendants(LayoutBox box, float dx, float dy)
        {
            if (box == null)
            {
                return;
            }

            foreach (var child in box.Children)
            {
                var childPosition = LayoutStyleResolver.GetEffectivePosition(child?.ComputedStyle);
                if (string.Equals(childPosition, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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
            var anchor = GetPositionAnchor(box.Geometry);
            float dx = targetX - anchor.X;
            float dy = targetY - anchor.Y;
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

        private static SKPoint GetPositionAnchor(BoxModel model)
        {
            if (model == null)
            {
                return SKPoint.Empty;
            }

            if (HasResolvedRect(model.MarginBox))
            {
                return new SKPoint(model.MarginBox.Left, model.MarginBox.Top);
            }

            if (HasResolvedRect(model.BorderBox))
            {
                return new SKPoint(model.BorderBox.Left, model.BorderBox.Top);
            }

            if (HasResolvedRect(model.PaddingBox))
            {
                return new SKPoint(model.PaddingBox.Left, model.PaddingBox.Top);
            }

            return new SKPoint(model.ContentBox.Left, model.ContentBox.Top);
        }

        private static bool HasResolvedRect(SKRect rect)
        {
            return float.IsFinite(rect.Left) &&
                   float.IsFinite(rect.Top) &&
                   float.IsFinite(rect.Right) &&
                   float.IsFinite(rect.Bottom) &&
                   (Math.Abs(rect.Left) > 0.01f ||
                    Math.Abs(rect.Top) > 0.01f ||
                    Math.Abs(rect.Right) > 0.01f ||
                    Math.Abs(rect.Bottom) > 0.01f);
        }

        private static SKPoint ResolveRelativeOffset(CssComputed style, LayoutState state)
        {
            if (style == null || !string.Equals(LayoutStyleResolver.GetEffectivePosition(style), "relative", StringComparison.OrdinalIgnoreCase))
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
            if (HasExplicitInset(style, "left") && style.Left.HasValue)
            {
                dx = (float)style.Left.Value;
            }
            else if (HasExplicitInset(style, "left") && style.LeftPercent.HasValue && cbWidth > 0f)
            {
                dx = (float)(style.LeftPercent.Value / 100.0 * cbWidth);
            }
            else if (HasExplicitInset(style, "right") && style.Right.HasValue)
            {
                dx = -(float)style.Right.Value;
            }
            else if (HasExplicitInset(style, "right") && style.RightPercent.HasValue && cbWidth > 0f)
            {
                dx = -(float)(style.RightPercent.Value / 100.0 * cbWidth);
            }

            float dy = 0f;
            if (HasExplicitInset(style, "top") && style.Top.HasValue)
            {
                dy = (float)style.Top.Value;
            }
            else if (HasExplicitInset(style, "top") && style.TopPercent.HasValue && cbHeight > 0f)
            {
                dy = (float)(style.TopPercent.Value / 100.0 * cbHeight);
            }
            else if (HasExplicitInset(style, "bottom") && style.Bottom.HasValue)
            {
                dy = -(float)style.Bottom.Value;
            }
            else if (HasExplicitInset(style, "bottom") && style.BottomPercent.HasValue && cbHeight > 0f)
            {
                dy = -(float)(style.BottomPercent.Value / 100.0 * cbHeight);
            }

            return new SKPoint(dx, dy);
        }

        private static bool HasExplicitInset(CssComputed style, string side)
        {
            if (style?.Map == null || string.IsNullOrWhiteSpace(side))
            {
                return false;
            }

            if (style.Map.ContainsKey(side) || style.Map.ContainsKey("inset") || style.Map.ContainsKey($"inset-{side}"))
            {
                return true;
            }

            return side switch
            {
                "top" => style.Map.ContainsKey("inset-block-start"),
                "bottom" => style.Map.ContainsKey("inset-block-end"),
                "left" => style.Map.ContainsKey("inset-inline-start"),
                "right" => style.Map.ContainsKey("inset-inline-end"),
                _ => false
            };
        }
    }
}
