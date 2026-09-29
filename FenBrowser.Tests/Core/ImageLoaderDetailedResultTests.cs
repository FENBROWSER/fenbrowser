using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
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

    [Fact]
    public async Task ScopedFetchers_PreserveIndependentResultsForSameUrl()
    {
        const string imageUrl = "https://assets.example.test/challenge.png";
        ImageLoader.ClearCache();
        try
        {
            var blockedContext = new ImageLoader.ImageLoaderRequestContext
            {
                OwnerId = "blocked-frame",
                FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
                {
                    FinalUri = uri,
                    FailureReason = BinaryFetchFailureReason.CspBlocked,
                    FailureDetail = "Blocked by img-src"
                })
            };
            var missingContext = new ImageLoader.ImageLoaderRequestContext
            {
                OwnerId = "missing-frame",
                FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
                {
                    FinalUri = uri,
                    StatusCode = (int)HttpStatusCode.NotFound,
                    FailureReason = BinaryFetchFailureReason.HttpError,
                    FailureDetail = "HTTP 404"
                })
            };

            using (ImageLoader.EnterRequestContext(blockedContext))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl));
            }
            using (ImageLoader.EnterRequestContext(missingContext))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl));
            }

            await WaitForLoadAsync(imageUrl);

            using (ImageLoader.EnterRequestContext(blockedContext))
            {
                Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out var blocked));
                Assert.Equal(BinaryFetchFailureReason.CspBlocked, blocked.FailureReason);
            }
            using (ImageLoader.EnterRequestContext(missingContext))
            {
                Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out var missing));
                Assert.Equal(BinaryFetchFailureReason.HttpError, missing.FailureReason);
                Assert.Equal(404, missing.StatusCode);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task GetImage_RegistersOwnerAndAdvancesGeneration()
    {
        const string imageUrl = "https://example.test/generation.png";
        var document = new Document("https://owner-alias.test/page");
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "generation-owner",
            FetchDetailedForDocumentAsync = (uri, ownerDocument) =>
            {
                Assert.Same(document, ownerDocument);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
                    ContentType = "image/png",
                    FinalUri = uri
                });
            }
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(
                    imageUrl,
                    targetWidth: 8,
                    targetHeight: 8,
                    ownerDocument: document));
            }

            await WaitForLoadAsync(imageUrl);
            Assert.True(ImageLoader.GetCacheGeneration("generation-owner") > 0);
            Assert.True(ImageLoader.GetCacheGeneration("https://owner-alias.test/page") > 0);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task DetachedLoads_AreBoundedByLoadSemaphore()
    {
        var activeGate = new object();
        int activeFetches = 0;
        int maxActiveFetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "semaphore-owner",
            FetchDetailedAsync = async uri =>
            {
                int active = Interlocked.Increment(ref activeFetches);
                lock (activeGate)
                {
                    maxActiveFetches = Math.Max(maxActiveFetches, active);
                }
                await Task.Delay(30);
                Interlocked.Decrement(ref activeFetches);
                return new BinaryFetchResult
                {
                    Body = Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
                    ContentType = "image/png",
                    FinalUri = uri
                };
            }
        };
        var urls = Enumerable.Range(0, 8)
            .Select(index => $"https://example.test/semaphore-{index}.png")
            .ToArray();
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                foreach (string url in urls)
                {
                    Assert.Null(ImageLoader.GetImage(url, targetWidth: 4, targetHeight: 4));
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && ImageLoader.PendingLoadCount > 0)
            {
                await Task.Delay(10);
            }

            Assert.Equal(0, ImageLoader.PendingLoadCount);
            Assert.InRange(maxActiveFetches, 1, 4);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void BrowserHostDispose_ReleasesImageOwnerLazyState()
    {
        const string imageUrl = "https://example.test/dispose-lazy.png";
        ImageLoader.ClearCache();
        var host = new BrowserHost();
        try
        {
            var ownerField = typeof(BrowserHost).GetField(
                "_imageLoaderContextId",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(ownerField);
            string ownerId = (string)ownerField!.GetValue(host)!;
            ImageLoader.RegisterLazyImage(
                imageUrl,
                new SKRect(0, 0, 10, 10),
                ownerId: ownerId);

            var probe = new ImageLoader.ImageLoaderRequestContext { OwnerId = ownerId };
            using (ImageLoader.EnterRequestContext(probe))
            {
                Assert.True(ImageLoader.IsLazyPending(imageUrl));
            }

            host.Dispose();

            using (ImageLoader.EnterRequestContext(probe))
            {
                Assert.False(ImageLoader.IsLazyPending(imageUrl));
            }
        }
        finally
        {
            host.Dispose();
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void BrowserHostDispose_MarksImageRequestContextUnavailable()
    {
        const string imageUrl = "https://example.test/dispose-context.png";
        ImageLoader.ClearCache();
        var host = new BrowserHost();
        try
        {
            var contextField = typeof(BrowserHost).GetField(
                "_imageLoaderContext",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(contextField);
            var context = (ImageLoader.ImageLoaderRequestContext)contextField!.GetValue(host)!;
            Assert.NotNull(context);

            host.Dispose();
            Assert.True(context.IsDisposed);

            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 4, targetHeight: 4));
            }

            Assert.Equal(0, ImageLoader.PendingLoadCount);
            Assert.Equal(0, ImageLoader.LazyRegistryCount);
        }
        finally
        {
            host.Dispose();
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task ReleaseOwner_DropsCacheGenerationAndScopedDiagnostics()
    {
        const string imageUrl = "https://example.test/released-owner.png";
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "released-owner",
            OwnerRootId = "released-owner",
            FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                Body = OnePixelPng(),
                ContentType = "image/png",
                FinalUri = uri
            })
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 4, targetHeight: 4));
            }

            await WaitForLoadAsync(imageUrl);
            Assert.True(ImageLoader.GetCacheGeneration("released-owner") > 0);
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.True(ImageLoader.ContainsCachedImage(imageUrl));
                Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out _));
            }

            ImageLoader.ReleaseOwner("released-owner");

            Assert.Equal(0, ImageLoader.GetCacheGeneration("released-owner"));
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.False(ImageLoader.TryGetLastLoadResult(imageUrl, out _));
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task DetachedLoad_ForReleasedOwnerCompletesPendingBookkeeping()
    {
        const string imageUrl = "https://example.test/detached-released.png";
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "detached-released-owner",
            OwnerRootId = "detached-released-owner",
            FetchDetailedAsync = async uri =>
            {
                await gate.Task.ConfigureAwait(false);
                return new BinaryFetchResult
                {
                    Body = OnePixelPng(),
                    ContentType = "image/png",
                    FinalUri = uri
                };
            }
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 4, targetHeight: 4));
            }

            Assert.Equal(1, ImageLoader.PendingLoadCount);

            ImageLoader.ReleaseOwner("detached-released-owner");
            gate.SetResult(true);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && ImageLoader.PendingLoadCount > 0)
            {
                await Task.Delay(10);
            }

            Assert.Equal(0, ImageLoader.PendingLoadCount);
            Assert.False(ImageLoader.ContainsCachedImage(imageUrl));
            Assert.Equal(0, ImageLoader.GetCacheGeneration("detached-released-owner"));
        }
        finally
        {
            gate.TrySetResult(true);
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task LoadSlotAdmission_IsBoundedAndDoesNotLeakPermit()
    {
        var semaphoreField = typeof(ImageLoader).GetField(
            "_loadSemaphore",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        var waitMethod = typeof(ImageLoader).GetMethod(
            "WaitForLoadSlotAsync",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(semaphoreField);
        Assert.NotNull(waitMethod);

        var semaphore = (SemaphoreSlim)semaphoreField!.GetValue(null)!;
        Assert.True(await semaphore.WaitAsync(TimeSpan.FromSeconds(10)));
        int held = 1;
        while (await semaphore.WaitAsync(0))
        {
            held++;
        }
        try
        {
            var wait = (Task<bool>)waitMethod!.Invoke(
                null,
                new object[] { TimeSpan.FromMilliseconds(250) })!;

            Task finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(wait, finished);
            Assert.False(await wait);
            Assert.Equal(0, semaphore.CurrentCount);
        }
        finally
        {
            for (int i = 0; i < held; i++)
            {
                semaphore.Release();
            }
        }

        Assert.True(await semaphore.WaitAsync(0));
    }

    [Fact]
    public async Task ReleaseOwner_WithFailedDataUri_CompletesAndClearsOwnerFailureMemo()
    {
        const string releasedOwnerId = "failed-data-uri-released-owner";
        const string retainedOwnerId = "failed-data-uri-retained-owner";
        string releasedDataUri = BrokenDataUri("released");
        string retainedDataUri = BrokenDataUri("retained");
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(NewOwnerContext(releasedOwnerId)))
            {
                Assert.Null(ImageLoader.GetImage(releasedDataUri, targetWidth: 4, targetHeight: 4));
            }

            using (ImageLoader.EnterRequestContext(NewOwnerContext(retainedOwnerId)))
            {
                Assert.Null(ImageLoader.GetImage(retainedDataUri, targetWidth: 4, targetHeight: 4));
                long memoizedMisses = ImageLoader.GetCacheSnapshot().MissCount;
                Assert.Null(ImageLoader.GetImage(retainedDataUri, targetWidth: 4, targetHeight: 4));
                Assert.Equal(memoizedMisses, ImageLoader.GetCacheSnapshot().MissCount);
            }

            Task release = Task.Run(() => ImageLoader.ReleaseOwner(releasedOwnerId));
            Task finished = await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(release, finished);
            await release;

            using (ImageLoader.EnterRequestContext(NewOwnerContext(retainedOwnerId)))
            {
                long retainedMisses = ImageLoader.GetCacheSnapshot().MissCount;
                Assert.Null(ImageLoader.GetImage(retainedDataUri, targetWidth: 4, targetHeight: 4));
                Assert.Equal(retainedMisses, ImageLoader.GetCacheSnapshot().MissCount);
            }

            using (ImageLoader.EnterRequestContext(NewOwnerContext(releasedOwnerId)))
            {
                long missesBeforeRetry = ImageLoader.GetCacheSnapshot().MissCount;
                Assert.Null(ImageLoader.GetImage(releasedDataUri, targetWidth: 4, targetHeight: 4));
                Assert.True(ImageLoader.GetCacheSnapshot().MissCount > missesBeforeRetry);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void DataUri_BeyondOrdinaryUrlLength_IsAdmittedAndDecoded()
    {
        string dataUri = LargePngDataUri();
        Assert.True(dataUri.Length > 8192);
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(NewOwnerContext("large-data-uri-owner")))
            {
                SKBitmap bitmap = ImageLoader.GetImage(
                    "  " + dataUri + "\n",
                    targetWidth: 24,
                    targetHeight: 16);

                Assert.NotNull(bitmap);
                Assert.Equal(24, bitmap.Width);
                Assert.Equal(16, bitmap.Height);
                Assert.True(ImageLoader.ContainsCachedImage(dataUri));
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void Url_BeyondOrdinaryUrlLength_IsRejectedBeforeFetch()
    {
        string oversizedUrl = "https://example.test/" + new string('a', 9000) + ".png";
        Assert.True(oversizedUrl.Length > 8192);
        int fetches = 0;
        var context = NewOwnerContext("oversized-url-owner");
        context.FetchDetailedAsync = uri =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(new BinaryFetchResult
            {
                Body = OnePixelPng(),
                ContentType = "image/png",
                FinalUri = uri
            });
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(oversizedUrl, targetWidth: 4, targetHeight: 4));
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && ImageLoader.PendingLoadCount > 0)
            {
                Thread.Sleep(10);
            }

            Assert.Equal(0, fetches);
            Assert.Equal(0, ImageLoader.PendingLoadCount);
            Assert.False(ImageLoader.ContainsCachedImage(oversizedUrl));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    private static ImageLoader.ImageLoaderRequestContext NewOwnerContext(string ownerId) =>
        new()
        {
            OwnerId = ownerId,
            OwnerRootId = ownerId
        };

    private static string BrokenDataUri(string marker) =>
        "data:image/png;base64," +
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "fen-bytes-that-are-not-a-decodable-image-" + marker));

    private static string LargePngDataUri()
    {
        const int width = 96;
        const int height = 96;
        var noise = new Random(20240917);
        using var source = new SKBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                source.SetPixel(x, y, new SKColor(
                    (byte)noise.Next(0, 256),
                    (byte)noise.Next(0, 256),
                    (byte)noise.Next(0, 256)));
            }
        }

        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    private static byte[] OnePixelPng() =>
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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
