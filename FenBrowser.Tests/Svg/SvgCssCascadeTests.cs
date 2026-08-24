using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
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
        public void UnsupportedProperty_OnlyRequiresFallbackWhenSelectorMatches()
        {
            const string svg =
                "<svg width='20' height='20'><style>" +
                ".unused { filter: blur(2px) } .used { mask: url(#m) }" +
                "</style><rect class='used' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("mask", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("filter", StringComparison.Ordinal));
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
        public void CssExternalResource_IsRejectedAndCannotEnterLegacyFallback()
        {
            const string svg =
                "<svg width='10' height='10'><style>rect { fill: url(https://example.invalid/a.svg#p) }</style>" +
                "<rect width='10' height='10'/></svg>";
            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());

            using var result = renderer.Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.HadResourceRejection);
            Assert.False(result.UsedLegacyFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("external resource", StringComparison.Ordinal));
        }

        [Fact]
        public void ExternalImage_IsRejectedAndCannotEnterLegacyFallback()
        {
            const string svg =
                "<svg width='10' height='10'><style>rect { filter: blur(1px) }</style>" +
                "<rect width='10' height='10'/><image href='https://example.invalid/a.png' width='10' height='10'/></svg>";
            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());

            using var result = renderer.Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.True(result.HadResourceRejection);
            Assert.False(result.UsedLegacyFallback);
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
    }
}
