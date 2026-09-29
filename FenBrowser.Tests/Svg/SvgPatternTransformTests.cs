using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Pattern tiles and paint-server transforms. patternTransform and
    /// gradientTransform are presentation attributes for the CSS transform property,
    /// so a CSS declaration uses CSS syntax; and a tile too large to rasterize still
    /// paints when the shape sees only one instance of it.
    /// </summary>
    public sealed class SvgPatternTransformTests
    {
        // A 100x100 objectBoundingBox tile on a 100x100 rect is 10000x10000 user
        // units, of which only the top-left 100x100 is visible.
        private const string HugeTile =
            "<pattern id='p' width='100' height='100' {0}>" +
            "<rect x='25' y='25' width='75' height='75' fill='red'/>" +
            "<rect width='75' height='75' fill='lime'/></pattern>" +
            "<rect width='100' height='100' fill='url(#p)'/>";

        [Fact]
        public void OversizedTile_PaintsTheVisibleInstance()
        {
            using var result = Render(string.Format(HugeTile, string.Empty));

            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(90, 90));
        }

        [Theory]
        [InlineData("style='transform: translate(-25px, -25px) scale(2)'")]
        [InlineData("patternTransform='translate(-25 -25) scale(2)'")]
        public void CssAndAttributeTransforms_AgreeOnAnOversizedTile(string transform)
        {
            // Scaled by 2 and pulled back by 25, the lime square covers the whole rect.
            using var result = Render(string.Format(HugeTile, transform));

            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(1, 1));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(98, 98));
        }

        [Fact]
        public void CssTransformWithUnits_AppliesToAGradient()
        {
            // translate(50px) moves a hard lime/red edge from x=50 to x=100.
            using var result = Render(
                "<linearGradient id='g' gradientUnits='userSpaceOnUse' x2='100' " +
                "style='transform: translateX(50px)'>" +
                "<stop offset='0.5' stop-color='lime'/><stop offset='0.5' stop-color='red'/></linearGradient>" +
                "<rect width='100' height='100' fill='url(#g)'/>");

            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(90, 50));
        }

        [Fact]
        public void TileSpanningInstances_StillRepeats()
        {
            // A 10x10 user-space tile repeats across the rect as before.
            using var result = Render(
                "<pattern id='p' patternUnits='userSpaceOnUse' width='10' height='10'>" +
                "<rect width='5' height='10' fill='lime'/></pattern>" +
                "<rect width='100' height='100' fill='url(#p)'/>");

            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(92, 50));
            Assert.Equal(0, result.Bitmap.GetPixel(97, 50).Alpha);
        }

        private static SvgRenderResult Render(string body)
        {
            var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" + body + "</svg>");
            Assert.True(result.Success, result.ErrorMessage);
            return result;
        }
    }
}
