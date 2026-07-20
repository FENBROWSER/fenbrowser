using System;
using System.Net;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

[Collection("Synthetic CAPTCHA")]
public sealed class ImageLoaderDetailedResultTests
{
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
