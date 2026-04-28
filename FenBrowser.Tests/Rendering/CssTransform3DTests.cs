using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CssTransform3DTests
    {
        [Fact]
        public void TranslatePercent_UsesReferenceBounds()
        {
            var transform = CssTransform3D.Parse("translate(-50%, -25%)");

            var matrix = transform.ToSKMatrix(new SKRect(0, 0, 200, 80));

            Assert.Equal(-100f, matrix.TransX, 0.01f);
            Assert.Equal(-20f, matrix.TransY, 0.01f);
        }
    }
}