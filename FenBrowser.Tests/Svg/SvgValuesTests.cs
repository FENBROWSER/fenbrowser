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

        [Theory]
        [InlineData("#ggg")]
        [InlineData("#12x4")]
        [InlineData("#abcdex")]
        public void Colors_InvalidHexDigitsAreRejected(string value)
        {
            Assert.False(SvgValues.TryParseColor(value.AsSpan(), out _));
        }

        [Fact]
        public void Colors_RgbaNumericAlphaUsesUnitInterval()
        {
            Assert.True(SvgValues.TryParseColor("rgba(255,0,0,0.5)".AsSpan(), out var color));
            Assert.Equal((byte)255, color.Red);
            Assert.InRange(color.Alpha, (byte)127, (byte)128);
        }

        [Theory]
        [InlineData("hsl(0,100%,50%)", 255, 0, 0, 255)]
        [InlineData("hsl(120,100%,50%)", 0, 255, 0, 255)]
        [InlineData("hsl(-120,100%,50%)", 0, 0, 255, 255)]
        [InlineData("hsla(240,100%,50%,0.5)", 0, 0, 255, 128)]
        public void Colors_HslFunctionsResolve(
            string value,
            byte red,
            byte green,
            byte blue,
            byte alpha)
        {
            Assert.True(SvgValues.TryParseColor(value.AsSpan(), out var color));
            Assert.Equal(red, color.Red);
            Assert.Equal(green, color.Green);
            Assert.Equal(blue, color.Blue);
            Assert.Equal(alpha, color.Alpha);
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
        public void LocalReference_TrimsSurroundingWhitespaceOnly()
        {
            Assert.True(SvgValues.TryParseLocalReference("  #green \t", out var fragment));
            Assert.Equal("green", fragment);
            Assert.True(SvgValues.TryParseLocalReference(" # red ", out fragment));
            Assert.Equal(" red", fragment);
            Assert.False(SvgValues.TryParseLocalReference("green", out _));
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

        [Theory]
        [InlineData("rotate(0)", 1f, 0f, 0f, 0f, 1f, 0f)]
        [InlineData("rotate(90)", 0f, -1f, 0f, 1f, 0f, 0f)]
        [InlineData("rotate(180)", -1f, 0f, 0f, 0f, -1f, 0f)]
        [InlineData("rotate(270)", 0f, 1f, 0f, -1f, 0f, 0f)]
        [InlineData("rotate(-90)", 0f, 1f, 0f, -1f, 0f, 0f)]
        [InlineData("rotate(360)", 1f, 0f, 0f, 0f, 1f, 0f)]
        public void Transform_RotateQuarterTurn_HasExactCosAndSin(
            string input, float sx, float kx, float tx, float ky, float sy, float ty)
        {
            Assert.True(SvgValues.TryParseTransformList(input.AsSpan(), out var m));
            Assert.Equal(sx, m.ScaleX);
            Assert.Equal(kx, m.SkewX);
            Assert.Equal(tx, m.TransX);
            Assert.Equal(ky, m.SkewY);
            Assert.Equal(sy, m.ScaleY);
            Assert.Equal(ty, m.TransY);
        }

        [Theory]
        [InlineData("rotate(0 170 150)", 1f, 0f, 0f, 0f, 1f, 0f)]
        [InlineData("rotate(90 170 150)", 0f, -1f, 320f, 1f, 0f, -20f)]
        [InlineData("rotate(180 170 150)", -1f, 0f, 340f, 0f, -1f, 300f)]
        [InlineData("rotate(270 170 150)", 0f, 1f, 20f, -1f, 0f, 320f)]
        [InlineData("rotate(-90 170 150)", 0f, 1f, 20f, -1f, 0f, 320f)]
        [InlineData("rotate(90 0 0)", 0f, -1f, 0f, 1f, 0f, 0f)]
        public void Transform_RotateQuarterTurnAroundPivot_HasExactCosAndSin(
            string input, float sx, float kx, float tx, float ky, float sy, float ty)
        {
            Assert.True(SvgValues.TryParseTransformList(input.AsSpan(), out var m));
            Assert.Equal(sx, m.ScaleX);
            Assert.Equal(kx, m.SkewX);
            Assert.Equal(tx, m.TransX);
            Assert.Equal(ky, m.SkewY);
            Assert.Equal(sy, m.ScaleY);
            Assert.Equal(ty, m.TransY);
        }

        [Fact]
        public void Transform_RotateNonQuarterTurn_KeepsFullPrecision()
        {
            Assert.True(SvgValues.TryParseTransformList("rotate(45)".AsSpan(), out var m));
            Assert.Equal(0.70710677f, m.ScaleX);
            Assert.Equal(-0.70710677f, m.SkewX);
            Assert.Equal(0.70710677f, m.SkewY);
            Assert.Equal(0.70710677f, m.ScaleY);
            Assert.Equal(0f, m.TransX);
            Assert.Equal(0f, m.TransY);
        }

        [Fact]
        public void Transform_RotateAttributeQuarterTurn_RendersIdenticalToEquivalentMatrix()
        {
            const string head = "<svg width='400' height='300' viewBox='0 0 400 300'>" +
                "<path d='M 170 -30 l -120 240 l 240 0 Z' fill='black' ";
            using var rotated = RenderRaw(
                head + "transform-origin='170 150' transform='rotate(90)'/></svg>");
            using var matrix = RenderRaw(
                head + "transform='matrix(0 1 -1 0 320 -20)'/></svg>");
            Assert.False(rotated.RequiresFallback);
            Assert.False(matrix.RequiresFallback);
            AssertIdenticalPixels(matrix, rotated);
        }

        [Fact]
        public void Length_UnitsResolve()
        {
            Assert.True(SvgValues.TryParseLength("1in".AsSpan(), out float inch, out var uInch));
            Assert.Equal(96f, SvgValues.ResolveUnits(inch, uInch, 16f, 100f), 2);

            Assert.True(SvgValues.TryParseLength("50%".AsSpan(), out float pct, out var uPct));
            Assert.Equal(16f, SvgValues.ResolveUnits(pct, uPct, 16f, 32f), 2);
        }

        [Theory]
        [InlineData("2ch", 2f, 20f)]
        [InlineData("2CH", 2f, 20f)]
        [InlineData("0.5ch", 0.5f, 20f)]
        public void ChUnit_ParsesAndResolvesAgainstHalfTheFontSize(
            string input,
            float expectedValue,
            float fontSize)
        {
            Assert.True(SvgValues.TryParseLength(input.AsSpan(), out float value, out var unit));
            Assert.Equal(expectedValue, value, 4);
            Assert.Equal(SvgValues.SvgUnit.Ch, unit);
            Assert.Equal(expectedValue * (fontSize * 0.5f),
                SvgValues.ResolveUnits(value, unit, fontSize, 0f), 4);
        }

        [Theory]
        [InlineData("1rem")]
        [InlineData("1q")]
        [InlineData("1ic")]
        [InlineData("1cap")]
        [InlineData("1lh")]
        [InlineData("1rlh")]
        [InlineData("1cqh")]
        [InlineData("1vw")]
        [InlineData("1px2")]
        [InlineData("1c")]
        public void UnitsOutsideTheAttributeVocabulary_StayUnparseable(string input)
        {
            Assert.False(SvgValues.TryParseLength(input.AsSpan(), out _, out _));
        }

        private readonly FenSvgRenderer _renderer = new();

        private SvgRenderResult RenderRaw(string svg)
        {
            var res = _renderer.Render(svg);
            Assert.True(res.Success, res.ErrorMessage ?? "(no error)");
            return res;
        }

        private static void AssertIdenticalPixels(SvgRenderResult expected, SvgRenderResult actual)
        {
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < expected.Bitmap.Height; y++)
            for (int x = 0; x < expected.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        private static SvgValues.PaintKind ParsePaintKind(string raw)
        {
            SvgValues.TryParsePaint(raw.AsSpan(), out var kind, out _, out _, out _);
            return kind;
        }
    }
}
