using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgMarkerTests
    {
        [Fact]
        public void MarkerEnd_UsesPathEndpointAndAutoOrientation()
        {
            const string svg =
                "<svg width='100' height='50'><defs><marker id='arrow' markerWidth='10' markerHeight='10' " +
                "refX='10' refY='5' orient='auto' markerUnits='userSpaceOnUse'>" +
                "<path d='M0 0L10 5L0 10Z' fill='red'/></marker></defs>" +
                "<line x1='10' y1='25' x2='80' y2='25' stroke='black' marker-end='url(#arrow)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasRed(result.Bitmap, 68, 18, 81, 33));
        }

        [Fact]
        public void MarkerShorthand_PaintsStartMiddleAndEndForPolyline()
        {
            const string svg =
                "<svg width='100' height='60'><defs><marker id='dot' markerWidth='6' markerHeight='6' " +
                "refX='3' refY='3' markerUnits='userSpaceOnUse'><circle cx='3' cy='3' r='3' fill='blue'/></marker></defs>" +
                "<polyline points='10 40 50 10 90 40' fill='none' stroke='black' marker='url(#dot)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasBlue(result.Bitmap, 6, 36, 15, 45));
            Assert.True(HasBlue(result.Bitmap, 46, 6, 55, 15));
            Assert.True(HasBlue(result.Bitmap, 86, 36, 95, 45));
        }

        [Fact]
        public void StrokeWidthMarkerUnits_ScaleMarkerInstance()
        {
            const string svg =
                "<svg width='80' height='50'><defs><marker id='m' markerWidth='3' markerHeight='3' refX='1.5' refY='1.5'>" +
                "<circle cx='1.5' cy='1.5' r='1.4' fill='red'/></marker></defs>" +
                "<line x1='10' y1='25' x2='50' y2='25' stroke='black' stroke-width='4' marker-end='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(HasRed(result.Bitmap, 44, 19, 57, 32));
        }

        [Fact]
        public void MarkerMidOnCurvedPath_RemainsExplicitFallback()
        {
            const string svg =
                "<svg width='60' height='40'><defs><marker id='m'><circle r='1'/></marker></defs>" +
                "<path d='M5 20 C20 0 40 40 55 20' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("marker-mid"));
        }

        private static bool HasRed(SKBitmap bitmap, int left, int top, int right, int bottom) =>
            HasColor(bitmap, left, top, right, bottom, red: true);

        private static bool HasBlue(SKBitmap bitmap, int left, int top, int right, int bottom) =>
            HasColor(bitmap, left, top, right, bottom, red: false);

        private static bool HasColor(SKBitmap bitmap, int left, int top, int right, int bottom, bool red)
        {
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                SKColor color = bitmap.GetPixel(x, y);
                if (color.Alpha > 0 && (red
                        ? color.Red > 180 && color.Blue < 80
                        : color.Blue > 180 && color.Red < 80)) return true;
            }
            return false;
        }
    }
}
