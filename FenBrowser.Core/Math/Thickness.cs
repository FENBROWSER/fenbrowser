using System;

namespace FenBrowser.Core
{
    /// <summary>
    /// Describes the thickness of a frame around a rectangle.
    /// Replaces Avalonia.Thickness.
    /// </summary>
    public struct Thickness : IEquatable<Thickness>
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }

        public static Thickness Empty => default;

        public double Horizontal => Left + Right;
        public double Vertical => Top + Bottom;
        public bool IsUniform => Left == Top && Top == Right && Right == Bottom;
        public bool IsZero => Left == 0d && Top == 0d && Right == 0d && Bottom == 0d;
        public bool HasNegative => Left < 0d || Top < 0d || Right < 0d || Bottom < 0d;

        public Thickness(double uniformLength)
        {
            Left = Top = Right = Bottom = uniformLength;
        }

        public Thickness(double horizontal, double vertical)
        {
            Left = Right = horizontal;
            Top = Bottom = vertical;
        }

        public Thickness(double left, double top, double right, double bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public static bool operator ==(Thickness a, Thickness b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Thickness a, Thickness b)
        {
            return !a.Equals(b);
        }

        public override bool Equals(object obj)
        {
            return obj is Thickness thickness && Equals(thickness);
        }

        public bool Equals(Thickness other)
        {
            return Left == other.Left &&
                   Top == other.Top &&
                   Right == other.Right &&
                   Bottom == other.Bottom;
        }

        public Thickness ClampNonNegative()
        {
            return new Thickness(
                System.Math.Max(0d, Left),
                System.Math.Max(0d, Top),
                System.Math.Max(0d, Right),
                System.Math.Max(0d, Bottom));
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Left, Top, Right, Bottom);
        }

        public override string ToString()
        {
            return $"{Left},{Top},{Right},{Bottom}";
        }
    }
}
