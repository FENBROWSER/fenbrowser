using FenBrowser.FenEngine.Typography;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class SkiaFontServiceTypefaceCacheAllocationTests
{
    [Fact]
    public void ResolveTypeface_RepeatedCacheHitsDoNotAllocateKeys()
    {
        const int iterations = 10_000;
        var fontService = new SkiaFontService();
        var expected = fontService.ResolveTypeface("Arial", 400, SKFontStyleSlant.Upright);
        SKTypeface resolved = null;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            resolved = fontService.ResolveTypeface("Arial", 400, SKFontStyleSlant.Upright);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(expected, resolved);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ResolveTypeface_CacheKeyPreservesFamilyWeightAndSlant()
    {
        var fontService = new SkiaFontService();

        var regular = fontService.ResolveTypeface("Arial", 400, SKFontStyleSlant.Upright);
        var repeated = fontService.ResolveTypeface("Arial", 400, SKFontStyleSlant.Upright);
        fontService.ResolveTypeface("Arial", 700, SKFontStyleSlant.Upright);
        fontService.ResolveTypeface("Arial", 400, SKFontStyleSlant.Italic);

        Assert.Same(regular, repeated);
        Assert.Equal(3, fontService.GetCacheSnapshot().TypefaceEntries);

        var fallbackService = new SkiaFontService();
        var nullFamily = fallbackService.ResolveTypeface(null);
        var explicitDefault = fallbackService.ResolveTypeface("default");

        Assert.Same(nullFamily, explicitDefault);
        Assert.Equal(1, fallbackService.GetCacheSnapshot().TypefaceEntries);
    }
}
