using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgPaintOrderTests
    {
        [Fact]
        public void StrokeBeforeFill_PaintsTheInnerStrokeBelowTheFill()
        {
            const string svg =
                "<svg width='40' height='40'><rect x='10' y='10' width='20' height='20' " +
                "fill='blue' stroke='red' stroke-width='12' paint-order='stroke'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(12, 20));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(7, 20));
        }

        [Fact]
        public void MarkersBeforeStroke_PaintsTheStrokeOverTheMarkerCenter()
        {
            const string svg =
                "<svg width='70' height='40'><defs><marker id='dot' markerWidth='12' markerHeight='12' " +
                "refX='6' refY='6' markerUnits='userSpaceOnUse'><circle cx='6' cy='6' r='6' fill='blue'/></marker></defs>" +
                "<line x1='10' y1='20' x2='50' y2='20' stroke='red' stroke-width='6' " +
                "marker-end='url(#dot)' paint-order='markers stroke'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(48, 20));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(50, 15));
        }

        [Fact]
        public void PaintOrder_IsInheritedAndCompletesOmittedPhasesInNormalOrder()
        {
            const string svg =
                "<svg width='40' height='40'><g style='paint-order:stroke'>" +
                "<rect x='10' y='10' width='20' height='20' fill='blue' stroke='red' stroke-width='12'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(12, 20));
        }

        [Fact]
        public void InvalidDuplicatePaintOrder_KeepsTheInheritedValue()
        {
            const string svg =
                "<svg width='40' height='40'><g paint-order='stroke'><rect x='10' y='10' width='20' height='20' " +
                "fill='blue' stroke='red' stroke-width='12' style='paint-order:fill fill'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(12, 20));
        }
    }
}
