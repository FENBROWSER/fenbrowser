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
        public void LegacyRenderer_RejectsNonZeroDocumentTime()
        {
            using var result = new SvgSkiaRenderer().Render(new SvgRenderRequest(
                "<svg width='1' height='1'/>", SvgRenderLimits.Default)
            {
                DocumentTimeSeconds = 1d
            });

            Assert.False(result.Success);
        }
    }
}
