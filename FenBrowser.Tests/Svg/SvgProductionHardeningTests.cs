using System;
using System.Buffers.Binary;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgProductionHardeningTests
    {
        [Fact]
        public void EmbeddedRaster_HeaderBudgetRejectsBeforePixelAllocation()
        {
            var encoded = BuildPngHeader(width: 50_000, height: 50_000);

            var decoded = SvgRenderEngine.TryDecodeEmbeddedBitmap(
                encoded,
                maxPixels: 16L * 1024 * 1024,
                maxDimension: 8192,
                out var bitmap,
                out var error);

            Assert.False(decoded);
            Assert.Null(bitmap);
            Assert.Equal("image decoded size exceeds raster budget; rejected", error);
        }

        [Fact]
        public void EmbeddedRaster_ValidPayloadDecodesWithinBudget()
        {
            using var source = new SKBitmap(2, 3);
            source.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(source);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var decoded = SvgRenderEngine.TryDecodeEmbeddedBitmap(
                data.ToArray(),
                maxPixels: 64,
                maxDimension: 8,
                out var bitmap,
                out var error);

            Assert.True(decoded, error);
            using (bitmap)
            {
                Assert.Equal(2, bitmap.Width);
                Assert.Equal(3, bitmap.Height);
                Assert.Equal(SKColors.CornflowerBlue, bitmap.GetPixel(1, 1));
            }
        }

        [Fact]
        public void RenderResult_DetachTransfersBitmapOwnership()
        {
            using var result = new SvgRenderResult { Bitmap = new SKBitmap(1, 1) };
            using var detached = result.DetachBitmap();

            Assert.NotNull(detached);
            Assert.Null(result.Bitmap);
        }

        [Fact]
        public void RenderResult_DisposeIsIdempotent()
        {
            using var recorder = new SKPictureRecorder();
            recorder.BeginRecording(new SKRect(0, 0, 1, 1));
            var result = new SvgRenderResult
            {
                Bitmap = new SKBitmap(1, 1),
                Picture = recorder.EndRecording()
            };

            result.Dispose();
            result.Dispose();

            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
        }

        [Fact]
        public void RenderLimits_NormalizeClampsUntrustedCallerValuesToHardCaps()
        {
            var limits = SvgRenderLimits.Normalize(new SvgRenderLimits
            {
                MaxRecursionDepth = int.MaxValue,
                MaxFilterCount = int.MaxValue,
                MaxRenderTimeMs = int.MaxValue,
                MaxElementCount = int.MaxValue,
                MaxSourceChars = int.MaxValue,
                MaxRasterWidth = int.MaxValue,
                MaxRasterHeight = int.MaxValue,
                MaxRasterPixels = long.MaxValue,
                MaxDecodedImagePixels = long.MaxValue,
                MaxDecodedImageBytes = int.MaxValue,
                MaxActiveLayers = int.MaxValue,
                MaxReferenceDepth = int.MaxValue,
                AllowExternalReferences = true
            });

            Assert.Equal(512, limits.MaxRecursionDepth);
            Assert.Equal(1_000, limits.MaxFilterCount);
            Assert.Equal(30_000, limits.MaxRenderTimeMs);
            Assert.Equal(250_000, limits.MaxElementCount);
            Assert.Equal(32 * 1024 * 1024, limits.MaxSourceChars);
            Assert.Equal(32_768, limits.MaxRasterWidth);
            Assert.Equal(32_768, limits.MaxRasterHeight);
            Assert.Equal(64L * 1024 * 1024, limits.MaxRasterPixels);
            Assert.Equal(64L * 1024 * 1024, limits.MaxDecodedImagePixels);
            Assert.Equal(32 * 1024 * 1024, limits.MaxDecodedImageBytes);
            Assert.Equal(16, limits.MaxActiveLayers);
            Assert.Equal(64, limits.MaxReferenceDepth);
            Assert.True(limits.AllowExternalReferences);
        }

        [Theory]
        [InlineData("hybrid", SvgRendererBackend.FirstPartyWithLegacyFallback)]
        [InlineData("AUTO", SvgRendererBackend.FirstPartyWithLegacyFallback)]
        [InlineData("first-party", SvgRendererBackend.FirstParty)]
        [InlineData("fen", SvgRendererBackend.FirstParty)]
        [InlineData("legacy", SvgRendererBackend.LegacySvgSkia)]
        [InlineData("svg-skia", SvgRendererBackend.LegacySvgSkia)]
        public void BackendConfiguration_ParsesCrossPlatformValues(
            string value,
            SvgRendererBackend expected)
        {
            Assert.True(SvgRendererConfiguration.TryParse(value, out var parsed));
            Assert.Equal(expected, parsed);
        }

        [Fact]
        public void FirstParty_UnsupportedTextSignalsFallbackRequirement()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='40' height='20'><text x='2' y='15'>Fen</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.False(result.UsedLegacyFallback);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        [Fact]
        public void Hybrid_UnsupportedTextUsesLegacyCompatibilityRenderer()
        {
            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());
            using var result = renderer.Render(
                "<svg width='80' height='30'><text x='2' y='22' font-size='20'>Fen</text></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.UsedLegacyFallback);
            Assert.Equal(SvgRendererBackend.LegacySvgSkia, result.Backend);
            Assert.Contains(result.Warnings, warning => warning.Contains("text", StringComparison.Ordinal));
        }

        [Fact]
        public void Hybrid_SupportedGeometryStaysOnFirstPartyRenderer()
        {
            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());
            using var result = renderer.Render(
                "<svg width='10' height='10'><rect width='10' height='10' fill='red'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback);
            Assert.False(result.UsedLegacyFallback);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        }

        [Fact]
        public void Hybrid_SecurityFailureNeverFallsBack()
        {
            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());
            using var result = renderer.Render(
                "<!DOCTYPE svg><svg width='10' height='10'><rect width='10' height='10'/></svg>");

            Assert.False(result.Success);
            Assert.False(result.UsedLegacyFallback);
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.Contains("DOCTYPE", result.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void Hybrid_ExternalImageCompatibilityPathRemainsNetworkIsolated()
        {
            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());
            using var result = renderer.Render(
                "<svg width='10' height='10'><image href='https://invalid.example/image.png' " +
                "width='10' height='10'/></svg>",
                SvgRenderLimits.Strict);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.UsedLegacyFallback);
            Assert.Equal(SvgRendererBackend.LegacySvgSkia, result.Backend);
            Assert.Equal(0, result.Bitmap.GetPixel(5, 5).Alpha);
        }

        [Fact]
        public void Hybrid_LayerBudgetFidelityLossUsesCompatibilityRenderer()
        {
            var svg = new System.Text.StringBuilder("<svg width='20' height='20'>");
            for (int i = 0; i < 10; i++) svg.Append("<g opacity='.9'>");
            svg.Append("<rect width='20' height='20' fill='red'/>");
            for (int i = 0; i < 10; i++) svg.Append("</g>");
            svg.Append("</svg>");

            var renderer = new HybridSvgRenderer(new FenSvgRenderer(), new SvgSkiaRenderer());
            using var result = renderer.Render(svg.ToString());

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.UsedLegacyFallback);
            Assert.Equal(SvgRendererBackend.LegacySvgSkia, result.Backend);
        }

        [Fact]
        public void SharedHybridRenderer_IsSafeUnderConcurrentMixedWorkload()
        {
            var renderer = SvgRendererFactory.GetRenderer(
                SvgRendererBackend.FirstPartyWithLegacyFallback);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

            System.Threading.Tasks.Parallel.For(0, 64, index =>
            {
                string svg = index % 2 == 0
                    ? "<svg width='8' height='8'><rect width='8' height='8' fill='lime'/></svg>"
                    : "<svg width='40' height='16'><text x='1' y='12'>Fen</text></svg>";
                using var result = renderer.Render(svg);
                if (!result.Success || result.Bitmap == null || result.Bitmap.Width <= 0)
                {
                    failures.Enqueue(result.ErrorMessage ?? "missing bitmap");
                }
            });

            Assert.Empty(failures);
        }

        [Theory]
        [InlineData("<style>rect{filter:blur(2px)}</style><rect width='10' height='10'/>")]
        [InlineData("<defs><pattern id='p'/></defs><rect width='10' height='10' fill='url(#p)'/>")]
        [InlineData("<rect width='10' height='10' style='mask:url(#m)'/>")]
        [InlineData("<path d='M0 0L10 10' marker-end='url(#m)'/>")]
        public void FirstParty_CompatibilityFeaturesAreNeverSilentlyDropped(string content)
        {
            using var result = new FenSvgRenderer().Render($"<svg width='10' height='10'>{content}</svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.RequiresFallback);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ObjectBoundingBoxClip_MapsUnitGeometryToShapeBounds()
        {
            const string svg =
                "<svg width='40' height='20'><defs>" +
                "<clipPath id='c' clipPathUnits='objectBoundingBox'>" +
                "<rect x='0' y='0' width='.5' height='1'/></clipPath></defs>" +
                "<rect x='10' y='5' width='20' height='10' fill='red' clip-path='url(#c)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(14, 10).Alpha > 0);
            Assert.Equal(0, result.Bitmap.GetPixel(26, 10).Alpha);
        }

        [Fact]
        public void ClipPath_ChildTransformIsApplied()
        {
            const string svg =
                "<svg width='30' height='15'><defs><clipPath id='c'>" +
                "<rect width='8' height='10' transform='translate(10 0)'/>" +
                "</clipPath></defs><rect width='30' height='15' fill='red' clip-path='url(#c)'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(4, 5).Alpha);
            Assert.True(result.Bitmap.GetPixel(12, 5).Alpha > 0);
        }

        [Fact]
        public void EmbeddedImage_DefaultAspectRatioMeetsAndCenters()
        {
            string imageUri = MakeSolidPngDataUri(4, 2, SKColors.Red);
            string svg = $"<svg width='20' height='20'><image href='{imageUri}' width='20' height='20'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(0, result.Bitmap.GetPixel(10, 2).Alpha);
            Assert.True(result.Bitmap.GetPixel(10, 10).Alpha > 0);
        }

        [Fact]
        public void EmbeddedImage_AspectRatioNoneStretchesToViewport()
        {
            string imageUri = MakeSolidPngDataUri(4, 2, SKColors.Red);
            string svg = $"<svg width='20' height='20'><image href='{imageUri}' width='20' height='20' preserveAspectRatio='none'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(10, 2).Alpha > 0);
            Assert.True(result.Bitmap.GetPixel(10, 18).Alpha > 0);
        }

        [Fact]
        public void MoveCommand_AdditionalPairsRenderAsLineSegments()
        {
            const string svg =
                "<svg width='20' height='20'><path d='M2 2 18 18' stroke='red' stroke-width='2' fill='none'/></svg>";

            using var result = new FenSvgRenderer().Render(svg);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Bitmap.GetPixel(10, 10).Alpha > 0);
        }

        private static string MakeSolidPngDataUri(int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }

        private static byte[] BuildPngHeader(int width, int height)
        {
            // Start with a valid PNG so the codec accepts the container, then
            // change only IHDR dimensions and its CRC. Pixel allocation must be
            // rejected from this metadata before the intentionally mismatched
            // compressed payload is ever decoded.
            using var bitmap = new SKBitmap(1, 1);
            bitmap.Erase(SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
            var crc = Crc32(png.AsSpan(12, 17));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), crc);
            return png;
        }

        private static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = unchecked((uint)-(int)(crc & 1));
                    crc = (crc >> 1) ^ (0xEDB88320u & mask);
                }
            }
            return ~crc;
        }
    }
}
