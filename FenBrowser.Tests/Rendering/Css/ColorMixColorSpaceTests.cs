using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering.Css
{
    public class ColorMixColorSpaceTests
    {
        [Theory]
        [InlineData("srgb")]
        [InlineData("srgb-linear")]
        [InlineData("xyz")]
        [InlineData("xyz-d50")]
        [InlineData("lab")]
        [InlineData("lch")]
        [InlineData("oklab")]
        [InlineData("oklch")]
        [InlineData("hsl")]
        [InlineData("hwb")]
        [InlineData("display-p3")]
        [InlineData("rec2020")]
        [InlineData("a98-rgb")]
        [InlineData("prophoto-rgb")]
        public void ParseColor_ColorMix_AllDeclaredSpaces_ReturnsColor(string colorSpace)
        {
            var color = CssParser.ParseColor($"color-mix(in {colorSpace}, red 40%, blue)");
            Assert.True(color.HasValue, $"Expected color-mix to parse in '{colorSpace}'.");
        }

        [Fact]
        public void ParseColor_ColorMix_NormalizesWeights()
        {
            var explicitEqual = CssParser.ParseColor("color-mix(in srgb, red 20%, blue 20%)");
            var implicitEqual = CssParser.ParseColor("color-mix(in srgb, red, blue)");

            Assert.True(explicitEqual.HasValue);
            Assert.True(implicitEqual.HasValue);
            Assert.Equal(implicitEqual.Value, explicitEqual.Value);
        }

        [Fact]
        public void ParseColor_ColorMix_SrgbProducesExpectedMidpoint()
        {
            var mixed = CssParser.ParseColor("color-mix(in srgb, red, blue)");
            Assert.True(mixed.HasValue);

            // srgb midpoint between red(255,0,0) and blue(0,0,255)
            Assert.Equal(new SKColor(127, 0, 127, 255), mixed.Value);
        }

        [Fact]
        public void ParseColor_ColorMix_HueInterpolationMethods_ParseWithoutFailure()
        {
            var shorter = CssParser.ParseColor("color-mix(in oklch shorter hue, hsl(350 100% 50%), hsl(10 100% 50%))");
            var longer = CssParser.ParseColor("color-mix(in oklch longer hue, hsl(350 100% 50%), hsl(10 100% 50%))");

            Assert.True(shorter.HasValue);
            Assert.True(longer.HasValue);
        }
    }
}
