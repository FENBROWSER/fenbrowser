using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgBlendModeTests
    {
        [Fact]
        public void MultiplyBlend_CompositesAgainstBackdrop()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><rect width='20' height='20' fill='red'/>" +
                "<rect width='20' height='20' fill='blue' style='mix-blend-mode:multiply'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            SKColor pixel = result.Bitmap.GetPixel(10, 10);
            Assert.True(pixel.Red < 10 && pixel.Green < 10 && pixel.Blue < 10);
        }

        [Fact]
        public void IsolationAndBlend_ObeyLayerDepthBudget()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><g style='isolation:isolate;mix-blend-mode:screen'>" +
                "<rect width='20' height='20'/></g></svg>",
                new SvgRenderLimits { MaxActiveLayers = 1 });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
        }

        [Fact]
        public void MetadataAndEmbeddingOnlyCss_DoNotChangeSvgPixelsOrRequireFallback()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' style='vertical-align:text-bottom;border:none'>" +
                "<title>Accessible title</title><desc>Description</desc><metadata>opaque</metadata>" +
                "<rect width='20' height='20' fill='red'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void UnknownBlendMode_FailsClosedAsExplicitUnsupported()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20'><rect width='20' height='20' style='mix-blend-mode:plus-lighter'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains(result.Warnings, warning => warning.Contains("mix-blend-mode"));
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
