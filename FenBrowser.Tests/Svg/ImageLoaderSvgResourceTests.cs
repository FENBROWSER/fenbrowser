using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.Tests.Svg;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageLoaderSvgResourceCollection
{
    public const string Name = "ImageLoader SVG resources";
}

[Collection(ImageLoaderSvgResourceCollection.Name)]
public sealed class ImageLoaderSvgResourceTests
{
    [Fact]
    public async Task Prewarm_AsynchronouslySnapshotsSameOriginNestedImages()
    {
        const string outerUrl = "https://example.test/assets/outer.svg";
        var nestedUri = new Uri("https://example.test/assets/red.png");
        byte[] png = Png(SKColors.Red);
        int fetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri =>
            {
                Interlocked.Increment(ref fetches);
                Assert.Equal(nestedUri, uri);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = png,
                    ContentType = "image/png",
                    FinalUri = uri,
                    FailureReason = BinaryFetchFailureReason.None
                });
            }
        };
        var originalBackend = SvgRendererConfiguration.Backend;
        ImageLoader.ClearCache();
        try
        {
            SvgRendererConfiguration.Backend = SvgRendererBackend.FirstParty;
            using var scope = ImageLoader.EnterRequestContext(context);
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>"));

            Assert.True(await ImageLoader.PrewarmImageAsync(outerUrl, stream));
            SKBitmap? bitmap = ImageLoader.GetImage(outerUrl);

            Assert.NotNull(bitmap);
            Assert.Equal(1, fetches);
            Assert.True(bitmap.GetPixel(2, 2).Red > 200);
        }
        finally
        {
            SvgRendererConfiguration.Backend = originalBackend;
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task Prewarm_DoesNotAuthorizeCrossOriginRedirectBytes()
    {
        const string outerUrl = "https://example.test/assets/redirect.svg";
        byte[] png = Png(SKColors.Red);
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                Body = png,
                ContentType = "image/png",
                FinalUri = new Uri("https://other.test/red.png"),
                FailureReason = BinaryFetchFailureReason.None
            })
        };
        var originalBackend = SvgRendererConfiguration.Backend;
        ImageLoader.ClearCache();
        try
        {
            SvgRendererConfiguration.Backend = SvgRendererBackend.FirstParty;
            using var scope = ImageLoader.EnterRequestContext(context);
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>"));

            Assert.True(await ImageLoader.PrewarmImageAsync(outerUrl, stream));
            SKBitmap? bitmap = ImageLoader.GetImage(outerUrl);

            Assert.NotNull(bitmap);
            Assert.Equal(0, bitmap.GetPixel(2, 2).Alpha);
        }
        finally
        {
            SvgRendererConfiguration.Backend = originalBackend;
            ImageLoader.ClearCache();
        }
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
