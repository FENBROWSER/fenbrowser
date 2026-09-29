using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Paint-server transforms. patternTransform and gradientTransform are
    /// presentation attributes for the CSS transform property, so a CSS
    /// declaration uses CSS syntax.
    /// </summary>
    public sealed class SvgPatternTransformTests
    {
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

        private static SvgRenderResult Render(string body)
        {
            var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" + body + "</svg>");
            Assert.True(result.Success, result.ErrorMessage);
            return result;
        }
    }
}
