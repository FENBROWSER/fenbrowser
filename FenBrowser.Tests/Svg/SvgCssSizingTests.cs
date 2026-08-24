using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// SVG CSS sizing resolution: viewport-relative geometry lengths, nested
    /// &lt;svg&gt; fill/stretch sizing keywords, and calc-size(). Positive,
    /// invalid, malformed/adversarial, boundary, and mixed-cascade coverage.
    /// </summary>
    public sealed class SvgCssSizingTests
    {
        private static void AssertPixelsEqual(SKBitmap actual, SKBitmap expected)
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.True(
                    expected.GetPixel(x, y).Equals(actual.GetPixel(x, y)),
                    $"pixel ({x},{y}) differs: {actual.GetPixel(x, y)} != {expected.GetPixel(x, y)}");
        }

        private static void AssertIdenticalRender(string actualSvg, string referenceSvg)
        {
            using var actual = new FenSvgRenderer().Render(actualSvg);
            using var expected = new FenSvgRenderer().Render(referenceSvg);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            AssertPixelsEqual(actual.Bitmap, expected.Bitmap);
        }

        [Fact]
        public void ViewportUnitGeometry_MatchesPercentageReferenceExactly()
        {
            AssertIdenticalRender(
                "<svg width='300' height='150'><style>rect{width:10vw;height:10vh}</style>" +
                "<rect fill='red'/></svg>",
                "<svg width='300' height='150'><rect width='10%' height='10%' fill='red'/></svg>");
        }

        [Fact]
        public void ViewportUnitGeometry_SupportsMixedDeclarationsAndAttributes()
        {
            AssertIdenticalRender(
                "<svg width='200' height='100'><style>.u{width:25vw}</style>" +
                "<rect class='u' y='10' height='calc(100% - 20px)' fill='red'/></svg>",
                "<svg width='200' height='100'>" +
                "<rect width='25%' y='10' height='calc(100% - 20px)' fill='red'/></svg>");
        }

        [Fact]
        public void ContainerUnits_FallBackToSmallViewportResolution()
        {
            AssertIdenticalRender(
                "<svg width='200' height='100'><rect style='width:25cqw;height:50cqh' fill='blue'/></svg>",
                "<svg width='200' height='100'><rect width='25%' height='50%' fill='blue'/></svg>");
        }

        [Fact]
        public void ViewportExtremeUnits_ResolveAgainstNearestViewportExtremes()
        {
            AssertIdenticalRender(
                "<svg width='300' height='150'><rect style='width:10vmin;height:10px' fill='red'/>" +
                "<rect x='20' style='width:10vmax;height:10px' fill='green'/></svg>",
                "<svg width='300' height='150'><rect width='15' height='10' fill='red'/>" +
                "<rect x='20' width='30' height='10' fill='green'/></svg>");
        }

        [Theory]
        [InlineData("stretch")]
        [InlineData("fit-content")]
        [InlineData("min-content")]
        [InlineData("max-content")]
        public void NestedSvgSizingKeywords_ResolveToContainingViewportExtent(string keyword)
        {
            AssertIdenticalRender(
                $"<svg width='100' height='100'><svg width='50' height='50' " +
                $"style='width:{keyword};height:{keyword}'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='100' height='100'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Fact]
        public void NestedSvgViewportUnits_ApplyViewportRelativeDimensions()
        {
            AssertIdenticalRender(
                "<svg width='300' height='150'><svg style='width:5vw;height:5vh'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='300' height='150'><svg width='5%' height='5%'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Fact]
        public void NestedSvgCalcSize_ResolvesKeywordBaseAndSizeArithmetic()
        {
            AssertIdenticalRender(
                "<svg width='100' height='100'><svg width='50' height='50' " +
                "style='width:calc-size(fit-content, size);height:calc-size(fit-content, size)'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='100' height='100'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");

            AssertIdenticalRender(
                "<svg width='100' height='100'><svg width='50' height='50' " +
                "style='width:calc-size(stretch, size - 20px);height:calc-size(auto, size - 20px)'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='80' height='80'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");

            AssertIdenticalRender(
                "<svg width='100' height='100'><svg style='width:calc-size(30%, size + 10px)'>" +
                "<circle cx='20' cy='20' r='15' fill='purple'/></svg></svg>",
                "<svg width='100' height='100'><svg width='40'>" +
                "<circle cx='20' cy='20' r='15' fill='purple'/></svg></svg>");
        }

        [Fact]
        public void NestedSvgPlainLengthsPercentagesCalcAndAuto_DoNotApply()
        {
            // Per the tentative SVG2/CSSWG interop position, declarations that
            // do not require layout context never override XML geometry here.
            AssertIdenticalRender(
                "<svg width='100' height='100'><svg style='width:50%;height:50%'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='100' height='100'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");

            AssertIdenticalRender(
                "<svg width='100' height='100'><svg width='50' height='50' " +
                "style='width:auto;height:auto;font-size:2px'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='50' height='50'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");

            AssertIdenticalRender(
                "<svg width='100' height='100'><svg width='50' height='50' " +
                "style='width:calc(100% - 10px);height:25em'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='50' height='50'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Theory]
        [InlineData("calc-size(")]
        [InlineData("calc-size(fit-content)")]
        [InlineData("calc-size(banana, size)")]
        [InlineData("calc-size(size, size)")]
        [InlineData("calc-size(fit-content size)")]
        [InlineData("calc-size(fit-content, )")]
        [InlineData("calc-size(fit-content, size")]
        [InlineData("CALC-SIZE(fit-content, size) extra")]
        public void NestedSvgMalformedCalcSize_IsIgnoredAndKeepsAttributeSizing(string declaration)
        {
            AssertIdenticalRender(
                $"<svg width='100' height='100'><svg width='60' height='60' " +
                $"style=\"width:{declaration};height:{declaration}\">" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='60' height='60'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Fact]
        public void NestedSvgNegativeSizing_IsInvalidAndIgnored()
        {
            AssertIdenticalRender(
                "<svg width='100' height='100'><svg width='70' height='70' " +
                "style='width:calc-size(stretch, 0px - size);height:-5vw'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='70' height='70'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Fact]
        public void NestedSvgDeeplyNestedAndOversizedSizing_StaysBounded()
        {
            string deepExpression = "calc-size(fit-content, ";
            for (int i = 0; i < 24; i++) deepExpression += "calc(";
            deepExpression += "size";
            for (int i = 0; i < 24; i++) deepExpression += ")";
            deepExpression += ")";

            // Exceeds the bounded attribute-value admission cap outright.
            string oversized = new string('9', 300_000) + "vw";
            // Under the cap but overflows float space during resolution.
            string overflowing = "1e308vw";

            const string reference =
                "<svg width='100' height='100'><svg width='45' height='45'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>";

            foreach (string declaration in new[] { deepExpression, oversized, overflowing })
            {
                AssertIdenticalRender(
                    $"<svg width='100' height='100'><svg width='45' height='45' " +
                    $"style=\"width:{declaration};height:{declaration}\">" +
                    "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                    reference);
            }
        }

        [Fact]
        public void ShapeInvalidSizingKeyword_IsInvalidInputNotCompatibilityRouting()
        {
            const string svg =
                "<svg width='30' height='10'><style>rect{width:min-content}</style>" +
                "<rect height='10' fill='red'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(0, result.Bitmap.GetPixel(15, 5).Alpha);
        }

        [Fact]
        public void MixedCascade_SpecificityDecidesBetweenViewportUnitsAndLengths()
        {
            const string svg =
                "<svg width='300' height='100'><style>" +
                "#a{width:10vw}.w{width:60px}#b{width:20vh}</style>" +
                "<rect id='a' class='w' height='20' fill='red'/>" +
                "<rect id='b' class='w' y='30' height='20' fill='blue'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(29, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(31, 10).Alpha);
            Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(19, 40));
            Assert.Equal(0, result.Bitmap.GetPixel(21, 40).Alpha);
        }

        [Fact]
        public void VarIndirection_ResolvesViewportUnitsBeforeSizingDecision()
        {
            const string svg =
                "<svg width='200' height='100'><style>g{--u:10vw}</style>" +
                "<g><rect width='var(--u)' height='20' fill='red'/></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(19, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(21, 10).Alpha);
        }

        [Fact]
        public void HugeViewportUnitValues_ClampDeterministicallyWithoutFailure()
        {
            const string svg =
                "<svg width='100' height='100'><svg style='width:1e9vw;height:2vh'>" +
                "<rect width='90' height='90' fill='teal'/></svg></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.NotNull(result.Bitmap);
            Assert.Equal(100, result.Bitmap.Width);
            Assert.Equal(100, result.Bitmap.Height);
            Assert.Equal(SKColors.Teal, result.Bitmap.GetPixel(50, 1));
            Assert.Equal(0, result.Bitmap.GetPixel(50, 3).Alpha);
        }

        [Fact]
        public void ZeroViewportUnitSizing_HidesNestedViewportLikeZeroAttributes()
        {
            AssertIdenticalRender(
                "<svg width='100' height='100'><svg style='width:0vw;height:50vh'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='0' height='50'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Fact]
        public async System.Threading.Tasks.Task ViewportUnitGeometry_IsSafeAcrossConcurrentRenders()
        {
            var renderer = new FenSvgRenderer();
            var tasks = new List<System.Threading.Tasks.Task>();
            for (int i = 0; i < 16; i++)
            {
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    using var result = renderer.Render(
                        "<svg width='120' height='60'><style>r{width:50vw}</style>" +
                        "<rect height='30' fill='red'/></svg>");
                    Assert.True(result.Success, result.ErrorMessage);
                    Assert.False(result.RequiresFallback);
                }));
            }

            await System.Threading.Tasks.Task.WhenAll(tasks.ToArray());
        }
    }
}
