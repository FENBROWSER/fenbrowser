using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class ImageLoaderCacheTelemetryTests
    {
        [Fact]
        public async Task GetCacheSnapshot_TracksHitsMissesAndCountWithoutLegacyDoubleCounting()
        {
            const string imageUrl = "https://example.test/cache-telemetry.png";

            ImageLoader.ClearCache();

            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());

            var prewarmed = await ImageLoader.PrewarmImageAsync(imageUrl, stream);
            var first = ImageLoader.GetImage(imageUrl);
            var second = ImageLoader.GetImage(imageUrl);
            var snapshot = ImageLoader.GetCacheSnapshot();

            Assert.True(prewarmed);
            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(1, snapshot.StaticImageCount);
            Assert.Equal(0, snapshot.AnimatedImageCount);
            Assert.True(snapshot.HitCount >= 2);
            Assert.Equal(0, snapshot.MissCount);

            ImageLoader.ClearCache();
        }
    }
}
