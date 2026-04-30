using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CssTransform3DTests
    {
        [Fact]
        public void Parse_TranslateCalcWithVarFallback_ResolvesDistance()
        {
            var transform = CssTransform3D.Parse("translate(calc(-7px * var(--r-globalnav-logical-factor, 1)))");
            var matrix = transform.ToSKMatrix(new SKRect(0, 0, 200, 120));

            Assert.Equal(-7f, matrix.TransX, 3);
            Assert.Equal(0f, matrix.TransY, 3);
        }

        [Fact]
        public void Parse_TranslatePercentAndCalcPercent_UsesReferenceBox()
        {
            var transform = CssTransform3D.Parse("translate(50%, calc(25% - 4px))");
            var matrix = transform.ToSKMatrix(new SKRect(0, 0, 200, 120));

            Assert.Equal(100f, matrix.TransX, 3);
            Assert.Equal(26f, matrix.TransY, 3);
        }
    }
}
