using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgMarkerTests
    {
        [Fact]
        public void MarkerEnd_UsesPathBearingInsteadOfEndpointChord()
        {
            const string svg =
                "<svg width='40' height='40'><defs>" +
                "<marker id='m' markerWidth='8' markerHeight='4' refX='0' refY='2' " +
                "markerUnits='userSpaceOnUse' overflow='visible' orient='auto'>" +
                "<path d='M0 0 L8 2 L0 4 Z' fill='red'/></marker></defs>" +
                "<circle cx='20' cy='20' r='10' fill='none' stroke='black' " +
                "marker-start='url(#m)' marker-end='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasRed(result.Bitmap, 28, 22, 33, 28));
            Assert.False(HasRed(result.Bitmap, 28, 12, 33, 20));
        }

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
        public void MarkerContextPaint_UsesReferencingElementFillAndStroke()
        {
            const string svg =
                "<svg width='40' height='20'><defs><marker id='m' markerWidth='20' markerHeight='20' " +
                "refX='0' refY='10' markerUnits='userSpaceOnUse' fill='context-fill'>" +
                "<rect width='10' height='20'/><rect x='10' width='10' height='20' fill='context-stroke'/>" +
                "</marker></defs><line x1='0' y1='10' x2='20' y2='10' fill='red' stroke='blue' " +
                "marker-end='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(25, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(35, 10));
        }

        [Fact]
        public void MarkerShorthand_CssDeclarationPaintsStartMiddleAndEndForPolyline()
        {
            const string svg =
                "<svg width='100' height='60'><defs><marker id='dot' markerWidth='6' markerHeight='6' " +
                "refX='3' refY='3' markerUnits='userSpaceOnUse'><circle cx='3' cy='3' r='3' fill='blue'/></marker></defs>" +
                "<polyline points='10 40 50 10 90 40' fill='none' stroke='black' style='marker:url(#dot)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(HasBlue(result.Bitmap, 6, 36, 15, 45));
            Assert.True(HasBlue(result.Bitmap, 46, 6, 55, 15));
            Assert.True(HasBlue(result.Bitmap, 86, 36, 95, 45));
        }

        [Fact]
        public void MarkerShorthand_PresentationAttributeIsNotADeclaration()
        {
            const string shorthand =
                "<svg width='100' height='60'><defs><marker id='dot' markerWidth='6' markerHeight='6' " +
                "refX='3' refY='3' markerUnits='userSpaceOnUse'><circle cx='3' cy='3' r='3' fill='blue'/></marker></defs>" +
                "<polyline points='10 40 50 10 90 40' fill='none' stroke='black' marker='url(#dot)'/></svg>";
            const string longhand =
                "<svg width='100' height='60'><defs><marker id='dot' markerWidth='6' markerHeight='6' " +
                "refX='3' refY='3' markerUnits='userSpaceOnUse'><circle cx='3' cy='3' r='3' fill='blue'/></marker></defs>" +
                "<polyline points='10 40 50 10 90 40' fill='none' stroke='black' marker-start='url(#dot)'/></svg>";

            using var ignored = new FenSvgRenderer().Render(shorthand);
            using var honoured = new FenSvgRenderer().Render(longhand);

            // `marker` is a shorthand, and shorthands are not presentation
            // attributes: the attribute form is dropped, the longhand is not.
            Assert.Equal(SvgRenderResult.IsAdmissible(ignored), SvgRenderResult.IsAdmissible(honoured));
            Assert.False(HasBlue(ignored.Bitmap, 0, 0, ignored.Bitmap.Width, ignored.Bitmap.Height));
            Assert.True(HasBlue(honoured.Bitmap, 6, 36, 15, 45));
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
        public void MarkerMidOnBearingPath_FailsClosedAsExplicitUnsupported()
        {
            const string svg =
                "<svg width='60' height='40'><defs><marker id='m'><circle r='1'/></marker></defs>" +
                "<path d='M5 20 L25 20 B45 l20 0' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
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

        [Theory]
        [InlineData("0.25turn")]
        [InlineData("0.25TURN")]
        [InlineData("90")]
        [InlineData("90deg")]
        [InlineData("90DEG")]
        [InlineData("100grad")]
        [InlineData("100GRAD")]
        [InlineData("1.5707963267948966rad")]
        public void MarkerOrientAngleUnits_PlaceTheStartVertexMarkerIdentically(string orient)
        {
            using var expected = new FenSvgRenderer().Render(MarkerOrientDocument("90deg"));
            using var actual = new FenSvgRenderer().Render(MarkerOrientDocument(orient));

            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < expected.Bitmap.Height; y++)
            for (int x = 0; x < expected.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void MarkerOrientTurn_PlacesContentOnTheNinetyDegreeBearing()
        {
            using var result = new FenSvgRenderer().Render(MarkerOrientDocument("0.25turn"));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            // refX=10 refY=20 on a 20x40 marker anchored at (20,20) lays the
            // content box over x 0..36, y 10..30 at a 90 degree orient. The start
            // bearing of `M20 20 L60 60` is 45 degrees, so these two windows fall
            // inside the box only when the angle really came from the turn unit
            // rather than from the tangent fallback.
            Assert.True(HasRed(result.Bitmap, 2, 11, 5, 14));
            Assert.True(HasRed(result.Bitmap, 33, 27, 36, 30));
        }

        [Fact]
        public void MarkerOrientThatIsNotAnAngle_FailsClosed()
        {
            using var result = new FenSvgRenderer().Render(MarkerOrientDocument("quarter-turn"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("orient"));
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

            AssertFailsClosed(result);
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
        public void MarkerDefaultOverflow_KeepsAntialiasedCoverageOnTheViewportEdge()
        {
            // The triangle is inscribed in the marker viewBox, so one of its
            // edges lies exactly on the marker viewport edge. The rotated
            // viewport clip must not swallow that edge's own coverage, which is
            // what a pixel-sampled clip boundary does. Skia still folds the
            // surviving coverage through the clip mask, which costs one 8-bit
            // step, so the contract is "keeps its coverage", not "bit identical".
            const string clipped =
                "<svg width='300' height='300'><defs>" +
                "<marker id='m' viewBox='-5 -5 10 10' markerWidth='2' markerHeight='2' " +
                "markerUnits='strokeWidth' orient='auto'>" +
                "<path d='M 0 -5 L 5 5 L -5 5 Z' fill='blue' stroke='none'/></marker></defs>" +
                "<path fill='none' stroke='black' stroke-width='16' marker-mid='url(#m)' " +
                "d='M 130 230 L 180 230 L 180 280'/></svg>";
            const string unclipped =
                "<svg width='300' height='300'>" +
                "<path fill='none' stroke='black' stroke-width='16' d='M 130 230 L 180 230 L 180 280'/>" +
                "<g transform='translate(180,230) rotate(45)'>" +
                "<path d='M 0 -16 L 16 16 L -16 16 Z' fill='blue' stroke='none'/></g></svg>";

            using var actual = new FenSvgRenderer().Render(clipped);
            using var expected = new FenSvgRenderer().Render(unclipped);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);

            int worst = 0;
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
            {
                SKColor a = actual.Bitmap.GetPixel(x, y);
                SKColor b = expected.Bitmap.GetPixel(x, y);
                worst = System.Math.Max(worst, System.Math.Abs(a.Red - b.Red));
                worst = System.Math.Max(worst, System.Math.Abs(a.Green - b.Green));
                worst = System.Math.Max(worst, System.Math.Abs(a.Blue - b.Blue));
                worst = System.Math.Max(worst, System.Math.Abs(a.Alpha - b.Alpha));
            }
            Assert.True(worst <= 1, $"marker viewport clip shifted coverage by {worst}");
        }

        [Fact]
        public void MarkerDefaultOverflow_StillClipsContentBeyondTheViewport()
        {
            const string svg =
                "<svg width='40' height='20'><defs><marker id='dot' markerWidth='4' markerHeight='4' " +
                "refX='2' refY='2' markerUnits='userSpaceOnUse' orient='auto'>" +
                "<circle cx='2' cy='2' r='6' fill='red'/></marker></defs>" +
                "<line x1='5' y1='10' x2='20' y2='10' stroke='none' marker-end='url(#dot)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            // The rotated clip bias is half a device pixel; a 2-unit overflow
            // still has to disappear entirely.
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

        [Fact]
        public void MarkerPercentageDimensionsAndReferencePointUseTheMarkerViewport()
        {
            const string percentage =
                "<svg width='100' height='40'><defs>" +
                "<marker id='m' markerWidth='50%' markerHeight='50%' refX='50%' refY='50%' " +
                "markerUnits='userSpaceOnUse' overflow='visible'><circle cx='25' cy='10' r='5' fill='red'/>" +
                "</marker></defs><line x1='10' y1='20' x2='30' y2='20' marker-end='url(#m)'/></svg>";
            const string numeric =
                "<svg width='100' height='40'><defs>" +
                "<marker id='m' markerWidth='50' markerHeight='20' refX='25' refY='10' " +
                "markerUnits='userSpaceOnUse' overflow='visible'><circle cx='25' cy='10' r='5' fill='red'/>" +
                "</marker></defs><line x1='10' y1='20' x2='30' y2='20' marker-end='url(#m)'/></svg>";

            using var actual = new FenSvgRenderer().Render(percentage);
            using var expected = new FenSvgRenderer().Render(numeric);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(actual.RequiresFallback, string.Join("; ", actual.Warnings));
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void MarkerProperties_InheritFromAncestorGroup()
        {
            const string svg =
                "<svg width='60' height='40'><defs>" +
                "<marker id='start' viewBox='0 0 10 10' refX='0' refY='5' markerUnits='userSpaceOnUse' " +
                "markerWidth='10' markerHeight='10' orient='auto' overflow='visible'>" +
                "<path d='M0 0 L10 5 L0 10 z' fill='green'/></marker>" +
                "<marker id='mid' viewBox='0 0 10 10' refX='0' refY='5' markerUnits='userSpaceOnUse' " +
                "markerWidth='10' markerHeight='10' orient='auto' overflow='visible'>" +
                "<path d='M0 0 L10 5 L0 10 z' fill='orange'/></marker>" +
                "<marker id='end' viewBox='0 0 10 10' refX='0' refY='5' markerUnits='userSpaceOnUse' " +
                "markerWidth='10' markerHeight='10' orient='auto' overflow='visible'>" +
                "<path d='M0 0 L10 5 L0 10 z' fill='blue'/></marker></defs>" +
                "<g style='marker-start:url(#start);marker-mid:url(#mid);marker-end:url(#end)'>" +
                "<path d='M10 10 h20 h20' fill='none' stroke='black'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasTint(result.Bitmap, 12, 8, 18, 13, 0, 100, 0));
            Assert.True(HasTint(result.Bitmap, 32, 8, 38, 13, 200, 120, 0));
            Assert.True(HasTint(result.Bitmap, 52, 8, 58, 13, 0, 0, 200));
            Assert.False(HasTint(result.Bitmap, 22, 8, 28, 13, 0, 100, 0));
        }

        [Fact]
        public void MarkerShorthand_CssInheritsAndIsOverriddenByDescendantNone()
        {
            const string svg =
                "<svg width='60' height='40'><defs>" +
                "<marker id='dot' refX='2' refY='2' markerWidth='4' markerHeight='4' " +
                "markerUnits='userSpaceOnUse'><circle cx='2' cy='2' r='2' fill='green'/></marker></defs>" +
                "<g style='marker:url(#dot)'>" +
                "<path d='M10 10 h20' fill='none' stroke='none' marker-start='none'/>" +
                "<path d='M10 30 h20' fill='none' stroke='none'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(HasTint(result.Bitmap, 7, 7, 14, 14, 0, 100, 0));
            Assert.True(HasTint(result.Bitmap, 28, 8, 32, 12, 0, 100, 0));
            Assert.True(HasTint(result.Bitmap, 8, 28, 12, 32, 0, 100, 0));
        }

        [Fact]
        public void ClosedPathReversalMarkers_TileTheVertexQuadrants()
        {
            const string svg =
                "<svg width='100' height='100' xmlns='http://www.w3.org/2000/svg'>" +
                "<marker id='m' markerWidth='100' markerHeight='50' markerUnits='userSpaceOnUse' " +
                "orient='auto' refX='50' refY='50'><path d='M50,-5L105,50h-110z' fill='green'/></marker>" +
                "<g marker-start='url(#m)'><path d='M50,0v50z'/><path d='M100,50h-50z'/>" +
                "<path d='M50,100v-50z'/><path d='M0,50h50z'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(100, result.Bitmap.Width);
            Assert.Equal(100, result.Bitmap.Height);
            for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void MarkerMid_OnReversedVertexUsesQuarterTurnBearing()
        {
            const string svg =
                "<svg width='60' height='40'><defs>" +
                "<marker id='m' markerWidth='20' markerHeight='20' refX='0' refY='0' " +
                "markerUnits='userSpaceOnUse' orient='auto' overflow='visible'>" +
                "<rect x='0' y='-1' width='20' height='2' fill='red'/></marker></defs>" +
                "<path d='M40 30 L40 10 L40 30 z' fill='none' stroke='none' marker-mid='url(#m)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasRed(result.Bitmap, 21, 9, 39, 11));
            Assert.False(HasRed(result.Bitmap, 41, 9, 59, 11));
            Assert.False(HasRed(result.Bitmap, 39, 11, 41, 29));
        }

        [Theory]
        [InlineData("visible")]
        [InlineData("auto")]
        public void MarkerOverflowUnclippedSpellings_PaintOutsideTheMarkerViewport(string overflow)
        {
            using var result = new FenSvgRenderer().Render(MarkerOverflowDocument(overflow));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            SKColor overflowPixel = result.Bitmap.GetPixel(24, 10);
            Assert.True(overflowPixel.Alpha > 200 && overflowPixel.Red > 200);
        }

        [Theory]
        [InlineData("")]
        [InlineData("hidden")]
        [InlineData("scroll")]
        [InlineData("visible-please")]
        [InlineData("clip")]
        public void MarkerOverflowClippedSpellings_ClipToTheMarkerViewport(string overflow)
        {
            using var result = new FenSvgRenderer().Render(MarkerOverflowDocument(overflow));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(24, 10).Alpha);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(21, 10));
        }

        [Fact]
        public void MarkerOverflowSpellings_LandOnTheTwoFramesTheMarkerReferenceAsserts()
        {
            using var byVisible = new FenSvgRenderer().Render(MarkerOverflowDocument("visible"));
            using var byAuto = new FenSvgRenderer().Render(MarkerOverflowDocument("auto"));
            using var byDefault = new FenSvgRenderer().Render(MarkerOverflowDocument(string.Empty));
            using var byHidden = new FenSvgRenderer().Render(MarkerOverflowDocument("hidden"));
            using var byScroll = new FenSvgRenderer().Render(MarkerOverflowDocument("scroll"));

            AssertFirstParty(byVisible);
            AssertFirstParty(byAuto);
            AssertFirstParty(byDefault);
            AssertFirstParty(byHidden);
            AssertFirstParty(byScroll);

            AssertSameFrame(byVisible.Bitmap, byAuto.Bitmap);
            AssertSameFrame(byDefault.Bitmap, byHidden.Bitmap);
            AssertSameFrame(byDefault.Bitmap, byScroll.Bitmap);
            Assert.NotEqual(byVisible.Bitmap.GetPixel(24, 10), byDefault.Bitmap.GetPixel(24, 10));
        }

        [Fact]
        public void MarkerViewportClip_PaintsTheSameFrameAsAnExplicitClipPathOnThatViewport()
        {
            using var explicitClip = new FenSvgRenderer().Render(
                "<svg width='40' height='20'><defs><clipPath id='vp'><rect width='4' height='4'/>" +
                "</clipPath><marker id='dot' markerWidth='4' markerHeight='4' refX='2' refY='2' " +
                "markerUnits='userSpaceOnUse' overflow='visible'>" +
                "<circle cx='2' cy='2' r='6' fill='red' clip-path='url(#vp)'/></marker></defs>" +
                "<line x1='5' y1='10' x2='20' y2='10' stroke='none' marker-end='url(#dot)'/></svg>");
            using var implicitClip = new FenSvgRenderer().Render(MarkerOverflowDocument(string.Empty));

            AssertFirstParty(explicitClip);
            AssertFirstParty(implicitClip);
            AssertSameFrame(explicitClip.Bitmap, implicitClip.Bitmap);
        }

        private static string MarkerOverflowDocument(string overflow)
        {
            string declaration = overflow.Length == 0
                ? string.Empty
                : " overflow='" + overflow + "'";
            return
                "<svg width='40' height='20'><defs><marker id='dot' markerWidth='4' markerHeight='4' " +
                "refX='2' refY='2' markerUnits='userSpaceOnUse'" + declaration + ">" +
                "<circle cx='2' cy='2' r='6' fill='red'/></marker></defs>" +
                "<line x1='5' y1='10' x2='20' y2='10' stroke='none' marker-end='url(#dot)'/></svg>";
        }

        private static void AssertFirstParty(SvgRenderResult result)
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        private static void AssertSameFrame(SKBitmap expected, SKBitmap actual)
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        }

        private static string MarkerOrientDocument(string orient) =>
            "<svg width='80' height='80' xmlns='http://www.w3.org/2000/svg'><defs>" +
            "<marker id='m' refX='10' refY='20' markerWidth='20' markerHeight='40' " +
            "overflow='visible' orient='" + orient + "'>" +
            "<rect x='0' y='4' width='20' height='36' fill='red'/></marker></defs>" +
            "<path d='M20 20 L60 60' fill='none' stroke='black' stroke-width='1' " +
            "marker-start='url(#m)'/></svg>";

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

        private static bool HasTint(
            SKBitmap bitmap,
            int left,
            int top,
            int right,
            int bottom,
            int minimumRed,
            int minimumGreen,
            int minimumBlue)
        {
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                SKColor color = bitmap.GetPixel(x, y);
                if (color.Alpha == 0) continue;
                if (color.Red >= minimumRed &&
                    color.Green >= minimumGreen &&
                    color.Blue >= minimumBlue) return true;
            }
            return false;
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
