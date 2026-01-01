using System;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    internal static class RectExtensions
    {
        public static SKRect WithX(this SKRect r, float x)
        {
            return new SKRect(x, r.Top, x + r.Width, r.Bottom); // SKRect(left, top, right, bottom)
        }

        public static SKRect WithY(this SKRect r, float y)
        {
            return new SKRect(r.Left, y, r.Right, y + r.Height);
        }

        public static SKRect WithWidth(this SKRect r, float w)
        {
            return new SKRect(r.Left, r.Top, r.Left + w, r.Bottom);
        }

        public static SKRect WithHeight(this SKRect r, float h)
        {
            return new SKRect(r.Left, r.Top, r.Right, r.Top + h);
        }

        public static bool Intersects(this SKRect r, SKRect other)
        {
            return r.IntersectsWith(other);
        }
    }
}
