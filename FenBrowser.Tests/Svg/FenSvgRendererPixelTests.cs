using System;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>Core paint/geometry semantics of the first-party renderer.</summary>
    public class FenSvgRendererPixelTests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        [Fact]
        public void RedRect_FillsExpectedPixels()
        {
            using var result = _renderer.Render(
                "<svg width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"red\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(16, 16));
        }

        [Fact]
        public void DefaultFill_IsBlack_PerSpec()
        {
            // No fill attribute anywhere: rect must render black. No source
            // rewrite is needed; the default fill comes from the cascade.
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\"><rect width=\"8\" height=\"8\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(4, 4);
            Assert.Equal((byte)255, px.Alpha);
            Assert.Equal((byte)0, px.Red);
        }

        [Fact]
        public void FillNone_RendersNothing()
        {
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"none\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void RootFillAttribute_InheritsToChildren()
        {
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\" fill=\"blue\"><rect width=\"6\" height=\"6\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(3, 3);
            Assert.Equal((byte)255, px.Blue);
            Assert.Equal((byte)0, px.Red);
        }

        [Fact]
        public void Circle_CenterFilled_EdgeOutside()
        {
            using var result = _renderer.Render(
                "<svg width=\"20\" height=\"20\"><circle cx=\"10\" cy=\"10\" r=\"6\" fill=\"green\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(SKColors.Green, result.Bitmap.GetPixel(10, 10));
            Assert.Equal(0, result.Bitmap.GetPixel(0, 0).Alpha);
        }

        [Fact]
        public void GroupOpacity_CompositesChildren()
        {
            using var result = _renderer.Render(
                "<svg width=\"10\" height=\"10\">" +
                "<g opacity=\"0.5\"><rect width=\"10\" height=\"10\" fill=\"red\"/></g></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(5, 5);
            // GetPixel returns UN-premultiplied color: red channel stays 255,
            // the alpha channel carries the composite.
            Assert.InRange(px.Alpha, 100, 155);
            Assert.Equal((byte)255, px.Red);
        }

        private static bool BitmapHasVisiblePixels(SKBitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
