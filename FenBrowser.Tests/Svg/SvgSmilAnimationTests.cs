using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgSmilAnimationTests
    {
        private static SvgRenderResult RenderAt(string svg, double seconds) =>
            new FenSvgRenderer().Render(new SvgRenderRequest(svg, SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = seconds
            });

        [Theory]
        [InlineData(2.9, "red")]
        [InlineData(3.0, "green")]
        [InlineData(3.25, "green")]
        [InlineData(3.5, "red")]
        public void Set_AppliesOnlyDuringItsActiveInterval(double seconds, string expected)
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='red'>" +
                "<set attributeName='fill' to='green' begin='3s' dur='.5s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, seconds);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(expected == "green" ? SKColors.Green : SKColors.Red,
                result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void Set_FreezeRetainsValueAfterActiveInterval()
        {
            const string svg =
                "<svg width='20' height='20'><rect width='20' height='20' fill='red'>" +
                "<set attributeName='fill' to='green' begin='1s' dur='1s' fill='freeze'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 10d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void Animate_InterpolatesNumericGeometryAtDocumentTime()
        {
            const string svg =
                "<svg width='30' height='10'><rect x='0' width='5' height='10' fill='green'>" +
                "<animate attributeName='x' from='0' to='20' begin='1s' dur='2s' fill='freeze'/>" +
                "</rect></svg>";

            using var middle = RenderAt(svg, 2d);
            using var frozen = RenderAt(svg, 4d);

            Assert.True(middle.Success, middle.ErrorMessage);
            Assert.True(frozen.Success, frozen.ErrorMessage);
            Assert.False(middle.RequiresFallback, string.Join("; ", middle.Warnings));
            Assert.False(frozen.RequiresFallback, string.Join("; ", frozen.Warnings));
            Assert.Equal(SKColors.Green, middle.Bitmap.GetPixel(12, 5));
            Assert.Equal(0, middle.Bitmap.GetPixel(2, 5).Alpha);
            Assert.Equal(SKColors.Green, frozen.Bitmap.GetPixel(22, 5));
        }

        [Fact]
        public void AnimateColor_InterpolatesPresentationColor()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='10' height='10'>" +
                "<animateColor attributeName='fill' from='red' to='blue' dur='2s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 1d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor center = result.Bitmap.GetPixel(5, 5);
            Assert.InRange(center.Red, (byte)127, (byte)128);
            Assert.Equal((byte)0, center.Green);
            Assert.InRange(center.Blue, (byte)127, (byte)128);
        }

        [Fact]
        public void Animate_RemoveRestoresUnderlyingValueAfterInterval()
        {
            const string svg =
                "<svg width='30' height='10'><rect x='0' width='5' height='10' fill='green'>" +
                "<animate attributeName='x' from='0' to='20' dur='1s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 2d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(2, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(22, 5).Alpha);
        }

        [Fact]
        public void Animate_UnsupportedSplineFailsClosed()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='5' height='10'>" +
                "<animate attributeName='width' from='0' to='10' dur='1s' " +
                "calcMode='spline' keySplines='0 0 1 1'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, .5d);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("animate", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Animate_ValuesAndKeyTimesSelectCurrentSegment()
        {
            const string svg =
                "<svg width='30' height='10'><rect width='5' height='10' fill='green'>" +
                "<animate attributeName='x' values='0;20;10' keyTimes='0;.5;1' dur='4s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 3d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(17, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(2, 5).Alpha);
        }

        [Fact]
        public void Animate_RepeatCountSamplesCurrentIterationAndFrozenEnd()
        {
            const string svg =
                "<svg width='20' height='10'><rect width='4' height='10' fill='green'>" +
                "<animate attributeName='x' from='0' to='10' dur='1s' " +
                "repeatCount='3' fill='freeze'/>" +
                "</rect></svg>";

            using var repeated = RenderAt(svg, 2.5d);
            using var frozen = RenderAt(svg, 4d);

            Assert.True(repeated.Success, repeated.ErrorMessage);
            Assert.True(frozen.Success, frozen.ErrorMessage);
            Assert.False(repeated.RequiresFallback, string.Join("; ", repeated.Warnings));
            Assert.False(frozen.RequiresFallback, string.Join("; ", frozen.Warnings));
            Assert.Equal(SKColors.Green, repeated.Bitmap.GetPixel(6, 5));
            Assert.Equal(SKColors.Green, frozen.Bitmap.GetPixel(11, 5));
        }

        [Fact]
        public void Animate_DiscreteValuesHoldCurrentValue()
        {
            const string svg =
                "<svg width='10' height='10'><rect width='10' height='10' fill='red'>" +
                "<animate attributeName='fill' values='red;green;blue' " +
                "calcMode='discrete' dur='3s'/>" +
                "</rect></svg>";

            using var result = RenderAt(svg, 1.5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void Animate_LocalHrefTargetsReferencedElement()
        {
            const string svg =
                "<svg width='20' height='10'><rect id='target' width='4' height='10' fill='green'/>" +
                "<animate href='#target' attributeName='x' from='0' to='10' dur='2s'/>" +
                "</svg>";

            using var result = RenderAt(svg, 1d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(6, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(1, 5).Alpha);
        }

        [Fact]
        public void Set_LocalHrefTargetReceivesTheAnimatedValue()
        {
            const string svg =
                "<svg width='20' height='10'><rect id='target' width='10' height='10' fill='green'/>" +
                "<set href='#target' attributeName='fill' to='red' begin='0s' dur='1s'/></svg>";

            using var result = RenderAt(svg, .5d);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1d)]
        public void InvalidDocumentTime_IsRejected(double seconds)
        {
            using var result = RenderAt("<svg width='1' height='1'/>", seconds);
            Assert.False(result.Success);
        }

        [Fact]
        public void FirstPartyRenderer_UsesNonZeroDocumentTime()
        {
            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
                "<svg width='1' height='1'/>", SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = 1d
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(SvgRenderResult.IsAdmissible(result));
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
