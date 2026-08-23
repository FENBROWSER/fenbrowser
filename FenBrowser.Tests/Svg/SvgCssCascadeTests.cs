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
