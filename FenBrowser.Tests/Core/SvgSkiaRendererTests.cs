using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class SvgSkiaRendererTests
{
    [Fact]
    public void DefaultLimits_AllowNormalComplexInlineArtworkBudget()
    {
        var limits = SvgRenderLimits.Default;

        Assert.Equal(250, limits.MaxRenderTimeMs);
        Assert.Equal(32, limits.MaxRecursionDepth);
        Assert.Equal(10, limits.MaxFilterCount);
        Assert.False(limits.AllowExternalReferences);
    }

    [Fact]
    public void ViewBoxOnlyIconRendersVisiblePixelsOnColdUse()
    {
        var renderer = new SvgSkiaRenderer();
        var svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" width=""32"" height=""32"">
    <path d=""M10.226 17.284c-2.965-.36-5.054-2.493-5.054-5.256 0-1.123.404-2.336 1.078-3.144C3 4 7 1 12 1s9 3 9 9-4 11-9 12c1-2 1-3-1.774-4.716Z""/>
</svg>";

        var result = renderer.Render(svg);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Bitmap);
        Assert.True(BitmapHasVisiblePixels(result.Bitmap));
    }

    private static bool BitmapHasVisiblePixels(SKBitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
