using FenBrowser.Host.WebDriver;
using SkiaSharp;

namespace FenBrowser.Tests.WebDriver;

/// <summary>Take Element Screenshot crops the viewport capture to the element's rect.</summary>
public sealed class ElementScreenshotCropTests
{
    private static string Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    [Fact]
    public void CropsToTheRectAndClipsAtTheEdge()
    {
        using var viewport = new SKBitmap(100, 50);
        viewport.Erase(SKColors.White);
        for (var y = 10; y < 20; y++)
            for (var x = 30; x < 50; x++)
                viewport.SetPixel(x, y, SKColors.Red);

        using var inside = SKBitmap.Decode(Convert.FromBase64String(
            HostBrowserDriver.CropScreenshot(Png(viewport), 30, 10, 20, 10)));
        Assert.Equal(20, inside.Width);
        Assert.Equal(10, inside.Height);
        Assert.Equal(SKColors.Red, inside.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, inside.GetPixel(19, 9));

        using var clipped = SKBitmap.Decode(Convert.FromBase64String(
            HostBrowserDriver.CropScreenshot(Png(viewport), 90, 40, 30, 30)));
        Assert.Equal(10, clipped.Width);
        Assert.Equal(10, clipped.Height);
    }

    [Fact]
    public void RectOutsideTheViewportGivesNothing()
    {
        using var viewport = new SKBitmap(100, 50);
        viewport.Erase(SKColors.White);

        Assert.Equal(string.Empty, HostBrowserDriver.CropScreenshot(Png(viewport), 200, 10, 20, 10));
    }
}
