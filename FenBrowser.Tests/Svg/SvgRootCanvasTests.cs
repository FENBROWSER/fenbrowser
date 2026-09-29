using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// The root svg element as a document canvas: its background colour paints the
    /// canvas (CSS Backgrounds 3 §2.11.2), and its width and height decide whether the
    /// image has a natural size (CSS Images 3 §4.1).
    /// </summary>
    public sealed class SvgRootCanvasTests
    {
        [Theory]
        [InlineData("style='background: blue'")]
        [InlineData("style='background-color: #0000ff'")]
        public void RootBackgroundPaintsTheCanvas(string style)
        {
            using var result = new FenSvgRenderer().Render(
                $"<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4' {style}></svg>");

            Assert.True(SvgRenderResult.IsAdmissible(result), result.ErrorMessage);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(2, 2));
        }

        [Fact]
        public void RootBackgroundFromAStyleSheetPaintsUnderTheContent()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'>" +
                "<style>svg { background-color: blue }</style><rect width='2' height='4' fill='lime'/></svg>");

            Assert.True(SvgRenderResult.IsAdmissible(result), result.ErrorMessage);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(1, 2));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(3, 2));
        }

        [Fact]
        public void RootBackgroundImageIsStillRefused()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4' " +
                "style='background: linear-gradient(red, blue)'></svg>");

            Assert.False(SvgRenderResult.IsAdmissible(result));
        }

        [Theory]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg'></svg>", false)]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10'></svg>", false)]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%'></svg>", false)]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='20'></svg>", true)]
        [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='2em' height='1em'></svg>", true)]
        public void NaturalSizeNeedsAConcreteWidthAndHeight(string svg, bool expected)
        {
            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(expected, result.HasNaturalSize);
        }
    }
}
