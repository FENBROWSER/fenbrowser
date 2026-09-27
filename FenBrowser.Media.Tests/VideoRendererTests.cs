using FenBrowser.Media.Buffers;
using FenBrowser.Media.Video;

namespace FenBrowser.Media.Tests;

public class VideoRendererTests
{
    private static VideoFrame Frame(double seconds, double duration = 0.1) =>
        VideoFrame.Allocate(MediaLimits.Default, VideoPixelFormat.I420, 16, 8, MediaTime.FromSeconds(seconds), MediaTime.FromSeconds(duration));

    [Fact]
    public void Select_ShowsTheFrameWhoseIntervalContainsTheClock()
    {
        using var renderer = new VideoRenderer(capacity: 4);
        renderer.Enqueue(Frame(0.0));
        renderer.Enqueue(Frame(0.1));
        renderer.Enqueue(Frame(0.2));

        Assert.Null(renderer.Select(MediaTime.FromSeconds(-0.01)));
        Assert.False(renderer.HasCurrent);

        using var first = renderer.Select(MediaTime.FromSeconds(0.05));
        Assert.Equal(MediaTime.Zero, first!.Timestamp);
        Assert.True(renderer.HasCurrent);
        Assert.Null(renderer.Select(MediaTime.FromSeconds(0.09)));

        using var second = renderer.Select(MediaTime.FromSeconds(0.1));
        Assert.Equal(MediaTime.FromSeconds(0.1), second!.Timestamp);
        Assert.Equal(2, renderer.PresentedFrames);
        Assert.Equal(0, renderer.DroppedFrames);
        Assert.Equal(3, renderer.DecodedFrames);
    }

    [Fact]
    public void Select_DropsFramesTheClockSkippedAndCountsThem()
    {
        using var renderer = new VideoRenderer(capacity: 8);
        for (int i = 0; i < 6; i++)
            renderer.Enqueue(Frame(i * 0.1));

        using var frame = renderer.Select(MediaTime.FromSeconds(0.35));
        Assert.Equal(MediaTime.FromSeconds(0.3), frame!.Timestamp);
        Assert.Equal(3, renderer.DroppedFrames);     // 0.0, 0.1, 0.2 were never shown
        Assert.Equal(1, renderer.PresentedFrames);
        Assert.Equal(2, renderer.QueuedCount);
    }

    [Fact]
    public void Capacity_IsEnforcedAndFlushDropsEverything()
    {
        using var renderer = new VideoRenderer(capacity: 2);
        renderer.Enqueue(Frame(0.0));
        renderer.Enqueue(Frame(0.1));
        Assert.True(renderer.IsFull);
        Assert.Throws<InvalidOperationException>(() => renderer.Enqueue(Frame(0.2)));

        renderer.Select(MediaTime.FromSeconds(0.1))?.Dispose();
        renderer.MarkEndOfStream();
        Assert.False(renderer.IsDrained(MediaTime.FromSeconds(0.15)));
        Assert.True(renderer.IsDrained(MediaTime.FromSeconds(0.2)));

        renderer.Flush();
        Assert.False(renderer.HasCurrent);
        Assert.Equal(0, renderer.QueuedCount);
        Assert.False(renderer.EndOfStream);
    }

    [Fact]
    public void SelectFirst_ShowsTheNextFrameRegardlessOfTheClock()
    {
        using var renderer = new VideoRenderer(capacity: 4);
        Assert.Null(renderer.SelectFirst());
        renderer.Enqueue(Frame(0.5));
        using var frame = renderer.SelectFirst();
        Assert.Equal(MediaTime.FromSeconds(0.5), frame!.Timestamp);
        Assert.Equal(MediaTime.FromSeconds(0.6), renderer.QueuedEnd);
        Assert.Equal(MediaTime.FromSeconds(0.1), renderer.BufferedAhead(MediaTime.FromSeconds(0.5)));
    }
}
