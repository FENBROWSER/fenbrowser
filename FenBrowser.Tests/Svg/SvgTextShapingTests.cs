using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgTextShapingTests
    {
        [Fact]
        public void MixedTextAndTspanRuns_PreserveSourceOrderAndPaintStyles()
        {
            const string svg =
                "<svg width='180' height='50'><text x='5' y='36' font-size='32'>" +
                "A<tspan fill='red'>B</tspan><tspan fill='blue'>C</tspan></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(TryGetColorBounds(result.Bitmap, c => c.Red > 180 && c.Green < 80 && c.Blue < 80, out var red));
            Assert.True(TryGetColorBounds(result.Bitmap, c => c.Blue > 180 && c.Red < 80 && c.Green < 80, out var blue));
            Assert.True(red.Left < blue.Left);
        }

        [Fact]
        public void TspanAbsolutePosition_StartsIndependentAnchoredChunk()
        {
            const string svg =
                "<svg width='160' height='40'><text x='10' y='30' font-size='24'>A" +
                "<tspan x='120' text-anchor='middle' fill='red'>BC</tspan></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(TryGetColorBounds(result.Bitmap, c => c.Red > 180 && c.Green < 80, out var red));
            Assert.InRange((red.Left + red.Right) / 2f, 110f, 130f);
        }

        [Fact]
        public void HiddenTspan_DoesNotPaintOrAdvanceVisibleRun()
        {
            const string svg =
                "<svg width='100' height='40'><style>.gone{display:none}</style>" +
                "<text x='4' y='30' font-size='24'>A<tspan class='gone'>WIDE</tspan>" +
                "<tspan fill='red'>B</tspan></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(TryGetColorBounds(result.Bitmap, c => c.Red > 180 && c.Green < 80, out var red));
            Assert.True(red.Left < 45);
        }

        [Fact]
        public void FontProperties_InheritFromStyledSvgAncestors()
        {
            const string svg =
                "<svg width='120' height='50'><style>svg { font-size: 32px; font-weight: bold }</style>" +
                "<g><text x='4' y='38'>Fen</text></g></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.True(TryGetColorBounds(result.Bitmap, c => c.Alpha > 0, out var bounds));
            Assert.True(bounds.Height > 18);
        }

        [Fact]
        public void XmlSpacePreserve_KeepsInterRunWhitespaceAdvance()
        {
            const string collapsed =
                "<svg width='180' height='40'><text x='4' y='30' font-size='24'>A   <tspan fill='red'>B</tspan></text></svg>";
            const string preserved =
                "<svg width='180' height='40'><text x='4' y='30' font-size='24' xml:space='preserve'>A   <tspan fill='red'>B</tspan></text></svg>";

            using var collapsedResult = new FenSvgRenderer().Render(collapsed);
            using var preservedResult = new FenSvgRenderer().Render(preserved);

            Assert.True(TryGetColorBounds(collapsedResult.Bitmap, c => c.Red > 180 && c.Green < 80, out var collapsedRed));
            Assert.True(TryGetColorBounds(preservedResult.Bitmap, c => c.Red > 180 && c.Green < 80, out var preservedRed));
            Assert.True(preservedRed.Left > collapsedRed.Left);
        }

        [Fact]
        public void DefaultWhitespace_DoesNotCollapseNonBreakingSpaces()
        {
            const string preserved =
                "<svg width='180' height='40'><text x='4' y='30' font-family='monospace' " +
                "xml:space='preserve'>Some  Text</text></svg>";
            const string nonBreaking =
                "<svg width='180' height='40'><text x='4' y='30' font-family='monospace'>" +
                "Some\u00A0\u00A0Text</text></svg>";

            using var actual = new FenSvgRenderer().Render(preserved);
            using var expected = new FenSvgRenderer().Render(nonBreaking);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.True(expected.Success, expected.ErrorMessage);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), actual.Bitmap.GetPixel(x, y));
        }

        [Theory]
        [InlineData("xx-small", 9)]
        [InlineData("x-small", 10)]
        [InlineData("small", 13)]
        [InlineData("medium", 16)]
        [InlineData("large", 18)]
        [InlineData("x-large", 24)]
        [InlineData("xx-large", 32)]
        public void FontSizeAbsoluteKeywords_MatchTheEquivalentPixelLength(
            string keyword,
            int pixels)
        {
            using var keywordResult = new FenSvgRenderer().Render(
                $"<svg width='200' height='60'><text x='4' y='46' font-family='sans-serif' " +
                $"font-size='{keyword}'>Fen</text></svg>");
            using var lengthResult = new FenSvgRenderer().Render(
                $"<svg width='200' height='60'><text x='4' y='46' font-family='sans-serif' " +
                $"font-size='{pixels}'>Fen</text></svg>");

            Assert.True(keywordResult.Success, keywordResult.ErrorMessage);
            Assert.False(keywordResult.RequiresFallback, string.Join("; ", keywordResult.Warnings));
            Assert.True(lengthResult.Success, lengthResult.ErrorMessage);
            Assert.True(TryGetColorBounds(lengthResult.Bitmap, c => c.Alpha > 0, out _));
            Assert.Equal(lengthResult.Bitmap.Width, keywordResult.Bitmap.Width);
            Assert.Equal(lengthResult.Bitmap.Height, keywordResult.Bitmap.Height);
            for (int y = 0; y < keywordResult.Bitmap.Height; y++)
            for (int x = 0; x < keywordResult.Bitmap.Width; x++)
                Assert.Equal(lengthResult.Bitmap.GetPixel(x, y), keywordResult.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void FontSizeKeywordThroughStylesheet_MatchesTheEquivalentPixelLength()
        {
            using var keywordResult = new FenSvgRenderer().Render(
                "<svg width='200' height='60'><style>text { font-size: xx-large }</style>" +
                "<text x='4' y='46' font-family='sans-serif'>Fen</text></svg>");
            using var lengthResult = new FenSvgRenderer().Render(
                "<svg width='200' height='60'><text x='4' y='46' font-family='sans-serif' " +
                "font-size='32'>Fen</text></svg>");

            Assert.True(keywordResult.Success, keywordResult.ErrorMessage);
            Assert.False(keywordResult.RequiresFallback, string.Join("; ", keywordResult.Warnings));
            for (int y = 0; y < keywordResult.Bitmap.Height; y++)
            for (int x = 0; x < keywordResult.Bitmap.Width; x++)
                Assert.Equal(lengthResult.Bitmap.GetPixel(x, y), keywordResult.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void FontSizePercentage_ResolvesAgainstTheInheritedSize()
        {
            using var percentage = new FenSvgRenderer().Render(
                "<svg width='200' height='60' style='font-size:20px'><g>" +
                "<text x='4' y='46' font-family='sans-serif' font-size='150%'>Fen</text>" +
                "</g></svg>");
            using var length = new FenSvgRenderer().Render(
                "<svg width='200' height='60'><text x='4' y='46' font-family='sans-serif' " +
                "font-size='30'>Fen</text></svg>");

            Assert.True(percentage.Success, percentage.ErrorMessage);
            Assert.False(percentage.RequiresFallback, string.Join("; ", percentage.Warnings));
            Assert.True(length.Success, length.ErrorMessage);
            for (int y = 0; y < percentage.Bitmap.Height; y++)
            for (int x = 0; x < percentage.Bitmap.Width; x++)
                Assert.Equal(length.Bitmap.GetPixel(x, y), percentage.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void FontSizeRelativeKeywords_ScaleTheInheritedSize()
        {
            using var larger = RenderRelative("larger");
            using var inherited = RenderRelative(null);
            using var smaller = RenderRelative("smaller");

            Assert.True(larger.Success, larger.ErrorMessage);
            Assert.True(inherited.Success, inherited.ErrorMessage);
            Assert.True(smaller.Success, smaller.ErrorMessage);
            Assert.False(larger.RequiresFallback, string.Join("; ", larger.Warnings));
            Assert.False(smaller.RequiresFallback, string.Join("; ", smaller.Warnings));
            TryGetColorBounds(larger.Bitmap, c => c.Alpha > 0, out var largerBounds);
            TryGetColorBounds(inherited.Bitmap, c => c.Alpha > 0, out var inheritedBounds);
            TryGetColorBounds(smaller.Bitmap, c => c.Alpha > 0, out var smallerBounds);
            Assert.True(largerBounds.Width > inheritedBounds.Width);
            Assert.True(inheritedBounds.Width > smallerBounds.Width);
            Assert.InRange(largerBounds.Width, inheritedBounds.Width + 2, inheritedBounds.Width * 2);
            Assert.InRange(smallerBounds.Width, inheritedBounds.Width / 2, inheritedBounds.Width - 2);
        }

        private static SvgRenderResult RenderRelative(string keyword)
        {
            string declaration = keyword == null ? string.Empty : $" font-size='{keyword}'";
            return new FenSvgRenderer().Render(
                "<svg width='200' height='60' style='font-size:20px'>" +
                $"<text x='4' y='46' font-family='sans-serif'{declaration}>Fen</text></svg>");
        }

        [Theory]
        [InlineData("huge")]
        [InlineData("tiny")]
        [InlineData("20pixels")]
        [InlineData("calc(20px)")]
        public void InvalidFontSize_KeepsTheInheritedSizeAndFailsClosed(string value)
        {
            using var invalid = new FenSvgRenderer().Render(
                "<svg width='200' height='60' style='font-size:20px'>" +
                $"<text x='4' y='46' font-family='sans-serif' font-size='{value}'>Fen</text></svg>");
            using var inherited = new FenSvgRenderer().Render(
                "<svg width='200' height='60' style='font-size:20px'>" +
                "<text x='4' y='46' font-family='sans-serif'>Fen</text></svg>");

            AssertFailsClosed(invalid);
            Assert.True(invalid.RequiresFallback);
            Assert.Contains(
                invalid.Warnings,
                warning => warning.Contains("font-size", StringComparison.Ordinal));
            Assert.Contains("unsupported-feature", invalid.FallbackReasonCodes);
            Assert.True(inherited.Success, inherited.ErrorMessage);
            Assert.False(inherited.RequiresFallback, string.Join("; ", inherited.Warnings));
        }

        [Fact]
        public void InheritedFillOpacity_AppliesToTextRuns()
        {
            using var inherited = new FenSvgRenderer().Render(
                "<svg width='200' height='60'><g fill='red' fill-opacity='0.5'>" +
                "<text x='4' y='46' font-size='32'>Fen</text></g></svg>");
            using var opaque = new FenSvgRenderer().Render(
                "<svg width='200' height='60'><g fill='red'>" +
                "<text x='4' y='46' font-size='32'>Fen</text></g></svg>");

            Assert.True(inherited.Success, inherited.ErrorMessage);
            Assert.False(inherited.RequiresFallback, string.Join("; ", inherited.Warnings));
            Assert.True(opaque.Success, opaque.ErrorMessage);
            Assert.True(TryGetMaxAlphaColor(inherited.Bitmap, out var faded));
            Assert.True(TryGetMaxAlphaColor(opaque.Bitmap, out var solid));
            Assert.Equal((byte)255, solid.Alpha);
            Assert.InRange(faded.Alpha, 110, 145);
            Assert.Equal((byte)255, faded.Red);
            Assert.Equal((byte)0, faded.Green);
            Assert.Equal((byte)0, faded.Blue);
        }

        [Fact]
        public void TspanIsolatedOpacity_FailsClosedAsExplicitUnsupported()
        {
            using var reduced = new FenSvgRenderer().Render(
                "<svg width='80' height='30'><text x='2' y='20'>A<tspan opacity='0.5'>B</tspan>" +
                "</text></svg>");
            using var full = new FenSvgRenderer().Render(
                "<svg width='80' height='30'><text x='2' y='20'>A<tspan opacity='1'>B</tspan>" +
                "</text></svg>");

            AssertFailsClosed(reduced);
            Assert.True(reduced.RequiresFallback);
            Assert.Contains("advanced-text-layout", reduced.FallbackReasonCodes);
            Assert.True(full.Success, full.ErrorMessage);
            Assert.False(full.RequiresFallback, string.Join("; ", full.Warnings));
        }

        [Fact]
        public void PerGlyphPositionList_FailsClosedAsExplicitUnsupported()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='80' height='30'><text x='2 10' y='20'>AB</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("per-glyph", StringComparison.Ordinal));
        }

        [Fact]
        public void NestedTextRuns_ObeyDocumentGlyphBudget()
        {
            var svg = new StringBuilder("<svg width='100' height='40'><text x='2' y='30'>");
            for (int i = 0; i < 5; i++)
            {
                svg.Append("<tspan>").Append('a', 4096).Append("</tspan>");
            }
            svg.Append("</text></svg>");

            using var result = new FenSvgRenderer().Render(svg.ToString());

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("glyph budget", StringComparison.Ordinal));
        }

        [Fact]
        public void SiblingTextElements_ObeyDocumentGlyphBudget()
        {
            var svg = new StringBuilder("<svg width='100' height='80'>");
            for (int element = 0; element < 2; element++)
            {
                svg.Append("<text x='2' y='").Append(20 + element * 35).Append("'>");
                for (int run = 0; run < 3; run++)
                    svg.Append("<tspan>").Append('a', 4096).Append("</tspan>");
                svg.Append("</text>");
            }
            svg.Append("</svg>");

            using var result = new FenSvgRenderer().Render(svg.ToString());

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("glyph budget", StringComparison.Ordinal));
        }

        [Fact]
        public async Task UnicodeShaping_IsSafeAcrossConcurrentRenders()
        {
            var renderer = new FenSvgRenderer();
            string[] texts = { "مرحبا", "Καλημέρα", "שלום", "नमस्ते" };
            var tasks = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
            {
                using var result = renderer.Render(
                    $"<svg width='160' height='40'><text x='3' y='30' font-size='24'>{texts[index % texts.Length]}</text></svg>");
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback);
                Assert.True(HasForeground(result.Bitmap));
            }));

            await Task.WhenAll(tasks);
        }

        private static bool TryGetColorBounds(SKBitmap bitmap, Func<SKColor, bool> predicate, out SKRectI bounds)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha == 0 || !predicate(color)) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
            bounds = right >= left ? new SKRectI(left, top, right + 1, bottom + 1) : SKRectI.Empty;
            return right >= left;
        }

        private static bool TryGetMaxAlphaColor(SKBitmap bitmap, out SKColor color)
        {
            int best = -1;
            color = default;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var candidate = bitmap.GetPixel(x, y);
                if (candidate.Alpha <= best) continue;
                best = candidate.Alpha;
                color = candidate;
            }
            return best > 0;
        }

        private static bool HasForeground(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
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
