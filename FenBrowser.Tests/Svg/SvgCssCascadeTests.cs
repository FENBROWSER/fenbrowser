using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgCssCascadeTests
    {
        [Fact]
        public void StylesheetClassRule_UsesSharedCssParserAndPaints()
        {
            const string svg =
                "<svg width='20' height='20'><style>.icon { fill: rgb(255 0 0); }</style>" +
                "<rect class='icon' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void CdataStylesheet_IsParsedWithoutEntityExpansion()
        {
            const string svg =
                "<svg width='20' height='20'><style><![CDATA[rect { fill: #00ff00 }]]></style>" +
                "<rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void Cascade_RespectsImportantSpecificityInlineAndSourceOrder()
        {
            const string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue } .winner { fill: green } #target { fill: red !important }" +
                "</style><rect id='target' class='winner' fill='yellow' style='fill: purple' width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void CascadeLayers_UseNormalAndImportantLayerOrdering()
        {
            const string svg =
                "<svg width='20' height='10'><style>" +
                "@layer reset, theme;" +
                "@layer theme { .normal { fill: green } .important { fill: green !important } }" +
                "@layer reset { .normal { fill: red } .important { fill: red !important } }" +
                "</style><rect class='normal' width='10' height='10'/>" +
                "<rect class='important' x='10' width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(15, 5));
        }

        [Fact]
        public void StaticCombinatorsAttributesAndNot_MatchSvgTree()
        {
            const string svg =
                "<svg width='30' height='10'><style>" +
                "g > rect[data-kind='ok']:not(.disabled) + circle { fill: lime }" +
                "</style><g><rect data-kind='ok' width='5' height='5'/>" +
                "<circle cx='20' cy='5' r='4'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(20, 5));
        }

        [Theory]
        [InlineData("rect:nth-child(2)", 1, "red")]
        [InlineData("rect:nth-child(2)", 2, "green")]
        [InlineData("rect:nth-child(odd)", 1, "green")]
        [InlineData("rect:nth-child(odd)", 2, "red")]
        [InlineData("rect:nth-child(odd)", 3, "green")]
        [InlineData("rect:nth-child(even)", 1, "red")]
        [InlineData("rect:nth-child(even)", 2, "green")]
        [InlineData("rect:nth-child(2n)", 1, "red")]
        [InlineData("rect:nth-child(2n)", 4, "green")]
        [InlineData("rect:nth-child(2n + 1)", 1, "green")]
        [InlineData("rect:nth-child(2n + 1)", 2, "red")]
        [InlineData("rect:nth-child( -n + 2 )", 1, "green")]
        [InlineData("rect:nth-child( -n + 2 )", 2, "green")]
        [InlineData("rect:nth-child( -n + 2 )", 3, "red")]
        [InlineData("rect:nth-child(N)", 3, "green")]
        [InlineData("rect:nth-last-child(1)", 3, "red")]
        [InlineData("rect:nth-last-child(1)", 4, "green")]
        [InlineData("rect:nth-last-child(2)", 3, "green")]
        [InlineData("rect:nth-last-child(2)", 4, "red")]
        [InlineData("rect:nth-of-type(1)", 1, "green")]
        [InlineData("rect:nth-of-type(1)", 2, "red")]
        [InlineData("rect:nth-of-type(2)", 2, "green")]
        [InlineData("rect:nth-last-of-type(1n)", 2, "green")]
        [InlineData("rect:not(:nth-child(2))", 1, "green")]
        [InlineData("rect:not(:nth-child(2))", 2, "red")]
        [InlineData("g > rect:nth-child(2)", 1, "red")]
        [InlineData("g > rect:nth-child(2)", 2, "green")]
        public void NthChildSelectors_CountSvgElementSiblings(string rule, int ordinal, string expected)
        {
            const string source =
                "<svg width='40' height='10'><style>{0} {{ fill: green }} rect {{ fill: red }}" +
                "</style><g>" +
                "<rect width='10' height='10'/><rect x='10' width='10' height='10'/>" +
                "<rect x='20' width='10' height='10'/><rect x='30' width='10' height='10'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(string.Format(source, rule));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(ExpectedColor(expected), result.Bitmap.GetPixel((ordinal - 1) * 10 + 5, 5));
        }

        [Fact]
        public void NthOfTypeSelectors_IgnoreSiblingsOfOtherElementNames()
        {
            const string source =
                "<svg width='20' height='10'><style>{0} {{ fill: green }} rect {{ fill: red }}" +
                "</style><g>" +
                "<rect width='10' height='10'/>" +
                "<circle cx='5' cy='5' r='2' fill='none'/>" +
                "<rect x='10' width='10' height='10'/>" +
                "</g></svg>";

            using var child = new FenSvgRenderer().Render(
                string.Format(source, "rect:nth-child(2)"));
            using var ofType = new FenSvgRenderer().Render(
                string.Format(source, "rect:nth-of-type(2)"));

            Assert.False(child.RequiresFallback, string.Join("; ", child.Warnings));
            Assert.False(ofType.RequiresFallback, string.Join("; ", ofType.Warnings));
            Assert.Equal(SKColors.Red, child.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Red, child.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Red, ofType.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Green, ofType.Bitmap.GetPixel(15, 5));
        }

        [Fact]
        public void NthChildOfSelector_FiltersSiblingsBeforeIndexing()
        {
            const string source =
                "<svg width='30' height='10'><style>{0} {{ fill: green }} rect {{ fill: red }}" +
                "</style><g><rect class='hit' x='0' width='10' height='10'/>" +
                "<rect class='miss' x='10' width='10' height='10'/>" +
                "<rect class='hit' x='20' width='10' height='10'/></g></svg>";

            using var filtered = new FenSvgRenderer().Render(
                string.Format(source, "rect:nth-child(2 of .hit)"));
            using var unfiltered = new FenSvgRenderer().Render(
                string.Format(source, "rect:nth-child(2)"));

            Assert.False(filtered.RequiresFallback, string.Join("; ", filtered.Warnings));
            Assert.False(unfiltered.RequiresFallback, string.Join("; ", unfiltered.Warnings));
            Assert.Equal(SKColors.Red, filtered.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Red, filtered.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Green, filtered.Bitmap.GetPixel(25, 5));
            Assert.Equal(SKColors.Red, unfiltered.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Green, unfiltered.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Red, unfiltered.Bitmap.GetPixel(25, 5));
        }

        [Fact]
        public void NthChildOfSelector_AcceptsAComplexOfList()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "rect { fill: red } :nth-child(1 of g > rect) { fill: green }" +
                "</style><g>" +
                "<rect width='10' height='10'/><rect x='10' width='10' height='10'/>" +
                "</g><g><rect y='10' width='10' height='10'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 15));
        }

        [Fact]
        public void NthLastChildOfSelector_CountsFromTheEndOfTheFilteredList()
        {
            const string source =
                "<svg width='30' height='10'><style>{0} {{ fill: green }} rect {{ fill: red }}" +
                "</style><g><rect class='hit' x='0' width='10' height='10'/>" +
                "<rect class='miss' x='10' width='10' height='10'/>" +
                "<rect class='hit' x='20' width='10' height='10'/></g></svg>";

            using var filtered = new FenSvgRenderer().Render(
                string.Format(source, "rect:nth-last-child(2 of .hit)"));
            using var unfiltered = new FenSvgRenderer().Render(
                string.Format(source, "rect:nth-last-child(2)"));

            Assert.False(filtered.RequiresFallback, string.Join("; ", filtered.Warnings));
            Assert.False(unfiltered.RequiresFallback, string.Join("; ", unfiltered.Warnings));
            Assert.Equal(SKColors.Green, filtered.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Red, filtered.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Red, filtered.Bitmap.GetPixel(25, 5));
            Assert.Equal(SKColors.Red, unfiltered.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Green, unfiltered.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Red, unfiltered.Bitmap.GetPixel(25, 5));
        }

        [Fact]
        public void StylesheetDisplayNone_SuppressesMatchingSubtree()
        {
            const string svg =
                "<svg width='20' height='20'><style>.hidden { display: none }</style>" +
                "<g class='hidden'><rect width='20' height='20' fill='red'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void GeometryProperties_OverridePresentationAttributes()
        {
            const string svg =
                "<svg width='40' height='20'><style>" +
                "rect { x: 20px; width: 20px; height: 20px } circle { cx: 10px; cy: 10px; r: 8px }" +
                "</style><rect x='0' width='5' height='5' fill='red'/>" +
                "<circle cx='35' cy='5' r='2' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(30, 10));
        }

        [Fact]
        public void EllipseAutoRadius_UsesTheSpecifiedCompanionRadius()
        {
            const string svg =
                "<svg width='40' height='20'><style>" +
                "#a{rx:8px;ry:auto} #b{rx:auto;ry:6px}" +
                "</style><ellipse id='a' cx='10' cy='10' fill='red'/>" +
                "<ellipse id='b' cx='30' cy='10' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(30, 10));
        }

        [Fact]
        public void CssVariables_ResolveInheritedValuesAndFallbacksInGeometry()
        {
            const string svg =
                "<svg width='30' height='10'><style>g{--size:10px}</style><g>" +
                "<rect width='var(--size)' height='10' fill='red'/>" +
                "<rect x='10' width='var(--missing, 20px)' height='10' fill='blue'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void CssVariables_EnforceCssLengthGrammarAfterSubstitution()
        {
            const string svg =
                "<svg width='120' height='40'><style>" +
                "#variable{--size:100;width:var(--size)} #declaration{width:100}" +
                "</style>" +
                "<rect width='100' height='10' fill='green'/>" +
                "<rect id='variable' y='10' height='10' fill='red'/>" +
                "<rect y='20' width='var(--missing,100)' height='10' fill='blue'/>" +
                "<rect id='declaration' y='30' height='10' fill='purple'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(50, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(50, 15).Alpha);
            Assert.Equal(0, result.Bitmap.GetPixel(50, 25).Alpha);
            Assert.Equal(0, result.Bitmap.GetPixel(50, 35).Alpha);
        }

        [Fact]
        public void ViewportUnitGeometryLength_ResolvesAgainstNearestViewport()
        {
            const string actualSvg =
                "<svg width='300' height='150'><style>rect{width:10vw}</style>" +
                "<rect y='70' height='10' fill='red'/></svg>";
            const string referenceSvg =
                "<svg width='300' height='150'><rect width='30' y='70' height='10' fill='red'/></svg>";

            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            Assert.Empty(actual.FallbackReasonCodes);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssGeometryCalc_ResolvesLengthPercentagesWithoutAmbientCssState()
        {
            const string calculated =
                "<svg width='100' height='100'><rect x='1' y='1' " +
                "width='calc(100% - 2px)' height='calc(100% - 2px)' fill='green'/></svg>";
            const string reference =
                "<svg width='100' height='100'><rect x='1' y='1' width='98' height='98' fill='green'/></svg>";

            using var actual = new FenSvgRenderer().Render(calculated);
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssGeometryMath_SupportsTypedMinMaxClampAndScalarProducts()
        {
            const string calculated =
                "<svg width='100' height='100'><rect x='calc(2 * 5px)' y='max(5px, 10%)' " +
                "width='min(80%, 90px)' height='clamp(10px, 20%, 30px)' fill='blue'/></svg>";
            const string reference =
                "<svg width='100' height='100'><rect x='10' y='10' width='80' height='20' fill='blue'/></svg>";

            using var actual = new FenSvgRenderer().Render(calculated);
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssGeometryMath_UsesInheritedAndRootFontContexts()
        {
            const string calculated =
                "<svg width='120' height='20' style='font-size:20px'><g style='font-size:2em'>" +
                "<rect x='calc(80px + 10% - 2em)' width='calc(5rem)' height='10' fill='red'/>" +
                "</g></svg>";
            const string reference =
                "<svg width='120' height='20'><rect x='12' width='100' height='10' fill='red'/></svg>";

            using var actual = new FenSvgRenderer().Render(calculated);
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssGeometryMath_IgnoresInvalidDimensionsAndExcessiveNesting()
        {
            string nested = "10px";
            for (int i = 0; i < 20; i++) nested = $"calc({nested})";
            string svg =
                "<svg width='30' height='10'><rect width='calc(10px * 2px)' height='10'/>" +
                $"<rect width='{nested}' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void CssDProperty_SupportsPathFunctionAndNone()
        {
            const string svg =
                "<svg width='30' height='20'><style>#moved { d: path('M 5 15 H 25') }</style>" +
                "<path id='moved' d='M 5 5 H 25' fill='none' stroke='red' stroke-width='2'/>" +
                "<path d='M 0 0 H 30 V 20 H 0 Z' style='d:none' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(result.Bitmap.GetPixel(15, 15).Red > 200);
            Assert.Equal(0, result.Bitmap.GetPixel(15, 5).Alpha);
        }

        [Fact]
        public void CssPathLength_CalibratesStrokeDashDistances()
        {
            const string calibrated =
                "<svg width='240' height='240'><style>rect{path-length:10px}</style>" +
                "<rect x='20' y='20' width='200' height='200' fill='none' stroke='black' " +
                "stroke-width='5' stroke-dasharray='.25'/></svg>";
            const string reference =
                "<svg width='240' height='240'><rect x='20' y='20' width='200' height='200' " +
                "fill='none' stroke='black' stroke-width='5' stroke-dasharray='20'/></svg>";

            using var actual = new FenSvgRenderer().Render(calibrated);
            using var expected = new FenSvgRenderer().Render(reference);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssZoom_ScalesGeometryAndPathLengthTogether()
        {
            const string actualSvg =
                "<svg width='480' height='480'><style>rect{zoom:2;path-length:10px}</style>" +
                "<rect x='20' y='20' width='200' height='200' fill='none' stroke='black' " +
                "stroke-width='5' stroke-dasharray='.25'/></svg>";
            const string referenceSvg =
                "<svg width='480' height='480'><rect x='20' y='20' width='200' height='200' " +
                "style='zoom:200%' fill='none' stroke='black' stroke-width='5' stroke-dasharray='20'/></svg>";

            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(expected.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Theory]
        [InlineData("normal")]
        [InlineData("-1")]
        [InlineData("invalid")]
        public void CssZoom_InvalidOrNormalValuesDoNotChangeRendering(string zoom)
        {
            const string referenceSvg =
                "<svg width='40' height='40'><rect x='5' y='5' width='10' height='10' fill='red'/></svg>";
            string actualSvg =
                $"<svg width='40' height='40'><rect style='zoom:{zoom}' x='5' y='5' " +
                "width='10' height='10' fill='red'/></svg>";

            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);
            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssZoom_OnRootSvgScalesItsUserCoordinateSystem()
        {
            const string actualSvg =
                "<svg width='40' height='40' style='zoom:2'><rect x='5' y='5' width='10' height='10' fill='red'/></svg>";
            const string referenceSvg =
                "<svg width='40' height='40'><g style='zoom:2'><rect x='5' y='5' width='10' height='10' fill='red'/></g></svg>";

            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);
            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void SvgImageBoxMarginAndPadding_DoNotChangeIsolatedPicturePixels()
        {
            const string actualSvg =
                "<svg width='100' height='100' style='padding-right:30px;margin:20px'>" +
                "<style>circle{padding:10px;margin-left:5px}</style>" +
                "<circle cx='50' cy='50' r='48'/></svg>";
            const string referenceSvg =
                "<svg width='100' height='100'><circle cx='50' cy='50' r='48'/></svg>";

            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);
            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssPathLengthZero_MakesDashedStrokeSolid()
        {
            const string actualSvg =
                "<svg width='120' height='120'><style>path{path-length:0}</style>" +
                "<path d='M10,10 L110,10 L110,110 L10,110Z' stroke-dasharray='1 1' " +
                "fill='none' stroke='black' stroke-width='10'/></svg>";
            const string referenceSvg =
                "<svg width='120' height='120'><path d='M10,10 L110,10 L110,110 L10,110Z' " +
                "fill='none' stroke='black' stroke-width='10'/></svg>";

            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void HtmlMetadataElements_AreNonVisualAndDoNotRouteToFallback()
        {
            const string svg =
                "<svg width='10' height='10'>" +
                "<html:link rel='match' href='ref.svg'/><html:meta name='assert' content='x'/>" +
                "<rect width='10' height='10' fill='green'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void UnsupportedProperty_OnlyFailsClosedWhenSelectorMatches()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                ".unused { filter: blur(2px) } .used { unicode-bidi: isolate-override }" +
                "</style><text class='used' x='0' y='12' font-size='8'>hi</text>" +
                "<rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("unicode-bidi", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("filter", StringComparison.Ordinal));
        }

        [Fact]
        public void UnsupportedProperty_OnAnUnmatchedSelector_RendersCleanly()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                ".unused { filter: blur(2px) } rect { fill: red }" +
                "</style><rect class='used' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.False(result.HadResourceRejection);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void WidthMediaQuery_IsLocalToSvgViewport()
        {
            const string source =
                "<svg width='{0}' height='10'><style>" +
                "@media (min-width: 50px) {{ rect {{ fill: red }} }}" +
                "</style><rect width='100%' height='10' fill='blue'/></svg>";

            using var narrow = new FenSvgRenderer().Render(string.Format(source, 40));
            using var wide = new FenSvgRenderer().Render(string.Format(source, 60));

            Assert.Equal(SKColors.Blue, narrow.Bitmap.GetPixel(20, 5));
            Assert.Equal(SKColors.Red, wide.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void ScreenAndWidthMediaQuery_IsLocalToSvgViewport()
        {
            const string source =
                "<svg width='{0}' height='10'><style>" +
                "@media screen and (min-width: 50px) {{ rect {{ fill: red }} }}" +
                "@media only all and (max-width: 50px) {{ rect {{ fill: lime }} }}" +
                "</style><rect width='100%' height='10' fill='blue'/></svg>";

            using var narrow = new FenSvgRenderer().Render(string.Format(source, 40));
            using var wide = new FenSvgRenderer().Render(string.Format(source, 60));

            Assert.False(narrow.RequiresFallback, string.Join("; ", narrow.Warnings));
            Assert.False(wide.RequiresFallback, string.Join("; ", wide.Warnings));
            Assert.Equal(SKColors.Lime, narrow.Bitmap.GetPixel(20, 5));
            Assert.Equal(SKColors.Red, wide.Bitmap.GetPixel(20, 5));
        }

        [Theory]
        [InlineData("not (min-width: 50px)", 40, "lime")]
        [InlineData("not (min-width: 50px)", 60, "blue")]
        [InlineData("not all and (max-width: 50px)", 40, "blue")]
        [InlineData("not all and (max-width: 50px)", 60, "lime")]
        [InlineData("not screen", 40, "blue")]
        [InlineData("screen", 40, "lime")]
        [InlineData("(max-width: 50px), (min-width: 50px)", 40, "lime")]
        [InlineData("(max-width: 50px), (min-width: 50px)", 60, "lime")]
        [InlineData("(min-width: 30px) and (max-width: 50px)", 40, "lime")]
        [InlineData("(min-width: 30px) and (max-width: 50px)", 60, "blue")]
        [InlineData("(min-width: 70px) or (max-width: 50px)", 40, "lime")]
        [InlineData("(min-width: 70px) or (max-width: 50px)", 60, "blue")]
        [InlineData("not ((min-width: 30px) and (max-width: 50px))", 40, "blue")]
        [InlineData("not ((min-width: 30px) and (max-width: 50px))", 60, "blue")]
        [InlineData("screen and (min-width: 30px) and (max-width: 50px)", 40, "lime")]
        [InlineData("only screen and (min-width: 30px) and (max-width: 50px)", 60, "blue")]
        [InlineData("(width: 40px)", 40, "lime")]
        [InlineData("(width: 40px)", 60, "blue")]
        public void MediaConditionKeywords_AreEvaluatedAgainstTheSvgViewport(
            string condition, int width, string expected)
        {
            const string source =
                "<svg width='{0}' height='10'><style>" +
                "rect {{ fill: blue }}" +
                "@media {1} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='100%' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(
                string.Format(source, width, condition));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(ExpectedColor(expected), result.Bitmap.GetPixel(20, 5));
        }

        [Theory]
        [InlineData("(min-resolution: 1dppx)", "lime")]
        [InlineData("(min-resolution: 96dpi)", "lime")]
        [InlineData("(max-resolution: 1dppx)", "lime")]
        [InlineData("(resolution: 1x)", "lime")]
        [InlineData("(min-resolution: 2dppx)", "blue")]
        [InlineData("(min-device-pixel-ratio: 3)", "blue")]
        [InlineData("(max-device-pixel-ratio: 2)", "lime")]
        [InlineData("(color)", "lime")]
        [InlineData("(min-color: 0)", "lime")]
        [InlineData("(min-color: 24)", "blue")]
        [InlineData("(max-monochrome: 0)", "lime")]
        [InlineData("(monochrome)", "blue")]
        [InlineData("(min-monochrome: 1)", "blue")]
        [InlineData("(min-device-width: 30px)", "lime")]
        [InlineData("(max-device-width: 30px)", "blue")]
        public void PictureScopedMediaFeatures_AnswerOnlyForTheStaticRaster(
            string condition, string expected)
        {
            string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                $"@media {condition} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(ExpectedColor(expected), result.Bitmap.GetPixel(20, 5));
        }

        [Theory]
        [InlineData("(orientation: portrait)")]
        [InlineData("(orientation: landscape)")]
        [InlineData("(aspect-ratio: 16/9)")]
        [InlineData("(prefers-color-scheme: dark)")]
        [InlineData("(hover)")]
        [InlineData("(hover: hover)")]
        [InlineData("(any-hover: hover)")]
        [InlineData("(pointer: fine)")]
        [InlineData("(any-pointer: coarse)")]
        [InlineData("(prefers-reduced-motion: reduce)")]
        [InlineData("(prefers-contrast: more)")]
        [InlineData("(forced-colors: active)")]
        [InlineData("(dynamic-range: high)")]
        [InlineData("(video-dynamic-range: high)")]
        [InlineData("(color-gamut: p3)")]
        [InlineData("(color-index: 256)")]
        [InlineData("(grid: 0)")]
        [InlineData("(update: fast)")]
        [InlineData("(scripting: enabled)")]
        [InlineData("(inverted-colors: inverted)")]
        [InlineData("(overflow-block: scroll)")]
        [InlineData("(overflow-inline: scroll)")]
        [InlineData("(prefers-reduced-data: reduce)")]
        [InlineData("(prefers-reduced-transparency: reduce)")]
        [InlineData("(min-height: 1px)")]
        [InlineData("(max-device-height: 1px)")]
        [InlineData("(device-aspect-ratio: 1/1)")]
        public void RealMediaFeaturesTheRendererCannotAnswer_FailClosed(string condition)
        {
            string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                $"@media {condition} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("media query feature", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("print")]
        [InlineData("speech")]
        [InlineData("not all")]
        public void UndeterminableMediaTypes_FailClosed(string condition)
        {
            string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                $"@media {condition} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            if (condition == "not all")
            {
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
                Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 5));
                return;
            }

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("media query type", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("(unknown-feature: 1)")]
        [InlineData("(totally-made-up: yes)")]
        [InlineData("(min-width: 50%)")]
        [InlineData("(max-width: 50)")]
        [InlineData("(min-width: bogus)")]
        [InlineData("(prefers-color-scheme:)")]
        public void InertMediaConditions_StayNonApplicableWithoutFailingClosed(string condition)
        {
            string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                $"@media {condition} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 5));
        }

        [Theory]
        [InlineData("screen, (prefers-color-scheme: dark)", "lime")]
        [InlineData("(prefers-color-scheme: dark), screen", "lime")]
        [InlineData("(prefers-color-scheme: dark) or (min-width: 10px)", "lime")]
        [InlineData("(min-width: 10px) or (prefers-color-scheme: dark)", "lime")]
        [InlineData("(prefers-color-scheme: dark) and (min-width: 500px)", "blue")]
        [InlineData("(min-width: 500px) and (prefers-color-scheme: dark)", "blue")]
        public void UndeterminedMediaFeature_OnlyFailsClosedWhenItDecidesTheCondition(
            string condition, string expected)
        {
            string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                $"@media {condition} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(ExpectedColor(expected), result.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void NegatedUndeterminedMediaFeature_FailsClosed()
        {
            const string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                "@media not (prefers-color-scheme: dark) { rect { fill: lime } }" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void StyleMediaAttribute_UndeterminedFeature_FailsClosed()
        {
            const string svg =
                "<svg width='40' height='10'>" +
                "<style media='(prefers-color-scheme: dark)'>rect { fill: lime }</style>" +
                "<rect width='40' height='10' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void StyleMediaAttribute_AcceptsNotAndOrConditions()
        {
            const string svg =
                "<svg width='40' height='10'>" +
                "<style>rect { fill: blue }</style>" +
                "<style media='not (min-width: 50px)'>rect { fill: lime }</style>" +
                "<style media='(min-width: 30px) and (max-width: 50px)'>rect { stroke: red }</style>" +
                "<rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void NotAllMediaCondition_StaysNonApplicable()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "@media not all { rect { fill: orange } }" +
                "rect { fill: red }" +
                "</style><rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("(min-width: 50%)")]
        [InlineData("(max-width: 50%)")]
        [InlineData("(min-width: 50)")]
        [InlineData("(min-device-width: 50%)")]
        [InlineData("(min-width: bogus)")]
        public void InvalidMediaFeatureLengths_StayNonApplicable(string condition)
        {
            string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: blue }" +
                $"@media {condition} {{ rect {{ fill: lime }} }}" +
                "</style><rect width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void EmptyMediaQueryList_IsDroppedLikeEveryEngineDropsIt()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "@media { rect { fill: lime } }" +
                "rect { fill: red }" +
                "</style><rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void StyleMediaAttributePrint_FailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><style media='print'>rect { fill: lime }</style>" +
                "<rect width='20' height='20' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("@scope (.a) { rect { fill: lime } }")]
        [InlineData("@scope (.a) to (.b) { rect { fill: lime } }")]
        [InlineData("@scope { rect { fill: lime } }")]
        [InlineData("@scope (.a) { @media screen { rect { fill: lime } } }")]
        public void ScopeRule_FailsClosedBecauseEveryEngineAppliesIt(string rule)
        {
            string svg =
                "<svg width='20' height='20'><style>" +
                rule +
                "</style><rect class='a' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void DynamicPseudoClassRules_StayNonMatchingInsteadOfDiscardingTheDocument()
        {
            const string svg =
                "<svg width='60' height='20'><style>" +
                "text { fill: black }" +
                ":link { fill: rgb(51, 0, 255) }" +
                ":visited { fill: purple }" +
                ":hover { fill: rgb(255, 140, 0) }" +
                "rect:hover { fill: none }" +
                "#act:focus { stroke: red }" +
                "text:active { text-decoration: underline; fill: red }" +
                "</style><text id='act' x='2' y='14' font-size='12'>act</text>" +
                "<rect x='30' y='2' width='16' height='16' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(38, 10));
        }

        [Fact]
        public void TargetRule_LeavesTheBaseStateUntouched()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" +
                "<style>:target { fill: green }</style>" +
                "<rect id='rect' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void DynamicPseudoClassRule_DoesNotSuppressBaseStateDeclarations()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                ":hover { text-decoration: underline } rect { fill: red }" +
                "</style><rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void NotDynamicPseudoClass_MatchesInTheStaticBaseState()
        {
            const string svg =
                "<svg width='20' height='20'><style>rect:not(:hover) { fill: lime }</style>" +
                "<rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("rect:has(circle) { fill: lime }")]
        [InlineData("rect:dir(rtl) { fill: lime }")]
        [InlineData("rect:nth-child(2n of circle:dir(rtl)) { fill: lime }")]
        [InlineData("rect:nth-child(nonsense) { fill: lime }")]
        [InlineData("rect:nth-of-type(2n of circle) { fill: lime }")]
        public void UnsupportedPseudoClasses_StillFailClosed(string rule)
        {
            string svg =
                "<svg width='20' height='20'><style>" + rule +
                "</style><rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void PseudoElementRule_IsNotApplicableWithoutDiscardingTheDocument()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "rect { fill: black } rect::first-letter { fill: green }" +
                "circle::before { fill: green }" +
                "</style><rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void LangRule_MatchesXmlLangRangesInTheStaticTree()
        {
            const string svg =
                "<svg width='50' height='10'><style>" +
                ":lang(en) { fill: green }" +
                ":lang(fr) { fill: blue }" +
                ":lang(fr-ca) { fill: purple }" +
                "</style><rect width='10' height='10' xml:lang='en'/>" +
                "<rect x='10' width='10' height='10' xml:lang='fr'/>" +
                "<rect x='20' width='10' height='10' xml:lang='fr-CA'/>" +
                "<rect x='30' width='10' height='10' xml:lang='de'/>" +
                "<rect x='40' width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Purple, result.Bitmap.GetPixel(25, 5));
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(35, 5));
            Assert.Equal(SKColors.Black, result.Bitmap.GetPixel(45, 5));
        }

        [Fact]
        public void LangRule_WithoutMatchingLanguage_LeavesTheDocumentRenderable()
        {
            const string svg =
                "<svg width='20' height='20'><style>tspan:lang(ja) { fill: lime }</style>" +
                "<text><tspan x='2' y='16' font-size='12'>Quick</tspan></text>" +
                "<rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void ScopePseudoClass_MatchesTheDocumentRootInADocumentStylesheet()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "g rect { fill: red } :scope { fill: lime }" +
                "</style><g><rect width='10' height='20'/><circle cx='15' cy='10' r='4'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 10));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(15, 10));
        }

        [Fact]
        public void ScopePseudoClass_DoesNotMatchDescendants()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                ":scope { fill: lime } g { fill: red }" +
                "</style><g><rect width='20' height='20'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void ScopePseudoClass_ComposesWithADescendantCombinator()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "g { fill: red } :scope g { fill: lime }" +
                "</style><g><rect width='20' height='20'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("#target", "lime")]
        [InlineData(":where(.hit)", "red")]
        [InlineData(":where(#target)", "red")]
        [InlineData(":is(.hit)", "red")]
        [InlineData(":not(.miss)", "red")]
        [InlineData(":is(.hit, #target)", "lime")]
        [InlineData("rect", "red")]
        public void FunctionalPseudoClassSpecificity_FollowsSelectors4(string rule, string expected)
        {
            // The competing rule is always the last declaration in the sheet, so each
            // case is decided by specificity alone and never by source order.
            const string source =
                "<svg width='20' height='10'><style>{0} {{ fill: lime }} .hit {{ fill: red }}</style>" +
                "<rect id='target' class='hit' width='20' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(string.Format(source, rule));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(ExpectedColor(expected), result.Bitmap.GetPixel(10, 5));
        }

        [Fact]
        public void UseShadowDescendantMatching_StopsAtTheClonedUseRoot()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "g rect { fill: lime } rect { fill: red }" +
                "</style><g><defs><rect id='shape' width='20' height='20'/></defs>" +
                "<use href='#shape'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void UseShadowDescendantMatching_StillAppliesBelowTheClonedUseRoot()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "g rect { fill: lime }" +
                "</style><g><defs><g id='shape'><rect width='20' height='20'/></g></defs>" +
                "<use href='#shape'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void UseShadowCascade_StaysCompleteUpToTheRootBudget()
        {
            string svg = BuildUseShadowDocument(64);

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
        }

        [Fact]
        public void UseShadowCascadeRootBudgetOverflow_FailsClosed()
        {
            string svg = BuildUseShadowDocument(65);

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("admission-budget", result.FallbackReasonCodes);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("root budget (64) exceeded", StringComparison.Ordinal));
        }

        [Fact]
        public void UseShadowCascadeElementBudgetOverflow_FailsClosed()
        {
            const int perTarget = 17000;
            var builder = new StringBuilder();
            builder.Append(
                "<svg width='20' height='20'><style>defs > g > rect { fill: blue }</style><defs>");
            for (int group = 0; group < 2; group++)
            {
                builder.Append($"<g id='t{group}'>");
                for (int i = 0; i < perTarget; i++) builder.Append("<rect width='1' height='1'/>");
                builder.Append("</g>");
            }
            builder.Append("</defs><use href='#t0' display='none'/><use href='#t1'/></svg>");

            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 8000;

            using var result = new FenSvgRenderer().Render(builder.ToString(), limits);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("admission-budget", result.FallbackReasonCodes);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("element budget (32768) exceeded", StringComparison.Ordinal));
        }

        private static string BuildUseShadowDocument(int targets)
        {
            var builder = new StringBuilder();
            builder.Append(
                "<svg width='20' height='20'><style>defs > g > rect { fill: blue }</style><defs>");
            for (int i = 0; i < targets; i++)
            {
                builder.Append($"<g id='t{i}'><rect width='2' height='2'/></g>");
            }
            builder.Append("</defs>");
            for (int i = 0; i < targets; i++)
            {
                builder.Append($"<use href='#t{i}'/>");
            }
            builder.Append("</svg>");
            return builder.ToString();
        }

        [Fact]
        public void NoEffectHintProperties_AreAcceptedWithoutDiscardingTheDocument()
        {
            const string svg =
                "<svg width='20' height='20'><style>rect {" +
                "shape-rendering: geometricPrecision; image-rendering: smooth; " +
                "text-rendering: optimizeLegibility; color-interpolation-filters: sRGB; " +
                "enable-background: new; overflow: visible; fill: red }" +
                "</style><rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("rect { shape-rendering: crispEdges }")]
        [InlineData("rect { image-rendering: pixelated }")]
        [InlineData("svg { overflow: bogus }")]
        [InlineData("marker { overflow: bogus }")]
        public void HintPropertiesThatChangePixels_StillFailClosed(string rule)
        {
            string svg =
                "<svg width='20' height='20'><defs>" +
                "<marker id='m' markerWidth='2' markerHeight='2'><rect width='2' height='2'/></marker>" +
                "</defs><style>" + rule +
                "</style><rect width='20' height='20' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
        }

        [Fact]
        public void OverflowOutsideViewportElements_IsANoOp()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "rect { overflow: hidden } circle { overflow: scroll }" +
                "</style><rect width='20' height='20' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void BackgroundOnNonViewportElements_IsInert()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "g { background: red } rect { background-color: lime } " +
                "tspan { background-image: url(#paint) } text { content: 'x' }" +
                "</style><g><rect width='20' height='20' fill='red'/>" +
                "<text x='0' y='10'><tspan>plain</tspan></text></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void BackgroundOnAViewportElement_StillFailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><style>svg { background-color: red }</style>" +
                "<rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, w => w.Contains("background-color", StringComparison.Ordinal));
        }

        [Fact]
        public void BackgroundOnANestedViewport_StillFailsClosed()
        {
            const string svg =
                "<svg width='20' height='20'><svg width='10' height='10' style='background: red'>" +
                "<rect width='10' height='10' fill='blue'/></svg></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void InertBackgroundWithAnExternalUrl_IsStillRejectedAsAResource()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "rect { background-image: url(https://example.invalid/bg.png) }" +
                "</style><rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void ContentOnlyPaintsThroughPseudoElements_SoItStaysInert()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                "rect { content: counters(c, '.', decimal); fill: red }" +
                "rect::before { content: 'x' }" +
                "</style><rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("text-decoration-color: red")]
        [InlineData("writing-mode: tb-rl")]
        [InlineData("unicode-bidi: isolate")]
        [InlineData("unicode-bidi: isolate-override")]
        [InlineData("line-spacing: 1.25")]
        [InlineData("white-space: pre-line")]
        [InlineData("text-align: center")]
        [InlineData("inline-size: 320px")]
        [InlineData("text-orientation: sideways")]
        [InlineData("shape-inside: url(#s)")]
        [InlineData("shape-margin: 20px")]
        [InlineData("shape-padding: 5px")]
        [InlineData("shape-subtract: circle()")]
        [InlineData("font-size-adjust: 1")]
        [InlineData("z-index: 1")]
        public void VisiblyRenderedProperties_StillFailClosed(string declaration)
        {
            string svg =
                "<svg width='20' height='20'><style>text {" + declaration +
                "} rect { fill: red }</style><text x='0' y='12' font-size='8'>hi</text>" +
                "<rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("inline-size", "320px", "line box")]
        [InlineData("writing-mode", "tb-rl", "vertical block flow")]
        [InlineData("text-align", "center", "block-size")]
        [InlineData("white-space", "pre-line", "newline")]
        [InlineData("z-index", "1", "document order")]
        [InlineData("text-decoration-color", "red", "per-run colour")]
        [InlineData("font-size-adjust", "1", "x-height")]
        [InlineData("font-size-adjust", "from-font", "x-height")]
        [InlineData("font-size-adjust", "0.5", "x-height")]
        [InlineData("shape-inside", "url(#s)", "exclusion shape")]
        [InlineData("line-spacing", "1.25", "not a CSS property")]
        public void RejectedProperty_ReportsTheCapabilityItWouldNeed(
            string property,
            string value,
            string expected)
        {
            string svg =
                "<svg width='20' height='20'><style>text {" + property + ": " + value +
                "}</style><text x='0' y='12' font-size='8'>hi</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains($"SVG CSS property '{property}'", string.Join("\n", result.Warnings));
            Assert.Contains(expected, string.Join("\n", result.Warnings));
        }

        [Theory]
        [InlineData("isolate")]
        [InlineData("isolate-override")]
        public void UnicodeBidiIsolatingValue_ReportsTheRunSequenceItWouldNeed(string value)
        {
            string svg =
                "<svg width='20' height='20'><style>text { unicode-bidi: " + value +
                "}</style><text x='0' y='12' font-size='8'>hi</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains("isolating run sequence", string.Join("\n", result.Warnings));
        }

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("plaintext isolate")]
        [InlineData("embed isolate")]
        [InlineData("isolate bidi-override")]
        [InlineData("embed plaintext")]
        [InlineData("plaintext embed")]
        [InlineData("bidi-override plaintext")]
        [InlineData("embed bidi-override plaintext")]
        public void ACombinedUnicodeBidiKeyword_ReportsTheRunSequenceItWouldNeed(string value)
        {
            // The declaration is a keyword set, so a whole-value comparison against
            // "isolate" and "isolate-override" never sees it. Recording it instead
            // paints a frame a browser never opens and still reports success.
            string svg =
                "<svg width='20' height='20'><style>text { unicode-bidi: " + value +
                "}</style><text x='0' y='12' font-size='8'>hi</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains("unicode-bidi", string.Join("\n", result.Warnings));
            Assert.Contains(value, string.Join("\n", result.Warnings));
        }

        [Theory]
        [InlineData("embed plaintext", "combines keywords")]
        [InlineData("plaintext embed", "combines keywords")]
        [InlineData("bidi-override plaintext", "combines keywords")]
        [InlineData("embed bidi-override plaintext", "combines keywords")]
        [InlineData("isolate plaintext", "isolating run sequence")]
        [InlineData("embed isolate", "isolating run sequence")]
        [InlineData("isolate-override plaintext", "isolating run sequence")]
        public void AUnicodeBidiDiagnosticNamesTheGapThatActuallyApplies(string value, string expected)
        {
            // A value that isolates and a value that only combines keywords are
            // refused for different reasons, and a diagnostic that named the
            // isolating run sequence for a value that isolates nothing would send
            // an operator looking for a stage the document never reached.
            string svg =
                "<svg width='20' height='20'><style>text { unicode-bidi: " + value +
                "}</style><text x='0' y='12' font-size='8'>hi</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            string warnings = string.Join("\n", result.Warnings);
            Assert.Contains(expected, warnings);
            if (expected == "combines keywords")
            {
                Assert.DoesNotContain("isolating run sequence", warnings);
            }
        }

        [Theory]
        [InlineData("unicode-bidi: isolate plaintext")]
        [InlineData("unicode-bidi: embed isolate")]
        [InlineData("unicode-bidi: embed plaintext")]
        [InlineData("unicode-bidi: embed bidi-override plaintext")]
        public void ACombinedUnicodeBidiKeyword_StillFailsClosed(string declaration)
        {
            string svg =
                "<svg width='20' height='20'><style>text {" + declaration +
                "} rect { fill: red }</style><text x='0' y='12' font-size='8'>hi</text>" +
                "<rect width='20' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void CssDirectionAndUnicodeBidi_ReachTheTextLayoutPass()
        {
            AssertSamePixels(
                "<svg width='220' height='40'><style>text { direction: rtl }</style>" +
                "<text x='100' y='28' font-size='20' fill='lime'>Ag</text></svg>",
                "<svg width='220' height='40'>" +
                "<text x='100' y='28' font-size='20' fill='lime' direction='rtl'>Ag</text></svg>");
            AssertSamePixels(
                "<svg width='220' height='40'><style>text { unicode-bidi: bidi-override }" +
                "</style><text x='4' y='28' font-size='20' fill='lime'>Ag</text></svg>",
                "<svg width='220' height='40'>" +
                "<text x='4' y='28' font-size='20' fill='lime' unicode-bidi='bidi-override'>" +
                "Ag</text></svg>");
            AssertDifferentPixels(
                "<svg width='220' height='40'><style>text { direction: rtl }</style>" +
                "<text x='100' y='28' font-size='20' fill='lime'>Ag</text></svg>",
                "<svg width='220' height='40'><style>text { direction: ltr }</style>" +
                "<text x='100' y='28' font-size='20' fill='lime'>Ag</text></svg>");
        }

        [Fact]
        public void FirstLetterPseudoElement_ReportsTheTextModelGapItWouldNeed()
        {
            const string svg =
                "<svg width='20' height='20'><style>text::first-letter { fill: green }" +
                "</style><text x='0' y='12' font-size='8'>hi</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains("'::first-letter'", string.Join("\n", result.Warnings));
        }

        [Fact]
        public void TextDecorationUnderline_RendersInsteadOfFailingClosed()
        {
            const string svg =
                "<svg width='20' height='20'><style>text { text-decoration: underline }" +
                "</style><text x='0' y='12' font-size='8' fill='black'>hi</text>" +
                "<rect width='20' height='20' fill='white'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.DoesNotContain("css-cascade", result.FallbackReasonCodes);
            Assert.NotNull(result.Bitmap);
        }

        [Fact]
        public void ClipOverflowOnNestedSvg_KeepsClippingTheViewport()
        {
            const string svg =
                "<svg width='20' height='20'><style>svg { overflow: clip }</style>" +
                "<svg width='10' height='10'><rect width='20' height='20' fill='red'/></svg></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(0, result.Bitmap.GetPixel(15, 15).Alpha);
        }

        [Fact]
        public void CascadePrecedence_SurvivesNonApplicableChains()
        {
            const string svg =
                "<svg width='40' height='10'><style>" +
                "rect { fill: red }" +
                ".winner { fill: green }" +
                "#target:hover { fill: purple }" +
                "#target::first-letter { fill: purple }" +
                "</style><rect id='target' class='winner' width='40' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(20, 5));
        }

        [Fact]
        public void SelfClosingStyle_DoesNotConsumeFollowingGeometry()
        {
            const string svg =
                "<svg width='10' height='10'><style/><rect width='10' height='10' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public async Task StylesheetCascade_IsSafeAcrossConcurrentRenders()
        {
            var renderer = new FenSvgRenderer();
            var tasks = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            {
                string color = index % 2 == 0 ? "red" : "blue";
                using var result = renderer.Render(
                    $"<svg width='8' height='8'><style>rect{{fill:{color}}}</style><rect width='8' height='8'/></svg>");
                Assert.True(result.Success, result.ErrorMessage);
                Assert.Equal(index % 2 == 0 ? SKColors.Red : SKColors.Blue, result.Bitmap.GetPixel(4, 4));
            }));

            await Task.WhenAll(tasks);
        }

        [Fact]
        public void CssExternalResource_FailsClosedWithoutPixels()
        {
            const string svg =
                "<svg width='10' height='10'><style>rect { fill: url(https://example.invalid/a.svg#p) }</style>" +
                "<rect width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
            Assert.Contains(result.Warnings, warning => warning.Contains("external resource", StringComparison.Ordinal));
        }

        [Fact]
        public void UnconsumedFontFaceUrl_DoesNotBecomeAResourceRejection()
        {
            const string svg =
                "<svg width='80' height='30'><style>" +
                "@font-face{font-family:Unused;src:url(https://example.invalid/font.woff)}" +
                "text{font-family:sans-serif}</style><text x='2' y='20'>safe</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.HadResourceRejection);
            Assert.False(result.RequiresFallback);
        }

        [Fact]
        public void CssImport_FailsClosedAsAResourceRejection()
        {
            const string svg =
                "<svg width='10' height='10'><style>@import url(https://example.invalid/a.css);" +
                "rect{fill:red}</style><rect width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.HadResourceRejection);
        }

        [Fact]
        public void ExternalImage_FailsClosedWithoutPixels()
        {
            const string svg =
                "<svg width='10' height='10'><style>rect { filter: blur(1px) }</style>" +
                "<rect width='10' height='10'/><image href='https://example.invalid/a.png' width='10' height='10'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.True(result.HadResourceRejection);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        }

        [Fact]
        public void GroupFillOpacity_InheritsIntoDescendantShapes()
        {
            const string svg =
                "<svg width='30' height='10'><style>g { fill-opacity: 0.5 }</style>" +
                "<g fill='red'><rect width='10' height='10'/>" +
                "<rect x='10' width='10' height='10' fill-opacity='1'/>" +
                "<rect x='20' width='10' height='10' fill-opacity='0.25'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            var inherited = result.Bitmap.GetPixel(5, 5);
            var overridden = result.Bitmap.GetPixel(15, 5);
            var replaced = result.Bitmap.GetPixel(25, 5);
            Assert.Equal((byte)255, inherited.Red);
            Assert.InRange(inherited.Alpha, 110, 145);
            Assert.Equal((byte)255, overridden.Alpha);
            Assert.Equal((byte)255, replaced.Red);
            Assert.InRange(replaced.Alpha, 50, 80);
        }

        [Fact]
        public void GroupStrokeOpacity_InheritsIntoDescendantShapes()
        {
            const string svg =
                "<svg width='30' height='30'>" +
                "<line x1='2' y1='4' x2='28' y2='4' stroke='blue' stroke-width='6'/>" +
                "<g stroke='blue' stroke-width='6' stroke-opacity='0.5'>" +
                "<line x1='2' y1='14' x2='28' y2='14'/>" +
                "<line x1='2' y1='24' x2='28' y2='24' stroke-opacity='1'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            var plain = result.Bitmap.GetPixel(15, 4);
            var inherited = result.Bitmap.GetPixel(15, 14);
            var overridden = result.Bitmap.GetPixel(15, 24);
            Assert.Equal((byte)255, plain.Alpha);
            Assert.Equal((byte)255, plain.Blue);
            Assert.Equal((byte)255, inherited.Blue);
            Assert.InRange(inherited.Alpha, 110, 145);
            Assert.Equal((byte)255, overridden.Alpha);
        }

        [Fact]
        public void InheritedFillOpacity_ClampsOutOfRangeAndZeroSuppressesPaint()
        {
            const string svg =
                "<svg width='40' height='20'><g fill='red'>" +
                "<rect width='10' height='20' fill-opacity='5'/>" +
                "<rect x='10' width='10' height='20' fill-opacity='0'/>" +
                "<rect x='20' width='10' height='20' fill-opacity='bogus'/>" +
                "<rect x='30' width='10' height='20'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(5, 10).Alpha);
            Assert.Equal(0, result.Bitmap.GetPixel(15, 10).Alpha);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(25, 10).Alpha);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(35, 10).Alpha);
        }

        [Fact]
        public void NonPositiveStrokeOpacity_SuppressesTheStroke()
        {
            const string svg =
                "<svg width='30' height='20'><g stroke='blue' stroke-width='6'>" +
                "<line x1='2' y1='6' x2='28' y2='6' stroke-opacity='-1'/>" +
                "<line x1='2' y1='14' x2='28' y2='14'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(0, result.Bitmap.GetPixel(15, 6).Alpha);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(15, 14).Alpha);
        }

        [Fact]
        public void PercentageFillOpacity_ClampsToTheUnitRange()
        {
            const string svg =
                "<svg width='40' height='20'><g fill='red'>" +
                "<rect width='10' height='20' fill-opacity='50%'/>" +
                "<rect x='10' width='10' height='20' fill-opacity='25%'/>" +
                "<rect x='20' width='10' height='20' fill-opacity='150%'/>" +
                "<rect x='30' width='10' height='20' fill-opacity='-50%'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.InRange(result.Bitmap.GetPixel(5, 10).Alpha, 120, 135);
            Assert.InRange(result.Bitmap.GetPixel(15, 10).Alpha, 56, 71);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(25, 10).Alpha);
            Assert.Equal(0, result.Bitmap.GetPixel(35, 10).Alpha);
        }

        [Fact]
        public void PercentageStrokeOpacity_ClampsToTheUnitRange()
        {
            const string svg =
                "<svg width='30' height='20'><g stroke='blue' stroke-width='6'>" +
                "<line x1='2' y1='6' x2='28' y2='6' stroke-opacity='50%'/>" +
                "<line x1='2' y1='14' x2='28' y2='14' stroke-opacity='0%'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.InRange(result.Bitmap.GetPixel(15, 6).Alpha, 120, 135);
            Assert.Equal(0, result.Bitmap.GetPixel(15, 14).Alpha);
        }

        [Fact]
        public void OpacityKeywords_MatchTheInheritedStyleClassification()
        {
            const string svg =
                "<svg width='30' height='10'><g fill='red' fill-opacity='0.5'>" +
                "<rect width='10' height='10'/>" +
                "<rect x='10' width='10' height='10' fill-opacity='initial'/>" +
                "<rect x='20' width='10' height='10' fill-opacity='inherit'/>" +
                "</g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.InRange(result.Bitmap.GetPixel(5, 5).Alpha, 120, 135);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(15, 5).Alpha);
            Assert.InRange(result.Bitmap.GetPixel(25, 5).Alpha, 120, 135);
        }

        [Fact]
        public void PercentageFillOpacity_ReplacesNonUnitInheritedOpacity()
        {
            const string svg =
                "<svg width='30' height='20'>" +
                "<g fill='red' fill-opacity='0.5'><rect width='10' height='20' fill-opacity='50%'/></g>" +
                "<g fill='red' fill-opacity='50%'><rect x='10' width='10' height='20'/></g>" +
                "<g fill='red' fill-opacity='50%'><rect x='20' width='10' height='20' fill-opacity='25%'/></g>" +
                "</svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.InRange(result.Bitmap.GetPixel(5, 10).Alpha, 120, 135);
            Assert.InRange(result.Bitmap.GetPixel(15, 10).Alpha, 120, 135);
            Assert.InRange(result.Bitmap.GetPixel(25, 10).Alpha, 56, 71);
        }

        [Fact]
        public void PercentageStrokeOpacity_ReplacesNonUnitInheritedOpacity()
        {
            const string svg =
                "<svg width='30' height='20'>" +
                "<g stroke='blue' stroke-width='6' stroke-opacity='0.5'>" +
                "<line x1='2' y1='6' x2='28' y2='6' stroke-opacity='50%'/></g>" +
                "<g stroke='blue' stroke-width='6' stroke-opacity='50%'>" +
                "<line x1='2' y1='14' x2='28' y2='14'/></g>" +
                "</svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(15, 6).Blue);
            Assert.InRange(result.Bitmap.GetPixel(15, 6).Alpha, 120, 135);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(15, 14).Blue);
            Assert.InRange(result.Bitmap.GetPixel(15, 14).Alpha, 120, 135);
        }

        [Fact]
        public void GradientTemplateChain_ResolvesStopsThroughEveryHref()
        {
            var svg = new StringBuilder("<svg width='30' height='10'><defs>");
            svg.Append("<linearGradient id='g4'>");
            for (int i = 0; i <= 16; i++)
            {
                svg.Append("<stop offset='").Append((i * 100 + 8) / 16)
                    .Append("%' stop-color='red'/>");
            }
            svg.Append("</linearGradient>")
                .Append("<linearGradient id='g3' href='#g4'/>")
                .Append("<linearGradient id='g2' href='#g3'/>")
                .Append("<linearGradient id='g1' href='#g2'/>")
                .Append("</defs>")
                .Append("<rect width='10' height='10' fill='url(#g1)'/>")
                .Append("<rect x='10' width='10' height='10' fill='url(#g2)'/>")
                .Append("<rect x='20' width='10' height='10' fill='url(#g3)'/></svg>");

            using var result = new FenSvgRenderer().Render(svg.ToString());

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(15, 5));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(25, 5));
        }

        [Fact]
        public void InheritedFillOpacity_AppliesToPaintServerFill()
        {
            const string svg =
                "<svg width='20' height='20'><defs>" +
                "<linearGradient id='g'><stop offset='0' stop-color='red'/>" +
                "<stop offset='1' stop-color='red'/></linearGradient></defs>" +
                "<g fill-opacity='0.5'><rect width='20' height='20' fill='url(#g)'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            var pixel = result.Bitmap.GetPixel(10, 10);
            Assert.Equal((byte)255, pixel.Red);
            Assert.InRange(pixel.Alpha, 110, 145);
        }

        [Fact]
        public void InheritedFillOpacity_ReachesNestedGroups()
        {
            const string svg =
                "<svg width='20' height='20'><g fill='red' fill-opacity='0.5'>" +
                "<g fill-opacity='0.5'><rect width='20' height='20'/></g></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            var pixel = result.Bitmap.GetPixel(10, 10);
            Assert.Equal((byte)255, pixel.Red);
            Assert.InRange(pixel.Alpha, 110, 145);
        }

        [Fact]
        public void CssFontSizeClamp_BoundsEmGeometryToTheMaximumFontSize()
        {
            const string actual =
                "<svg width='600' height='40'><rect style='font-size:20000px' width='0.1em' " +
                "height='40' fill='red'/></svg>";
            const string clamped =
                "<svg width='600' height='40'><rect style='font-size:4096px' width='0.1em' " +
                "height='40' fill='red'/></svg>";

            using var oversized = new FenSvgRenderer().Render(actual);
            using var bounded = new FenSvgRenderer().Render(clamped);

            Assert.True(oversized.Success, oversized.ErrorMessage);
            Assert.False(oversized.RequiresFallback, string.Join("; ", oversized.Warnings));
            Assert.True(bounded.Success, bounded.ErrorMessage);
            Assert.Equal((byte)255, bounded.Bitmap.GetPixel(400, 20).Alpha);
            Assert.Equal(0, bounded.Bitmap.GetPixel(450, 20).Alpha);
            for (int y = 0; y < oversized.Bitmap.Height; y++)
            for (int x = 0; x < oversized.Bitmap.Width; x++)
                Assert.Equal(bounded.Bitmap.GetPixel(x, y), oversized.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void CssCascade_ObeysRenderDeadline()
        {
            var svg = new StringBuilder("<svg width='10' height='10'><style>");
            for (int i = 0; i < 4096; i++)
            {
                svg.Append(".c").Append(i).Append("{fill:red}");
            }
            svg.Append("</style><rect class='c4095' width='10' height='10'/></svg>");
            var limits = SvgRenderLimits.Default;
            limits.MaxRenderTimeMs = 1;

            using var result = new FenSvgRenderer().Render(svg.ToString(), limits);

            Assert.False(result.Success);
            Assert.Contains("time limit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("color", "notacolor")]
        [InlineData("fill", "notacolor")]
        [InlineData("opacity", "half")]
        [InlineData("fill-opacity", "half")]
        [InlineData("stroke-opacity", "half")]
        [InlineData("stroke-width", "thick")]
        [InlineData("stroke-linecap", "flat")]
        [InlineData("stroke-linejoin", "sharp")]
        [InlineData("stroke-dasharray", "1 2 3 4 5 6 7 --")]
        [InlineData("clip-rule", "winding")]
        [InlineData("fill-rule", "winding")]
        [InlineData("visibility", "gone")]
        [InlineData("filter", "url(")]
        [InlineData("mask", "url(")]
        [InlineData("marker-end", "url(")]
        [InlineData("width", "auto")]
        [InlineData("r", "wide")]
        [InlineData("transform", "translate(1 2")]
        [InlineData("d", "path(")]
        [InlineData("font-size", "huge")]
        [InlineData("font-weight", "semibold")]
        [InlineData("font-style", "slanted")]
        [InlineData("text-anchor", "centre")]
        [InlineData("paint-order", "fill fill")]
        [InlineData("letter-spacing", "wide")]
        [InlineData("stroke-dashoffset", "later")]
        public void InvalidDeclarationValue_IsRejectedByTheCascadeGate(string property, string value)
        {
            Assert.True(SvgCssCascade.IsDefinitelyInvalid(property, value));
        }

        [Theory]
        [InlineData("color", "currentColor")]
        [InlineData("fill", "red")]
        [InlineData("fill", "url(#g)")]
        [InlineData("fill", "context-stroke")]
        [InlineData("opacity", "0.5")]
        [InlineData("stroke-opacity", "0")]
        [InlineData("stroke-width", "2")]
        [InlineData("stroke-linecap", "round")]
        [InlineData("stroke-linejoin", "bevel")]
        [InlineData("clip-rule", "evenodd")]
        [InlineData("fill-rule", "nonzero")]
        [InlineData("visibility", "hidden")]
        [InlineData("filter", "none")]
        [InlineData("marker-end", "url(#m)")]
        [InlineData("width", "10px")]
        [InlineData("width", "0")]
        [InlineData("r", "4.5px")]
        [InlineData("transform", "translate(1 2)")]
        [InlineData("transform", "none")]
        [InlineData("d", "none")]
        [InlineData("d", "M0 0L1 1")]
        [InlineData("d", "path('M0 0L1 1')")]
        [InlineData("font-size", "12px")]
        [InlineData("font-size", "small")]
        [InlineData("font-weight", "bold")]
        [InlineData("font-style", "italic")]
        [InlineData("text-anchor", "middle")]
        [InlineData("paint-order", "fill stroke")]
        [InlineData("paint-order", "normal")]
        [InlineData("letter-spacing", "2")]
        [InlineData("opacity", "inherit")]
        [InlineData("fill", "var(--paint)")]
        [InlineData("--custom-property", "notacolor")]
        [InlineData("marker-mid", "none")]
        public void SupportedDeclarationValue_IsAcceptedByTheCascadeGate(string property, string value)
        {
            Assert.False(SvgCssCascade.IsDefinitelyInvalid(property, value));
        }

        [Fact]
        public void InvalidStylesheetDeclaration_DoesNotWinOverLaterValidDeclarations()
        {
            const string svg =
                "<svg width='20' height='20'><style>rect { fill: notacolor; fill: blue }</style>" +
                "<rect width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void InvalidStylesheetDeclaration_LeavesThePresentationAttributeAuthoritative()
        {
            const string svg =
                "<svg width='20' height='20'><style>rect { fill: notacolor; stroke-linecap: flat }</style>" +
                "<rect width='20' height='20' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Theory]
        [InlineData("bold 24px/1.5 Arial, sans-serif",
            "font-style: normal; font-weight: bold; font-size: 24px; font-family: Arial, sans-serif")]
        [InlineData("italic 500 18px serif",
            "font-style: italic; font-weight: 500; font-size: 18px; font-family: serif")]
        [InlineData("oblique 20px monospace",
            "font-style: oblique; font-weight: normal; font-size: 20px; font-family: monospace")]
        [InlineData("24px serif",
            "font-style: normal; font-weight: normal; font-size: 24px; font-family: serif")]
        [InlineData("20px 'My Font', serif",
            "font-style: normal; font-weight: normal; font-size: 20px; font-family: 'My Font', serif")]
        [InlineData("normal normal 400 normal 16px/2 sans-serif",
            "font-style: normal; font-weight: 400; font-size: 16px; font-family: sans-serif")]
        [InlineData("16PX SERIF",
            "font-style: normal; font-weight: normal; font-size: 16PX; font-family: SERIF")]
        public void FontShorthand_PaintsTheSameRunAsTheEquivalentLonghands(
            string shorthand,
            string longhands)
        {
            AssertSamePixels(
                TextDocument($"text {{ font: {shorthand} }}"),
                TextDocument($"text {{ {longhands} }}"));
        }

        [Theory]
        [InlineData("12px serif",
            "font-style: normal; font-weight: normal; font-size: 12px; font-family: serif")]
        [InlineData("12px/1.4 serif",
            "font-style: normal; font-weight: normal; font-size: 12px; font-family: serif")]
        [InlineData("italic 12px serif",
            "font-style: italic; font-weight: normal; font-size: 12px; font-family: serif")]
        public void FontShorthand_ResetsOmittedFontSubpropertiesToInitial(
            string shorthand,
            string longhands)
        {
            AssertSamePixels(
                TextDocument($"text {{ font-weight: 700; font: {shorthand} }}"),
                TextDocument($"text {{ {longhands} }}"));
            AssertDifferentPixels(
                TextDocument($"text {{ font-weight: 700; font: {shorthand} }}"),
                TextDocument("text { font-weight: 700; font-size: 12px; font-family: serif }"));
        }

        [Fact]
        public void FontShorthand_KeepsDeclarationOrderAgainstLaterLonghands()
        {
            AssertSamePixels(
                TextDocument("text { font: bold 12px serif; font-weight: 300 }"),
                TextDocument("text { font-size: 12px; font-family: serif; font-weight: 300 }"));
            AssertSamePixels(
                TextDocument("text { font-weight: 300; font: bold 12px serif }"),
                TextDocument("text { font-size: 12px; font-family: serif; font-weight: 700 }"));
        }

        [Fact]
        public void FontShorthand_LosesToAHigherSpecificityLonghand()
        {
            AssertSamePixels(
                "<svg width='220' height='40'><style>#lead { font-weight: 300 } " +
                "text { font: bold 12px serif }</style>" +
                "<text id='lead' x='4' y='28' fill='lime'>Ag</text></svg>",
                "<svg width='220' height='40'><style>text { font-size: 12px; " +
                "font-family: serif; font-weight: 300 }</style>" +
                "<text id='lead' x='4' y='28' fill='lime'>Ag</text></svg>");
        }

        [Fact]
        public void FontShorthand_OverridesAPresentationAttributeOnTheSameElement()
        {
            AssertSamePixels(
                "<svg width='220' height='40'><style>text { font: 12px serif }</style>" +
                "<text x='4' y='28' fill='lime' font-weight='bold' font-style='italic'>Ag</text></svg>",
                TextDocument("text { font-size: 12px; font-family: serif; " +
                             "font-weight: normal; font-style: normal }"));
        }

        [Theory]
        [InlineData("caption")]
        [InlineData("menu")]
        [InlineData("small-caps 18px serif")]
        [InlineData("expanded 18px serif")]
        [InlineData("condensed 18px serif")]
        [InlineData("75% 18px serif")]
        [InlineData("0.5 18px serif")]
        [InlineData("oblique 20deg 18px serif")]
        [InlineData("oblique from-font 18px serif")]
        [InlineData("calc(10px + 8px) serif")]
        [InlineData("var(--stack)")]
        [InlineData("inherit")]
        public void FontShorthand_ThatCannotPaintFaithfully_FailsClosed(string shorthand)
        {
            using var result = new FenSvgRenderer().Render(TextDocument($"text {{ font: {shorthand} }}"));

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("12foo serif")]
        [InlineData("12px/ serif")]
        [InlineData("italic italic 18px serif")]
        [InlineData("18px Arial sans-serif")]
        [InlineData("18px 2px serif")]
        [InlineData("bold bold 18px serif")]
        [InlineData("18px Arial, , serif")]
        [InlineData("18px")]
        [InlineData("italic 18px")]
        [InlineData("18px/1.5")]
        public void FontShorthand_ThatIsNotValidCss_IsDroppedLikeCssDropsIt(string shorthand)
        {
            AssertSamePixels(
                TextDocument($"text {{ font: {shorthand} }}"),
                TextDocument("text { fill: lime }"));
        }

        [Theory]
        [InlineData("line-height: 2")]
        [InlineData("line-height: 1.5")]
        [InlineData("line-height: normal")]
        [InlineData("line-height: 120%")]
        [InlineData("line-height: inherit")]
        [InlineData("font-variant: normal")]
        [InlineData("font-variant: inherit")]
        [InlineData("font-stretch: normal")]
        [InlineData("font-stretch: 100%")]
        [InlineData("font-size-adjust: none")]
        public void FontLonghandsWithNoRenderedEffect_KeepTheDocumentRenderable(string declaration)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='220' height='40'><style>text {" + declaration +
                "} text { fill: lime }</style><text x='4' y='28'>Ag</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasPaintedInk(result.Bitmap));
        }

        [Theory]
        [InlineData("font-variant: small-caps")]
        [InlineData("font-stretch: condensed")]
        [InlineData("font-stretch: 75%")]
        [InlineData("font-size-adjust: 0.5")]
        [InlineData("font-size-adjust: from-font")]
        [InlineData("font-style: oblique 20deg")]
        [InlineData("font-style: oblique from-font")]
        [InlineData("font-style: oblique 0.5turn")]
        [InlineData("font-size: 3ic")]
        [InlineData("font-size: 2cap")]
        [InlineData("font-size: 2lh")]
        [InlineData("letter-spacing: 2lh")]
        public void FontLonghandsWithAVisibleEffect_FailClosedInsteadOfPaintingTheInheritedValue(
            string declaration)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='220' height='40'><style>text {" + declaration +
                "}</style><text x='4' y='28' font-size='20'>Ag</text></svg>");

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("700")]
        [InlineData("normal")]
        [InlineData("bold")]
        [InlineData("lighter")]
        public void FontWeightLonghand_AcceptsTheSpecRangeAndKeywords(string value)
        {
            Assert.False(SvgCssCascade.IsDefinitelyInvalid("font-weight", value));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("1001")]
        [InlineData("-400")]
        [InlineData("semibold")]
        [InlineData("bolderer")]
        public void FontWeightLonghand_RejectsValuesOutsideTheSpecRange(string value)
        {
            Assert.True(SvgCssCascade.IsDefinitelyInvalid("font-weight", value));
        }

        [Theory]
        [InlineData("text::first-letter")]
        [InlineData("text::first-line")]
        [InlineData("text:FIRST-LETTER")]
        public void PseudoElementOnPaintedText_FailsClosedInsteadOfDroppingTheRule(string selector)
        {
            string svg =
                "<svg width='220' height='40'><style>" + selector +
                " { fill: lime } rect { fill: red }</style>" +
                "<text x='4' y='28' font-size='20'>Ag</text>" +
                "<rect width='220' height='40'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("tspan::first-letter")]
        [InlineData("tspan::first-line")]
        public void PseudoElementOnAPaintedTspan_FailsClosed(string selector)
        {
            string svg =
                "<svg width='220' height='40'><style>" + selector +
                " { fill: lime }</style><text x='4' y='28' font-size='20'>" +
                "<tspan>Ag</tspan></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void PseudoElementOnAPaintedTextPath_FailsClosed()
        {
            const string svg =
                "<svg width='220' height='60'><style>textPath::first-letter { fill: lime }" +
                "</style><defs><path id='p' d='M4 40 H200'/></defs>" +
                "<text font-size='20'><textPath href='#p'>Ag</textPath></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("rect::first-letter")]
        [InlineData("circle::first-line")]
        [InlineData("g::first-letter")]
        [InlineData("text::before")]
        [InlineData("text::after")]
        public void PseudoElementWithoutPaintedText_StaysInert(string selector)
        {
            string svg =
                "<svg width='40' height='40'><style>" + selector +
                " { fill: lime }</style><rect width='40' height='40' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(20, 20));
        }

        [Theory]
        [InlineData("text { letter-spacing: 2ch }", "text { font-size: 20px; letter-spacing: 1em }")]
        [InlineData("text { font-size: 4ch }", "text { font-size: 2em }")]
        [InlineData("text { stroke-width: 4ch }", "text { stroke-width: 2em }")]
        [InlineData("text { stroke-dasharray: 4ch 8ch }", "text { stroke-dasharray: 2em 4em }")]
        public void ChUnit_ResolvesLikeTheEquivalentEmLength(string chRule, string emRule)
        {
            string Template(string rule) =>
                "<svg width='220' height='40'><style>text { fill: lime; " + rule +
                "}</style><path d='M4 30 H160' stroke='black' stroke-width='2'/>" +
                "<text x='4' y='28' font-size='20'>Ag</text></svg>";

            AssertSamePixels(Template(chRule), Template(emRule));
        }

        [Fact]
        public void ChUnit_PositionsTextLikeTheEquivalentEmOffset()
        {
            string Template(string x) =>
                "<svg width='220' height='40'><text x='" + x +
                "' y='28' font-size='20' fill='lime'>Ag</text></svg>";

            AssertSamePixels(Template("2ch"), Template("1em"));
        }

        [Fact]
        public void RemUnit_StaysCssOnlyBecauseTheTextModelHasNoRootFontContext()
        {
            using var shorthand = new FenSvgRenderer().Render(
                "<svg width='220' height='40'><style>text { font: 1rem serif }</style>" +
                "<text x='4' y='28'>Ag</text></svg>");

            AssertFailsClosed(shorthand);

            using var longhand = new FenSvgRenderer().Render(
                "<svg width='220' height='40'><style>text { font-size: 1rem }</style>" +
                "<text x='4' y='28'>Ag</text></svg>");

            AssertFailsClosed(longhand);

            using var geometry = new FenSvgRenderer().Render(
                "<svg width='220' height='40'><style>rect { width: 2rem; height: 1rem }" +
                "</style><rect width='10' height='10' fill='lime'/></svg>");

            Assert.True(geometry.Success, geometry.ErrorMessage);
            Assert.False(geometry.RequiresFallback, string.Join("; ", geometry.Warnings));
        }

        private static string TextDocument(string rule) =>
            "<svg width='220' height='40'><style>" + rule +
            "</style><text x='4' y='28' font-size='20' fill='lime'>Ag</text></svg>";

        private static bool HasPaintedInk(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
            }
            return false;
        }

        private static void AssertSamePixels(string firstSvg, string secondSvg)
        {
            using var first = new FenSvgRenderer().Render(firstSvg);
            using var second = new FenSvgRenderer().Render(secondSvg);
            Assert.True(first.Success, first.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.True(second.Success, second.ErrorMessage);
            Assert.False(second.RequiresFallback, string.Join("; ", second.Warnings));
            Assert.Equal(second.Bitmap.Width, first.Bitmap.Width);
            Assert.Equal(second.Bitmap.Height, first.Bitmap.Height);
            for (int y = 0; y < first.Bitmap.Height; y++)
            for (int x = 0; x < first.Bitmap.Width; x++)
                Assert.Equal(second.Bitmap.GetPixel(x, y), first.Bitmap.GetPixel(x, y));
        }

        private static void AssertDifferentPixels(string firstSvg, string secondSvg)
        {
            using var first = new FenSvgRenderer().Render(firstSvg);
            using var second = new FenSvgRenderer().Render(secondSvg);
            Assert.True(first.Success, first.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.True(second.Success, second.ErrorMessage);
            bool identical = first.Bitmap.Width == second.Bitmap.Width &&
                             first.Bitmap.Height == second.Bitmap.Height;
            for (int y = 0; y < first.Bitmap.Height && identical; y++)
            for (int x = 0; x < first.Bitmap.Width; x++)
            {
                if (first.Bitmap.GetPixel(x, y) == second.Bitmap.GetPixel(x, y)) continue;
                identical = false;
                break;
            }
            Assert.False(identical, "expected the two documents to paint different pixels");
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

        private static SKColor ExpectedColor(string name) => name switch
        {
            "lime" => SKColors.Lime,
            "blue" => SKColors.Blue,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };
    }
}

