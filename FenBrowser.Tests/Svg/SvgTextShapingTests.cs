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
        public void PerGlyphPositionList_RemainsExplicitCompatibilityCase()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='80' height='30'><text x='2 10' y='20'>AB</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
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

            Assert.True(result.Success, result.ErrorMessage);
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

        private static bool HasForeground(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0) return true;
            return false;
        }
    }
}
