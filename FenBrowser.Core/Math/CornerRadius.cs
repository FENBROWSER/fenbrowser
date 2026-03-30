using System;

namespace FenBrowser.Core
{
    public struct CornerRadius : IEquatable<CornerRadius>
    {
        public double TopLeft { get; set; }
        public double TopRight { get; set; }
        public double BottomRight { get; set; }
        public double BottomLeft { get; set; }

        public static CornerRadius Empty => default;

        public CornerRadius(double uniformRadius)
        {
            TopLeft = TopRight = BottomRight = BottomLeft = uniformRadius;
        }

        public CornerRadius(double topLeft, double topRight, double bottomRight, double bottomLeft)
        {
            TopLeft = topLeft;
            TopRight = topRight;
            BottomRight = bottomRight;
            BottomLeft = bottomLeft;
        }

        public bool IsUniform => TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft;

        public bool IsZero => TopLeft == 0d && TopRight == 0d && BottomRight == 0d && BottomLeft == 0d;

        public bool HasNegative => TopLeft < 0d || TopRight < 0d || BottomRight < 0d || BottomLeft < 0d;

        public CornerRadius ClampNonNegative()
        {
            return new CornerRadius(
                System.Math.Max(0d, TopLeft),
                System.Math.Max(0d, TopRight),
                System.Math.Max(0d, BottomRight),
                System.Math.Max(0d, BottomLeft));
        }

        public bool Equals(CornerRadius other)
        {
            return TopLeft == other.TopLeft &&
                   TopRight == other.TopRight &&
                   BottomRight == other.BottomRight &&
                   BottomLeft == other.BottomLeft;
        }

        public override bool Equals(object obj)
        {
            return obj is CornerRadius other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(TopLeft, TopRight, BottomRight, BottomLeft);
        }

        public static bool operator ==(CornerRadius left, CornerRadius right) => left.Equals(right);

        public static bool operator !=(CornerRadius left, CornerRadius right) => !left.Equals(right);

        public override string ToString()
        {
            return $"{TopLeft},{TopRight},{BottomRight},{BottomLeft}";
        }
    }
}
