using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// A brokered frame rasterizes overdraw above the viewport as well as below it, so the
/// host's scroll preview does not expose white while scrolling up.
/// </summary>
public sealed class BrokeredFrameBandTests
{
    [Fact]
    public void MidPage_BandReachesAsFarAboveAsBelow()
    {
        var (top, height) = FenBrowser.Host.Program.ComputeBrokeredFrameRasterBand(800f, 5000f);
        Assert.Equal(4600f, top);
        Assert.Equal(400f + 800f + 400f, height);
    }

    [Fact]
    public void NearTheTop_BandStopsAtTheDocumentStart()
    {
        var (top, height) = FenBrowser.Host.Program.ComputeBrokeredFrameRasterBand(800f, 150f);
        Assert.Equal(0f, top);
        Assert.Equal(150f + 800f + 400f, height);

        var (atTop, atTopHeight) = FenBrowser.Host.Program.ComputeBrokeredFrameRasterBand(800f, 0f);
        Assert.Equal(0f, atTop);
        Assert.Equal(FenBrowser.Host.Program.ComputeBrokeredFrameRasterHeight(800f), atTopHeight);
    }

    [Fact]
    public void TallViewport_BandStaysWithinTheSharedMemorySurface()
    {
        var (_, height) = FenBrowser.Host.Program.ComputeBrokeredFrameRasterBand(1800f, 5000f);
        Assert.True(height <= FrameSharedMemory.MaxHeight);
        Assert.True(height >= 1800f);
    }
}
