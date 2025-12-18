using System;

namespace FenBrowser.Core
{
    public struct CornerRadius : IEquatable<CornerRadius>
    {
        public double TopLeft { get; set; }
        public double TopRight { get; set; }
        public double BottomRight { get; set; }
        public double BottomLeft { get; set; }

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

        public bool Equals(CornerRadius other)
        {
            return TopLeft == other.TopLeft &&
                   TopRight == other.TopRight &&
                   BottomRight == other.BottomRight &&
                   BottomLeft == other.BottomLeft;
        }

        public override string ToString()
        {
            return $"{TopLeft},{TopRight},{BottomRight},{BottomLeft}";
        }
    }
}
