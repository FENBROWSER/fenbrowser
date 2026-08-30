using System.Reflection;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Widgets;
using SkiaSharp;

namespace FenBrowser.Tests.Host;

public sealed class TabWidgetRenderingTests
{
    [Fact]
    public void FaviconSampling_UsesLinearFilteringAndMipmaps()
    {
        var field = typeof(TabWidget).GetField(
            "FaviconSampling",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var sampling = Assert.IsType<SKSamplingOptions>(field!.GetValue(null));
        Assert.Equal(SKFilterMode.Linear, sampling.Filter);
        Assert.Equal(SKMipmapMode.Linear, sampling.Mipmap);
        Assert.False(sampling.UseCubic);
    }

    [Fact]
    public void Paint_WhenLoading_ReplacesFaviconAndInvalidatesItsActualBounds()
    {
        using var tab = new BrowserTab();
        using var favicon = new SKBitmap(32, 32);
        favicon.Erase(SKColors.Red);
        tab.Favicon = favicon;

        var loadingProperty = typeof(BrowserIntegration).GetProperty(nameof(BrowserIntegration.IsLoading));
        Assert.NotNull(loadingProperty);
        loadingProperty!.SetValue(tab.Browser, true);

        var widget = new TabWidget(tab)
        {
            Bounds = new SKRect(100, 20, 280, 52)
        };

        using var loadingBitmap = new SKBitmap(300, 72);
        using (var loadingCanvas = new SKCanvas(loadingBitmap))
        {
            loadingCanvas.Clear(SKColors.Transparent);
            widget.Paint(loadingCanvas);
        }

        var faviconBounds = new SKRect(108, 28, 124, 44);
        Assert.Equal(faviconBounds, widget.DirtyRect);
        Assert.NotEqual(SKColors.Red, loadingBitmap.GetPixel(116, 36));

        loadingProperty.SetValue(tab.Browser, false);
        widget.ClearDirtyRect();

        using var loadedBitmap = new SKBitmap(300, 72);
        using (var loadedCanvas = new SKCanvas(loadedBitmap))
        {
            loadedCanvas.Clear(SKColors.Transparent);
            widget.Paint(loadedCanvas);
        }

        Assert.Null(widget.DirtyRect);
        Assert.Equal(SKColors.Red, loadedBitmap.GetPixel(116, 36));
        widget.Detach();
    }
}
