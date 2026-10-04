using System;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// Percentage parts of the four physical sides of a margin or padding.
    /// CSS 2.1 §8.3 / §8.4 (and CSS Box 3 §padding): a percentage margin or padding
    /// refers to the logical width of the containing block - for the vertical sides
    /// too - so it cannot be turned into pixels at cascade time. The used side is the
    /// matching <see cref="Thickness"/> side plus this percentage of that width: a
    /// plain percentage has a zero length part, calc(100% - 20px) a -20px one.
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
        /// The used sides: each side's length from <paramref name="lengths"/> plus its
        /// percentage of <paramref name="containingBlockWidth"/>. An indefinite basis
        /// (the min/max-content case of CSS Sizing 3 §5.2.1) resolves percentages to
        /// zero.
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
                lengths.Left + (Left ?? 0) * basis / 100.0,
                lengths.Top + (Top ?? 0) * basis / 100.0,
                lengths.Right + (Right ?? 0) * basis / 100.0,
                lengths.Bottom + (Bottom ?? 0) * basis / 100.0);
        }

        public bool Equals(CssSidePercents other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override bool Equals(object obj) => obj is CssSidePercents other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

        public static bool operator ==(CssSidePercents a, CssSidePercents b) => a.Equals(b);

        public static bool operator !=(CssSidePercents a, CssSidePercents b) => !a.Equals(b);
    }
}
