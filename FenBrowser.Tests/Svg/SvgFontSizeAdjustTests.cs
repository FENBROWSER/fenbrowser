using System;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgFontSizeAdjustTests
    {
        private const string MetricFont = "font-family='monospace'";

        private static string Document(string declarations) =>
            "<svg width='400' height='320'><text x='20' y='200' " + MetricFont +
            " font-size='20' fill='black' " + declarations + ">X</text></svg>";

        private static string UnpositionedDocument(string declarations) =>
            "<svg width='400' height='320'><text " + MetricFont +
            " font-size='20' fill='black' " + declarations + ">X</text></svg>";

        private static string Nested(string outer, string inner) =>
            "<svg width='400' height='320'><g " + outer + "><text x='20' y='200' " +
            MetricFont + " font-size='20' fill='black' " + inner + ">X</text></g></svg>";

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

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.FallbackReasonCodes);
        }

        private static bool TryGetInkBounds(SKBitmap bitmap, out SKRectI bounds)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
            bounds = right >= left ? new SKRectI(left, top, right + 1, bottom + 1) : SKRectI.Empty;
            return right >= left;
        }

        /// <summary>
        /// A ratio is applied to the size the face is drawn at, and every part of a
        /// glyph scales linearly in that size, so the ink box of a run at ratio
        /// <c>r</c> is <c>r</c> times the ink box of the same run at ratio 1 whatever
        /// the face's own x-height ratio happens to be. Asserting that relation
        /// pins the scaling rule without naming a face metric the test would have
        /// to take from the implementation to compute the expected box.
        /// </summary>
        [Fact]
        public void RequestedRatioScalesTheUsedFontSizeExactly()
        {
            using var full = new FenSvgRenderer().Render(Document("font-size-adjust='1'"));
            using var half = new FenSvgRenderer().Render(Document("font-size-adjust='0.5'"));

            Assert.True(full.Success, full.ErrorMessage);
            Assert.False(full.RequiresFallback, string.Join("; ", full.Warnings));
            Assert.True(half.Success, half.ErrorMessage);
            Assert.False(half.RequiresFallback, string.Join("; ", half.Warnings));
            Assert.True(TryGetInkBounds(full.Bitmap, out var fullBounds));
            Assert.True(TryGetInkBounds(half.Bitmap, out var halfBounds));

            Assert.InRange(fullBounds.Width, 2 * halfBounds.Width - 1, 2 * halfBounds.Width + 1);
            Assert.InRange(fullBounds.Height, 2 * halfBounds.Height - 1, 2 * halfBounds.Height + 1);
            Assert.InRange(halfBounds.Width * 2, fullBounds.Width - 1, fullBounds.Width + 1);
            Assert.InRange(halfBounds.Height * 2, fullBounds.Height - 1, fullBounds.Height + 1);
            Assert.True(fullBounds.Width != halfBounds.Width,
                "the ratio must move the run, not leave it at the computed size");
        }

        [Fact]
        public void RequestedRatioScalesTheBaselineAdvanceNotOnlyTheGlyphs()
        {
            using var full = new FenSvgRenderer().Render(
                "<svg width='600' height='120'><text x='20' y='80' " + MetricFont +
                " font-size='20' fill='black' font-size-adjust='1'>XXXX</text></svg>");
            using var half = new FenSvgRenderer().Render(
                "<svg width='600' height='120'><text x='20' y='80' " + MetricFont +
                " font-size='20' fill='black' font-size-adjust='0.5'>XXXX</text></svg>");

            Assert.True(full.Success, full.ErrorMessage);
            Assert.True(half.Success, half.ErrorMessage);
            Assert.True(TryGetInkBounds(full.Bitmap, out var fullBounds));
            Assert.True(TryGetInkBounds(half.Bitmap, out var halfBounds));

            Assert.InRange(fullBounds.Width, 2 * halfBounds.Width - 2, 2 * halfBounds.Width + 2);
        }

        [Theory]
        [InlineData("font-size-adjust='none'")]
        [InlineData("font-size-adjust='from-font'")]
        [InlineData("font-size-adjust='initial'")]
        [InlineData("font-size-adjust='revert'")]
        public void InertRatioValuesLeaveTheRunAtItsComputedSize(string declarations)
        {
            AssertSamePixels(Document(declarations), Document(string.Empty));
        }

        [Theory]
        [InlineData("font-size-adjust='inherit'")]
        [InlineData("font-size-adjust='unset'")]
        public void InheritedRatioKeywordsKeepTheAncestorRatio(string declarations)
        {
            AssertSamePixels(
                Nested("font-size-adjust='1'", declarations),
                Nested("font-size-adjust='1'", string.Empty));
        }

        [Fact]
        public void InheritedRatioReachesATspanThroughTheAncestorChain()
        {
            AssertSamePixels(
                Nested("font-size-adjust='0.5'", string.Empty),
                Document("font-size-adjust='0.5'"));
        }

        [Theory]
        [InlineData("font-size-adjust='calc(1)'")]
        [InlineData("font-size-adjust='1px'")]
        [InlineData("font-size-adjust='medium'")]
        [InlineData("font-size-adjust='-1'")]
        [InlineData("font-size-adjust='0'")]
        [InlineData("font-size-adjust='1 2'")]
        [InlineData("font-size-adjust='bogus'")]
        public void UnreadableRatioValueFailsClosedInsteadOfGuessingASize(string declarations)
        {
            using var result = new FenSvgRenderer().Render(Document(declarations));

            AssertFailsClosed(result);
        }

        /// <summary>
        /// Whether an <c>em</c> length on the adjusted element resolves against the
        /// computed or the adjusted size is the one reading the used-size model does
        /// not settle, so a run that depends on the answer is reported instead of
        /// being painted from one side of it.
        /// </summary>
        [Theory]
        [InlineData("font-size-adjust='1' letter-spacing='0.2em'")]
        [InlineData("font-size-adjust='1' word-spacing='1ex'")]
        [InlineData("font-size-adjust='1' baseline-shift='1em'")]
        [InlineData("font-size-adjust='1' textLength='10em'")]
        [InlineData("font-size-adjust='1' dx='0.5em'")]
        public void RatioBesideAFontRelativeLengthFailsClosed(string declarations)
        {
            using var result = new FenSvgRenderer().Render(Document(declarations));

            AssertFailsClosed(result);
        }

        [Theory]
        [InlineData("font-size-adjust='1' x='2em' y='200'")]
        [InlineData("font-size-adjust='1' x='20' y='10ex'")]
        public void RatioBesideAFontRelativePositionFailsClosed(string declarations)
        {
            using var result = new FenSvgRenderer().Render(UnpositionedDocument(declarations));

            AssertFailsClosed(result);
        }

        [Theory]
        [InlineData("font-size-adjust='1' letter-spacing='2px'")]
        [InlineData("font-size-adjust='1' word-spacing='3'")]
        [InlineData("font-size-adjust='1' baseline-shift='4px'")]
        [InlineData("font-size-adjust='1' textLength='120'")]
        public void RatioBesideAnAbsoluteLengthStillPaints(string declarations)
        {
            using var result = new FenSvgRenderer().Render(Document(declarations));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Theory]
        [InlineData("font-size-adjust='1' x='20' y='200'")]
        [InlineData("font-size-adjust='1' x='10%' y='50%'")]
        public void RatioBesideAVectorOrViewportPositionStillPaints(string declarations)
        {
            using var result = new FenSvgRenderer().Render(UnpositionedDocument(declarations));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Fact]
        public void RatioAppliesToTextPathRuns()
        {
            const string path = "M 20 200 L 380 200";
            using var result = new FenSvgRenderer().Render(
                "<svg width='400' height='320'><defs><path id='p' d='" + path + "'/></defs>" +
                "<text " + MetricFont + " font-size='20' fill='black' " +
                "font-size-adjust='0.5'><textPath href='#p'>XXXX</textPath></text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
        }

        [Theory]
        [InlineData("white-space: pre-line")]
        [InlineData("white-space: pre-wrap")]
        [InlineData("white-space: pre")]
        [InlineData("text-decoration-color: red")]
        [InlineData("inline-size: 320px")]
        [InlineData("text-align: center")]
        [InlineData("shape-inside: url(#s)")]
        [InlineData("shape-margin: 1em")]
        [InlineData("shape-padding: 1em")]
        [InlineData("shape-subtract: url(#s)")]
        [InlineData("writing-mode: vertical-rl")]
        [InlineData("line-spacing: 1.25")]
        public void DeferredTextLayoutPropertiesStillFailClosed(string declaration)
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='400' height='320'><text x='20' y='200' " + MetricFont +
                " font-size='20' fill='black' style='" + declaration + "'>one line</text></svg>");

            AssertFailsClosed(result);
        }
    }
}
