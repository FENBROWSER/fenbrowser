using System;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Value-parser coverage: strict numeric grammar, unit resolution, colors,
    /// transforms, and paint parsing including the fragment-only reference rule.
    /// </summary>
    public class SvgValuesTests
    {
        [Theory]
        [InlineData("10", 10f)]
        [InlineData("-3.5", -3.5f)]
        [InlineData("+.5", 0.5f)]
        [InlineData("1e2", 100f)]
        [InlineData("1E-2", 0.01f)]
        [InlineData(" 42 ", 42f)]
        public void Numbers_ValidGrammar_Parse(string input, float expected)
        {
            Assert.True(SvgValues.TryParseNumber(input.AsSpan(), out float v));
            Assert.Equal(expected, v, 4);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("1.2.3")]
        [InlineData("--5")]
        [InlineData("1e")]
        [InlineData("1e+")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("12px")] // units are not part of bare numbers
        [InlineData("1 2")]
        public void Numbers_InvalidGrammar_Fail(string input)
        {
            Assert.False(SvgValues.TryParseNumber(input.AsSpan(), out _));
        }

        [Fact]
        public void Number_Overflow_FailsInsteadOfInfinity()
        {
            // 1e60 overflows float; parser must fail rather than emit Infinity.
            Assert.False(SvgValues.TryParseNumber("1e60".AsSpan(), out _));
        }

        [Fact]
        public void CoordinateClamp_BoundsMagnitude()
        {
            Assert.Equal(1e9f, SvgValues.ClampCoord(1e30f));
            Assert.Equal(-1e9f, SvgValues.ClampCoord(-1e30f));
            Assert.Equal(0f, SvgValues.ClampCoord(float.NaN));
        }

        [Fact]
        public void Colors_HexForms()
        {
            Assert.True(SvgValues.TryParseColor("#f00".AsSpan(), out var c3));
            Assert.Equal((byte)255, c3.Red);
            Assert.Equal((byte)255, c3.Alpha);

            Assert.True(SvgValues.TryParseColor("#00ff0080".AsSpan(), out var c8));
            Assert.Equal((byte)0x00, c8.Red);
            Assert.Equal((byte)0x80, c8.Alpha);
        }

        [Fact]
        public void Colors_Named_AndRgb()
        {
            Assert.True(SvgValues.TryParseColor("rebeccapurple".AsSpan(), out var rp));
            Assert.Equal((byte)0x66, rp.Red);
            Assert.Equal((byte)0x99, rp.Blue);

            Assert.True(SvgValues.TryParseColor("rgb(1,2,3)".AsSpan(), out var rgb));
            Assert.Equal((byte)1, rgb.Red);
            Assert.Equal((byte)3, rgb.Blue);

            Assert.True(SvgValues.TryParseColor("rgba(255, 0, 0, 50%)".AsSpan(), out var rgba));
            Assert.InRange(rgba.Alpha, 125, 130); // deterministic half-alpha
            Assert.Equal((byte)255, rgba.Red);
        }

        [Fact]
        public void Paint_RemoteUrlRef_IsRejected_Structurally()
        {
            // SECURITY: remote references can never resolve - there is no fetch
            // path anywhere in the renderer. They parse as "paints nothing".
            var kind = ParsePaintKind("url(http://evil.example/x.png)");
            Assert.Equal(SvgValues.PaintKind.None, kind);
        }

        [Fact]
        public void Paint_RemoteUrlRef_WithFallbackColor_UsesFallback()
        {
            Assert.True(SvgValues.TryParsePaint(
                "url(nothing:bad) red".AsSpan(),
                out var kind, out var color, out _, out _));
            Assert.Equal(SvgValues.PaintKind.Color, kind);
            Assert.Equal((byte)255, color.Red);
        }

        [Fact]
        public void Paint_FragmentRef_IsHonored()
        {
            Assert.True(SvgValues.TryParsePaint(
                "url(#grad)".AsSpan(),
                out var kind, out _, out var fragment, out _));
            Assert.Equal(SvgValues.PaintKind.ServerRef, kind);
            Assert.Equal("grad", fragment);
        }

        [Fact]
        public void TransformList_MultiplyInOrder()
        {
            // translate(10,20) scale(2): point (1,1) -> scale -> (2,2) -> translate -> (12,22)
            Assert.True(SvgValues.TryParseTransformList("translate(10,20) scale(2)".AsSpan(), out var m));
            float x = 1f, y = 1f;
            float px = m.ScaleX * x + m.SkewX * y + m.TransX;
            float py = m.SkewY * x + m.ScaleY * y + m.TransY;
            Assert.Equal(12f, px, 3);
            Assert.Equal(22f, py, 3);
        }

        [Fact]
        public void TransformList_MalformedFunction_InvalidatesAll()
        {
            Assert.False(SvgValues.TryParseTransformList("translate(10) bogusFn(3)".AsSpan(), out _));
        }

        [Fact]
        public void Transform_RotateAroundPoint_MapsPivotToItself()
        {
            Assert.True(SvgValues.TryParseTransformList("rotate(90 5 5)".AsSpan(), out var m));
            float px = m.ScaleX * 5f + m.SkewX * 5f + m.TransX;
            float py = m.SkewY * 5f + m.ScaleY * 5f + m.TransY;
            Assert.Equal(5f, px, 3);
            Assert.Equal(5f, py, 3);
        }

        [Fact]
        public void Length_UnitsResolve()
        {
            Assert.True(SvgValues.TryParseLength("1in".AsSpan(), out float inch, out var uInch));
            Assert.Equal(96f, SvgValues.ResolveUnits(inch, uInch, 16f, 100f), 2);

            Assert.True(SvgValues.TryParseLength("50%".AsSpan(), out float pct, out var uPct));
            Assert.Equal(16f, SvgValues.ResolveUnits(pct, uPct, 16f, 32f), 2);
        }

        private static SvgValues.PaintKind ParsePaintKind(string raw)
        {
            SvgValues.TryParsePaint(raw.AsSpan(), out var kind, out _, out _, out _);
            return kind;
        }
    }
}
