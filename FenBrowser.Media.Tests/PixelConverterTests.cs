using FenBrowser.Media.Buffers;
using FenBrowser.Media.Video;

namespace FenBrowser.Media.Tests;

/// <summary>Reference values from the BT.601 limited-range equations (ITU-R BT.601-7 §2.5.4).</summary>
public class PixelConverterTests
{
    private static VideoFrame Solid(VideoPixelFormat format, byte y, byte u, byte v, int width = 4, int height = 4)
    {
        var frame = VideoFrame.Allocate(MediaLimits.Default, format, width, height, MediaTime.Zero, MediaTime.Zero);
        frame.GetPlane(0).Fill(y);
        if (format == VideoPixelFormat.I420)
        {
            frame.GetPlane(1).Fill(u);
            frame.GetPlane(2).Fill(v);
        }
        else
        {
            var uv = frame.GetPlane(1);
            for (int i = 0; i < uv.Length; i += 2)
            {
                uv[i] = u;
                uv[i + 1] = v;
            }
        }

        return frame;
    }

    [Theory]
    [InlineData(VideoPixelFormat.I420, 235, 128, 128, 255, 255, 255)]   // reference white
    [InlineData(VideoPixelFormat.I420, 16, 128, 128, 0, 0, 0)]          // reference black
    [InlineData(VideoPixelFormat.I420, 126, 128, 128, 128, 128, 128)]   // mid grey
    [InlineData(VideoPixelFormat.I420, 81, 90, 240, 255, 0, 0)]         // BT.601 red
    [InlineData(VideoPixelFormat.I420, 145, 54, 34, 0, 255, 0)]         // BT.601 green
    [InlineData(VideoPixelFormat.I420, 41, 240, 110, 0, 0, 255)]        // BT.601 blue
    [InlineData(VideoPixelFormat.Nv12, 81, 90, 240, 255, 0, 0)]
    [InlineData(VideoPixelFormat.I420, 0, 128, 128, 0, 0, 0)]           // below range clamps
    public void ConvertsSolidColours(VideoPixelFormat format, byte y, byte u, byte v, int r, int g, int b)
    {
        using var frame = Solid(format, y, u, v);
        var bgra = new byte[frame.Width * 4 * frame.Height];
        PixelConverter.ToBgra(frame, bgra, frame.Width * 4);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            Assert.InRange(bgra[i], b - 2, b + 2);
            Assert.InRange(bgra[i + 1], g - 2, g + 2);
            Assert.InRange(bgra[i + 2], r - 2, r + 2);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void OddSizesAndPaddedStridesAreHandled()
    {
        using var frame = Solid(VideoPixelFormat.I420, 235, 128, 128, width: 5, height: 3);
        int stride = 5 * 4 + 12;
        var bgra = new byte[stride * 3];
        PixelConverter.ToBgra(frame, bgra, stride);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
                Assert.Equal(255, bgra[y * stride + x * 4 + 2]);
            Assert.Equal(0, bgra[y * stride + 5 * 4]); // padding untouched
        }
    }

    [Fact]
    public void HdUsesBt709()
    {
        Assert.False(PixelConverter.UsesBt709(480));
        Assert.True(PixelConverter.UsesBt709(720));
        Assert.True(PixelConverter.UsesBt709(1080));
    }

    [Fact]
    public void Presenter_PublishesAndSharesPicturesByReference()
    {
        var presenter = new VideoPresenter();
        int notified = 0;
        presenter.FrameAvailable += () => notified++;
        Assert.Null(presenter.Acquire());

        using (var frame = Solid(VideoPixelFormat.I420, 81, 90, 240))
            presenter.Publish(frame, MediaLimits.Default);
        Assert.Equal(1, notified);
        Assert.Equal(1, presenter.Sequence);

        var picture = presenter.Acquire();
        Assert.NotNull(picture);
        Assert.Equal(4, picture.Width);
        Assert.Equal(16, picture.Stride);
        Assert.InRange(picture.Pixels[2], 253, 255);

        using (var frame = Solid(VideoPixelFormat.I420, 16, 128, 128))
            presenter.Publish(frame, MediaLimits.Default);
        // The painter's reference keeps the first picture alive after it was replaced.
        Assert.InRange(picture.Pixels[2], 253, 255);
        picture.Release();
        Assert.Throws<ObjectDisposedException>(() => picture.Pixels.Length);

        var second = presenter.Acquire()!;
        Assert.Equal(2, second.Sequence);
        second.Release();
        presenter.Clear();
        Assert.Null(presenter.Acquire());
    }
}
