using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GeometryNormalizationTests
    {
        [Fact]
        public void NormalizeRect_FixesNonFiniteAndInvertedCoordinates()
        {
            var input = new SKRect(float.NaN, float.PositiveInfinity, -10f, -20f);

            var normalized = LayoutHelper.NormalizeRect(input);

            Assert.True(float.IsFinite(normalized.Left));
            Assert.True(float.IsFinite(normalized.Top));
            Assert.True(float.IsFinite(normalized.Right));
            Assert.True(float.IsFinite(normalized.Bottom));
            Assert.True(normalized.Right >= normalized.Left);
            Assert.True(normalized.Bottom >= normalized.Top);
        }

        [Fact]
        public void NormalizeRect_WithContainer_ClampsInsideBounds()
        {
            var rect = new SKRect(-50f, -25f, 300f, 250f);
            var container = new SKRect(10f, 20f, 110f, 120f);

            var normalized = LayoutHelper.NormalizeRect(rect, container, clampToContainer: true);

            Assert.True(normalized.Left >= container.Left);
            Assert.True(normalized.Top >= container.Top);
            Assert.True(normalized.Right <= container.Right);
            Assert.True(normalized.Bottom <= container.Bottom);
            Assert.True(normalized.Width >= 0f);
            Assert.True(normalized.Height >= 0f);
        }
    }
}
