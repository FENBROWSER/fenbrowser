using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgReplacedSizingRefusalTests
    {
        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        public void NestedSvgSizingKeyword_FailsClosedNamingIntrinsicSizing(string keyword)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                $"<svg width='{keyword}' height='{keyword}'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("SVG nested <svg> sizing property", StringComparison.Ordinal) &&
                warning.Contains($"'width: {keyword}'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("calc-size(auto, size)")]
        [InlineData("calc-size(fit-content, size)")]
        [InlineData("fit-content(50%)")]
        [InlineData("anchor-size(--edge)")]
        [InlineData("calc(anchor-size(--a) + 4px)")]
        public void NestedSvgSizingFunction_FailsClosedNamingIntrinsicSizing(string value)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                $"<svg width='{value}' height='{value}'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"'width: {value}'", StringComparison.Ordinal));
        }

        [Fact]
        public void NestedSvgCssViewportUnitSizing_ThatCannotResolve_FailsClosed()
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                "<svg width='70' height='70' style='width:calc(5vw + 1ic);height:calc(5vh + 1ic)'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("'width: calc(5vw + 1ic)'", StringComparison.Ordinal));
        }

        [Fact]
        public void NestedSvgCssViewportUnitSizing_ThatResolvesNegative_IsIgnoredNotRefused()
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                "<svg width='70' height='70' style='width:-5vw;height:-5vh'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(50 * 50, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        public void NestedSvgCssSizingKeyword_KeepsAttributeSizingAndIsNotRefused(string keyword)
        {
            AssertIdenticalRender(
                "<svg width='100' height='100'><svg width='50' height='50' " +
                $"style='width:{keyword};height:{keyword}'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>",
                "<svg width='100' height='100'><svg width='50' height='50'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");
        }

        [Theory]
        [InlineData("width", "min-content")]
        [InlineData("height", "min-content")]
        [InlineData("width", "stretch")]
        [InlineData("height", "stretch")]
        public void RootSvgSizingKeyword_FailsClosedNamingIntrinsicSizing(
            string property, string keyword)
        {
            using var result = Render(
                "<svg " +
                (property == "width" ? $"width='{keyword}' height='40'" : "width='40' ") +
                (property == "height" ? $"height='{keyword}'" : "") + " xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"SVG root <svg> sizing property '{property}: {keyword}'",
                    StringComparison.Ordinal));
        }

        [Fact]
        public void RootSvgSizingKeyword_WithoutViewBox_StillFailsClosed()
        {
            using var result = Render(
                "<svg width='min-content' height='min-content' " +
                "xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.Null(result.Bitmap);
        }

        [Fact]
        public void RootSvgCssSizingKeyword_FailsClosedNamingIntrinsicSizing()
        {
            using var result = Render(
                "<svg viewBox='0 0 40 40' style='width:min-content;height:min-content' " +
                "xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("SVG root <svg> sizing property 'width: min-content'",
                    StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        public void SymbolSizingKeyword_FailsClosedNamingIntrinsicSizing(string keyword)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                $"<defs><symbol id='s' width='{keyword}' height='{keyword}'>" +
                "<rect width='50' height='50' fill='green'/></symbol></defs>" +
                "<use href='#s'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("SVG symbol sizing property", StringComparison.Ordinal) &&
                warning.Contains($"'width: {keyword}'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("symbol")]
        [InlineData("svg")]
        public void UseInstanceSizingKeyword_FailsClosedNamingIntrinsicSizing(string target)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                (target == "symbol"
                    ? "<defs><symbol id='s' width='50' height='50'>"
                    : "<defs><svg id='s' width='50' height='50'>") +
                "<rect width='50' height='50' fill='green'/>" +
                (target == "symbol" ? "</symbol>" : "</svg>") +
                "</defs>" +
                "<use href='#s' width='min-content' height='min-content'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("'width: min-content'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("calc(anchor-size(--a) + 4px)")]
        [InlineData("contain")]
        [InlineData("banana")]
        [InlineData("none")]
        public void ImageSizingAttribute_ThatCannotResolve_FailsClosedNamingIntrinsicSizing(
            string value)
        {
            using var result = Render(
                "<svg width='80' height='60'>" +
                $"<image width='{value}' height='{value}' href='{GreenPng(16)}'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"SVG image sizing property 'width: {value}'",
                    StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("auto", 16)]
        [InlineData("40px", 40)]
        [InlineData("50%", 30)]
        [InlineData("calc(10px + 30px)", 40)]
        [InlineData("calc(2px + 1vw)", 3)]
        [InlineData("2em", 32)]
        [InlineData("2rem", 32)]
        [InlineData("min(40px, 20px)", 20)]
        [InlineData("clamp(10px, 20px, 40px)", 20)]
        public void ImageResolvableSizing_StillResolvesAndPaints(string declaration, int side)
        {
            using var result = Render(
                "<svg width='80' height='60'>" +
                $"<image style='width:{declaration};height:{declaration}' " +
                $"href='{GreenPng(16)}'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(side * side, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        public void ImageNonPositiveExtent_StillDisablesRenderingWithoutRefusal(string value)
        {
            using var result = Render(
                "<svg width='80' height='60'>" +
                $"<image width='{value}' height='{value}' href='{GreenPng(16)}'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.Warnings);
            Assert.Equal(0, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("50", "50", 50)]
        [InlineData("50%", "50%", 50)]
        [InlineData("auto", "auto", 100)]
        public void NestedSvgResolvableExtent_StillSizesTheViewport(
            string width, string height, int side)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                $"<svg width='{width}' height='{height}'>" +
                "<rect width='100%' height='100%' fill='green'/></svg></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(side * side, Foreground(result.Bitmap));
        }

        [Fact]
        public void NestedSvgZeroExtent_StillDisablesRenderingWithoutRefusal()
        {
            using var result = Render(
                "<svg width='100' height='100'><svg width='0' height='0'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.Warnings);
            Assert.Equal(0, Foreground(result.Bitmap));
        }

        [Fact]
        public void NestedSvgViewportUnitCssSizing_StillResolves()
        {
            AssertIdenticalRender(
                "<svg width='300' height='150'><svg style='width:5vw;height:5vh'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>",
                "<svg width='300' height='150'><svg width='5%' height='5%'>" +
                "<circle cx='50' cy='50' r='40' fill='green'/></svg></svg>");
        }

        [Fact]
        public void SymbolResolvableExtent_StillSizesTheInstance()
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                "<defs><symbol id='s' width='40' height='40'>" +
                "<rect width='50' height='50' fill='green'/></symbol></defs>" +
                "<use href='#s'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(40 * 40, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("auto", "auto", 100)]
        [InlineData("", "", 100)]
        public void SymbolAutoExtent_StillTakesTheFullViewport(
            string width, string height, int side)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                $"<defs><symbol id='s' width='{width}' height='{height}'>" +
                "<rect width='100%' height='100%' fill='green'/></symbol></defs>" +
                "<use href='#s'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(side * side, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("symbol", 40)]
        [InlineData("svg", 40)]
        public void UseInstanceAutoExtent_FallsThroughToTheReferencedExtent(string target, int side)
        {
            using var result = Render(
                "<svg width='100' height='100'>" +
                (target == "symbol"
                    ? "<defs><symbol id='s' width='40' height='40'>"
                    : "<defs><svg id='s' width='40' height='40'>") +
                "<rect width='100%' height='100%' fill='green'/>" +
                (target == "symbol" ? "</symbol>" : "</svg>") +
                "</defs>" +
                "<use href='#s' width='auto' height='auto'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(side * side, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("100", "100", 100, 100)]
        [InlineData("100%", "100%", 300, 150)]
        [InlineData("auto", "auto", 300, 150)]
        public void RootResolvableExtent_StillSubstitutesTheDefaultViewport(
            string width, string height, int expectedWidth, int expectedHeight)
        {
            using var result = Render(
                "<svg width='" + width + "' height='" + height + "' " +
                "xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(expectedWidth, (int)result.Width);
            Assert.Equal(expectedHeight, (int)result.Height);
        }

        [Theory]
        [InlineData("50%", "50%", 30, 20)]
        [InlineData("calc(10px + 20px)", "calc(5px + 15px)", 30, 20)]
        [InlineData("30px", "20px", 30, 20)]
        public void RootResolvableCssSizing_StillResolves(
            string width, string height, int expectedWidth, int expectedHeight)
        {
            using var result = Render(
                "<svg width='60' height='40' style='width:" + width + ";height:" + height + "' " +
                "xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(expectedWidth, (int)result.Width);
            Assert.Equal(expectedHeight, (int)result.Height);
        }

        [Theory]
        [InlineData("<rect width='min-content' height='min-content' fill='green'/>")]
        [InlineData("<rect style='width:min-content;height:min-content' fill='green'/>")]
        [InlineData("<circle cx='20' cy='15' r='min-content' fill='green'/>")]
        [InlineData("<rect width='stretch' height='stretch' fill='green'/>")]
        public void ShapeSizingKeyword_StillCollapsesToZero(string shape)
        {
            using var result = Render(
                "<svg width='40' height='30' xmlns='http://www.w3.org/2000/svg'>" +
                shape + "</svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(0, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("min-width", "100px")]
        [InlineData("max-width", "10px")]
        [InlineData("min-height", "100%")]
        [InlineData("max-height", "1em")]
        public void SizeConstraint_StaysRefusedByTheCascadeAlone(string property, string value)
        {
            using var result = Render(
                "<svg width='80' height='60'>" +
                $"<image style='width:40px;height:40px;{property}:{value}' " +
                $"href='{GreenPng(16)}'/></svg>");

            AssertFailsClosed(result);
            Assert.Equal(new[] { "css-cascade" }, result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"SVG CSS property '{property}'", StringComparison.Ordinal));
        }

        [Fact]
        public void ImageViewportUnitSizing_StillResolvesAndIsNotRefused()
        {
            using var result = Render(
                "<svg width='80' height='60'>" +
                $"<image width='10vw' height='10vh' href='{GreenPng(16)}'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.Warnings);
            Assert.True(Foreground(result.Bitmap) > 0);
        }

        [Fact]
        public void RootViewportUnitSizing_StaysRefusedAsAHostViewportGap()
        {
            using var result = Render(
                "<svg style='width:10vw;height:10vh' xmlns='http://www.w3.org/2000/svg'>" +
                "<rect width='50' height='50' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.Equal(new[] { "unsupported-property" }, result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("requires a host viewport", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("requires intrinsic", StringComparison.Ordinal));
        }

        [Fact]
        public void EmbeddedImageAutoRatio_StillEstablishesTheReferencedViewport()
        {
            using var result = Render(
                "<svg width='80' height='60'>" +
                "<image style='width:auto;height:auto' href='" +
                SvgDataUri("<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'>" +
                           "<rect width='40' height='30' fill='green'/></svg>") + "'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(40 * 30, Foreground(result.Bitmap));
        }

        private static SvgRenderResult Render(string svg) =>
            new FenSvgRenderer().Render(svg);

        private static void AssertIdenticalRender(string actualSvg, string referenceSvg)
        {
            using var actual = Render(actualSvg);
            using var expected = Render(referenceSvg);

            Assert.True(actual.Success, actual.ErrorMessage);
            Assert.True(expected.Success, expected.ErrorMessage);
            Assert.False(actual.RequiresFallback);
            Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
            Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
            for (int y = 0; y < actual.Bitmap.Height; y++)
            for (int x = 0; x < actual.Bitmap.Width; x++)
                Assert.True(
                    actual.Bitmap.GetPixel(x, y).Equals(expected.Bitmap.GetPixel(x, y)),
                    $"pixel ({x},{y}) differs");
        }

        private static string GreenPng(int side)
        {
            using var bitmap = new SKBitmap(side, side);
            bitmap.Erase(SKColors.Green);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }

        private static string SvgDataUri(string svg) =>
            "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        private static int Foreground(SKBitmap bitmap)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0)
                    count++;
            return count;
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.True(result.RequiresFallback);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
