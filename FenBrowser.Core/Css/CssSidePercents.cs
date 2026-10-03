using System;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// Percentage values of the four physical sides of a margin or padding.
    /// CSS 2.1 §8.3 / §8.4 (and CSS Box 3 §padding): a percentage margin or padding
    /// refers to the logical width of the containing block - for the vertical sides
    /// too - so it cannot be turned into pixels at cascade time. A side left null
    /// takes its length from the matching <see cref="Thickness"/> side.
    /// </summary>
    public readonly struct CssSidePercents : IEquatable<CssSidePercents>
    {
        public CssSidePercents(double? left, double? top, double? right, double? bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public double? Left { get; }
        public double? Top { get; }
        public double? Right { get; }
        public double? Bottom { get; }

        public bool IsEmpty => !Left.HasValue && !Top.HasValue && !Right.HasValue && !Bottom.HasValue;

        public bool HasPositiveTop => Top > 0;
        public bool HasPositiveBottom => Bottom > 0;

        /// <summary>
        /// The used sides: each percentage side resolved against
        /// <paramref name="containingBlockWidth"/>, every other side taken from
        /// <paramref name="lengths"/>. An indefinite basis (the min/max-content case of
        /// CSS Sizing 3 §5.2.1) resolves percentages to zero.
        /// </summary>
        public Thickness Resolve(Thickness lengths, double containingBlockWidth)
        {
            if (IsEmpty)
            {
                return lengths;
            }

            double basis = double.IsFinite(containingBlockWidth) && containingBlockWidth > 0
                ? containingBlockWidth
                : 0;
            return new Thickness(
                Left.HasValue ? Left.Value * basis / 100.0 : lengths.Left,
                Top.HasValue ? Top.Value * basis / 100.0 : lengths.Top,
                Right.HasValue ? Right.Value * basis / 100.0 : lengths.Right,
                Bottom.HasValue ? Bottom.Value * basis / 100.0 : lengths.Bottom);
        }

        public bool Equals(CssSidePercents other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override bool Equals(object obj) => obj is CssSidePercents other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

        public static bool operator ==(CssSidePercents a, CssSidePercents b) => a.Equals(b);

        public static bool operator !=(CssSidePercents a, CssSidePercents b) => !a.Equals(b);
    }
}
