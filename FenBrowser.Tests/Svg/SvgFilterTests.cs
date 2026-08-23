using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgFilterTests
    {
        [Fact]
        public void GaussianBlur_RendersThroughFirstPartyFilterLayer()
        {
            const string svg =
                "<svg width='80' height='50'><defs><filter id='b'>" +
                "<feGaussianBlur stdDeviation='3'/></filter></defs>" +
                "<rect x='25' y='15' width='20' height='20' fill='red' filter='url(#b)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(23, 25).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(35, 25).Red > 100);
        }

        [Fact]
        public void SequentialBlurAndOffset_UsePreviousPrimitiveOutput()
        {
            const string svg =
                "<svg width='100' height='50'><defs><filter id='f'>" +
                "<feGaussianBlur stdDeviation='1'/><feOffset dx='30' dy='0'/>" +
                "</filter></defs><rect x='5' y='15' width='15' height='15' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(42, 22).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(12, 22).Alpha);
        }

        [Fact]
        public void ColorMatrix_AppliesNormalizedSvgOffsets()
        {
            const string svg =
                "<svg width='30' height='30'><defs><filter id='f'><feColorMatrix values='" +
                "0 0 0 0 1  0 0 0 0 0  0 0 0 0 0  0 0 0 1 0'/></filter></defs>" +
                "<rect x='5' y='5' width='20' height='20' fill='blue' filter='url(#f)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            SKColor pixel = result.Bitmap.GetPixel(15, 15);
            Assert.True(pixel.Red > 240 && pixel.Green < 10 && pixel.Blue < 10);
        }

        [Fact]
        public void GroupFilter_CompositesChildrenAsOneSourceGraphic()
        {
            const string svg =
                "<svg width='80' height='40'><defs><filter id='f'><feOffset dx='20'/></filter></defs>" +
                "<g filter='url(#f)'><rect x='2' y='4' width='10' height='10'/>" +
                "<rect x='2' y='22' width='10' height='10'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(25, 8).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(25, 26).Alpha > 0);
        }

        [Theory]
        [InlineData("<feGaussianBlur stdDeviation='999'/>")]
        [InlineData("<feTurbulence/>")]
        [InlineData("<feOffset in='named'/>")]
        [InlineData("<feOffset result='named'/>")]
        public void UnsupportedOrUnboundedPrimitive_RemainsExplicitFallback(string primitive)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg width='30' height='30'><defs><filter id='f'>{primitive}</filter></defs>" +
                "<rect width='20' height='20' filter='url(#f)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ConcurrentFilterRenders_DoNotShareNativeFilterOwnership()
        {
            const string svg =
                "<svg width='30' height='30'><defs><filter id='f'><feGaussianBlur stdDeviation='2'/></filter></defs>" +
                "<circle cx='15' cy='15' r='8' filter='url(#f)'/></svg>";

            Parallel.For(0, 32, _ =>
            {
                using var result = new FenSvgRenderer().Render(svg);
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback);
            });
        }
    }
}
