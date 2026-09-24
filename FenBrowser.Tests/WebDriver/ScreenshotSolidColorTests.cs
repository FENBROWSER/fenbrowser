using FenBrowser.Host.WebDriver;
using SkiaSharp;

namespace FenBrowser.Tests.WebDriver;

/// <summary>
/// The screenshot retry decides "not painted yet" from a single-colour capture;
/// the check reads the pixel buffer and reports the colour it found.
/// </summary>
public sealed class ScreenshotSolidColorTests
{
    [Fact]
    public void SolidBitmapReportsItsColour()
    {
        using var bitmap = new SKBitmap(64, 32);
        bitmap.Erase(SKColors.White);

        Assert.NotNull(HostBrowserDriver.SolidColorOf(bitmap));
    }

    [Fact]
    public void BitmapWithOneDifferentPixelIsNotSolid()
    {
        using var bitmap = new SKBitmap(64, 32);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(63, 31, SKColors.Black);

        Assert.Null(HostBrowserDriver.SolidColorOf(bitmap));
    }

    [Fact]
    public void SameColourGivesTheSameValue()
    {
        using var a = new SKBitmap(8, 8);
        using var b = new SKBitmap(16, 4);
        a.Erase(SKColors.Red);
        b.Erase(SKColors.Red);

        Assert.Equal(HostBrowserDriver.SolidColorOf(a), HostBrowserDriver.SolidColorOf(b));
    }
}
