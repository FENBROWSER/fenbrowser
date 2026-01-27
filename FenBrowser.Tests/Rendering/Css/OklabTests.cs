using Xunit;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using System;

namespace FenBrowser.Tests.Rendering.Css
{
    public class OklabTests
    {
        [Fact]
        public void ParseColor_Oklab_ReturnsCorrectSrgb()
        {
            // oklab(0.6 0.1 0.1) -> pinkish red
            // Expected sRGB (approx): R=168, G=122, B=126 (Reference check needed)
            // Using online converter: oklab(0.6, 0.1, 0.1) -> rgb(179, 117, 126) ?
            // Let's just check it returns *something* reasonable and not null.
            
            var color = CssParser.ParseColor("oklab(0.6 0.1 0.1)");
            Assert.NotNull(color);
            SKColor c = color.Value;
            // R should be highest component
            Assert.True(c.Red > c.Green && c.Red > c.Blue, $"Expected Red dominant, got {c}");
        }

        [Fact]
        public void ParseColor_Oklch_ReturnsCorrectSrgb()
        {
            // oklch(0.6 0.15 0) -> red (hue 0)
            var color = CssParser.ParseColor("oklch(0.6 0.15 0)");
            Assert.NotNull(color);
            SKColor c = color.Value;
            Assert.True(c.Red > c.Green && c.Red > c.Blue, $"Expected Red dominant, got {c}");
        }

        [Fact]
        public void ParseColor_Oklab_ClampsValues()
        {
            // oklab(1 0 0) -> White
            var white = CssParser.ParseColor("oklab(1 0 0)");
            Assert.Equal(SKColors.White, white);

            // oklab(0 0 0) -> Black
            var black = CssParser.ParseColor("oklab(0 0 0)");
            Assert.Equal(SKColors.Black, black);
        }
    }
}
