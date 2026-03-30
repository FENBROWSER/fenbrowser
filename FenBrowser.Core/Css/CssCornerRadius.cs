using System;

namespace FenBrowser.Core.Css
{
    public struct CssLength : IEquatable<CssLength>
    {
        public float Value;
        public bool IsPercent;

        public static CssLength Zero => default;

        public CssLength(float value, bool isPercent = false)
        {
            Value = value;
            IsPercent = isPercent;
        }

        public static implicit operator CssLength(float value) => new CssLength(value);

        public bool IsZero => Value == 0f;

        public bool HasNegative => Value < 0f;

        public CssLength ClampNonNegative()
        {
            return HasNegative ? new CssLength(0f, IsPercent) : this;
        }

        public bool Equals(CssLength other) => Value == other.Value && IsPercent == other.IsPercent;

        public override bool Equals(object obj) => obj is CssLength other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Value, IsPercent);

        public static bool operator ==(CssLength left, CssLength right) => left.Equals(right);

        public static bool operator !=(CssLength left, CssLength right) => !left.Equals(right);

        public override string ToString() => IsPercent ? $"{Value}%" : $"{Value}px";
    }

    public struct CssCornerRadius : IEquatable<CssCornerRadius>
    {
        public CssLength TopLeft { get; set; }
        public CssLength TopRight { get; set; }
        public CssLength BottomRight { get; set; }
        public CssLength BottomLeft { get; set; }

        public static CssCornerRadius Empty => default;

        public CssCornerRadius(CssLength uniform)
        {
            TopLeft = TopRight = BottomRight = BottomLeft = uniform;
        }

        public CssCornerRadius(CssLength tl, CssLength tr, CssLength br, CssLength bl)
        {
            TopLeft = tl;
            TopRight = tr;
            BottomRight = br;
            BottomLeft = bl;
        }

        public bool IsUniform => TopLeft.Equals(TopRight) && TopRight.Equals(BottomRight) && BottomRight.Equals(BottomLeft);

        public bool IsZero => TopLeft.IsZero && TopRight.IsZero && BottomRight.IsZero && BottomLeft.IsZero;

        public bool HasNegative => TopLeft.HasNegative || TopRight.HasNegative || BottomRight.HasNegative || BottomLeft.HasNegative;

        public bool HasPercent => TopLeft.IsPercent || TopRight.IsPercent || BottomRight.IsPercent || BottomLeft.IsPercent;

        public CssCornerRadius ClampNonNegative()
        {
            return new CssCornerRadius(
                TopLeft.ClampNonNegative(),
                TopRight.ClampNonNegative(),
                BottomRight.ClampNonNegative(),
                BottomLeft.ClampNonNegative());
        }

        public bool Equals(CssCornerRadius other)
        {
            return TopLeft.Equals(other.TopLeft) &&
                   TopRight.Equals(other.TopRight) &&
                   BottomRight.Equals(other.BottomRight) &&
                   BottomLeft.Equals(other.BottomLeft);
        }

        public override bool Equals(object obj) => obj is CssCornerRadius other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(TopLeft, TopRight, BottomRight, BottomLeft);

        public static bool operator ==(CssCornerRadius left, CssCornerRadius right) => left.Equals(right);

        public static bool operator !=(CssCornerRadius left, CssCornerRadius right) => !left.Equals(right);

        public override string ToString()
        {
            return $"{TopLeft},{TopRight},{BottomRight},{BottomLeft}";
        }
    }
}
