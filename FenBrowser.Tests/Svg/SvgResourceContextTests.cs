using FenBrowser.FenEngine.Adapters;
using SkiaSharp;

namespace FenBrowser.Tests.Svg;

public sealed class SvgResourceContextTests
{
    [Fact]
    public void AuthorizedSameOriginRaster_RendersWithoutRejection()
    {
        var uri = new Uri("https://example.test/assets/red.png");
        var resolver = new MemoryResolver((uri, Png(SKColors.Red)));

        using var result = Render(
            "<svg width='4' height='4'><image href='assets/red.png' width='4' height='4'/></svg>",
            new Uri("https://example.test/page.svg"), resolver);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.HadResourceRejection);
        Assert.Equal(1, resolver.CallCount);
        Assert.True(result.Bitmap!.GetPixel(2, 2).Red > 200);
    }

    [Fact]
    public void DefaultPolicyRejectsAuthorizedSameOriginBeforeResolverCall()
    {
        var uri = new Uri("https://example.test/assets/red.png");
        var resolver = new MemoryResolver((uri, Png(SKColors.Red)));

        using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
            "<svg width='4' height='4'><image href='assets/red.png' width='4' height='4'/></svg>",
            SvgRenderLimits.Default)
        {
            BaseUri = new Uri("https://example.test/page.svg"),
            ResourceResolver = resolver
        });

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Contains("external-resource", result.ResourceRejectionReasonCodes);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void MissingResolverContext_FailsClosed()
    {
        using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
            "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>",
            ExternalReferenceLimits()));

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Contains("resource-context-missing", result.ResourceRejectionReasonCodes);
    }

    [Fact]
    public void ExplicitExternalReferenceWithoutTrustedResolver_FailsClosed()
    {
        var uri = new Uri("https://example.test/red.png");
        var resolver = new MemoryResolver((uri, Png(SKColors.Red)));

        using var result = new FenSvgRenderer().Render(new SvgRenderRequest(
            "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>",
            new SvgRenderLimits { AllowExternalReferences = true })
        {
            BaseUri = new Uri("https://example.test/page.svg")
        });

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Contains("resource-context-missing", result.ResourceRejectionReasonCodes);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void CrossOriginReference_IsRejectedBeforeResolverCall()
    {
        var resolver = new MemoryResolver();

        using var result = Render(
            "<svg width='4' height='4'><image href='https://other.test/red.png' width='4' height='4'/></svg>",
            new Uri("https://example.test/page.svg"), resolver);

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Equal(0, resolver.CallCount);
        Assert.Contains("cross-origin-resource", result.ResourceRejectionReasonCodes);
    }

    [Fact]
    public void ResolverCannotSubstituteDifferentUri()
    {
        var requested = new Uri("https://example.test/red.png");
        var resolver = new MemoryResolver((requested, Png(SKColors.Red)))
        {
            ReturnedUri = new Uri("https://example.test/other.png")
        };

        using var result = Render(
            "<svg width='4' height='4'><image href='red.png' width='4' height='4'/></svg>",
            new Uri("https://example.test/page.svg"), resolver);

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Contains("resolver-uri-mismatch", result.ResourceRejectionReasonCodes);
    }

    [Fact]
    public void SiblingResources_ObeyCountBudget()
    {
        var uri = new Uri("https://example.test/red.png");
        var resolver = new MemoryResolver((uri, Png(SKColors.Red)));
        var limits = ExternalReferenceLimits();
        limits.MaxResourceCount = 1;

        using var result = Render(
            "<svg width='4' height='2'><image href='red.png' width='2' height='2'/>" +
            "<image href='red.png' x='2' width='2' height='2'/></svg>",
            new Uri("https://example.test/page.svg"), resolver, limits);

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Contains("resource-budget", result.ResourceRejectionReasonCodes);
    }

    [Fact]
    public void SiblingResources_ObeyCumulativeByteBudget()
    {
        byte[] png = Png(SKColors.Red);
        var uri = new Uri("https://example.test/red.png");
        var resolver = new MemoryResolver((uri, png));
        var limits = ExternalReferenceLimits();
        limits.MaxCumulativeResourceBytes = png.Length;

        using var result = Render(
            "<svg width='4' height='2'><image href='red.png' width='2' height='2'/>" +
            "<image href='red.png' x='2' width='2' height='2'/></svg>",
            new Uri("https://example.test/page.svg"), resolver, limits);

        AssertFailsClosed(result);
        Assert.True(result.HadResourceRejection);
        Assert.Contains("resource-budget", result.ResourceRejectionReasonCodes);
    }

    [Fact]
    public void NestedExternalSvg_UsesItsOwnUriAsRelativeBase()
    {
        var child = new Uri("https://example.test/nested/child.svg");
        var raster = new Uri("https://example.test/nested/red.png");
        var resolver = new MemoryResolver(
            (child, System.Text.Encoding.UTF8.GetBytes(
                "<svg width='2' height='2'><image href='red.png' width='2' height='2'/></svg>"),
                "image/svg+xml"),
            (raster, Png(SKColors.Red), "image/png"));

        using var result = Render(
            "<svg width='4' height='4'><image href='nested/child.svg' width='4' height='4'/></svg>",
            new Uri("https://example.test/page.svg"), resolver);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.HadResourceRejection);
        Assert.Contains(raster, resolver.RequestedUris);
        Assert.True(result.Bitmap!.GetPixel(2, 2).Red > 200);
    }

    [Fact]
    public void ImageClip_DoesNotLeakToFollowingSibling()
    {
        var uri = new Uri("https://example.test/red.png");
        var resolver = new MemoryResolver((uri, Png(SKColors.Red)));

        using var result = Render(
            "<svg width='4' height='2'><image href='red.png' width='1' height='1'/>" +
            "<rect x='2' width='2' height='2' fill='blue'/></svg>",
            new Uri("https://example.test/page.svg"), resolver);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Bitmap!.GetPixel(3, 1).Blue > 200);
    }

    [Fact]
    public void RequestOverloads_AreNullSafe()
    {
        ISvgRenderer firstParty = SvgRendererFactory.GetRenderer(SvgRendererBackend.FirstParty);

        using var first = firstParty.Render((SvgRenderRequest)null!);
        using var second = new FenSvgRenderer().Render((string)null!);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Null(first.Bitmap);
        Assert.Null(second.Bitmap);
    }

    private static void AssertFailsClosed(SvgRenderResult result)
    {
        Assert.False(result.Success, result.ErrorMessage);
        Assert.Null(result.Bitmap);
        Assert.Null(result.Picture);
        Assert.Equal(0f, result.Width);
        Assert.Equal(0f, result.Height);
        Assert.False(SvgRenderResult.IsAdmissible(result));
        Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    private static SvgRenderResult Render(
        string source,
        Uri baseUri,
        ISvgResourceResolver resolver,
        SvgRenderLimits? limits = null) =>
        new FenSvgRenderer().Render(new SvgRenderRequest(source, limits ?? ExternalReferenceLimits())
        {
            BaseUri = baseUri,
            ResourceResolver = resolver
        });

    private static SvgRenderLimits ExternalReferenceLimits()
    {
        var limits = SvgRenderLimits.Default;
        limits.AllowExternalReferences = true;
        return limits;
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class MemoryResolver : ISvgResourceResolver
    {
        private readonly Dictionary<Uri, (byte[] Content, string ContentType)> _resources = new();

        public MemoryResolver()
        {
        }

        public MemoryResolver(params (Uri Uri, byte[] Content)[] resources)
        {
            foreach (var item in resources)
                _resources[item.Uri] = (item.Content, "image/png");
        }

        public MemoryResolver(params (Uri Uri, byte[] Content, string ContentType)[] resources)
        {
            foreach (var item in resources)
                _resources[item.Uri] = (item.Content, item.ContentType);
        }

        public int CallCount { get; private set; }
        public List<Uri> RequestedUris { get; } = new();
        public Uri? ReturnedUri { get; init; }

        public bool TryResolve(
            Uri absoluteUri,
            SvgResourceKind kind,
            out SvgResolvedResource resource,
            out string error)
        {
            CallCount++;
            RequestedUris.Add(absoluteUri);
            if (_resources.TryGetValue(absoluteUri, out var found))
            {
                resource = new SvgResolvedResource(
                    ReturnedUri ?? absoluteUri, found.ContentType, found.Content);
                error = string.Empty;
                return true;
            }
            resource = default;
            error = "test resource not found";
            return false;
        }
    }
}
