using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.Tests.Svg;

/// <summary>
/// Shares one non-parallel collection with the other SVG tests so the process-wide
/// ImageLoader cache, lazy registry, and renderer state cannot race. The image
/// pipeline renders SVG with the first-party renderer only, so these tests assert
/// first-party semantics directly instead of selecting a backend.
/// </summary>
[Collection(SvgRendererBackendStateCollection.Name)]
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
        ImageLoader.ClearCache();
        try
        {
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
        ImageLoader.ClearCache();
        try
        {
            using var scope = ImageLoader.EnterRequestContext(context);
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>"));

            Assert.False(await ImageLoader.PrewarmImageAsync(outerUrl, stream));
            Assert.Null(ImageLoader.GetImage(outerUrl));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task LazySvgLoad_RetainsRequestedSizeAndScopedDiagnostics()
    {
        const string imageUrl = "https://example.test/assets/lazy.svg";
        byte[] svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg width='200' height='100'><rect width='200' height='100' fill='red'/></svg>");
        int fetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = svg,
                    ContentType = "image/svg+xml",
                    FinalUri = uri
                });
            }
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                ImageLoader.UpdateViewport(new SKRect(0, 0, 100, 100));
                Assert.Null(ImageLoader.GetImage(
                    imageUrl,
                    isLazy: true,
                    elementBounds: new SKRect(1000, 1000, 1100, 1100),
                    targetWidth: 32,
                    targetHeight: 16));
                Assert.True(ImageLoader.IsLazyPending(imageUrl));
                Assert.False(ImageLoader.IsLazyPending(imageUrl, targetWidth: 64, targetHeight: 16));

                ImageLoader.UpdateViewport(new SKRect(950, 950, 1050, 1050));
                await WaitForLoadAsync(imageUrl, context);

                Assert.False(ImageLoader.IsLazyPending(imageUrl));
                Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out var result));
                Assert.Equal(BinaryFetchFailureReason.None, result.FailureReason);
                SKBitmap? bitmap = ImageLoader.GetImage(
                    imageUrl,
                    targetWidth: 32,
                    targetHeight: 16);
                Assert.NotNull(bitmap);
                Assert.Equal(32, bitmap.Width);
                Assert.Equal(16, bitmap.Height);
                Assert.Equal(1, fetches);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Theory]
    [InlineData("https://example.test/assets/vector.svg?cache=1", "image/png")]
    [InlineData("https://example.test/assets/vector", "image/svg+xml")]
    public async Task SvgMetadata_UsesOneScopedCacheEntry(string imageUrl, string contentType)
    {
        byte[] svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg width='80' height='40'><rect width='80' height='40' fill='blue'/></svg>");
        int fetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = svg,
                    ContentType = contentType,
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
                    targetWidth: 24,
                    targetHeight: 12));
                await WaitForLoadAsync(imageUrl, context);

                Assert.True(ImageLoader.TryGetLastLoadResult(imageUrl, out _));
                SKBitmap? bitmap = ImageLoader.GetImage(
                    imageUrl,
                    targetWidth: 24,
                    targetHeight: 12);
                Assert.NotNull(bitmap);
                Assert.Equal(24, bitmap.Width);
                Assert.Equal(12, bitmap.Height);
                Assert.True(ImageLoader.ContainsCachedImage(imageUrl));
                Assert.Equal(1, fetches);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void DataSvgMime_UsesSvgRasterPath()
    {
        const string svg = "<svg width='40' height='20'><rect width='40' height='20' fill='green'/></svg>";
        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        ImageLoader.ClearCache();
        try
        {
            SKBitmap? bitmap = ImageLoader.GetImage(
                dataUri,
                targetWidth: 20,
                targetHeight: 10);
            Assert.NotNull(bitmap);
            Assert.Equal(20, bitmap.Width);
            Assert.Equal(10, bitmap.Height);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task LazyDataSvg_DecodesAfterViewportActivation()
    {
        const string svg = "<svg width='40' height='20'><rect width='40' height='20' fill='green'/></svg>";
        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N")
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                ImageLoader.UpdateViewport(new SKRect(0, 0, 100, 100));
                Assert.Null(ImageLoader.GetImage(
                    dataUri,
                    isLazy: true,
                    elementBounds: new SKRect(1000, 1000, 1100, 1100),
                    targetWidth: 20,
                    targetHeight: 10));
                Assert.True(ImageLoader.IsLazyPending(dataUri, targetWidth: 20, targetHeight: 10));

                ImageLoader.UpdateViewport(new SKRect(950, 950, 1050, 1050));
                await WaitForLoadAsync(dataUri, context);

                SKBitmap? bitmap = ImageLoader.GetImage(dataUri, targetWidth: 20, targetHeight: 10);
                Assert.NotNull(bitmap);
                Assert.Equal(20, bitmap.Width);
                Assert.Equal(10, bitmap.Height);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task SvgMimeRasterPayload_IsRejectedWithoutRasterFallback()
    {
        const string imageUrl = "https://example.test/assets/spoofed.svg";
        byte[] png = Png(SKColors.Red);
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                Body = png,
                ContentType = "image/svg+xml",
                FinalUri = uri
            })
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 8, targetHeight: 8));
                await WaitForLoadAsync(imageUrl, context);

                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 8, targetHeight: 8));
                Assert.Equal(0, ImageLoader.CacheCount);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void RasterMimeSvgPayload_IsRejectedAsDataUri()
    {
        const string svg = "<svg width='40' height='20'><rect width='40' height='20' fill='green'/></svg>";
        string dataUri = "data:image/png;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        ImageLoader.ClearCache();
        try
        {
            Assert.Null(ImageLoader.GetImage(dataUri, targetWidth: 20, targetHeight: 10));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Theory]
    [InlineData(16, 8, 16, 8)]
    [InlineData(16, null, 16, 8)]
    [InlineData(null, 8, 16, 8)]
    public void RasterDataUri_DecodesAtAdmittedTargetSize(
        int? targetWidth,
        int? targetHeight,
        int expectedWidth,
        int expectedHeight)
    {
        string dataUri = "data:image/png;base64," +
            Convert.ToBase64String(Png(40, 20, SKColors.Blue));
        ImageLoader.ClearCache();
        try
        {
            SKBitmap? bitmap = ImageLoader.GetImage(
                dataUri,
                targetWidth: targetWidth,
                targetHeight: targetHeight);
            Assert.NotNull(bitmap);
            Assert.Equal(expectedWidth, bitmap.Width);
            Assert.Equal(expectedHeight, bitmap.Height);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void OversizedDataUriBase64_IsRejectedByEnvelopeAdmission()
    {
        var limits = SvgRenderLimits.Default;
        int maxBytes = Math.Min(limits.MaxDecodedImageBytes, limits.MaxSourceChars);
        long maxEncodedLength = (maxBytes + 2L) / 3L * 4L;
        int maxEncodedChars = checked((int)maxEncodedLength);
        string dataUri = "data:image/svg+xml;base64," + new string('A', maxEncodedChars + 1);

        ImageLoader.ClearCache();
        try
        {
            Assert.Null(ImageLoader.GetImage(dataUri, targetWidth: 10, targetHeight: 10));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task NetworkRaster_DecodesAtAdmittedTargetSize()
    {
        const string imageUrl = "https://example.test/assets/target.png";
        byte[] png = Png(40, 20, SKColors.Blue);
        int fetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = png,
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
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 16, targetHeight: 8));
                await WaitForLoadAsync(imageUrl, context);
                SKBitmap? bitmap = ImageLoader.GetImage(imageUrl, targetWidth: 16, targetHeight: 8);
                Assert.NotNull(bitmap);
                Assert.Equal(16, bitmap.Width);
                Assert.Equal(8, bitmap.Height);
                Assert.Equal(1, fetches);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Theory]
    [InlineData(5000, null)]
    [InlineData(null, 5000)]
    public void SvgTargetWithOneKnownDimension_UsesIntrinsicDimensionForPixelAdmission(
        int? targetWidth,
        int? targetHeight)
    {
        const string svg = "<svg width='5000' height='5000'/>";
        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        ImageLoader.ClearCache();
        try
        {
            Assert.Null(ImageLoader.GetImage(
                dataUri,
                targetWidth: targetWidth,
                targetHeight: targetHeight));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void SvgWithoutTargetSize_RejectsIntrinsicPixelBounds()
    {
        const string svg = "<svg width='9000' height='9000'/>";
        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        ImageLoader.ClearCache();
        try
        {
            Assert.Null(ImageLoader.GetImage(dataUri));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task SizeSpecificSvgIdentity_DoesNotReturnNaturalBitmap()
    {
        const string imageUrl = "https://example.test/assets/size-identity.svg";
        byte[] svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg width='40' height='20'><rect width='40' height='20' fill='blue'/></svg>");
        int fetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = svg,
                    ContentType = "image/svg+xml",
                    FinalUri = uri
                });
            }
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 16, targetHeight: 8));
                await WaitForLoadAsync(imageUrl, context);
                SKBitmap? sized = ImageLoader.GetImage(imageUrl, targetWidth: 16, targetHeight: 8);
                Assert.NotNull(sized);
                Assert.Equal(16, sized.Width);
                Assert.Equal(8, sized.Height);

                Assert.Null(ImageLoader.GetImage(imageUrl));
                await WaitForLoadAsync(imageUrl, context);
                SKBitmap? natural = ImageLoader.GetImage(imageUrl);
                Assert.NotNull(natural);
                Assert.Equal(40, natural.Width);
                Assert.Equal(20, natural.Height);
                Assert.Equal(2, fetches);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task SvgBackend_IsNotPartOfExactCacheIdentity()
    {
        const string imageUrl = "https://example.test/assets/backend-identity.svg";
        const string svg = "<svg width='20' height='10'/>";
        ImageLoader.ClearCache();
        try
        {
            Assert.True(await PrewarmSvgAsync(imageUrl, svg, 10, 5));
            Assert.True(await PrewarmSvgAsync(imageUrl, svg, 10, 5));
            Assert.Equal(1, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void SvgCacheKey_CarriesNoBackendSelectionSegment()
    {
        var method = typeof(ImageLoader).GetMethod(
            "CreateCanonicalKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var context = new ImageLoader.ImageLoaderRequestContext { OwnerId = "cache-key-owner" };

        string key = (string)method!.Invoke(
            null,
            new object?[]
            {
                "https://example.test/assets/key.svg",
                context,
                null,
                null,
                new Uri("https://example.test/assets/key.svg"),
                true,
                true
            })!;

        Assert.DoesNotContain("backend:", key, StringComparison.Ordinal);
        Assert.Contains("cache-key-owner\n", key, StringComparison.Ordinal);
        Assert.Contains("\nbase:https://example.test/assets/key.svg", key, StringComparison.Ordinal);
    }

    [Fact]
    public void SvgBase_IsPartOfExactCacheIdentity()
    {
        const string svg = "<svg width='20' height='10'/>";
        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        var firstDocument = new FenBrowser.Core.Dom.V2.Document("https://one.test/page.svg");
        var secondDocument = new FenBrowser.Core.Dom.V2.Document("https://two.test/page.svg");
        ImageLoader.ClearCache();
        try
        {
            Assert.NotNull(ImageLoader.GetImage(dataUri, targetWidth: 10, targetHeight: 5, ownerDocument: firstDocument));
            Assert.NotNull(ImageLoader.GetImage(dataUri, targetWidth: 10, targetHeight: 5, ownerDocument: secondDocument));
            Assert.Equal(2, ImageLoader.CacheCount);
            Assert.True(ImageLoader.ContainsCachedImage(dataUri, firstDocument));
            Assert.True(ImageLoader.ContainsCachedImage(dataUri, secondDocument));
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task SvgResourcePreload_AlwaysPreloadsSameOriginNestedResources()
    {
        const string imageUrl = "https://example.test/assets/policy.svg";
        int fetches = 0;
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new BinaryFetchResult
                {
                    Body = Png(SKColors.Red),
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
                await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                    "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>"));
                Assert.True(await ImageLoader.PrewarmImageAsync(imageUrl, stream));
                Assert.Equal(1, fetches);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task ConcurrentSvgPrewarm_InsertsOneCanonicalBitmap()
    {
        const string imageUrl = "https://example.test/assets/atomic.svg";
        const string svg = "<svg width='20' height='10'/>";
        ImageLoader.ClearCache();
        try
        {
            long version = ImageLoader.CacheVersion;
            bool[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                PrewarmSvgAsync(imageUrl, svg, 10, 5)));

            Assert.All(results, Assert.True);
            Assert.Equal(1, ImageLoader.CacheCount);
            Assert.Equal(version + 1, ImageLoader.CacheVersion);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void DataGif_DecodesWithinAnimatedAdmissionLimits()
    {
        const string dataUri =
            "data:image/gif;base64,R0lGODlhAQABAID/AMDAwAAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==";
        ImageLoader.ClearCache();
        try
        {
            SKBitmap? bitmap = ImageLoader.GetImage(dataUri, targetWidth: 8, targetHeight: 8);
            Assert.NotNull(bitmap);
            Assert.Equal(8, bitmap.Width);
            Assert.Equal(8, bitmap.Height);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void DataGif_WithDocumentOwner_DecodesWithinAnimatedAdmissionLimits()
    {
        const string dataUri =
            "data:image/gif;base64,R0lGODlhAQABAID/AMDAwAAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==";
        var document = new FenBrowser.Core.Dom.V2.Document("https://example.test/page");
        ImageLoader.ClearCache();
        try
        {
            SKBitmap? bitmap = ImageLoader.GetImage(
                dataUri,
                targetWidth: 8,
                targetHeight: 8,
                ownerDocument: document);
            Assert.NotNull(bitmap);
            Assert.True(ImageLoader.ContainsCachedImage(dataUri, document));
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void InlineSvg_ExternalResourceRejectionFailsClosed()
    {
        const string svg =
            "<svg width='16' height='16'><image href='red.png' width='16' height='16'/></svg>";
        ImageLoader.ClearCache();
        try
        {
            Assert.Null(ImageLoader.GetInlineSvgImage(
                svg,
                16,
                16,
                new Uri("https://example.test/page.svg")));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void InlineSvg_FirstPartyPartialResultFailsClosed()
    {
        const string svg =
            "<svg width='80' height='30'><text x='2' y='15' writing-mode='vertical-rl'>Fen</text></svg>";
        ImageLoader.ClearCache();
        try
        {
            using SKBitmap? bitmap = ImageLoader.GetInlineSvgImage(svg, 80, 30);
            Assert.Null(bitmap);
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void SvgImage_SpacedNamespaceDeclarationRendersAsWritten()
    {
        // XML 1.0 §2.3 Eq allows whitespace around '=', so this root already
        // declares the SVG namespace; the loader must not declare it a second time.
        using SKBitmap? bitmap = RenderInline(
            "<svg xmlns = 'http://www.w3.org/2000/svg' width='20' height='20'>" +
            "<rect width='20' height='20' fill='lime'/></svg>");

        Assert.NotNull(bitmap);
        Assert.Equal(SKColors.Lime, bitmap!.GetPixel(10, 10));
    }

    [Fact]
    public void SvgImage_UndeclaredNamespaceStillRendersAsSvg()
    {
        using SKBitmap? bitmap = RenderInline(
            "<svg width='20' height='20'><rect width='20' height='20' fill='lime'/></svg>");

        Assert.NotNull(bitmap);
        Assert.Equal(SKColors.Lime, bitmap!.GetPixel(10, 10));
    }

    private static SKBitmap? RenderInline(string svg)
    {
        ImageLoader.ClearCache();
        try
        {
            SKBitmap? cached = ImageLoader.GetInlineSvgImage(svg, 20, 20);
            return cached?.Copy();
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void SharedAdmissionPredicate_RejectsEveryNonAuthoritativeResultShape()
    {
        Assert.False(SvgRenderResult.IsAdmissible(null));
        Assert.False(ImageLoader.IsAdmissibleSvgPixels(null));

        using var failed = new SvgRenderResult
        {
            Success = false,
            Backend = SvgRendererBackend.FirstParty
        };
        Assert.False(SvgRenderResult.IsAdmissible(failed));
        Assert.False(ImageLoader.IsAdmissibleSvgPixels(failed));
        Assert.NotNull(SvgRenderResult.DescribeRejection(failed));

        using var requiresFallback = new SvgRenderResult
        {
            Success = true,
            Backend = SvgRendererBackend.FirstParty,
            RequiresFallback = true,
            FallbackReasonCodes = new[] { "unsupported-feature" }
        };
        Assert.False(SvgRenderResult.IsAdmissible(requiresFallback));
        Assert.False(ImageLoader.IsAdmissibleSvgPixels(requiresFallback));
        Assert.NotNull(SvgRenderResult.DescribeRejection(requiresFallback));

        using var resourceRejected = new SvgRenderResult
        {
            Success = true,
            Backend = SvgRendererBackend.FirstParty,
            HadResourceRejection = true,
            ResourceRejectionReasonCodes = new[] { "resource-context-missing" }
        };
        Assert.False(SvgRenderResult.IsAdmissible(resourceRejected));
        Assert.False(ImageLoader.IsAdmissibleSvgPixels(resourceRejected));
        Assert.NotNull(SvgRenderResult.DescribeRejection(resourceRejected));

        using var codesWithoutFlag = new SvgRenderResult
        {
            Success = true,
            Backend = SvgRendererBackend.FirstParty,
            ResourceRejectionReasonCodes = new[] { "external-resource" }
        };
        Assert.False(SvgRenderResult.IsAdmissible(codesWithoutFlag));
        Assert.False(ImageLoader.IsAdmissibleSvgPixels(codesWithoutFlag));
    }

    [Fact]
    public void SharedAdmissionPredicate_AdmitsFirstPartyBitmapAndRejectsAnyOtherBackend()
    {
        using var bitmap = new SKBitmap(2, 2);
        using var firstParty = new SvgRenderResult
        {
            Success = true,
            Backend = SvgRendererBackend.FirstParty,
            Bitmap = bitmap
        };

        Assert.True(SvgRenderResult.IsAdmissible(firstParty));
        Assert.True(ImageLoader.IsAdmissibleSvgPixels(firstParty));
        Assert.Null(SvgRenderResult.DescribeRejection(firstParty));

        using var otherBackend = new SvgRenderResult
        {
            Success = true,
            Backend = (SvgRendererBackend)7,
            Bitmap = bitmap
        };

        Assert.False(ImageLoader.IsAdmissibleSvgPixels(otherBackend));
    }

    [Fact]
    public void ImageLoaderSvgRenderer_ReturnsBitmapAndNeverAPicture()
    {
        ISvgRenderer renderer = ImageLoader.CreateSvgRenderer();
        Assert.IsType<FenSvgRenderer>(renderer);

        using var rendered = renderer.Render(
            "<svg width='20' height='10'><rect width='20' height='10' fill='blue'/></svg>");
        Assert.True(SvgRenderResult.IsAdmissible(rendered), rendered.ErrorMessage);
        Assert.NotNull(rendered.Bitmap);
        Assert.Null(rendered.Picture);

        using var rejected = renderer.Render(
            "<svg width='20' height='10'><image href='red.png' width='20' height='10'/></svg>");
        Assert.False(rejected.Success);
        Assert.Null(rejected.Bitmap);
        Assert.Null(rejected.Picture);
        Assert.True(rejected.HadResourceRejection);
    }

    [Fact]
    public async Task NetworkSvg_ResourceRejectionIsNeverCached()
    {
        const string imageUrl = "https://example.test/assets/rejected.svg";
        byte[] svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg width='20' height='10'><image href='red.png' width='20' height='10'/></svg>");
        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                Body = svg,
                ContentType = "image/svg+xml",
                FinalUri = uri
            })
        };
        ImageLoader.ClearCache();
        try
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 10, targetHeight: 5));
                await WaitForLoadAsync(imageUrl, context);

                Assert.Null(ImageLoader.GetImage(imageUrl, targetWidth: 10, targetHeight: 5));
                Assert.False(ImageLoader.ContainsCachedImage(imageUrl));
                Assert.Equal(0, ImageLoader.CacheCount);
            }
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public void ImageLoader_SvgRasterizationHasASingleFirstPartyAdmissionPath()
    {
        string sourcePath = System.IO.Path.Combine(
            FindRepositoryRoot(),
            "FenBrowser.FenEngine",
            "Rendering",
            "ImageLoader.cs");
        string source = System.IO.File.ReadAllText(sourcePath);

        Assert.Contains("SvgRenderResult.IsAdmissible(result)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsUnsafeSvgRenderResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("result.Picture", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawPicture", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DetachPicture", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "FenBrowser.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public async Task Prewarm_UnknownUrlDetectsSvgFromBoundedPrefix()
    {
        const string imageUrl = "https://example.test/assets/vector";
        const string svg =
            "<svg width='20' height='10'><rect width='20' height='10' fill='blue'/></svg>";
        ImageLoader.ClearCache();
        try
        {
            await using var stream = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(svg));
            Assert.True(await ImageLoader.PrewarmImageAsync(
                imageUrl,
                stream,
                targetWidth: 10,
                targetHeight: 5));
            Assert.NotNull(ImageLoader.GetImage(
                imageUrl,
                targetWidth: 10,
                targetHeight: 5));
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task Prewarm_UnknownStreamStopsAtAdmissionCeiling()
    {
        const string imageUrl = "https://example.test/assets/oversized";
        var limits = SvgRenderLimits.Default;
        int maxBytes = Math.Min(limits.MaxDecodedImageBytes, limits.MaxSourceChars);
        ImageLoader.ClearCache();
        try
        {
            await using var stream = new MemoryStream(new byte[maxBytes + 1]);
            Assert.False(await ImageLoader.PrewarmImageAsync(imageUrl, stream));
            Assert.Equal(0, ImageLoader.CacheCount);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(513, false)]
    public void AnimatedGifAdmission_RejectsExcessiveAggregateFrames(int frameCount, bool admitted)
    {
        var method = typeof(ImageLoader).GetMethod(
            "TryAdmitAnimatedGif",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var imageInfo = new SKImageInfo(
            2048,
            2048,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        bool result = (bool)method!.Invoke(
            null,
            new object[] { frameCount, imageInfo, SvgRenderLimits.Default });

        Assert.Equal(admitted, result);
    }

    [Fact]
    public void AnimatedGifAdmission_RejectsAggregateByteBudgetWhenPixelBudgetFits()
    {
        var method = typeof(ImageLoader).GetMethod(
            "TryAdmitAnimatedGif",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var imageInfo = new SKImageInfo(
            2048,
            2048,
            SKColorType.RgbaF16,
            SKAlphaType.Premul);
        var limits = SvgRenderLimits.Default;

        Assert.Equal(8, imageInfo.BytesPerPixel);
        Assert.False((bool)method!.Invoke(
            null,
            new object[] { 2, imageInfo, limits })!);
    }

    [Fact]
    public void AnimatedGifAdmission_RejectsOversizedSingleFrameBeforeFrameArrayAllocation()
    {
        var method = typeof(ImageLoader).GetMethod(
            "TryAdmitAnimatedGif",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var imageInfo = new SKImageInfo(
            9000,
            9000,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        bool result = (bool)method!.Invoke(
            null,
            new object[] { 2, imageInfo, SvgRenderLimits.Default });

        Assert.False(result);
    }

    [Fact]
    public async Task Prewarm_UnknownStreamIsReadThroughBoundedCeiling()
    {
        const string imageUrl = "https://example.test/assets/bounded-read";
        var limits = SvgRenderLimits.Default;
        int maxBytes = Math.Min(limits.MaxDecodedImageBytes, limits.MaxSourceChars);
        var payload = new byte[maxBytes + 1];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)'A';
        }

        var probe = new CountingReadStream(payload);
        ImageLoader.ClearCache();
        try
        {
            Assert.False(await ImageLoader.PrewarmImageAsync(imageUrl, probe));
            Assert.Equal(0, ImageLoader.CacheCount);
            Assert.InRange(probe.BytesRead, 1, maxBytes + 81920);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    [Fact]
    public async Task Prewarm_UnknownSvgStreamIsClassifiedAfterBoundedPrefixRead()
    {
        const string imageUrl = "https://example.test/assets/bounded-svg";
        const string svg =
            "<svg width='20' height='10'><rect width='20' height='10' fill='blue'/></svg>";
        var probe = new CountingReadStream(System.Text.Encoding.UTF8.GetBytes(svg));
        ImageLoader.ClearCache();
        try
        {
            Assert.True(await ImageLoader.PrewarmImageAsync(
                imageUrl,
                probe,
                targetWidth: 10,
                targetHeight: 5));
            Assert.Equal(probe.Length, probe.BytesRead);
            SKBitmap? bitmap = ImageLoader.GetImage(
                imageUrl,
                targetWidth: 10,
                targetHeight: 5);
            Assert.NotNull(bitmap);
            Assert.Equal(10, bitmap.Width);
            Assert.Equal(5, bitmap.Height);
        }
        finally
        {
            ImageLoader.ClearCache();
        }
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly byte[] _payload;
        private int _position;

        public CountingReadStream(byte[] payload)
        {
            _payload = payload;
        }

        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _payload.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _position >= _payload.Length)
            {
                return 0;
            }

            int read = Math.Min(count, _payload.Length - _position);
            Array.Copy(_payload, _position, buffer, offset, read);
            _position += read;
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static async Task<bool> PrewarmSvgAsync(
        string imageUrl,
        string svg,
        int targetWidth,
        int targetHeight)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
        return await ImageLoader.PrewarmImageAsync(
            imageUrl,
            stream,
            targetWidth: targetWidth,
            targetHeight: targetHeight);
    }

    private static async Task WaitForLoadAsync(
        string imageUrl,
        ImageLoader.ImageLoaderRequestContext context)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using (ImageLoader.EnterRequestContext(context))
            {
                if (ImageLoader.PendingLoadCount == 0 &&
                    ImageLoader.TryGetLastLoadResult(imageUrl, out _))
                {
                    return;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Image load did not complete for {imageUrl}");
    }

    private static byte[] Png(SKColor color) => Png(2, 2, color);

    private static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
