using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

[Collection("Synthetic CAPTCHA")]
public sealed class ImageDataUriPrewarmTests
{
    [Fact]
    public async Task PrewarmImages_DecodesDataUriWithoutNetworkFetch()
    {
        const string dataUri =
            "data:image/gif;base64,R0lGODlhAQABAID/AMDAwAAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==";
        ImageLoader.ClearCache();
        var previousByteFetcher = ImageLoader.FetchBytesAsync;
        try
        {
            var document = new HtmlParser(
                $"<!doctype html><html><body><img src='{dataUri}'></body></html>",
                new Uri("https://example.test/page")).Parse();
            var byteFetches = 0;
            var streamFetches = 0;
            ImageLoader.FetchBytesAsync = _ =>
            {
                Interlocked.Increment(ref byteFetches);
                return Task.FromResult<byte[]>(null);
            };
            Func<Uri, Task<Stream>> loader = _ =>
            {
                Interlocked.Increment(ref streamFetches);
                return Task.FromResult<Stream>(null);
            };

            await InvokePrewarmImagesAsync(
                document.DocumentElement,
                new Uri("https://example.test/page"),
                loader,
                1280d);

            Assert.True(ImageLoader.ContainsCachedImage(dataUri));
            Assert.Equal(0, byteFetches);
            Assert.Equal(0, streamFetches);
        }
        finally
        {
            ImageLoader.FetchBytesAsync = previousByteFetcher;
            ImageLoader.ClearCache();
        }
    }

    private static Task InvokePrewarmImagesAsync(
        Element root,
        Uri baseUri,
        Func<Uri, Task<Stream>> loader,
        double viewportWidth)
    {
        var method = typeof(CustomHtmlEngine).GetMethod(
            "PrewarmImagesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(
            null,
            new object[] { root, baseUri, loader, viewportWidth }));
    }
}
