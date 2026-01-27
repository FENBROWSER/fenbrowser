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
            box.Geometry.ContentBox = new SKRect(left, top, left + contentW, top + contentH);
            
            box.Geometry.PaddingBox = new SKRect(
                (float)-p.Left, (float)-p.Top, 
                contentW + (float)p.Right, contentH + (float)p.Bottom
            );
            
            box.Geometry.BorderBox = new SKRect(
                (float)(-p.Left - b.Left), (float)(-p.Top - b.Top),
                contentW + (float)(p.Right + b.Right), contentH + (float)(p.Bottom + b.Bottom)
            );
            
            box.Geometry.MarginBox = new SKRect(
                (float)(-p.Left - b.Left - m.Left), (float)(-p.Top - b.Top - m.Top),
                contentW + (float)(p.Right + b.Right + m.Right), contentH + (float)(p.Bottom + b.Bottom + m.Bottom)
            );
        }

        public static void SetPosition(LayoutBox box, float x, float y)
        {
             // Align MarginBox TopLeft to x,y
             float dx = x - box.Geometry.MarginBox.Left;
             float dy = y - box.Geometry.MarginBox.Top;
             
             ShiftBoxModel(box.Geometry, dx, dy);
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
    }
}
