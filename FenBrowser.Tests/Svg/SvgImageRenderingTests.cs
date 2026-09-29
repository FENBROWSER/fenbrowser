using System;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// image-rendering (CSS Images 3 §7.1): pixelated, crisp-edges and the SVG 1.1
    /// optimizeSpeed scale raster images with nearest-neighbour sampling, and the
    /// property is inherited. They used to refuse the whole document.
    /// </summary>
    public sealed class SvgImageRenderingTests
    {
        [Theory]
        [InlineData("pixelated")]
        [InlineData("crisp-edges")]
        [InlineData("optimizeSpeed")]
        public void PixelArtValues_ScaleWithoutBlending(string value)
        {
            using var result = Render($"style='image-rendering: {value}'");

            // Just left of the middle of the upper row: pure red, no blend with blue.
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(9, 5));
        }

        private static SvgRenderResult Render(string groupStyle)
        {
            var result = new FenSvgRenderer().Render(
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" +
                $"<g {groupStyle}><image width='20' height='20' preserveAspectRatio='none' href='{Checkerboard()}'/></g></svg>");
            Assert.True(SvgRenderResult.IsAdmissible(result), result.ErrorMessage);
            return result;
        }

        private static string Checkerboard()
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.SetPixel(0, 0, SKColors.Red);
            bitmap.SetPixel(1, 0, SKColors.Blue);
            bitmap.SetPixel(0, 1, SKColors.Blue);
            bitmap.SetPixel(1, 1, SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }
    }
}
