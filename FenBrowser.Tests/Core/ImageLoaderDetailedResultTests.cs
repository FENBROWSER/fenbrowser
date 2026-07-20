using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

[Collection("Synthetic CAPTCHA")]
public sealed class ImageLoaderDetailedResultTests
{
    [Fact]
    public async Task DocumentFetcher_DoesNotReuseTopLevelCacheAndPreservesStructuredBlock()
    {
        const string imageUrl = "https://frame.example.test/challenge.png";
        var document = new HtmlParser(
            "<!doctype html><html><body></body></html>",
            new Uri("https://frame.example.test/widget/")).Parse();
        var genericFetchCount = 0;
        var documentFetchCount = 0;
        ImageLoader.ClearCache();
        try
        {
            var context = new ImageLoader.ImageLoaderRequestContext
            {
                OwnerId = "frame-context-test",
                FetchDetailedAsync = uri =>
                {
                    genericFetchCount++;
                    return Task.FromResult(new BinaryFetchResult { FinalUri = uri });
                },
                FetchDetailedForDocumentAsync = (uri, ownerDocument) =>
                {
                    documentFetchCount++;
                    Assert.Same(document, ownerDocument);
                    return Task.FromResult(new BinaryFetchResult
                    {
                        FinalUri = uri,
                        FailureReason = BinaryFetchFailureReason.CspBlocked,
                        FailureDetail = "Blocked by img-src",
                        CspAllowed = false
                    });
                }
            };

            using (ImageLoader.EnterRequestContext(context))
            {
                var png = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                using var stream = new MemoryStream(png, writable: false);
                Assert.True(await ImageLoader.PrewarmImageAsync(imageUrl, stream));
                Assert.NotNull(ImageLoader.GetImage(imageUrl));
                Assert.Null(ImageLoader.GetImage(imageUrl, ownerDocument: document));
            }
            await WaitForLoadAsync(imageUrl);

            Assert.Equal(0, genericFetchCount);
            Assert.Equal(1, documentFetchCount);
            Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out var result));
            Assert.Equal(BinaryFetchFailureReason.CspBlocked, result.FailureReason);
            Assert.False(result.CspAllowed);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task DetailedFetcher_PreservesStructuredHttpFailure()
    {
        const string imageUrl = "https://example.test/missing.png";
        var previousDetailedFetcher = ImageLoader.FetchDetailedAsync;
        var previousByteFetcher = ImageLoader.FetchBytesAsync;
        ImageLoader.ClearCache();
        try
        {
            ImageLoader.FetchBytesAsync = null;
            ImageLoader.FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                FinalUri = uri,
                StatusCode = (int)HttpStatusCode.NotFound,
                FailureReason = BinaryFetchFailureReason.HttpError,
                FailureDetail = "HTTP 404"
            });

            Assert.Null(ImageLoader.GetImage(imageUrl));
            await WaitForLoadAsync(imageUrl);

            Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out var result));
            Assert.Equal(BinaryFetchFailureReason.HttpError, result.FailureReason);
            Assert.Equal(404, result.StatusCode);
            Assert.False(result.Succeeded);
        }
        finally
        {
            ImageLoader.FetchDetailedAsync = previousDetailedFetcher;
            ImageLoader.FetchBytesAsync = previousByteFetcher;
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task DetailedFetcher_PreservesDecodeFailureSeparatelyFromNetworkSuccess()
    {
        const string imageUrl = "https://example.test/not-an-image.png";
        var previousDetailedFetcher = ImageLoader.FetchDetailedAsync;
        var previousByteFetcher = ImageLoader.FetchBytesAsync;
        ImageLoader.ClearCache();
        try
        {
            ImageLoader.FetchBytesAsync = null;
            ImageLoader.FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                Body = new byte[] { 1, 2, 3, 4 },
                FinalUri = uri,
                StatusCode = (int)HttpStatusCode.OK,
                ContentType = "image/png"
            });

            Assert.Null(ImageLoader.GetImage(imageUrl));
            await WaitForLoadAsync(imageUrl);

            Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out var result));
            Assert.Equal(BinaryFetchFailureReason.None, result.FailureReason);
            Assert.Equal("image/png", result.DecodeFormat);
            Assert.False(string.IsNullOrWhiteSpace(result.DecodeFailureReason));
            Assert.False(ImageLoader.ContainsCachedImage(imageUrl));
        }
        finally
        {
            ImageLoader.FetchDetailedAsync = previousDetailedFetcher;
            ImageLoader.FetchBytesAsync = previousByteFetcher;
            ImageLoader.ClearCache();
        }
    }

    private static async Task WaitForLoadAsync(string imageUrl)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (ImageLoader.PendingLoadCount == 0 &&
                ImageLoader.TryGetLastLoadResult(imageUrl, out _))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Image load did not complete for {imageUrl}");
    }
}
