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

    // CSS Backgrounds 3 §2.11.2: the root element's background paints the SVG
    // document's canvas. css/css-lists/list-style-image-gradients draws its
    // reference markers from `<svg style='background: blue'>`.
    [Fact]
    public void RootBackgroundPaintsTheCanvas()
    {
        var result = new SvgSkiaRenderer().Render(
            "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4' style='background: blue'></svg>");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.HasNaturalSize);
        Assert.Equal(SKColors.Blue, result.Bitmap.GetPixel(2, 2));
    }

    // CSS Images 3 §4.1: an SVG root without a concrete width and height has no
    // natural size, so its users size it with the default object size.
    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'></svg>", false)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10'></svg>", false)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='100%' height='100%'></svg>", false)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='20'></svg>", true)]
    public void NaturalSizeNeedsAConcreteWidthAndHeight(string svg, bool expected)
    {
        var result = new SvgSkiaRenderer().Render(svg);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.HasNaturalSize);
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
