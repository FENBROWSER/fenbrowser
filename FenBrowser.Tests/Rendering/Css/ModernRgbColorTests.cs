using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering.Css
{
    public class ModernRgbColorTests
    {
        [Fact]
        public void ParseColor_ModernRgbSpaceSlashAlpha_ReturnsColor()
        {
            var color = CssParser.ParseColor("rgb(10 20 30 / 50%)");
            Assert.True(color.HasValue);
            Assert.Equal(new SKColor(10, 20, 30, 127), color.Value);
        }

        [Fact]
        public void ParseColor_ModernRgbSpaceNoAlpha_ReturnsOpaqueColor()
        {
            var color = CssParser.ParseColor("rgb(10 20 30)");
            Assert.True(color.HasValue);
            Assert.Equal(new SKColor(10, 20, 30, 255), color.Value);
        }
    }
}
