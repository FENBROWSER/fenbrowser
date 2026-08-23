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
