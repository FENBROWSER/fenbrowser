using System;

namespace FenBrowser.Core.Css
{
    public struct CssLength : IEquatable<CssLength>
    {
        public float Value;
        public bool IsPercent;

        public CssLength(float value, bool isPercent = false)
        {
            Value = value;
            IsPercent = isPercent;
        }

        public static implicit operator CssLength(float value) => new CssLength(value);
        
        public bool Equals(CssLength other) => Value == other.Value && IsPercent == other.IsPercent;
    }

    public struct CssCornerRadius : IEquatable<CssCornerRadius>
    {
        public CssLength TopLeft { get; set; }
        public CssLength TopRight { get; set; }
        public CssLength BottomRight { get; set; }
        public CssLength BottomLeft { get; set; }

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

        public bool Equals(CssCornerRadius other)
        {
            return TopLeft.Equals(other.TopLeft) &&
                   TopRight.Equals(other.TopRight) &&
                   BottomRight.Equals(other.BottomRight) &&
                   BottomLeft.Equals(other.BottomLeft);
        }
    }
}
