using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class ImageLoaderCssFunctionTests
{
    [Fact]
    public void GetImage_DecodesUnpaddedBase64DataUri()
    {
        const string unpaddedPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK6QAAAAASUVORK5CYII";
        ImageLoader.ClearCache();

        try
        {
            var bitmap = ImageLoader.GetImage("data:image/png;base64," + unpaddedPng);

            Assert.NotNull(bitmap);
            Assert.Equal(1, bitmap.Width);
            Assert.Equal(1, bitmap.Height);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task GetImage_IgnoresCssImageFunctionsWithoutStartingAsyncLoad()
    {
        ImageLoader.ClearCache();
        var originalFetch = ImageLoader.FetchBytesAsync;
        var originalRepaint = ImageLoader.RequestRepaint;
        var originalRelayout = ImageLoader.RequestRelayout;
        var fetchCount = 0;
        var repaintCount = 0;
        var relayoutCount = 0;

        try
        {
            ImageLoader.FetchBytesAsync = _ =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult<byte[]>(null);
            };
            ImageLoader.RequestRepaint = () => Interlocked.Increment(ref repaintCount);
            ImageLoader.RequestRelayout = () => Interlocked.Increment(ref relayoutCount);

            var bitmap = ImageLoader.GetImage("conic-gradient(from 0deg, transparent 0, black 100%)");
            await Task.Delay(50);
            var snapshot = ImageLoader.GetCacheSnapshot();

            Assert.Null(bitmap);
            Assert.Equal(0, snapshot.PendingLoadCount);
            Assert.Equal(0, snapshot.MissCount);
            Assert.Equal(0, Volatile.Read(ref fetchCount));
            Assert.Equal(0, Volatile.Read(ref repaintCount));
            Assert.Equal(0, Volatile.Read(ref relayoutCount));
        }
        finally
        {
            ImageLoader.FetchBytesAsync = originalFetch;
            ImageLoader.RequestRepaint = originalRepaint;
            ImageLoader.RequestRelayout = originalRelayout;
            ImageLoader.ClearCache();
        }
    }
}
