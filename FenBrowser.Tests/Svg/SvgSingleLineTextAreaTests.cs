using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// SVG 2 text-in-an-area properties under
    /// <see cref="SvgRenderLimits.LayOutTextAreasOnOneLine"/>: the text lays out on one
    /// line, which is how Chromium paints it (measured: inline-size and shape-inside
    /// leave the run identical to plain text, white-space pre keeps spaces and paints
    /// newlines as spaces). Comparisons are against plain text in the same engine, so
    /// they hold for any installed font.
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgSingleLineTextAreaTests
    {
        private const string Words = "Lorem ipsum dolor sit amet consectetur";

        [Theory]
        [InlineData("inline-size:80px")]
        [InlineData("inline-size:50%;line-spacing:1.25")]
        [InlineData("shape-inside:circle(40px);shape-padding:4px;shape-margin:2px")]
        [InlineData("inline-size:80px;text-align:center")]
        public void AreaProperties_LeaveTheRunIdenticalToPlainText(string style)
        {
            using var plain = RenderSingleLine(Text(""));
            using var area = RenderSingleLine(Text($" style='{style}'"));

            Assert.True(SameImage(plain.Bitmap, area.Bitmap));
        }

        [Fact]
        public void WhiteSpacePre_KeepsSpacesOnOneLine()
        {
            string newline = "\n";
            using var collapsed = RenderSingleLine(Text(" style='white-space:pre-line'", "a   b" + newline + "c"));
            using var preserved = RenderSingleLine(Text(" style='white-space:pre'", "a   b" + newline + "c"));

            SKRectI narrow = Ink(collapsed.Bitmap);
            SKRectI wide = Ink(preserved.Bitmap);
            Assert.True(wide.Width > narrow.Width, $"pre {wide.Width} should be wider than pre-line {narrow.Width}");
            Assert.Equal(narrow.Top, wide.Top);
            Assert.Equal(narrow.Bottom, wide.Bottom);
        }

        [Fact]
        public void DefaultLimits_StillRefuseTextAreas()
        {
            using var result = new FenSvgRenderer().Render(Text(" style='inline-size:80px'"));

            Assert.False(result.Success);
            Assert.Contains("css-cascade", result.FallbackReasonCodes);
        }

        [Fact]
        public void VerticalWritingMode_StaysRefused()
        {
            // Chromium lays vertical text out vertically; one horizontal line is not its rendering.
            var limits = SvgRenderLimits.Default;
            limits.LayOutTextAreasOnOneLine = true;

            using var result = new FenSvgRenderer().Render(Text(" style='writing-mode:vertical-rl'"), limits);

            Assert.False(result.Success);
        }

        [Fact]
        public void ImageLoader_RendersATextAreaDocument()
        {
            ImageLoader.ClearCache();
            try
            {
                using SKBitmap? bitmap = ImageLoader.GetInlineSvgImage(Text(" style='inline-size:80px'"), 400, 40);

                Assert.NotNull(bitmap);
                Assert.False(Ink(bitmap!).IsEmpty);
            }
            finally
            {
                ImageLoader.ClearCache();
            }
        }

        private static string Text(string attributes, string content = Words) =>
            "<svg xmlns='http://www.w3.org/2000/svg' width='400' height='40'>" +
            $"<text x='10' y='24' font-size='16'{attributes}>{content}</text></svg>";

        private static SvgRenderResult RenderSingleLine(string svg)
        {
            var limits = SvgRenderLimits.Default;
            limits.LayOutTextAreasOnOneLine = true;
            var result = new FenSvgRenderer().Render(svg, limits);
            Assert.True(result.Success, result.ErrorMessage);
            return result;
        }

        private static bool SameImage(SKBitmap a, SKBitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
            return true;
        }

        private static SKRectI Ink(SKBitmap bitmap)
        {
            int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < 64) continue;
                left = System.Math.Min(left, x);
                top = System.Math.Min(top, y);
                right = System.Math.Max(right, x + 1);
                bottom = System.Math.Max(bottom, y + 1);
            }
            return left == int.MaxValue ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
        }
    }
}
