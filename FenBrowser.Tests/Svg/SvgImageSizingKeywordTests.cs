using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// CSS sizing keywords and sizing functions on an &lt;image&gt;, which is a
    /// replaced element: its used size is the content-based (intrinsic) size and
    /// min/max-* then clamp that size. The first-party renderer computes none of
    /// those suggestions, so every value the cascade hands to the image box
    /// resolver is refused by name instead of leaving the image unsized. Values
    /// the cascade classifies as invalid never arrive and keep sizing from auto.
    /// </summary>
    public sealed class SvgImageSizingKeywordTests
    {
        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        public void ImageSizingKeyword_FailsClosedNamingIntrinsicSizing(string keyword)
        {
            using var result = Render(Image(keyword, keyword));

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("requires intrinsic replaced-element sizing", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"'width: {keyword}'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("min-content", "width", "width: min-content")]
        [InlineData("max-content", "width", "width: max-content")]
        [InlineData("stretch", "height", "height: stretch")]
        [InlineData("fit-content", "height", "height: fit-content")]
        public void ImageSizingKeyword_OnEitherAxis_FailsClosed(
            string keyword,
            string property,
            string expected)
        {
            using var result = Render(Image(
                property == "width" ? keyword : "40px",
                property == "height" ? keyword : "40px"));

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains(expected, StringComparison.Ordinal));
        }

        [Fact]
        public void ImageSizingKeyword_AsPresentationAttribute_FailsClosed()
        {
            // WPT svg/styling/image-sizing-min-content.tentative.svg states the
            // keyword in a style declaration; the presentation-attribute form is
            // the same value through the same cascade and the same refusal.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='300' height='150'>" +
                "<image width='min-content' height='min-content' href='" + GreenPng(16) + "'/>" +
                "</svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("'width: min-content'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("calc-size(auto, size)")]
        [InlineData("calc-size(fit-content, size)")]
        [InlineData("calc-size(30%, size + 10px)")]
        public void ImageCalcSize_FailsClosedNamingIntrinsicSizing(string declaration)
        {
            using var result = Render(Image(declaration, declaration));

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"'width: {declaration}'", StringComparison.Ordinal));
        }

        [Fact]
        public void ImageAnchorSize_IsDroppedByTheCascadeAndSizesFromAuto()
        {
            // anchor-size() has a dashed-ident argument, so the cascade's geometry
            // grammar check drops it. The image box guard covers it in case that
            // decision ever changes, but today it never arrives.
            using var result = Render(Image("anchor-size(--edge)", "anchor-size(--edge)"));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(16 * 16, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("fit-content(50%)")]
        [InlineData("fit-content(20px)")]
        [InlineData("fit-content(min(10px, 40px))")]
        public void ImageFitContentFunction_IsDroppedByTheCascadeAndSizesFromAuto(string declaration)
        {
            // SvgCssCascade.IsInvalidGeometryValue keeps the fit-content keyword
            // but not the fit-content() function, so the function form is dropped
            // as invalid and the image sizes from auto. That grammar decision
            // belongs to the cascade; the image box refuses the value if it ever
            // reaches it.
            using var result = Render(Image(declaration, declaration));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(16 * 16, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("calc-size(fit-content")]
        [InlineData("calc-size(fit-content, ")]
        [InlineData("calc-size(banana, size)")]
        [InlineData("calc-size(fit-content, size +")]
        public void MalformedCalcSize_FailsClosedRatherThanDroppingTheImage(string declaration)
        {
            // The cascade keeps every calc-size() form as a valid geometry value, so
            // a malformed one reaches the box resolver too. Dropping the image
            // silently there would paint nothing while reporting success.
            using var result = Render(Image(declaration, declaration));

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("banana")]
        [InlineData("min-content(")]
        [InlineData("fit-content(")]
        [InlineData("fit-content(banana)")]
        [InlineData("anchor-size(edge)")]
        [InlineData("contain")]
        [InlineData("fill-available")]
        [InlineData("none")]
        public void UnrecognisedImageSizingValue_IsDroppedAndSizesFromAuto(string declaration)
        {
            // SvgCssCascade.IsInvalidGeometryValue classifies these as invalid
            // geometry values, so the declaration never reaches the walk and the
            // image sizes from auto - the used value a browser that rejects the
            // declaration produces. contain and fill-available are owned by that
            // grammar decision, not by the image box resolver.
            using var result = Render(Image(declaration, declaration));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Empty(result.FallbackReasonCodes);
            Assert.Equal(16 * 16, Foreground(result.Bitmap));
        }

        [Theory]
        [InlineData("min-width", "100px")]
        [InlineData("max-width", "10px")]
        [InlineData("min-height", "100%")]
        [InlineData("max-height", "1em")]
        public void ImageSizeConstraint_FailsClosedAtTheCascade(string property, string value)
        {
            // min/max-* are not honoured anywhere in the picture renderer, so the
            // cascade refuses them document-wide before sizing is ever reached.
            using var result = Render(Image("40px", "40px", property, value));

            AssertFailsClosed(result);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains($"SVG CSS property '{property}'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        [InlineData("contain")]
        public void ImageOffsetSizingKeyword_BehavesAsAutoAndStillPaints(string keyword)
        {
            // x/y keywords are dropped as invalid declarations, so auto - zero - is
            // already the used value: painting the image at the origin is the spec
            // frame and no refusal is warranted.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='60'>" +
                "<image x='" + keyword + "' y='" + keyword + "' width='40' height='30' href='" +
                GreenPng(16) + "'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(30 * 30, Foreground(result.Bitmap));
            Assert.True(result.Bitmap.GetPixel(5, 5).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(4, 15).Alpha);
            Assert.Equal(0, result.Bitmap.GetPixel(39, 29).Alpha);
        }

        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        public void NestedSvgSizingKeyword_KeepsAttributeSizingAndIsNotRefused(string keyword)
        {
            // The nested-<svg> decision is unchanged: an SVG viewport has no
            // intrinsic size, so the keyword does not apply and the geometry
            // attributes stay in charge (svgwg#1059).
            using var actual = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" +
                "<svg width='50' height='50' style='width:" + keyword + ";height:" + keyword + "'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");
            using var expected = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" +
                "<svg width='50' height='50'>" +
                "<rect width='50' height='50' fill='green'/></svg></svg>");

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

        [Theory]
        [InlineData("min-content")]
        [InlineData("max-content")]
        [InlineData("fit-content")]
        [InlineData("stretch")]
        public void EmbeddedSvgImageSizingKeyword_FailsClosedNamingIntrinsicSizing(string keyword)
        {
            // The referenced-document path establishes the referenced viewport from
            // the same box, so it refuses for the same reason.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='60'>" +
                "<image style='width:" + keyword + ";height:" + keyword + "' href='" +
                SvgDataUri("<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'>" +
                           "<rect width='40' height='30' fill='green'/></svg>") + "'/>" +
                "</svg>");

            AssertFailsClosed(result);
            Assert.Contains("unsupported-property", result.FallbackReasonCodes);
        }

        [Fact]
        public void ShapeSizingKeyword_IsNotRoutedToIntrinsicSizingRefusal()
        {
            // A shape's width is not a replaced-element size: the keyword is an
            // invalid declaration there, dropped by the cascade like any other.
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'>" +
                "<rect style='width:min-content;height:min-content' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(0, Foreground(result.Bitmap));
        }

        [Fact]
        public void ImageWithoutSizingKeyword_StillPaintsInsideTheDeclaredBox()
        {
            using var result = Render(Image("40px", "30px"));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(30 * 30, Foreground(result.Bitmap));
            Assert.True(result.Bitmap.GetPixel(5, 5).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(39, 29).Alpha);
        }

        [Fact]
        public void ImageWithAutoDimension_StillUsesTheIntrinsicRatio()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='60'>" +
                "<image width='40' href='" + GreenPng(16) + "'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(40 * 40, Foreground(result.Bitmap));
        }

        private static SvgRenderResult Render(string svg) =>
            new FenSvgRenderer().Render(svg);

        private static string Image(
            string width,
            string height,
            string extraProperty = null,
            string extraValue = null)
        {
            string style = extraProperty == null
                ? $"width:{width};height:{height}"
                : $"width:{width};height:{height};{extraProperty}:{extraValue}";
            return
                "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='60'>" +
                "<image style='" + style + "' href='" + GreenPng(16) + "'/></svg>";
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
