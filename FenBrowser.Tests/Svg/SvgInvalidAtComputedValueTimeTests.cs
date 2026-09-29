using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// CSS Variables 1 §3.1: a declaration whose var() cannot be substituted, or that
    /// is invalid after substitution, is invalid at computed-value time. It still
    /// wins the cascade, so the presentation attribute it overrides stays overridden,
    /// and the property behaves as 'unset'.
    /// </summary>
    public sealed class SvgInvalidAtComputedValueTimeTests
    {
        [Theory]
        // An inherited property falls back to the parent's value, not the attribute.
        [InlineData("<rect width='10' height='10' fill='#00ff00' style='fill: var(--missing)'/>", 0, 0, 0, 255)]
        [InlineData("<g fill='blue'><rect width='10' height='10' fill='#00ff00' style='fill: var(--missing)'/></g>", 0, 0, 255, 255)]
        // A substituted value that is not a paint is invalid at computed-value time too.
        [InlineData("<g style='--x: 12px'><rect width='10' height='10' fill='#00ff00' style='fill: var(--x)'/></g>", 0, 0, 0, 255)]
        // A non-inherited property takes its initial value: opacity 1, not the attribute's 0.5.
        [InlineData("<rect width='10' height='10' fill='#00ff00' opacity='0.5' style='opacity: var(--missing)'/>", 0, 255, 0, 255)]
        // Without var() an invalid declaration is dropped at parse time and the attribute applies.
        [InlineData("<rect width='10' height='10' fill='#00ff00' style='fill: 12px'/>", 0, 255, 0, 255)]
        // A fallback inside var() is a successful substitution.
        [InlineData("<rect width='10' height='10' fill='red' style='fill: var(--missing, #00ff00)'/>", 0, 255, 0, 255)]
        public void UnsubstitutableDeclaration_BehavesAsUnset(string body, byte r, byte g, byte b, byte a)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" + body + "</svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(new SKColor(r, g, b, a), result.Bitmap.GetPixel(5, 5));
        }
    }
}
