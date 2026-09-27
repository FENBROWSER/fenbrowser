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
        public void TspanIsolatedOpacity_LayersOnlyItsOwnRun()
        {
            using var reduced = new FenSvgRenderer().Render(
                "<svg width='40' height='30'><text x='2' y='20'><tspan opacity='0.5'>B</tspan>" +
                "</text></svg>");
            using var full = new FenSvgRenderer().Render(
                "<svg width='40' height='30'><text x='2' y='20'><tspan opacity='1'>B</tspan>" +
                "</text></svg>");

            Assert.True(reduced.Success, reduced.ErrorMessage);
            Assert.False(reduced.RequiresFallback, string.Join("; ", reduced.Warnings));
            Assert.True(full.Success, full.ErrorMessage);
            Assert.False(full.RequiresFallback, string.Join("; ", full.Warnings));
            Assert.True(TryGetMaxAlphaColor(reduced.Bitmap, out var faded));
            Assert.True(TryGetMaxAlphaColor(full.Bitmap, out var solid));
            Assert.Equal((byte)255, solid.Alpha);
            Assert.InRange(faded.Alpha, 110, 145);
        }

        [Fact]
        public void AnchorElementOpacity_ReachesNestedTspanRuns()
        {
            using var reduced = new FenSvgRenderer().Render(
                "<svg width='40' height='30'><text x='2' y='20'>" +
                "<a href='https://www.w3.org' opacity='0.5'><tspan>B</tspan></a></text></svg>");
            using var full = new FenSvgRenderer().Render(
                "<svg width='40' height='30'><text x='2' y='20'>" +
                "<a href='https://www.w3.org' opacity='1'><tspan>B</tspan></a></text></svg>");

            Assert.True(reduced.Success, reduced.ErrorMessage);
            Assert.False(reduced.RequiresFallback, string.Join("; ", reduced.Warnings));
            Assert.False(reduced.HadResourceRejection);
            Assert.True(full.Success, full.ErrorMessage);
            Assert.True(TryGetMaxAlphaColor(reduced.Bitmap, out var faded));
            Assert.True(TryGetMaxAlphaColor(full.Bitmap, out var solid));
            Assert.Equal((byte)255, solid.Alpha);
            Assert.InRange(faded.Alpha, 110, 145);
        }

        [Fact]
        public void AnchorElementDirectContentRendersTheSameGlyphsAsTheTspanForm()
        {
            // WPT svg/import/linking-a-08-t-manual.svg and struct-frag-05-t-manual.svg
            // write the label straight into <a> ("Link inside text"), and
            // svg/import/styling-css-06-b-manual.svg does the same for its
            // :visited / :hover labels. The retained character data must render.
            const string head = "<svg width='260' height='50' font-family='monospace' font-size='20'>";

            AssertSamePixels(
                head + "<text x='130' y='34' text-anchor='middle' fill='black'>" +
                       "<a href='https://www.w3.org'>Link inside text</a></text></svg>",
                head + "<text x='130' y='34' text-anchor='middle' fill='black'>" +
                       "Link inside text</text></svg>");
            AssertSamePixels(
                head + "<text x='8' y='34' fill='black'>" +
                       "<a href='https://www.w3.org'>Link inside text</a></text></svg>",
                head + "<text x='8' y='34' fill='black'><tspan>Link inside text</tspan></text></svg>");
        }

        [Fact]
        public void AnchorElementDirectContentAppliesTheLinkOpacityOnce()
        {
            // WPT svg/text/reftests/opacity.svg puts opacity on the link and on a
            // nested tspan in separate subtests. The direct-text spelling must reach
            // the same alpha as the tspan spelling, never the link's alpha twice.
            AssertSamePixels(
                "<svg width='60' height='40'><text x='4' y='30' font-size='24'>" +
                "<a href='https://www.w3.org' opacity='0.5'>B</a></text></svg>",
                "<svg width='60' height='40'><text x='4' y='30' font-size='24'>" +
                "<a href='https://www.w3.org' opacity='0.5'><tspan>B</tspan></a></text></svg>");

            using var direct = new FenSvgRenderer().Render(
                "<svg width='60' height='40'><text x='4' y='30' font-size='24'>" +
                "<a href='https://www.w3.org' opacity='0.5'>B</a></text></svg>");
            Assert.True(direct.Success, direct.ErrorMessage);
            Assert.False(direct.RequiresFallback, string.Join("; ", direct.Warnings));
            Assert.True(TryGetMaxAlphaColor(direct.Bitmap, out var faded));
            Assert.InRange(faded.Alpha, 110, 145);
        }

        [Fact]
        public void AnchorElementMixedContentPreservesSourceOrder()
        {
            // WPT svg/text/reftests/opacity.svg alternates link and tspan siblings;
            // the direct character data, the link and the sibling tspan share one
            // advance chain and must paint in source order.
            const string svg =
                "<svg width='180' height='40'><text x='4' y='30' font-size='24'>" +
                "A<a href='https://www.w3.org' fill='red'>B</a>C</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetColorBounds(result.Bitmap, _ => true, out var all));
            Assert.True(TryGetColorBounds(result.Bitmap,
                c => c.Red > 180 && c.Green < 80 && c.Blue < 80, out var red));
            Assert.True(all.Left < red.Left, "the leading run must precede the link run");
            Assert.True(red.Right < all.Right, "the trailing run must follow the link run");
        }

        [Fact]
        public void HiddenAnchorElement_DoesNotPaintOrAdvanceTheVisibleRun()
        {
            // WPT svg/text/reftests/textpath-pathlength-css-display-none.tentative.svg
            // hides SVG text subtrees with display:none. A hidden link now carrying
            // retained character data must behave exactly like a hidden tspan.
            using var link = new FenSvgRenderer().Render(
                "<svg width='180' height='40'><text x='4' y='30' font-size='24'>A" +
                "<a href='https://www.w3.org' display='none'>WIDE</a>B</text></svg>");
            using var tspan = new FenSvgRenderer().Render(
                "<svg width='180' height='40'><text x='4' y='30' font-size='24'>A" +
                "<tspan display='none'>WIDE</tspan>B</text></svg>");

            Assert.True(link.Success, link.ErrorMessage);
            Assert.False(link.RequiresFallback, string.Join("; ", link.Warnings));
            Assert.True(tspan.Success, tspan.ErrorMessage);
            Assert.False(tspan.RequiresFallback, string.Join("; ", tspan.Warnings));
            Assert.True(TryGetInkBounds(link.Bitmap, out var linkBounds));
            Assert.True(TryGetInkBounds(tspan.Bitmap, out var tspanBounds));
            Assert.Equal(tspanBounds, linkBounds);
            Assert.True(linkBounds.Right < 80, "the hidden run must not advance the visible run");
        }

        [Fact]
        public void AnchorElementDirectContentObeysTheSameCharacterBudgetAsTspan()
        {
            // WPT svg/import/styling-css-06-b-manual.svg and
            // struct-frag-05-t-manual.svg both put the label in the link, so the link
            // is bounded by the same per-element render budget as the text it wraps.
            const string head =
                "<svg width='120' height='40' font-family='monospace' font-size='8'>" +
                "<text x='2' y='30'><a href='https://www.w3.org'>";
            const string tail = "</a></text></svg>";

            using var atLimit = new FenSvgRenderer().Render(head + new string('a', 4096) + tail);
            Assert.True(atLimit.Success, atLimit.ErrorMessage);
            Assert.False(atLimit.RequiresFallback, string.Join("; ", atLimit.Warnings));

            using var overLimit = new FenSvgRenderer().Render(head + new string('a', 4097) + tail);
            AssertFailsClosed(overLimit);
            Assert.True(overLimit.RequiresFallback);
            Assert.Contains(overLimit.Warnings, warning =>
                warning.Contains("render length budget", StringComparison.Ordinal));
        }

        [Fact]
        public void PerGlyphPositionList_PlacesEachCharacterAtItsOwnCoordinate()
        {
            const string listed =
                "<svg width='120' height='30'><text x='2 60' y='20' fill='red'>AB</text></svg>";
            const string equivalent =
                "<svg width='120' height='30'>" +
                "<text x='2' y='20' fill='red'>A</text>" +
                "<text x='60' y='20' fill='red'>B</text></svg>";

            using var result = new FenSvgRenderer().Render(listed);
            using var expected = new FenSvgRenderer().Render(equivalent);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.Equal(expected.Bitmap.Width, result.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, result.Bitmap.Height);
            for (int y = 0; y < result.Bitmap.Height; y++)
            for (int x = 0; x < result.Bitmap.Width; x++)
                Assert.Equal(expected.Bitmap.GetPixel(x, y), result.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void PerGlyphPositionList_BeyondTheListBudgetFailsClosed()
        {
            string positions = string.Join(' ', Enumerable.Repeat("4", 5000));
            using var result = new FenSvgRenderer().Render(
                $"<svg width='120' height='30'><text x='{positions}' y='20'>AB</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
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

        private const string MetricFont = "font-family='monospace'";

        /// <summary>
        /// WPT svg/text/reftests/first-letter.svg styles ::first-letter on a text
        /// element. Unlike ::before or ::after the pseudo-element paints the first
        /// letter of the first line, so the first-party cascade cannot drop the
        /// rule and paint the whole run in the inherited fill.
        /// </summary>
        private const string FirstLetterSheet =
            "<style>text { font-family: monospace; font-size: 40px; } " +
            "text::first-letter { fill: green; }</style>";

        private static string FirstLetterDocument(string body) =>
            $"<svg width='260' height='120'>{FirstLetterSheet}<text x='10' y='80' " +
            $"fill='black'>{body}</text></svg>";

        [Fact]
        public void FirstLetterPseudoElementFailsClosedInsteadOfPaintingTheWholeRun()
        {
            using var styled = new FenSvgRenderer().Render(FirstLetterDocument("XXXX"));

            AssertFailsClosed(styled);
            Assert.True(styled.RequiresFallback);
            Assert.Contains("dynamic-content", styled.FallbackReasonCodes);
            Assert.Contains(styled.Warnings, warning =>
                warning.Contains("first-letter", StringComparison.Ordinal));
        }

        [Fact]
        public void TspanFirstLetterPseudoElementFailsClosedToo()
        {
            using var styled = new FenSvgRenderer().Render(
                FirstLetterDocument("<tspan>XXXX</tspan>"));

            AssertFailsClosed(styled);
            Assert.True(styled.RequiresFallback);
        }

        [Fact]
        public void StylesheetWithoutFirstLetterIsUnaffected()
        {
            // The guard is scoped to the pseudo-element, so an ordinary rule set
            // keeps painting, and a run that styles no first letter at all is
            // byte-identical to the same document with no sheet.
            AssertSamePixels(
                "<svg width='260' height='120'><style>text { fill: green; }</style>" +
                "<text x='10' y='80' font-family='monospace' font-size='40' " +
                "fill='black'>XXXX</text></svg>",
                "<svg width='260' height='120'><text x='10' y='80' font-family='monospace' " +
                "font-size='40' fill='green'>XXXX</text></svg>");
        }

        [Fact]
        public void StyleSheetWithoutAnyTextIsNotRoutedToFallback()
        {
            // A sheet that only mentions the pseudo-element inside a selector the
            // document never paints still leaves a shape-only document alone,
            // because the guard runs with the text layout pass.
            using var result = new FenSvgRenderer().Render(
                "<svg width='60' height='60'>" + FirstLetterSheet +
                "<rect x='5' y='5' width='50' height='50' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        private static string Baseline(string declarations, string baselineY = "80") =>
            $"<svg width='260' height='200'><text x='10' y='{baselineY}' {MetricFont} " +
            $"font-size='40' fill='black' {declarations}>X</text></svg>";

        private static string ScaledBaseline(string declarations, string fontSize, string baselineY) =>
            $"<svg width='520' height='420'><text x='10' y='{baselineY}' {MetricFont} " +
            $"font-size='{fontSize}' fill='black' {declarations}>X</text></svg>";

        private static string LanguageTagged(string language, string content) =>
            $"<svg width='300' height='80'><text x='10' y='60' font-size='48' " +
            $"fill='black' {language}>{content}</text></svg>";

        [Theory]
        [InlineData("lang='ja'")]
        [InlineData("lang='zh-CN'")]
        [InlineData("lang='zh-TW'")]
        [InlineData("lang='ko'")]
        [InlineData("lang='en'")]
        [InlineData("lang='fr-Latn'")]
        public void LanguageTagKeepsLatinTextOnThePlatformDefaultFace(string language)
        {
            // The tag only steers runs the platform default cannot draw, so Latin
            // content under any tag is byte-identical to the untagged run.
            AssertSamePixels(LanguageTagged(language, "Quick"), LanguageTagged(string.Empty, "Quick"));
        }

        [Theory]
        [InlineData("lang='ja'", "xml:lang='zh-CN'")]
        [InlineData("lang='zh-TW'", "xml:lang='ja'")]
        public void HtmlLangWinsOverXmlLangOnTheSameElement(string html, string xml)
        {
            AssertSamePixels(
                LanguageTagged($"{html} {xml}", "Quick"),
                LanguageTagged(html, "Quick"));
        }

        [Fact]
        public void XmlLangIsUsedWhenLangIsAbsent()
        {
            AssertSamePixels(
                LanguageTagged("xml:lang='ja'", "Quick"),
                LanguageTagged("lang='ja'", "Quick"));
        }

        [Fact]
        public void LanguageIsInheritedFromAnAncestorLikeEveryOtherTextProperty()
        {
            AssertSamePixels(
                "<svg width='300' height='80' lang='ja'><text x='10' y='60' font-size='48' " +
                "fill='black'>Quick</text></svg>",
                LanguageTagged("lang='ja'", "Quick"));
        }

        [Fact]
        public void LanguageTagNeverForcesAFallback()
        {
            // A language tag is an ordinary inherited property, so it must not
            // make an otherwise supported document report compatibility.
            using var result = new FenSvgRenderer().Render(
                LanguageTagged("lang='ja'", "\u4ECA\u9AA8\u76F4"));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasForeground(result.Bitmap));
        }

        [Fact]
        public void LanguageTagSelectsTheTypefaceInstalledForThatLanguage()
        {
            // WPT svg/text/reftests/lang-attribute.svg renders the same CJK run
            // three times under ja, zh-CN and zh-TW and requires the three to
            // differ, so the tag cannot collapse onto one fallback face. The
            // assertion holds only where the platform installs a face for the
            // tag, which is what the guard establishes.
            if (!HasInstalledFamilyFor("ja", CjkSample) || !HasInstalledFamilyFor("zh-TW", CjkSample))
                return;

            AssertDifferentPixels(
                LanguageTagged("lang='ja'", CjkSample),
                LanguageTagged("lang='zh-TW'", CjkSample));
        }

        [Fact]
        public void LanguageTagTheTableDoesNotDistinguishLeavesTheRunAlone()
        {
            // A tag with no entry in the language table cannot select a face, so
            // the run keeps the platform character fallback it would have used
            // without the tag.
            AssertSamePixels(
                LanguageTagged("lang='en-Latn'", CjkSample),
                LanguageTagged(string.Empty, CjkSample));
        }

        private const string CjkSample = "\u4ECA\u9AA8\u76F4";

        /// <summary>
        /// WPT svg/text/reftests/lang-attribute-dynamic.svg and
        /// svg/text/reftests/xml-lang-attribute-dynamic.svg both paint a run from a
        /// <c>:lang()</c> rule and then install the language the rule matches from
        /// a script. A static render never runs the script, so the run it paints
        /// carries the inherited fill while the reference carries the styled one,
        /// and the two documents differ in colour with identical glyph outlines.
        /// </summary>
        private const string LanguageRuleSheet = "<style>tspan:lang(ja) { fill: lime; }</style>";

        private static string LanguageScriptDocument(string body, string tspanAttributes) =>
            $"<svg width='340' height='220'>{LanguageRuleSheet}" +
            $"<script>document.querySelector('tspan').setAttribute(" +
            $"'lang', 'ja');</script><text fill='black'>" +
            $"<tspan x='10' y='100' font-size='90' {tspanAttributes}>{body}</tspan></text></svg>";

        [Theory]
        [InlineData("")]
        [InlineData("lang='en'")]
        [InlineData("xml:lang='en'")]
        public void LanguageStyledTextUnderAScriptedDomFailsClosed(string tspanAttributes)
        {
            // The guard is document-level and deliberately does not look at the tag
            // the tree happens to carry: a script that can still change it leaves
            // the paint undecided whichever spelling is already in the markup.
            using var result = new FenSvgRenderer().Render(
                LanguageScriptDocument("Quick", tspanAttributes));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("language-styled", StringComparison.Ordinal));
        }

        [Fact]
        public void XmlLangScriptedDocumentIsNotLeftHalfStyled()
        {
            // The xml:lang twin installs its tag through setAttributeNS, so the
            // selector the cascade matched on is never the one the reference used.
            // Both halves of the pair therefore have to leave the first-party path
            // for the same reason.
            using var result = new FenSvgRenderer().Render(
                $"<svg width='340' height='220'>{LanguageRuleSheet}" +
                "<script>document.querySelector('tspan').setAttributeNS(" +
                "'http://www.w3.org/XML/1998/namespace', 'xml:lang', 'ja');</script>" +
                "<text fill='black'><tspan x='10' y='100' font-size='90'>Quick</tspan></text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("dynamic-content", result.FallbackReasonCodes);
        }

        [Fact]
        public void LanguageSelectorWithNoScriptKeepsPaintingTheStyledRun()
        {
            // The guard is the conjunction, not the selector alone. A document whose
            // language is already final lets the cascade decide the paint, so the
            // rule applies and the run comes out in the styled colour.
            using var result = new FenSvgRenderer().Render(
                "<svg width='340' height='220'><style>tspan:lang(ja) { fill: lime; }</style>" +
                "<text fill='black'><tspan x='10' y='100' font-size='90' lang='ja'>Quick</tspan>" +
                "<tspan x='10' y='200' font-size='90'>Brown</tspan></text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasForeground(result.Bitmap));
        }

        [Fact]
        public void ScriptedDocumentWithNoLanguageSelectorIsRefusedAtAdmission()
        {
            // The document-level script admission now decides this document, so the
            // language guard below it is unreachable for a scripted document. Its
            // own property - a sheet with no :lang() rule is not a language sheet -
            // is asserted on the script-free spelling, and the scripted spelling
            // must fail closed rather than paint the pre-script fill.
            using var scriptFree = new FenSvgRenderer().Render(
                "<svg width='340' height='220'><style>tspan { fill: lime; }</style>" +
                "<text fill='black'><tspan x='10' y='100' font-size='90'>Quick</tspan></text></svg>");

            Assert.True(scriptFree.Success, scriptFree.ErrorMessage);
            Assert.False(scriptFree.RequiresFallback, string.Join("; ", scriptFree.Warnings));
            Assert.True(HasForeground(scriptFree.Bitmap));

            using var scripted = new FenSvgRenderer().Render(
                "<svg width='340' height='220'><style>tspan { fill: lime; }</style>" +
                "<script>document.querySelector('tspan').setAttribute('fill', 'red');</script>" +
                "<text fill='black'><tspan x='10' y='100' font-size='90'>Quick</tspan></text></svg>");

            AssertFailsClosed(scripted);
            Assert.Contains("dynamic-content", scripted.FallbackReasonCodes);
        }

        [Fact]
        public void LanguageNameOutsideASelectorDoesNotRouteToFallback()
        {
            // A functional pseudo-class is only spelled with a leading colon, so a
            // bare mention of the same name in a declaration value is not a rule
            // the cascade would drop.
            using var scriptFree = new FenSvgRenderer().Render(
                "<svg width='340' height='220'><style>tspan { content: 'lang(ja)'; }</style>" +
                "<text fill='black'><tspan x='10' y='100' font-size='90'>Quick</tspan></text></svg>");

            Assert.True(scriptFree.Success, scriptFree.ErrorMessage);
            Assert.False(scriptFree.RequiresFallback, string.Join("; ", scriptFree.Warnings));
            Assert.DoesNotContain("css-cascade", scriptFree.FallbackReasonCodes);
        }

        [Fact]
        public void LanguageScopedTypefaceSelectionIsDeterministic()
        {
            // The resolver probes installed families in a fixed order, so the same
            // language and the same run must land on the same face on every render
            // in the process, with no dependence on what was resolved before.
            using var first = new FenSvgRenderer().Render(LanguageTagged("lang='zh-TW'", CjkSample));
            using var warmup = new FenSvgRenderer().Render(LanguageTagged("lang='ja'", CjkSample));
            using var second = new FenSvgRenderer().Render(LanguageTagged("lang='zh-TW'", CjkSample));

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.False(first.RequiresFallback, string.Join("; ", first.Warnings));
            Assert.Equal(first.Bitmap.Width, second.Bitmap.Width);
            Assert.Equal(first.Bitmap.Height, second.Bitmap.Height);
            for (int y = 0; y < first.Bitmap.Height; y++)
            for (int x = 0; x < first.Bitmap.Width; x++)
                Assert.Equal(first.Bitmap.GetPixel(x, y), second.Bitmap.GetPixel(x, y));
        }

        [Fact]
        public void LanguageTaggedCjkRunIsNeverLeftOnAFaceThatCannotDrawIt()
        {
            // Every language the table distinguishes has to resolve a face that
            // actually covers the run, or the render would silently paint nothing
            // and still claim success.
            foreach (string language in new[] { "ja", "ko", "zh-CN", "zh-TW" })
            {
                using var result = new FenSvgRenderer().Render(
                    LanguageTagged($"lang='{language}'", CjkSample));

                Assert.True(result.Success, $"{language}: {result.ErrorMessage}");
                Assert.False(result.RequiresFallback, $"{language}: " + string.Join("; ", result.Warnings));
                Assert.True(HasForeground(result.Bitmap), $"lang={language} painted no glyphs");
            }
        }

        private static readonly string[] JapaneseProbes =
        {
            "Yu Gothic", "MS Gothic", "Meiryo", "Hiragino Sans", "Noto Sans CJK JP"
        };

        private static readonly string[] TraditionalChineseProbes =
        {
            "Microsoft JhengHei", "PMingLiU", "MingLiU", "Noto Sans CJK TC", "PingFang TC"
        };

        private static bool HasInstalledFamilyFor(string language, string text)
        {
            string[] probes = language.Equals("ja", StringComparison.OrdinalIgnoreCase)
                ? JapaneseProbes
                : TraditionalChineseProbes;
            foreach (string probe in probes)
            {
                var typeface = SKTypeface.FromFamilyName(probe);
                if (typeface == null) continue;
                bool installed = typeface.FamilyName.Trim()
                    .Equals(probe, StringComparison.OrdinalIgnoreCase);
                bool covers = installed;
                if (installed)
                {
                    using var font = new SKFont(typeface);
                    covers = font.ContainsGlyphs(text);
                }
                if (covers) return true;
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

        /// <summary>
        /// Verifies that <paramref name="second"/> is <paramref name="first"/>
        /// translated by a constant number of rows and returns that offset,
        /// positive when the second render sits lower.
        /// </summary>
        private static int VerticalOffset(SvgRenderResult first, SvgRenderResult second)
        {
            Assert.True(TryGetInkBounds(first.Bitmap, out var firstBounds));
            Assert.True(TryGetInkBounds(second.Bitmap, out var secondBounds));
            Assert.Equal(firstBounds.Left, secondBounds.Left);
            Assert.Equal(firstBounds.Width, secondBounds.Width);
            int rows = secondBounds.Top - firstBounds.Top;
            for (int y = 0; y < first.Bitmap.Height; y++)
            {
                int other = y + rows;
                if (other < 0 || other >= second.Bitmap.Height) continue;
                for (int x = 0; x < first.Bitmap.Width; x++)
                {
                    Assert.Equal(
                        second.Bitmap.GetPixel(x, other).Alpha,
                        first.Bitmap.GetPixel(x, y).Alpha);
                }
            }
            return rows;
        }

        [Theory]
        [InlineData("text-before-edge", "hanging")]
        [InlineData("text-before-edge", "text-top")]
        [InlineData("before-edge", "hanging")]
        [InlineData("text-after-edge", "ideographic")]
        [InlineData("text-after-edge", "text-bottom")]
        [InlineData("after-edge", "ideographic")]
        [InlineData("auto", "alphabetic")]
        [InlineData("alphabetic", "auto")]
        public void DominantBaseline_AliasesResolveToTheSameBaseline(string first, string second)
        {
            AssertSamePixels(
                Baseline($"dominant-baseline='{first}'"),
                Baseline($"dominant-baseline='{second}'"));
        }

        [Theory]
        [InlineData("text-before-edge")]
        [InlineData("text-after-edge")]
        [InlineData("central")]
        [InlineData("middle")]
        [InlineData("hanging")]
        [InlineData("ideographic")]
        public void AlignmentBaseline_AgreesWithDominantBaseline(string value)
        {
            AssertSamePixels(
                Baseline($"alignment-baseline='{value}'"),
                Baseline($"dominant-baseline='{value}'"));
        }

        [Theory]
        [InlineData("text-before-edge")]
        [InlineData("text-after-edge")]
        [InlineData("central")]
        [InlineData("middle")]
        [InlineData("hanging")]
        [InlineData("ideographic")]
        public void DominantBaseline_OnlyTranslatesTheRunVertically(string value)
        {
            using var shifted = new FenSvgRenderer().Render(Baseline($"dominant-baseline='{value}'"));
            using var plain = new FenSvgRenderer().Render(Baseline(string.Empty));

            Assert.True(shifted.Success, shifted.ErrorMessage);
            Assert.False(shifted.RequiresFallback, string.Join("; ", shifted.Warnings));
            Assert.True(plain.Success, plain.ErrorMessage);
            int rows = VerticalOffset(plain, shifted);
            if (value.StartsWith("text-before-edge", StringComparison.Ordinal) ||
                value is "hanging" or "before-edge" or "text-top")
                Assert.True(rows > 0, $"{value} must move the run down onto the before edge");
            else if (value is "text-after-edge" or "ideographic" or "text-bottom" or "after-edge")
                Assert.True(rows < 0, $"{value} must move the run up onto the after edge");
            else
                Assert.NotEqual(0, rows);
        }

        [Fact]
        public void DominantBaseline_EdgeBaselinesBracketCentral()
        {
            // WPT svg/text/reftests/dominant-baseline-text-before-edge.svg,
            // -text-after-edge.svg and -central-large-font-size.svg place the
            // before edge, the after edge and the central baseline one ascent,
            // one descent and the midpoint of the two away from the alphabetic
            // baseline. The three shifts are therefore not independent constants
            // read off the same table: a flipped sign, a metric taken from the
            // wrong face, or an ascent/descent pair that does not belong to the
            // run's own font moves one of them and breaks the relation, which is
            // why the identity is asserted rather than the sign alone. A real face
            // carries no whole-pixel metric, so each measured shift rounds to the
            // row it lands in and the relation is asserted to within that row.
            using var alphabetic = new FenSvgRenderer().Render(Baseline(string.Empty));
            using var before = new FenSvgRenderer().Render(
                Baseline("dominant-baseline='text-before-edge'"));
            using var after = new FenSvgRenderer().Render(
                Baseline("dominant-baseline='text-after-edge'"));
            using var central = new FenSvgRenderer().Render(Baseline("dominant-baseline='central'"));

            int beforeRows = VerticalOffset(alphabetic, before);
            int afterRows = VerticalOffset(alphabetic, after);
            int centralRows = VerticalOffset(alphabetic, central);

            Assert.True(beforeRows > 0, "the before edge sits above the alphabetic baseline");
            Assert.True(afterRows < 0, "the after edge sits below the alphabetic baseline");
            Assert.InRange(2 * centralRows - beforeRows - afterRows, -1, 1);
        }

        [Fact]
        public void DominantBaseline_EdgeShiftsScaleWithTheRunFontSize()
        {
            // WPT svg/text/reftests/dominant-baseline-hanging-small-font-size.svg
            // sets a fractional font-size inside a fractional viewBox, so the
            // shifts have to be the run's own metrics scaled by the run's own
            // font-size. Doubling the font-size doubles every shift; a shift
            // pinned to a pixel constant, or to the 0.8/0.2 ascent/descent pair
            // the Ahem-based reference happens to carry, would not. A real face
            // has no whole-pixel metric, so the measured ink bounds carry a row
            // of rounding and the ratio is asserted to within that row.
            foreach (string keyword in new[]
                     {
                         "hanging", "text-before-edge", "text-after-edge", "central", "middle"
                     })
            {
                using var small = new FenSvgRenderer().Render(ScaledBaseline(string.Empty, "40", "260"));
                using var smallShifted = new FenSvgRenderer().Render(
                    ScaledBaseline($"dominant-baseline='{keyword}'", "40", "260"));
                using var large = new FenSvgRenderer().Render(ScaledBaseline(string.Empty, "80", "260"));
                using var largeShifted = new FenSvgRenderer().Render(
                    ScaledBaseline($"dominant-baseline='{keyword}'", "80", "260"));

                int smallRows = VerticalOffset(small, smallShifted);
                int largeRows = VerticalOffset(large, largeShifted);

                Assert.NotEqual(0, smallRows);
                Assert.InRange(largeRows, 2 * smallRows - 1, 2 * smallRows + 1);
            }
        }

        [Fact]
        public void DominantBaseline_AuthoredInAStyleSheetFailsClosed()
        {
            // WPT svg/text/reftests/lengthAdjust-large-font.svg authors the run's
            // whole text style in an ancestor <style> sheet instead of on the
            // element, and the three dominant-baseline reftests set the property
            // as a presentation attribute. A sheet rule the cascade cannot resolve
            // into a text property has to be reported where it is seen, because
            // the alternative is a run painted on the alphabetic baseline that
            // reports success for a document that asked for the hanging baseline.
            const string sheet =
                "<svg width='260' height='200'><style>text { dominant-baseline: hanging }</style>" +
                "<text x='10' y='80' font-family='monospace' font-size='40' " +
                "fill='black'>X</text></svg>";

            using var result = new FenSvgRenderer().Render(sheet);

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("dominant-baseline", StringComparison.Ordinal));
        }

        [Fact]
        public void BaselineShift_PositiveLengthLiftsTheRunByExactlyThatAmount()
        {
            using var shifted = new FenSvgRenderer().Render(Baseline("baseline-shift='9'"));
            using var plain = new FenSvgRenderer().Render(Baseline(string.Empty));

            Assert.True(shifted.Success, shifted.ErrorMessage);
            Assert.False(shifted.RequiresFallback, string.Join("; ", shifted.Warnings));
            Assert.Equal(-9, VerticalOffset(plain, shifted));
        }

        [Fact]
        public void BaselineShift_PercentageResolvesAgainstTheFontSize()
        {
            AssertSamePixels(
                Baseline("baseline-shift='25%'"),
                Baseline("baseline-shift='10'", "80"));
        }

        private static string Rotated(string declarations, string content = "X", string baselineY = "80") =>
            $"<svg width='260' height='200'><text x='10' y='{baselineY}' {MetricFont} " +
            $"font-size='40' fill='black' {declarations}>{content}</text></svg>";

        [Fact]
        public void Rotate_TurnsTheGlyphAndIsAdmitted()
        {
            AssertDifferentPixels(Rotated(string.Empty), Rotated("rotate='30'"));
        }

        [Theory]
        [InlineData("rotate='30'")]
        [InlineData("rotate='30 30'")]
        [InlineData("rotate='30 30 30 30 30'")]
        public void Rotate_SingleAngleAppliesToEveryCharacter(string declarations)
        {
            AssertSamePixels(Rotated(declarations, "AB"), Rotated("rotate='30 30'", "AB"));
        }

        [Fact]
        public void Rotate_ShortListKeepsItsLastAngleForTheRemainingCharacters()
        {
            AssertSamePixels(
                Rotated("rotate='0 30'", "ABC"),
                Rotated("rotate='0 30 30 30'", "ABC"));
        }

        /// <summary>
        /// A half turn about the glyph origin maps the ink above the baseline to
        /// the same distance below it, so the two ink boxes are mirror images
        /// across the resolved baseline.
        /// </summary>
        [Fact]
        public void Rotate_TurnsTheGlyphAboutItsOwnOriginOnTheBaseline()
        {
            using var plain = new FenSvgRenderer().Render(Rotated(string.Empty));
            using var flipped = new FenSvgRenderer().Render(Rotated("rotate='180'"));

            Assert.True(plain.Success, plain.ErrorMessage);
            Assert.False(plain.RequiresFallback, string.Join("; ", plain.Warnings));
            Assert.True(flipped.Success, flipped.ErrorMessage);
            Assert.False(flipped.RequiresFallback, string.Join("; ", flipped.Warnings));
            Assert.True(TryGetInkBounds(plain.Bitmap, out var plainBounds));
            Assert.True(TryGetInkBounds(flipped.Bitmap, out var flippedBounds));
            Assert.True(plainBounds.Bottom <= 80,
                "the unrotated glyph must not leave ink below its own baseline");
            Assert.True(flippedBounds.Top >= 80,
                "a half turned glyph must not leave ink above its own baseline");
            Assert.InRange(plainBounds.Top + flippedBounds.Bottom, 158f, 162f);
        }

        /// <summary>
        /// A tspan that declares no list keeps the list and the running index of
        /// the nearest ancestor that declared one, so the flattened list is the
        /// ancestor's read as one continuous sequence of characters.
        /// </summary>
        [Fact]
        public void Rotate_TspanWithoutAListContinuesTheAncestorListAndIndex()
        {
            AssertSamePixels(
                Rotated("rotate='0 90'", "AB<tspan>CD</tspan>"),
                Rotated("rotate='0 90 90 90'", "ABCD"));
        }

        [Fact]
        public void Rotate_TspanDeclaringAListRestartsTheAncestorIndexAfterIt()
        {
            AssertSamePixels(
                Rotated("rotate='0 90'", "AB<tspan rotate='45 30'>CD</tspan>EF"),
                Rotated("rotate='0 90 45 30 90 90'", "ABCDEF"));
        }

        [Fact]
        public void Rotate_TextAnchorElementDeclaresItsOwnList()
        {
            AssertSamePixels(
                Rotated("rotate='0 90'", "AB<a rotate='45 30'>CD</a>EF"),
                Rotated("rotate='0 90 45 30 90 90'", "ABCDEF"));
        }

        [Fact]
        public void Rotate_UnderlinedRunTurnsTheDecorationWithTheGlyph()
        {
            AssertDifferentPixels(
                Rotated("rotate='0' text-decoration='underline'", "XY"),
                Rotated("rotate='90' text-decoration='underline'", "XY"));
        }

        /// <summary>
        /// A rotation turns the glyph but does not move the pen, so a character
        /// that inherits a zero angle past a rotated run starts at the same
        /// coordinate either way.
        /// </summary>
        [Fact]
        public void Rotate_LeavesTheAdvanceUnchanged()
        {
            const string plain =
                "<svg width='300' height='200'><text x='10' y='80' font-family='monospace' " +
                "font-size='40' fill='black'>AB<tspan fill='red'>C</tspan></text></svg>";
            const string rotated =
                "<svg width='300' height='200'><text x='10' y='80' font-family='monospace' " +
                "font-size='40' fill='black' rotate='25 70 0'>AB<tspan fill='red'>C</tspan></text></svg>";

            using var unrotated = new FenSvgRenderer().Render(plain);
            using var turned = new FenSvgRenderer().Render(rotated);

            Assert.True(turned.Success, turned.ErrorMessage);
            Assert.False(turned.RequiresFallback, string.Join("; ", turned.Warnings));
            Assert.True(TryGetColorBounds(unrotated.Bitmap, IsRed, out var unrotatedRed));
            Assert.True(TryGetColorBounds(turned.Bitmap, IsRed, out var turnedRed));
            Assert.Equal(unrotatedRed, turnedRed);
        }

        [Fact]
        public void Rotate_NonNumericValueFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(Rotated("rotate='sideways'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("advanced-text-layout", result.FallbackReasonCodes);
        }

        [Fact]
        public void Rotate_BidirectionalRunFailsClosed()
        {
            const string svg =
                "<svg width='300' height='80'><text x='10' y='60' font-family='monospace' " +
                "font-size='30' fill='black' rotate='30'>a\u05D0</text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("bidirectional", StringComparison.Ordinal));
        }

        [Fact]
        public void Rotate_ExceedingTheValueBudgetFailsClosed()
        {
            var overBudget = new StringBuilder();
            for (int i = 0; i < 4200; i++) overBudget.Append(i == 0 ? "0" : " 0");
            using var result = new FenSvgRenderer().Render(Rotated("rotate='" + overBudget + "'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("value budget", StringComparison.Ordinal));
        }

        /// <summary>
        /// WPT svg/text/reftests/text-context-fill.svg puts text inside a marker
        /// that follows the path tangent, so the marker lays its content out in
        /// the same orientation the shapes already use and the run stays
        /// admissible.
        /// </summary>
        [Fact]
        public void TextInsideATangentMarkerStaysAdmitted()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100' width='150' height='100'>" +
                "<defs><marker id='m' viewBox='0 0 25 10' refY='5' markerWidth='125' refX='10' " +
                "markerHeight='50'><text x='12' y='8' font-size='9' fill='black'>X</text>" +
                "</marker></defs>" +
                "<line y2='30' x2='50' y1='30' x1='10' stroke='green' marker-end='url(#m)'/>" +
                "</svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        /// <summary>
        /// WPT svg/painting/reftests/paint-context-006.svg puts a rotated glyph
        /// inside a marker that declares its own angle. The marker angle and the
        /// glyph angle turn the same run about two different points, and the
        /// single pass has not established that composition, so the document is
        /// reported rather than painted at an unverified composed position.
        /// </summary>
        [Fact]
        public void TextInsideAnExplicitlyRotatedMarkerFailsClosed()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 300'>" +
                "<defs><marker id='m' refX='0' refY='0' markerWidth='200' markerHeight='200' " +
                "markerUnits='userSpaceOnUse' orient='0.25turn'>" +
                "<text x='20' y='150' font-size='60' fill='black' rotate='-45'>A</text>" +
                "</marker></defs>" +
                "<path d='M 200 150 L 300 150' stroke='black' fill='none' marker-start='url(#m)'/>" +
                "</svg>";

            using var result = new FenSvgRenderer().Render(svg);

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("explicitly rotated marker", StringComparison.Ordinal));
        }

        [Fact]
        public void TextInsideAMarkerWithZeroOrientStaysAdmitted()
        {
            const string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 300'>" +
                "<defs><marker id='m' refX='0' refY='0' markerWidth='200' markerHeight='200' " +
                "markerUnits='userSpaceOnUse' orient='0'>" +
                "<text x='20' y='100' font-size='60' fill='black'>A</text>" +
                "</marker></defs>" +
                "<path d='M 200 150 L 300 150' stroke='black' fill='none' marker-start='url(#m)'/>" +
                "</svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Theory]
        [InlineData("unicode-bidi='isolate'")]
        [InlineData("unicode-bidi='isolate-override'")]
        [InlineData("writing-mode='vertical-rl'")]
        [InlineData("dominant-baseline='mathematical'")]
        [InlineData("dominant-baseline='use-script'")]
        [InlineData("baseline-shift='sub'")]
        [InlineData("baseline-shift='super'")]
        [InlineData("textLength='20' lengthAdjust='squeeze'")]
        [InlineData("text-decoration='blink'")]
        [InlineData("text-rendering='bogus'")]
        [InlineData("font-variant='small-caps'")]
        [InlineData("font-stretch='condensed'")]
        [InlineData("glyph-orientation-vertical='90'")]
        [InlineData("font='20px monospace'")]
        public void UnsupportedAdvancedTextValues_FailClosedInsteadOfPaintingWrongPixels(string declarations)
        {
            using var result = new FenSvgRenderer().Render(Baseline(declarations));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("direction='ltr'")]
        [InlineData("unicode-bidi='normal'")]
        [InlineData("writing-mode='horizontal-tb'")]
        [InlineData("writing-mode='lr'")]
        [InlineData("text-rendering='auto'")]
        [InlineData("text-rendering='optimizeSpeed'")]
        [InlineData("text-rendering='optimizeLegibility'")]
        [InlineData("text-rendering='geometricPrecision'")]
        [InlineData("text-decoration='none'")]
        [InlineData("rotate='0'")]
        [InlineData("rotate='0 0 0'")]
        [InlineData("rotate='auto'")]
        [InlineData("word-spacing='normal'")]
        [InlineData("word-spacing='0'")]
        [InlineData("dominant-baseline='auto'")]
        [InlineData("baseline-shift='0'")]
        [InlineData("baseline-shift='inherit'")]
        [InlineData("baseline-shift='baseline'")]
        [InlineData("font-variant='normal'")]
        [InlineData("font-stretch='normal'")]
        public void InertAdvancedTextValues_AreAdmittedAndPaintIdentically(string declarations)
        {
            AssertSamePixels(Baseline(declarations), Baseline(string.Empty));
        }

        [Theory]
        [InlineData("unicode-bidi='embed'")]
        [InlineData("unicode-bidi='inline'")]
        [InlineData("unicode-bidi='bidi-override'")]
        [InlineData("unicode-bidi='plaintext'")]
        public void BidiFrameValues_AreAdmittedAndPaintIdenticallyAtTheBaseLevel(
            string declarations)
        {
            AssertSamePixels(Baseline(declarations), Baseline(string.Empty));
        }

        [Fact]
        public void DirectionAttribute_ReachesTheLayoutPassAndSwapsTheChunkAnchors()
        {
            AssertSamePixels(
                Baseline("direction='rtl'"), Baseline("text-anchor='end'"));
        }

        private const string BidirectionalFont = "font-family='monospace' font-size='20'";

        private static string Paragraph(string content) =>
            $"<svg width='320' height='60'><text x='10' y='40' {BidirectionalFont} " +
            $"fill='black'>{content}</text></svg>";

        [Theory]
        [InlineData("&#x202A;Start Anchor&#x202C;", "Start Anchor")]
        [InlineData("&#x202B;Start Anchor&#x202C;", "Start Anchor")]
        [InlineData("&#x202D;Start Anchor&#x202C;", "Start Anchor")]
        [InlineData("Start &#x202A;Anchor&#x202C;", "Start Anchor")]
        [InlineData("&#x202B;Start&#x202C; Anchor", "Start Anchor")]
        // The same paragraph written with the raw formatting characters.
        [InlineData("\u202AStart Anchor\u202C", "Start Anchor")]
        [InlineData("\u202BStart\u202C Anchor", "Start Anchor")]
        [InlineData("\u202EStart\u202C Anchor", "tratS Anchor")]
        [InlineData("\u202DAnchor\u202C", "Anchor")]
        // A directional override reverses the visual order of the characters it
        // encloses and leaves everything outside it alone.
        [InlineData("&#x202E;Start Anchor&#x202C;", "rohcnA tratS")]
        [InlineData("&#x202E;Start&#x202C; Anchor", "tratS Anchor")]
        [InlineData("Start &#x202E;Anchor&#x202C;", "Start rohcnA")]
        [InlineData("&#x202E;Start 12&#x202C;", "21 tratS")]
        [InlineData("\u202EStart Anchor\u202C", "rohcnA tratS")]
        [InlineData("Start \u202EAnchor\u202C", "Start rohcnA")]
        public void ExplicitDirectionalControlsResolveToTheVisualOrder(
            string content,
            string visualOrder)
        {
            // WPT svg/text/reftests/text-bidi-controls-anchors-1.svg lays the same
            // controls out with text-anchor over a left-to-right paragraph.
            AssertSamePixels(Paragraph(content), Paragraph(visualOrder));
        }

        [Fact]
        public void ExplicitDirectionalControlsDoNotChangeTheRunExtent()
        {
            // The embedding controls reorder glyphs without moving the run, so a
            // text-anchor keeps the same anchor point.
            AssertSamePixels(
                "<svg width='320' height='60'><text x='160' y='40' text-anchor='middle' " +
                $"{BidirectionalFont} fill='black'>&#x202B;Start Anchor&#x202C;</text></svg>",
                "<svg width='320' height='60'><text x='160' y='40' text-anchor='middle' " +
                $"{BidirectionalFont} fill='black'>Start Anchor</text></svg>");
        }

        [Theory]
        [InlineData("start", "end")]
        [InlineData("middle", "middle")]
        [InlineData("end", "start")]
        public void RightToLeftBaseDirectionSwapsTheStartAndEndAnchors(
            string rightToLeftAnchor,
            string leftToRightAnchor)
        {
            // WPT svg/text/reftests/text-bidi-controls-anchors-2.svg sets
            // direction on a <g>, which the bounded parse never inspects, so the
            // text layout pass is the stage that resolves it. The start and end
            // of a chunk follow the base direction, so the same content anchored
            // at the same point has to paint identically once the two anchors are
            // exchanged.
            AssertSamePixels(
                "<svg width='320' height='60'><g direction='rtl' transform='translate(200 20)'>" +
                $"<text y='20' style='fill: black' text-anchor='{rightToLeftAnchor}'>" +
                "Start Anchor</text></g></svg>",
                "<svg width='320' height='60'>" +
                $"<text x='200' y='40' style='fill: black' text-anchor='{leftToRightAnchor}'>" +
                "Start Anchor</text></svg>");
        }

        [Theory]
        [InlineData("\u202BStart Anchor\u202C", "Start Anchor")]
        [InlineData("\u202BStart\u202C Anchor", "Anchor Start")]
        [InlineData("Start \u202BAnchor\u202C", "Anchor Start")]
        public void RightToLeftBaseDirectionResolvesTheReferenceVisualOrder(
            string content,
            string visualOrder)
        {
            // The rows of WPT svg/text/reftests/text-bidi-controls-anchors-2.svg
            // whose expected visual order is one directional run, which the
            // reference expresses as a left-to-right literal.
            AssertSamePixels(
                "<svg width='320' height='60'><g direction='rtl'>" +
                $"<text x='200' y='40' text-anchor='start' style='fill: black'>{content}</text></g></svg>",
                "<svg width='320' height='60'>" +
                $"<text x='200' y='40' text-anchor='end' style='fill: black'>{visualOrder}</text></svg>");
        }

        [Fact]
        public void RightToLeftOverrideReversesTheWholeLine()
        {
            // A directional override over the whole line reverses it, and the
            // numbers in it are part of the same run, so the trailing run paints
            // leftmost. A left-to-right literal would shape different advance
            // pairs than the reversed line the shaper produces, so the visual
            // order is asserted from the painted runs instead.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'>" +
                "<text x='10' y='40' font-size='20'>&#x202E;" +
                "<tspan fill='#f00'>Start</tspan><tspan fill='#00f'> 12</tspan>" +
                "&#x202C;</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetInkBounds(result.Bitmap, out var ink));
            Assert.True(TryGetColorBounds(result.Bitmap, IsRed, out var red), $"no red ink in {ink}");
            Assert.True(TryGetColorBounds(result.Bitmap, IsBlue, out var blue), $"no blue ink in {ink}");
            Assert.True(blue.Right < red.Left,
                $"expected the trailing run leftmost, red=[{red.Left}..{red.Right}] " +
                $"blue=[{blue.Left}..{blue.Right}]");
        }

        [Theory]
        [InlineData("<tspan fill='#00f'>&#x202E;Start&#x202C;</tspan><tspan fill='#f00'> Anchor</tspan>", true)]
        [InlineData("<tspan fill='#f00'>Start </tspan><tspan fill='#00f'>&#x202E;Anchor&#x202C;</tspan>", false)]
        public void RightToLeftOverrideOrdersAMixedDirectionalRun(string content, bool redIsLeftmost)
        {
            // The two rows of WPT svg/text/reftests/
            // text-bidi-controls-anchors-2.svg whose override covers one word of
            // the line. The override makes that word its own directional run, and
            // a right-to-left paragraph places the run it authored last leftmost,
            // whichever word the override covers.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g direction='rtl'>" +
                $"<text x='300' y='40' font-size='20'>{content}</text></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetInkBounds(result.Bitmap, out var ink));
            Assert.True(TryGetColorBounds(result.Bitmap, IsRed, out var red), $"no red ink in {ink}");
            Assert.True(TryGetColorBounds(result.Bitmap, IsBlue, out var blue), $"no blue ink in {ink}");
            if (redIsLeftmost) Assert.True(red.Right < blue.Left, "expected the red word leftmost");
            else Assert.True(blue.Right < red.Left, "expected the blue word leftmost");
        }

        [Fact]
        public void RightToLeftScriptLaysOutAsOneRightToLeftIsland()
        {
            // A left-to-right paragraph keeps the surrounding text in place and
            // lays the right-to-left word out as a single island, so the word's
            // first character is the rightmost of the three.
            const string svg =
                "<svg width='320' height='60'><text x='10' y='44' font-size='30' fill='black'>" +
                "<tspan fill='#f00'>\u05D0</tspan><tspan fill='#0f0'>\u05D1</tspan>" +
                "<tspan fill='#00f'>\u05D2</tspan></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetColorBounds(result.Bitmap, IsRed, out var red));
            Assert.True(TryGetColorBounds(result.Bitmap, IsGreen, out var green));
            Assert.True(TryGetColorBounds(result.Bitmap, IsBlue, out var blue));
            Assert.True(green.Right < red.Left,
                "the middle character of a right-to-left word paints to the left of the first");
        }

        [Fact]
        public void MixedDirectionRunsResolveToTheVisualOrder()
        {
            // WPT svg/text/reftests/tspan-opacity-mixed-direction.svg interleaves
            // a right-to-left word with left-to-right text. Logical order is
            // A, alef, bet, gimel, B; the resolved levels are 0, 1, 1, 1, 0, so
            // the visual order reverses the word in place.
            const string svg =
                "<svg width='320' height='60'><text x='10' y='44' font-size='30' fill='black'>" +
                "<tspan fill='#000'>A</tspan><tspan fill='#f00'>\u05D0</tspan>" +
                "<tspan fill='#0f0'>\u05D1</tspan><tspan fill='#00f'>\u05D2</tspan>" +
                "<tspan fill='#000'>B</tspan></text></svg>";

            using var result = new FenSvgRenderer().Render(svg);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetColorBounds(result.Bitmap, IsRed, out var red));
            Assert.True(TryGetColorBounds(result.Bitmap, IsGreen, out var green));
            Assert.True(TryGetColorBounds(result.Bitmap, IsBlue, out var blue));
            Assert.True(blue.Right < green.Left && green.Right < red.Left,
                "a right-to-left word reverses in place between its left-to-right neighbours");
        }

        /// <summary>
        /// WPT svg/text/reftests/tspan-opacity-mixed-direction.svg carries its
        /// opacity on a <c>tspan</c> whose two glyphs the paragraph bidi pass
        /// moves apart, and compares against a reference that splits the same
        /// characters over two <c>tspan</c>s. Splitting changes nothing the
        /// document asks for, so the two forms have to paint the same pixels:
        /// the re-cut piece is a level run, and a level run is shaped in the
        /// face its own characters resolve to rather than the face the whole
        /// source run resolved to. The named face covers the Latin piece and
        /// not the Hebrew one, so a piece that keeps the source run's face
        /// paints the Latin glyphs too narrow and shifts every later glyph.
        /// </summary>
        [Fact]
        public void MixedDirectionTspanResolvesATypefaceForEachReorderedPiece()
        {
            AssertSamePixels(
                "<svg width='320' height='60'><text x='20' y='44' font-family='monospace' " +
                "font-size='30' fill='black'><tspan style='opacity: 0.5'>a\u05D1</tspan>\u05D2b</text></svg>",
                "<svg width='320' height='60'><text x='20' y='44' font-family='monospace' " +
                "font-size='30' fill='black' style='direction: ltr; unicode-bidi: bidi-override'>" +
                "<tspan style='opacity: 0.5'>a</tspan>\u05D2" +
                "<tspan style='opacity: 0.5'>\u05D1</tspan>b</text></svg>");
        }

        [Fact]
        public void RightToLeftScriptWithAnInheritedBaseDirectionIsAdmitted()
        {
            // WPT svg/import/text-intro-05-t-manual.svg, -06-, -07-, -11- and -12-
            // all set direction on a <g> and paint Arabic runs that the platform
            // typeface covers.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g direction='rtl' font-size='20'>" +
                "<text x='300' y='40' text-anchor='start' xml:lang='ar'>\u0645\u0631\u062D\u0628\u0627</text>" +
                "</g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasForeground(result.Bitmap));
        }

        [Fact]
        public void AnExplicitIsolateInitiatorFailsClosed()
        {
            // The isolating run sequence rules are a separate stage, so a
            // paragraph that carries an isolate is reported rather than laid out
            // with the isolate treated as ordinary text.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>&#x2067;abc&#x2069;</text></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("bidirectional", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate")]
        [InlineData("isolate-override")]
        public void AnIsolateSetOnTheTextElementFailsClosed(string value)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' unicode-bidi='" + value + "' " +
                "font-family='monospace' font-size='20' fill='black'>" +
                "abc &#x05D0;&#x05D1; def</text></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate")]
        [InlineData("isolate-override")]
        public void AnIsolateDeliveredByASetAnimationFailsClosed(string value)
        {
            // A set delivers its value through AnimatedProperties, which
            // GetPresentationProperty prefers over both the attribute and the
            // cascade, so neither admission gate can see it.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>abc &#x05D0;&#x05D1; def" +
                "<set attributeName='unicode-bidi' to='" + value +
                "' begin='0s' dur='1s' fill='freeze'/></text></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate")]
        [InlineData("isolate-override")]
        public void AnIsolateAtTheSampledKeyframeFailsClosed(string value)
        {
            // The isolate is the first keyframe, so it is the value the document
            // time samples and therefore the value the run is laid out with.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>abc &#x05D0;&#x05D1; def" +
                "<animate attributeName='unicode-bidi' from='" + value +
                "' to='normal' begin='0s' dur='1s' fill='freeze'/></text></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate")]
        [InlineData("isolate-override")]
        public void AnIsolateSetOnAnAncestorOfTheAnimationFailsClosed(string value)
        {
            // The isolate is delivered to a container, so only the inherited text
            // style sees it.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>abc &#x05D0;&#x05D1; def</text>" +
                "<set attributeName='unicode-bidi' to='" + value +
                "' begin='0s' dur='1s' fill='freeze'/></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate")]
        [InlineData("isolate-override")]
        public void AnIsolateResolvedThroughACssVariableFailsClosed(string value)
        {
            // var() substitution happens inside GetPresentationProperty, so the
            // substituted isolate reaches the same check.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><style>g{--b:" + value + ";unicode-bidi:var(--b)}</style>" +
                "<g><text x='10' y='40' font-family='monospace' font-size='20' fill='black'>" +
                "abc &#x05D0;&#x05D1; def</text></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal));
        }

        private const string UnicodeBidiBody = "abc &#x05D0;&#x05D1; def";

        private static string UnicodeBidiRtlBase(string declarations) =>
            "<svg width='320' height='60'><g direction='rtl'><text x='10' y='40' " +
            $"font-family='monospace' font-size='20' fill='black' {declarations}>" +
            UnicodeBidiBody + "</text></g></svg>";

        private static string UnicodeBidiRtlBaseWithTspan(string declarations) =>
            "<svg width='320' height='60'><g direction='rtl'><text x='10' y='40' " +
            $"font-family='monospace' font-size='20' fill='black'><tspan {declarations}>" +
            UnicodeBidiBody + "</tspan></text></g></svg>";

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("plaintext isolate")]
        [InlineData("embed isolate")]
        [InlineData("isolate bidi-override")]
        [InlineData("bidi-override isolate")]
        [InlineData("isolate-override plaintext")]
        [InlineData("normal isolate")]
        [InlineData("isolate isolate")]
        [InlineData("isolate  plaintext")]
        [InlineData("Isolate PlainText")]
        public void AnIsolateCombinedWithAnotherKeywordFailsClosed(string value)
        {
            // The grammar is a keyword set, not a single keyword, so comparing the
            // trimmed whole value to "isolate" is not the same question as asking
            // whether the value isolates. "isolate plaintext" isolates and takes
            // the paragraph level from the first strong character; the run is laid
            // out against the wrong level and still reports Success.
            using var result = new FenSvgRenderer().Render(
                UnicodeBidiRtlBase("unicode-bidi='" + value + "'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("requires compatibility fallback", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("embed isolate")]
        [InlineData("isolate-override plaintext")]
        public void AnIsolateCombinedWithAnotherKeywordOnATspanFailsClosed(string value)
        {
            using var result = new FenSvgRenderer().Render(
                UnicodeBidiRtlBaseWithTspan("unicode-bidi='" + value + "'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
        }

        [Theory]
        [InlineData("embed bidi-override plaintext")]
        [InlineData("plaintext plaintext")]
        [InlineData("normal embed bidi-override")]
        [InlineData("embed plaintext plaintext")]
        public void AUnicodeBidiValueLongerThanTheGrammarFailsClosed(string value)
        {
            // A run of more tokens than the grammar admits is a value the text
            // model cannot reason about at all, so it is refused rather than
            // truncated to whichever prefix happens to match.
            using var result = new FenSvgRenderer().Render(
                UnicodeBidiRtlBase("unicode-bidi='" + value + "'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
        }

        [Theory]
        [InlineData("embed plaintext")]
        [InlineData("plaintext embed")]
        [InlineData("bidi-override plaintext")]
        [InlineData("plaintext bidi-override")]
        public void AnEmbeddingKeywordCombinedWithPlaintextFailsClosed(string value)
        {
            // The one legal two-keyword run the engine cannot compute: a browser
            // opens the embedding frame and takes the paragraph level from the
            // first strong character, and no whole-value comparison here matches,
            // so the frame is dropped in silence under a success report.
            using var result = new FenSvgRenderer().Render(
                UnicodeBidiRtlBase("unicode-bidi='" + value + "'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
        }

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("embed bidi-override plaintext")]
        [InlineData("embed plaintext")]
        public void ACombinedIsolateFromTheStyleSheetFailsClosed(string value)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g direction='rtl'><style>text { unicode-bidi: " +
                value + " }</style><text x='10' y='40' font-family='monospace' font-size='20' " +
                "fill='black'>" + UnicodeBidiBody + "</text></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("requires compatibility fallback", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("embed plaintext")]
        public void ACombinedIsolateResolvedThroughACssVariableFailsClosed(string value)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g direction='rtl'><style>g{--b:" + value +
                ";unicode-bidi:var(--b)}</style><g><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>" + UnicodeBidiBody + "</text></g></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("requires compatibility fallback", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("embed isolate")]
        [InlineData("embed plaintext")]
        public void ACombinedIsolateDeliveredByAnimationFailsClosed(string value)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g direction='rtl'><text x='10' y='40' " +
                "font-family='monospace' font-size='20' fill='black'>" + UnicodeBidiBody +
                "<animate attributeName='unicode-bidi' from='" + value +
                "' to='normal' begin='0s' dur='1s' fill='freeze'/></text></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal) ||
                warning.Contains("requires compatibility fallback", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("isolate plaintext")]
        [InlineData("embed isolate")]
        [InlineData("plaintext embed")]
        [InlineData("normal embed bidi-override")]
        public void ACombinedIsolateInheritedFromAnAncestorFailsClosed(string value)
        {
            // The bounded parse only inspects text and tspan attributes, so on a
            // container the inherited text style is the only thing that sees the
            // value.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g direction='rtl'><g unicode-bidi='" + value +
                "'><text x='10' y='40' font-family='monospace' font-size='20' fill='black'>" +
                UnicodeBidiBody + "</text></g></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("isolating run sequence", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("normal")]
        [InlineData("embed")]
        [InlineData("bidi-override")]
        [InlineData("plaintext")]
        [InlineData("embed bidi-override")]
        [InlineData("bidi-override embed")]
        [InlineData("embed  bidi-override")]
        [InlineData("normal embed")]
        [InlineData("embed embed")]
        [InlineData("inherit")]
        [InlineData("initial")]
        [InlineData("unset")]
        [InlineData("revert")]
        [InlineData("bogus")]
        [InlineData("inline")]
        public void AUnicodeBidiValueTheTextModelComputesStaysAdmitted(string value)
        {
            using var result = new FenSvgRenderer().Render(
                UnicodeBidiRtlBase("unicode-bidi='" + value + "'"));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(HasForeground(result.Bitmap));
        }

        [Theory]
        [InlineData("ISOLATE")]
        [InlineData("Isolate")]
        [InlineData("Isolate-Override")]
        [InlineData("  isolate  ")]
        [InlineData("plaintext isolate")]
        [InlineData("isolate plaintext")]
        [InlineData("embed isolate")]
        [InlineData("embed plaintext")]
        [InlineData("plaintext embed")]
        [InlineData("bidi-override plaintext")]
        [InlineData("embed bidi-override plaintext")]
        public void AUnicodeBidiValueTheTextModelCannotComputeIsRefusedWhateverItsCaseOrSpacing(string value)
        {
            // An isolate carries no embedding frame but still changes the
            // paragraph, which is why it is refused rather than dropped, and the
            // refusal cannot be evaded by respelling or respacing the value.
            using var result = new FenSvgRenderer().Render(
                UnicodeBidiRtlBase("unicode-bidi='" + value + "'"));

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("unicode-bidi=''")]
        [InlineData("unicode-bidi='   '")]
        [InlineData("unicode-bidi='bogus'")]
        [InlineData("unicode-bidi='inline'")]
        public void AnUnresolvableUnicodeBidiValueIsDroppedAndPaintsTheBaseDocument(string declaration)
        {
            // An empty, blank, or unrecognised value matches no keyword a browser
            // accepts either, so the run keeps the inherited value on both sides.
            AssertSamePixels(
                UnicodeBidiRtlBase(declaration),
                UnicodeBidiRtlBase(string.Empty));
        }

        [Fact]
        public void AnAdmittedUnicodeBidiValueStillReachesTheTextPass()
        {
            // The first pair is the control that makes the second one meaningful:
            // a plaintext declaration over a latin-first run takes the paragraph
            // level from the first strong character, so it lands away from the
            // inherited right-to-left base. The value therefore reaches the text
            // pass and is honoured there, and the combined value on the same
            // document matching that inherited base is a drop rather than a
            // coincidence.
            AssertDifferentPixels(
                UnicodeBidiRtlBase("unicode-bidi='plaintext'"),
                UnicodeBidiRtlBase(string.Empty));
            AssertSamePixels(
                UnicodeBidiRtlBase("unicode-bidi='embed bidi-override'"),
                UnicodeBidiRtlBase(string.Empty));
            AssertSamePixels(
                UnicodeBidiRtlBase("unicode-bidi='bidi-override embed'"),
                UnicodeBidiRtlBase(string.Empty));
            AssertSamePixels(
                UnicodeBidiRtlBase("unicode-bidi='  embed   bidi-override  '"),
                UnicodeBidiRtlBase(string.Empty));
        }

        [Fact]
        public void AnAnimateToAnIsolateFailsClosed()
        {
            // A to-only animate carries no sampleable value list, so the SMIL
            // stage refuses it before the text pass runs. The document still must
            // not paint, whatever the stage that catches it.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>abc &#x05D0;&#x05D1; def" +
                "<animate attributeName='unicode-bidi' to='isolate' begin='0s' dur='1s' fill='freeze'/></text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
        }

        [Fact]
        public void AnIsolateAnimatedOnlyAfterTheSampledTimeStaysAdmitted()
        {
            // The first keyframe is normal, so the sampled value at document time
            // is a value the pass implements and the frame is painted.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' font-family='monospace' " +
                "font-size='20' fill='black'>abc" +
                "<animate attributeName='unicode-bidi' from='normal' to='isolate' " +
                "begin='0s' dur='1s' fill='freeze'/></text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        private const string RtlOuter =
            "<svg width='400' height='60'><g direction='rtl'><g direction='@@'>" +
            "<text x='10' y='40' font-family='monospace' font-size='20' fill='black'>abc</text>" +
            "</g></g></svg>";

        private const string PlainOuter =
            "<svg width='400' height='60'><text x='10' y='40' font-family='monospace' " +
            "font-size='20' fill='black'>abc</text></svg>";

        private const string PlaintextOuter =
            "<svg width='400' height='60'><g unicode-bidi='plaintext'><g unicode-bidi='@@'>" +
            "<text x='10' y='40' font-family='monospace' font-size='20' fill='black'>" +
            "&#x05D0;&#x05D1;&#x05D2; abc</text></g></g></svg>";

        private const string PlaintextOnly =
            "<svg width='400' height='60'><g unicode-bidi='plaintext'>" +
            "<text x='10' y='40' font-family='monospace' font-size='20' fill='black'>" +
            "&#x05D0;&#x05D1;&#x05D2; abc</text></g></svg>";

        private const string HebrewPlain =
            "<svg width='400' height='60'><text x='10' y='40' font-family='monospace' " +
            "font-size='20' fill='black'>&#x05D0;&#x05D1;&#x05D2; abc</text></svg>";

        private const string RtlOnly =
            "<svg width='400' height='60'><g direction='rtl'>" +
            "<text x='10' y='40' font-family='monospace' font-size='20' fill='black'>abc</text></g></svg>";

        [Theory]
        [InlineData("initial")]
        [InlineData("revert")]
        public void DirectionCssWideInitialKeywordsTakeTheInitialValueNotTheParent(string keyword)
        {
            // direction is inherited, but initial and revert resolve to the
            // property's initial value ltr, which is what a browser lays out.
            AssertSamePixels(RtlOuter.Replace("@@", keyword), PlainOuter);
            AssertDifferentPixels(RtlOuter.Replace("@@", keyword), RtlOnly);
        }

        [Theory]
        [InlineData("inherit")]
        [InlineData("unset")]
        public void DirectionCssWideInheritedKeywordsStillTakeTheParentValue(string keyword)
        {
            // Both properties are inherited, so unset is defined as inherit.
            AssertSamePixels(RtlOuter.Replace("@@", keyword), RtlOnly);
        }

        [Theory]
        [InlineData("initial")]
        [InlineData("revert")]
        [InlineData("normal")]
        public void UnicodeBidiCssWideInitialKeywordsTakeNormalNotTheParent(string keyword)
        {
            AssertSamePixels(PlaintextOuter.Replace("@@", keyword), HebrewPlain);
            AssertDifferentPixels(PlaintextOuter.Replace("@@", keyword), PlaintextOnly);
        }

        [Theory]
        [InlineData("inherit")]
        [InlineData("unset")]
        public void UnicodeBidiCssWideInheritedKeywordsStillTakeTheParentValue(string keyword)
        {
            AssertSamePixels(PlaintextOuter.Replace("@@", keyword), PlaintextOnly);
        }

        [Fact]
        public void UnicodeBidiInlineIsDroppedBecauseItIsNotACssKeyword()
        {
            // inline is not a unicode-bidi value, so the declaration is invalid and
            // the property keeps the value it inherited. Treating it as embed used
            // to reset a plaintext base level that a browser keeps.
            AssertSamePixels(PlaintextOuter.Replace("@@", "inline"), PlaintextOnly);
            AssertDifferentPixels(PlaintextOuter.Replace("@@", "inline"), HebrewPlain);
        }

        [Fact]
        public void AnUnsupportedUnicodeBidiKeywordIsDroppedRatherThanPainted()
        {
            // An unrecognised value is invalid at parse time, so the run keeps the
            // inherited value and no frame is invented for it.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g unicode-bidi='bogus'>" +
                "<text x='10' y='40' font-family='monospace' font-size='20' fill='black'>" +
                "abc</text></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Fact]
        public void AVerticalWritingModeOnAnAncestorFailsClosed()
        {
            // WPT svg/import/text-align-05-b-manual.svg sets writing-mode on a <g>,
            // which the bounded parse never inspects. A vertical run is laid out
            // by a separate subsystem, so it is reported rather than painted as a
            // horizontal line.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g writing-mode='tb'>" +
                "<text x='10' y='40' font-size='20' fill='black'>Start Anchor</text></g></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("writing mode", StringComparison.Ordinal));
        }

        [Fact]
        public void AVerticalWritingModeOnTheTextElementStillFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><text x='10' y='40' writing-mode='tb' " +
                "font-size='20' fill='black'>Start Anchor</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
        }

        [Fact]
        public void ADirectionalOverrideInsideAnEmbeddingResolvesToTheVisualOrder()
        {
            // A unicode-bidi override on an ancestor opens an embedding frame, so
            // the paragraph resolves the nested direction the frame declares.
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><g unicode-bidi='embed' direction='rtl'>" +
                "<text x='10' y='40' font-family='monospace' font-size='20' fill='black'>" +
                "Start Anchor</text></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Fact]
        public void PlaintextResolvesTheBaseLevelFromTheFirstStrongCharacter()
        {
            // unicode-bidi="plaintext" takes the paragraph level from the first
            // strong character of the content, so a right-to-left first character
            // lands on the same layout direction="rtl" produces.
            AssertSamePixels(
                "<svg width='320' height='60'><g unicode-bidi='plaintext'>" +
                "<text x='10' y='40' font-size='20' fill='black'>\u05D0\u05D1\u05D2 abc</text></g></svg>",
                "<svg width='320' height='60'><g direction='rtl'>" +
                "<text x='10' y='40' font-size='20' fill='black'>\u05D0\u05D1\u05D2 abc</text></g></svg>");
        }

        [Fact]
        public void PlaintextTakesTheLeftToRightBaseLevelFromALatinFirstCharacter()
        {
            AssertSamePixels(
                "<svg width='320' height='60'><g unicode-bidi='plaintext'>" +
                "<text x='10' y='40' font-size='20' fill='black'>abc \u05D0\u05D1\u05D2</text></g></svg>",
                "<svg width='320' height='60'>" +
                "<text x='10' y='40' font-size='20' fill='black'>abc \u05D0\u05D1\u05D2</text></svg>");
        }

        [Fact]
        public void BidiTextOnATextPathIsReported()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='320' height='60'><defs><path id='p' d='M10 40 H300'/></defs>" +
                "<text font-size='20'><textPath href='#p'>\u05D0\u05D1</textPath></text></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("textPath", StringComparison.Ordinal));
        }

        [Fact]
        public void AParagraphLongerThanTheBidiBudgetFailsClosed()
        {
            var budget = new StringBuilder();
            for (int i = 0; i < 1100; i++) budget.Append((char)('a' + (i % 26)));
            budget.Append('\u05D0');

            using var result = new FenSvgRenderer().Render(
                "<svg width='1400' height='60'><text x='10' y='40' font-size='12' fill='black'>" +
                budget + "</text></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("bidirectional", StringComparison.Ordinal));
        }

        [Fact]
        public void TextLength_ListValuedXIsEquivalentToASingleValue()
        {
            const string listed =
                "<svg width='400' height='100'><text x='0 50 100 150 200 250 300 350 400 450 500 550' y='80' " +
                "font-family='monospace' font-size='50' textLength='400' " +
                "lengthAdjust='spacingAndGlyphs' fill='black'>XXXXXXXXXXXX</text></svg>";
            const string single =
                "<svg width='400' height='100'><text x='0' y='80' font-family='monospace' " +
                "font-size='50' textLength='400' lengthAdjust='spacingAndGlyphs' " +
                "fill='black'>XXXXXXXXXXXX</text></svg>";
            AssertSamePixels(listed, single);
        }

        [Theory]
        [InlineData("spacing")]
        [InlineData("spacingAndGlyphs")]
        public void TextLength_ListValuedYIsEquivalentToASingleValue(string lengthAdjust)
        {
            string listed = $"<svg width='400' height='100'><text x='0' y='80 80 80 80 80 80' " +
                            $"font-family='monospace' font-size='50' textLength='400' " +
                            $"lengthAdjust='{lengthAdjust}' fill='black'>XXXXXX</text></svg>";
            string single = $"<svg width='400' height='100'><text x='0' y='80' " +
                            $"font-family='monospace' font-size='50' textLength='400' " +
                            $"lengthAdjust='{lengthAdjust}' fill='black'>XXXXXX</text></svg>";
            AssertSamePixels(listed, single);
        }

        [Fact]
        public void TextLength_ListWithoutOneValuePerCharacterFailsClosed()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='260' height='200'><text x='10' y='80' font-family='monospace' " +
                "font-size='40' fill='black' textLength='10 20'>X</text></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("advanced-text-layout", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("spacing")]
        [InlineData("spacingAndGlyphs")]
        public void TextLength_ReachesTheDeclaredAdvance(string lengthAdjust)
        {
            using var natural = new FenSvgRenderer().Render(TextLengthDocument(string.Empty));
            using var adjusted = new FenSvgRenderer().Render(TextLengthDocument(
                " textLength='400' lengthAdjust='" + lengthAdjust + "'"));

            Assert.True(adjusted.Success, adjusted.ErrorMessage);
            Assert.False(adjusted.RequiresFallback, string.Join("; ", adjusted.Warnings));
            Assert.True(TryGetInkBounds(natural.Bitmap, out var naturalBounds));
            Assert.True(TryGetInkBounds(adjusted.Bitmap, out var adjustedBounds));
            Assert.True(adjustedBounds.Width > naturalBounds.Width + 40,
                "the adjusted run must be wider than the natural run");
            Assert.InRange(adjustedBounds.Width, 394f, 401f);
        }

        [Fact]
        public void TextLength_SpacingAndGlyphsScaleTheOutlinesAsWellAsTheSpacing()
        {
            AssertDifferentPixels(
                TextLengthDocument(" textLength='400' lengthAdjust='spacing'"),
                TextLengthDocument(" textLength='400' lengthAdjust='spacingAndGlyphs'"));
        }

        [Fact]
        public void TextLength_WithoutTextLengthIsInert()
        {
            AssertSamePixels(
                TextLengthDocument(" lengthAdjust='squeeze'"),
                TextLengthDocument(string.Empty));
        }

        [Fact]
        public void TextLength_RunSizedByAnAncestorStyleSheetFontShorthandReachesTheDeclaredLength()
        {
            // WPT svg/text/reftests/lengthAdjust-large-font.svg takes the run's
            // font-size and font-family from `text { font: 100px/1 Ahem }` in a
            // <style> sheet and declares no font-size on the element at all, so
            // the declared textLength governs the run only if the sheet's `font`
            // shorthand is expanded into the longhands the text layout reads. A
            // sheet rule that never reached the run would leave the initial 16px
            // font, whose ink is a fraction of the height asserted below.
            const string sheet =
                "<svg width='200' height='120'><style>text { font: 60px monospace }</style>" +
                "<text x='0' y='80' fill='black' textLength='120' " +
                "lengthAdjust='spacingAndGlyphs'>XXXX</text></svg>";
            const string longhands =
                "<svg width='200' height='120'><text x='0' y='80' font-size='60' " +
                "font-family='monospace' fill='black' textLength='120' " +
                "lengthAdjust='spacingAndGlyphs'>XXXX</text></svg>";

            AssertSamePixels(sheet, longhands);

            using var result = new FenSvgRenderer().Render(sheet);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.True(TryGetInkBounds(result.Bitmap, out var bounds));
            Assert.True(bounds.Height > 30,
                "the sheet's 60px font must size the run, not the initial 16px font");
        }

        private static string TextLengthDocument(string extra) =>
            "<svg width='700' height='100'><text x='0' y='80' font-family='monospace' " +
            $"font-size='50' fill='black'{extra}>XXXXXXXXXXXX</text></svg>";

        [Fact]
        public void TextDecoration_UnderlineAddsABandBelowTheBaseline()
        {
            using var plain = new FenSvgRenderer().Render(Baseline(string.Empty));
            using var underlined = new FenSvgRenderer().Render(Baseline("text-decoration='underline'"));

            Assert.True(underlined.Success, underlined.ErrorMessage);
            Assert.False(underlined.RequiresFallback, string.Join("; ", underlined.Warnings));
            Assert.True(TryGetInkBounds(plain.Bitmap, out var plainBounds));
            Assert.True(TryGetInkBounds(underlined.Bitmap, out var underlinedBounds));
            Assert.True(underlinedBounds.Bottom > plainBounds.Bottom,
                "the underline must extend below the glyph ink");
            Assert.Equal(plainBounds.Left, underlinedBounds.Left);
        }

        [Fact]
        public void TextDecoration_CombinationsPaintEveryRequestedBand()
        {
            AssertDifferentPixels(
                Baseline("text-decoration='underline'"),
                Baseline("text-decoration='underline overline'"));
            AssertDifferentPixels(
                Baseline("text-decoration='underline'"),
                Baseline("text-decoration='line-through'"));
        }

        private const string DecorationGlyphs = "test";

        private const string DecorationFillLayer =
            "fill='blue' stroke-width='4' stroke-opacity='0.5' style='marker:url(#m)'";

        private const string DecorationStrokeLayer =
            "fill='none' stroke='lime' stroke-width='4' stroke-opacity='0.5'";

        private const string DecorationMarkerLayer =
            "fill='none' style='marker:url(#m)'";

        private const string DecorationMarker =
            "<marker id='m' refX='5' refY='5' viewBox='0 0 10 10' overflow='visible'>" +
            "<circle cx='5' cy='5' r='3' fill='black' stroke='black' fill-opacity='0.5'/>" +
            "</marker>";

        private static string DecorationSheet(string layers) =>
            "<svg width='340' height='140'><defs>" + DecorationMarker + "</defs>" + layers + "</svg>";

        private static string DecorationLayer(string paintOrder, string layer) =>
            $"<text y='95' font-family='monospace' font-size='48' paint-order='{paintOrder}' " +
            $"text-decoration='underline' {layer}>{DecorationGlyphs}</text>";

        /// <summary>
        /// The reference for WPT svg/painting/reftests/paint-order-text-decorations.svg
        /// splits one underlined text element carrying a fill and a stroke into one
        /// element per paint layer, ordered by paint-order, so a band is emitted by
        /// whichever layer owns the phase it is painted in. A single element that
        /// drops the stroke band, or paints the two bands in a fixed order, breaks
        /// the equivalence the reference encodes.
        /// </summary>
        [Theory]
        [InlineData("fill stroke markers", "fill|stroke|markers")]
        [InlineData("fill markers stroke", "fill|markers|stroke")]
        [InlineData("stroke fill markers", "stroke|fill|markers")]
        [InlineData("stroke markers fill", "stroke|markers|fill")]
        [InlineData("markers fill stroke", "markers|fill|stroke")]
        [InlineData("markers stroke fill", "markers|stroke|fill")]
        public void TextDecoration_EveryPaintOrderMatchesItsLayeredDecomposition(
            string paintOrder, string layerOrder)
        {
            string single = DecorationLayer(
                paintOrder,
                "fill='blue' stroke='lime' stroke-width='4' stroke-opacity='0.5' " +
                "style='marker:url(#m)'");
            var builder = new System.Text.StringBuilder();
            foreach (string layer in layerOrder.Split('|'))
            {
                builder.Append(DecorationLayer(
                    paintOrder,
                    layer switch
                    {
                        "fill" => DecorationFillLayer,
                        "stroke" => DecorationStrokeLayer,
                        _ => DecorationMarkerLayer
                    }));
            }

            AssertSamePixels(DecorationSheet(single), DecorationSheet(builder.ToString()));
        }

        [Fact]
        public void TextDecoration_BandsShareTheRunSpanAndStraddleTheBaseline()
        {
            using var plain = new FenSvgRenderer().Render(Baseline(string.Empty));
            using var underlined = new FenSvgRenderer().Render(
                Baseline("text-decoration='underline'"));
            using var overlined = new FenSvgRenderer().Render(
                Baseline("text-decoration='overline'"));
            using var struck = new FenSvgRenderer().Render(
                Baseline("text-decoration='line-through'"));

            Assert.True(underlined.Success, underlined.ErrorMessage);
            Assert.False(underlined.RequiresFallback, string.Join("; ", underlined.Warnings));
            Assert.True(TryGetInkBounds(plain.Bitmap, out var plainBounds));
            Assert.True(TryGetInkBounds(underlined.Bitmap, out var underlineBounds));
            Assert.True(TryGetInkBounds(overlined.Bitmap, out var overlineBounds));
            Assert.True(TryGetInkBounds(struck.Bitmap, out var strikeBounds));

            Assert.Equal(plainBounds.Left, underlineBounds.Left);
            Assert.Equal(underlineBounds.Left, overlineBounds.Left);
            Assert.Equal(underlineBounds.Left, strikeBounds.Left);
            Assert.Equal(underlineBounds.Right, overlineBounds.Right);
            Assert.Equal(underlineBounds.Right, strikeBounds.Right);
            Assert.True(overlineBounds.Top < plainBounds.Top,
                "the overline must sit above the glyph ink");
            Assert.True(underlineBounds.Bottom > plainBounds.Bottom,
                "the underline must sit below the glyph ink");
            Assert.True(strikeBounds.Bottom >= plainBounds.Bottom,
                "the strikeout must not start below the glyph ink");
            Assert.True(strikeBounds.Bottom < underlineBounds.Bottom,
                "the strikeout must stop above the underline");
        }

        [Fact]
        public void WordSpacing_AddsAdvanceAfterEverySpaceOnly()
        {
            const string plain =
                "<svg width='260' height='80'><text x='10' y='50' font-family='monospace' " +
                "font-size='24' fill='black'>Fen F<tspan fill='red'>X</tspan></text></svg>";
            const string spaced =
                "<svg width='260' height='80'><text x='10' y='50' font-family='monospace' " +
                "font-size='24' fill='black' word-spacing='13'>Fen F<tspan fill='red'>X</tspan></text></svg>";

            using var plainResult = new FenSvgRenderer().Render(plain);
            using var spacedResult = new FenSvgRenderer().Render(spaced);
            Assert.True(plainResult.Success, plainResult.ErrorMessage);
            Assert.False(plainResult.RequiresFallback, string.Join("; ", plainResult.Warnings));
            Assert.True(spacedResult.Success, spacedResult.ErrorMessage);
            Assert.False(spacedResult.RequiresFallback, string.Join("; ", spacedResult.Warnings));
            Assert.True(TryGetColorBounds(plainResult.Bitmap, c => c.Red > 180 && c.Green < 80, out var plainRed));
            Assert.True(TryGetColorBounds(spacedResult.Bitmap, c => c.Red > 180 && c.Green < 80, out var spacedRed));
            Assert.Equal(plainRed.Left + 13, spacedRed.Left);
        }

        [Fact]
        public void WordSpacing_IsInertWithoutASpace()
        {
            AssertSamePixels(
                "<svg width='260' height='80'><text x='10' y='50' font-family='monospace' " +
                "font-size='24' fill='black' word-spacing='13'>Fen</text></svg>",
                "<svg width='260' height='80'><text x='10' y='50' font-family='monospace' " +
                "font-size='24' fill='black'>Fen</text></svg>");
        }

        private static bool TryGetInkBounds(SKBitmap bitmap, out SKRectI bounds) =>
            TryGetColorBounds(bitmap, _ => true, out bounds);

        private static bool IsRed(SKColor color) => color.Red > 180 && color.Green < 80;

        private static bool IsGreen(SKColor color) => color.Green > 180 && color.Red < 80;

        private static bool IsBlue(SKColor color) => color.Blue > 180 && color.Red < 80;

        [Fact]
        public void SiblingTextElements_ObeyDocumentGlyphBudget()        {
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
                string text = texts[index % texts.Length];
                using var result = renderer.Render(
                    $"<svg width='160' height='40'><text x='3' y='30' font-size='24'>{text}</text></svg>");
                if (IsRightToLeftScriptText(text))
                {
                    Assert.True(result.Success, result.ErrorMessage);
                    Assert.False(result.RequiresFallback);
                    Assert.True(HasForeground(result.Bitmap));
                    return;
                }
                Assert.True(result.Success, result.ErrorMessage);
                Assert.False(result.RequiresFallback);
                Assert.True(HasForeground(result.Bitmap));
            }));

            await Task.WhenAll(tasks);
        }

        private static bool IsRightToLeftScriptText(string text) =>
            text.Any(c => c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or
                (>= '\uFE70' and <= '\uFEFE'));

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
