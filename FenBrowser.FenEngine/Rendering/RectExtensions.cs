using System;
using Avalonia;

namespace FenBrowser.FenEngine.Rendering
{
    internal static class RectExtensions
    {
        public static Rect WithX(this Rect r, double x)
        {
            return new Rect(x, r.Y, r.Width, r.Height);
        }

        public static Rect WithY(this Rect r, double y)
        {
            return new Rect(r.X, y, r.Width, r.Height);
        }

        public static Rect WithWidth(this Rect r, double w)
        {
            return new Rect(r.X, r.Y, w, r.Height);
        }

        public static Rect WithHeight(this Rect r, double h)
        {
            return new Rect(r.X, r.Y, r.Width, h);
        }

        public static bool Intersects(this Rect r, Rect other)
        {
            return !(r.Right < other.Left || r.Left > other.Right || r.Bottom < other.Top || r.Top > other.Bottom);
        }
    }
}
