using System.Text;
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
        public void MarkerMidOnBearingPath_RemainsExplicitFallback()
        {
            const string svg =
                "<svg width='60' height='40'><defs><marker id='m'><circle r='1'/></marker></defs>" +
                "<path d='M5 20 L25 20 B45 l20 0' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("marker-mid"));
        }

        [Fact]
        public void MarkerMidOnArcPath_UsesEllipseTangent()
        {
            const string svg =
                "<svg width='70' height='50'><defs><marker id='m' markerWidth='8' markerHeight='4' " +
                "refX='0' refY='2' markerUnits='userSpaceOnUse' overflow='visible' orient='auto'>" +
                "<path d='M0 0 L8 2 L0 4 Z' fill='red'/></marker></defs>" +
                "<path d='M5 25 A15 10 0 0 1 35 25 A15 10 0 0 1 65 25' fill='none' stroke='black' " +
                "marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasRed(result.Bitmap, 30, 17, 43, 34));
        }

        [Fact]
        public void MarkerMidOnCubicPath_UsesControlPointTangents()
        {
            const string svg =
                "<svg width='70' height='50'><defs><marker id='m' markerWidth='8' markerHeight='4' " +
                "refX='0' refY='2' markerUnits='userSpaceOnUse' overflow='visible' orient='auto'>" +
                "<path d='M0 0 L8 2 L0 4 Z' fill='red'/></marker></defs>" +
                "<path d='M5 40 C15 10 25 10 35 25 C45 40 55 40 65 10' fill='none' stroke='black' " +
                "marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasRed(result.Bitmap, 30, 20, 46, 40));
        }

        [Fact]
        public void MarkerMidOnLinearPath_PaintsEveryVertex()
        {
            const string svg =
                "<svg width='60' height='50'><defs><marker id='m' markerWidth='6' markerHeight='6' " +
                "refX='3' refY='3' markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<circle cx='3' cy='3' r='3' fill='blue'/></marker></defs>" +
                "<path d='M5 40 H30 V10' fill='none' stroke='black' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasBlue(result.Bitmap, 27, 37, 34, 44));
        }

        [Fact]
        public void LinearPathMarkerRoles_SpanAllSubpaths()
        {
            const string svg =
                "<svg width='60' height='30'><defs>" +
                "<marker id='start' refX='2' refY='2' markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<circle cx='2' cy='2' r='2' fill='red'/></marker>" +
                "<marker id='mid' refX='2' refY='2' markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<circle cx='2' cy='2' r='2' fill='blue'/></marker>" +
                "<marker id='end' refX='2' refY='2' markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<circle cx='2' cy='2' r='2' fill='green'/></marker></defs>" +
                "<path d='M5 5 l10 0 M35 20 h10' fill='none' stroke='black' " +
                "marker-start='url(#start)' marker-mid='url(#mid)' marker-end='url(#end)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(5, 5).Red > 200);
            Assert.True(result.Bitmap.GetPixel(15, 5).Blue > 200);
            Assert.True(result.Bitmap.GetPixel(35, 20).Blue > 200);
            Assert.True(result.Bitmap.GetPixel(45, 20).Green > 100);
        }

        [Fact]
        public void MarkerDefaultOrient_IsZeroDegrees()
        {
            const string svg =
                "<svg width='40' height='40'><defs><marker id='m' markerWidth='10' markerHeight='2' " +
                "refX='5' refY='1' markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<rect width='10' height='2' fill='red'/></marker></defs>" +
                "<path d='M5 20 H20 V35' fill='none' stroke='black' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(24, 20).Red > 200);
            Assert.Equal(0, result.Bitmap.GetPixel(16, 24).Alpha);
        }

        [Fact]
        public void ClosedPathStartMarker_AveragesClosingAndOutgoingTangents()
        {
            const string svg =
                "<svg width='40' height='40'><defs><marker id='m' refX='0' refY='2' " +
                "markerWidth='8' markerHeight='4' markerUnits='userSpaceOnUse' overflow='visible' orient='auto'>" +
                "<path d='M0 0 L8 2 L0 4 Z' fill='red'/></marker></defs>" +
                "<path d='M20 10 L10 20 L20 30 L30 20 Z' fill='none' stroke='black' " +
                "marker-start='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(14, 10).Red > 200);
        }

        [Fact]
        public void LinearPathMarkerVertices_RespectInstanceBudget()
        {
            var data = new StringBuilder("M0 0");
            for (int i = 1; i <= 4096; i++) data.Append(" L").Append(i).Append(" 0");
            string svg =
                "<svg width='20' height='20'><defs><marker id='m'><circle r='1'/></marker></defs>" +
                $"<path d='{data}' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("marker instance budget"));
        }

        [Fact]
        public void MarkerOverflowVisible_PaintsOutsideTheMarkerViewport()
        {
            const string svg =
                "<svg width='40' height='20'><defs><marker id='dot' markerWidth='4' markerHeight='4' " +
                "refX='2' refY='2' markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<circle cx='2' cy='2' r='6' fill='red'/></marker></defs>" +
                "<line x1='5' y1='10' x2='20' y2='10' stroke='none' marker-end='url(#dot)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            SKColor overflowPixel = result.Bitmap.GetPixel(24, 10);
            Assert.True(overflowPixel.Alpha > 200 && overflowPixel.Red > 200);
        }

        [Fact]
        public void MarkerDefaultOverflow_ClipsToTheMarkerViewport()
        {
            const string svg =
                "<svg width='40' height='20'><defs><marker id='dot' markerWidth='4' markerHeight='4' " +
                "refX='2' refY='2' markerUnits='userSpaceOnUse'>" +
                "<circle cx='2' cy='2' r='6' fill='red'/></marker></defs>" +
                "<line x1='5' y1='10' x2='20' y2='10' stroke='none' marker-end='url(#dot)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(0, result.Bitmap.GetPixel(24, 10).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(21, 10));
        }

        [Fact]
        public void ZeroAreaMarkerViewBox_SuppressesMarkerContent()
        {
            const string svg =
                "<svg width='40' height='20'><defs><marker id='m' viewBox='0 0 0 10' " +
                "markerWidth='10' markerHeight='10' markerUnits='userSpaceOnUse'>" +
                "<rect width='10' height='10' fill='red'/></marker></defs>" +
                "<line x1='5' y1='10' x2='30' y2='10' stroke='black' marker-end='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(HasRed(result.Bitmap, 0, 0, result.Bitmap.Width, result.Bitmap.Height));
            Assert.True(result.Bitmap.GetPixel(15, 10).Alpha > 0);
        }

        [Fact]
        public void NestedSvgOverflowVisible_PaintsOutsideItsViewport()
        {
            const string svg =
                "<svg width='20' height='20'><svg width='4' height='4' style='overflow:visible'>" +
                "<circle cx='2' cy='2' r='6' fill='red'/></svg></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(result.Bitmap.GetPixel(7, 2).Red > 200);
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
