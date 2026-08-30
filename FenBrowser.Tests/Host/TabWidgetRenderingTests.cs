using System.Reflection;
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
}
