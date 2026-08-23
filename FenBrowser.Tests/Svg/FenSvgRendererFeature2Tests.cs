using System;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>Feature coverage added in the hardening pass.</summary>
    public class FenSvgRendererFeature2Tests
    {
        private readonly FenSvgRenderer _renderer = new FenSvgRenderer();

        // ------------------------------------------------------------ inline style

        [Fact]
        public void InlineStyle_ShadowsPresentationAttribute()
        {
            var result = _renderer.Render(
                "<svg width='10' height='10'>" +
                "<rect width='10' height='10' fill='red' style='fill:blue'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(5, 5);
            Assert.Equal((byte)255, px.Blue);
            Assert.Equal((byte)0, px.Red);
        }

        [Fact]
        public void InlineStyle_Alone_Applies()
        {
            // CSS "green" is #008000; use lime (#00FF00) for a full-channel check.
            var result = _renderer.Render(
                "<svg width='10' height='10'><rect width='10' height='10' style=\"fill:lime\"/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(5, 5);
            Assert.Equal((byte)255, px.Green);
            Assert.Equal((byte)0, px.Red);
        }

        [Fact]
        public void InlineStyle_MalformedDeclarations_Skipped()
        {
            var result = _renderer.Render(
                "<svg width='10' height='10'>" +
                "<rect width='10' height='10' style=';;;fill : red ; bad; : x ;'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(5, 5).Red);
        }

        // ------------------------------------------------------------- clipPath

        [Fact]
        public void ClipPath_UserSpace_RestrictsPaint()
        {
            var result = _renderer.Render(
                "<svg width='20' height='10'>" +
                "<clipPath id='c'><rect x='0' y='0' width='10' height='10'/></clipPath>" +
                "<rect x='0' y='0' width='20' height='10' fill='red' clip-path='url(#c)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(5, 5).Red);   // clipped-in
            Assert.Equal(0, result.Bitmap.GetPixel(15, 5).Alpha);         // clipped-out
        }

        [Fact]
        public void ClipPath_Cycle_Terminates()
        {
            var result = _renderer.Render(
                "<svg width='10' height='10'>" +
                "<clipPath id='a'><use href='#a'/></clipPath>" +
                "<rect width='10' height='10' fill='red' clip-path='url(#a)'/></svg>",
                new SvgRenderLimits { MaxRenderTimeMs = 2000 });

            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void ClipPath_MissingReference_DoesNotHideElement()
        {
            var result = _renderer.Render(
                "<svg width='10' height='10'><rect width='10' height='10' fill='red' clip-path='url(#nope)'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(5, 5).Red);
        }

        // ---------------------------------------------------------------- images

        private static string RedPngDataUri(int w, int h)
        {
            using var bmp = new SKBitmap(w, h);
            bmp.Erase(SKColors.Red);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }

        [Fact]
        public void Image_DataUri_DecodesAndPlaces()
        {
            var result = _renderer.Render(
                $"<svg width='20' height='10'><image href='{RedPngDataUri(4, 4)}' x='3' y='3' width='6' height='4'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            var px = result.Bitmap.GetPixel(6, 5);
            Assert.True(px.Red > 200 && px.Alpha > 200, $"expected red image pixel, got {px}");
            Assert.Equal(0, result.Bitmap.GetPixel(0, 0).Alpha);
        }

        [Fact]
        public void Image_ExternalReference_IsIgnored_FailClosed()
        {
            var result = _renderer.Render(
                "<svg width='10' height='10'><image href='http://evil.example/x.png' width='10' height='10'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void Image_InvalidBase64_IsRejectedGracefully()
        {
            var result = _renderer.Render(
                "<svg width='10' height='10'><image href='data:image/png;base64,!!!!not-base64!!!!' width='10' height='10'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void Image_DecodedRasterBomb_RejectedByBudget()
        {
            // MaxDecodedImagePixels is independent of the document raster
            // budget: the 4x4 image is dropped while the 10x10 doc renders.
            var result = _renderer.Render(
                "<svg width='10' height='10'>" +
                $"<image href='{RedPngDataUri(4, 4)}' x='1' y='1' width='6' height='6'/>" +
                "</svg>",
                new SvgRenderLimits
                {
                    MaxRenderTimeMs = 500,
                    MaxDecodedImagePixels = 1
                });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(3, 3).Alpha); // image dropped
        }

        // -------------------------------------------------- security regressions

        [Fact]
        public void LowercaseDoctype_IsRejected()
        {
            var result = _renderer.Render(
                "<!doctype svg SYSTEM \"x\"><svg width='1' height='1'/>");
            Assert.False(result.Success);
            Assert.Contains("DOCTYPE", result.ErrorMessage);
        }

        [Fact]
        public void NestedOpacityGroups_PastLayerCap_StillSucceed()
        {
            var sb = new System.Text.StringBuilder("<svg width='8' height='8'>");
            for (int i = 0; i < 30; i++) sb.Append("<g opacity='0.5'>");
            sb.Append("<rect width='8' height='8' fill='red'/>");
            for (int i = 0; i < 30; i++) sb.Append("</g>");
            sb.Append("</svg>");

            // Deep nesting must terminate quickly and safely; compositing math
            // legitimately drives alpha toward zero (0.5^N), and the layer cap
            // bounds memory regardless of nesting depth.
            var result = _renderer.Render(sb.ToString(), new SvgRenderLimits { MaxRenderTimeMs = 1500 });
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void LayerCap_Boundary_StillCompositesVisibly()
        {
            var sb = new System.Text.StringBuilder("<svg width='8' height='8'>");
            for (int i = 0; i < 4; i++) sb.Append("<g opacity='0.75'>");
            sb.Append("<rect width='8' height='8' fill='red'/>");
            for (int i = 0; i < 4; i++) sb.Append("</g>");
            sb.Append("</svg>");

            var result = _renderer.Render(sb.ToString());
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(4, 4).Alpha >= 60,
                $"expected visible composite, got {result.Bitmap.GetPixel(4, 4)}");
        }

        [Fact]
        public void DeepDistinctUseChain_TerminatesUnderBudget()
        {
            var sb = new System.Text.StringBuilder("<svg width='10' height='10'>");
            for (int i = 0; i < 40; i++)
            {
                string inner = i < 39 ? $"<use href='#s{i + 1}'/>" : "<rect width='2' height='2' fill='red'/>";
                sb.Append($"<symbol id='s{i}'><g>{inner}</g></symbol>");
            }
            sb.Append("<use href='#s0'/></svg>");

            var result = _renderer.Render(sb.ToString(), new SvgRenderLimits { MaxRenderTimeMs = 1500 });
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void OversizedIdValue_IsIgnoredSafely()
        {
            var bigId = new string('a', 600);
            var result = _renderer.Render(
                $"<svg width='10' height='10'><rect id='{bigId}' width='10' height='10' fill='red'/></svg>");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(5, 5).Red);
        }
    }
}
