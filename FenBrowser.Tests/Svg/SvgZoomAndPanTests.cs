using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// The zoom-and-pan transform of a standalone SVG document (SVG 2 §5.1.1,
    /// currentScale and currentTranslate), applied in viewport space at raster time.
    /// </summary>
    public sealed class SvgZoomAndPanTests
    {
        private const string Document =
            "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" +
            "<rect width='200' height='200' fill='lime'/></svg>";

        [Fact]
        public void Scale_ShrinksTheContentInsideTheViewport()
        {
            using var result = Render(new SvgZoomAndPan(0.5f, 0f, 0f));

            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(99, 99));
            Assert.Equal(100, result.Bitmap.Width);
        }

        [Fact]
        public void Translate_PansAfterScaling()
        {
            // Scaled to 0.25 the rect covers 50x50, then moves to 20..70.
            using var result = Render(new SvgZoomAndPan(0.25f, 20f, 20f));

            Assert.Equal(0, result.Bitmap.GetPixel(10, 10).Alpha);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(40, 40));
            Assert.Equal(0, result.Bitmap.GetPixel(80, 80).Alpha);
        }

        [Theory]
        [InlineData(float.NaN, 0f)]
        [InlineData(1f, float.PositiveInfinity)]
        [InlineData(20000f, 0f)]
        public void AnInvalidTransform_IsRefused(float scale, float translate)
        {
            using var result = new FenSvgRenderer().Render(new SvgRenderRequest(Document, SvgRenderLimits.Default)
            {
                ZoomAndPan = new SvgZoomAndPan(scale, translate, 0f)
            });

            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
        }

        private static SvgRenderResult Render(SvgZoomAndPan zoom)
        {
            var result = new FenSvgRenderer().Render(new SvgRenderRequest(Document, SvgRenderLimits.Default)
            {
                ZoomAndPan = zoom
            });
            Assert.True(result.Success, result.ErrorMessage);
            return result;
        }
    }
}
