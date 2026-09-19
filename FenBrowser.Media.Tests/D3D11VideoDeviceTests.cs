using FenBrowser.Media.Buffers;
using FenBrowser.Media.Gpu.Windows;
using FenBrowser.Media.Video;
using System.Runtime.Versioning;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The Direct3D 11 device hardware decoders share (design §5, M7): pictures left in GPU
/// textures come back either converted to BGRA by the video processor or as the NV12 the
/// decoder wrote. A machine without a GPU (CI) skips; the decoders then register nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public class D3D11VideoDeviceTests
{
    private static D3D11VideoDevice? Device()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return D3D11VideoDevice.TryCreate(out var device, out _) ? device : null;
    }

    /// <summary>A gradient with distinct chroma per column block, odd-sized to exercise cropping.</summary>
    private static VideoFrame Pattern(int width, int height)
    {
        var frame = VideoFrame.Allocate(MediaLimits.Default, VideoPixelFormat.Nv12, width, height, MediaTime.FromSeconds(1), MediaTime.FromSeconds(0.04));
        var y = frame.GetPlane(0);
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                y[row * frame.GetStride(0) + col] = (byte)(16 + (row * 7 + col * 3) % 220);
        var uv = frame.GetPlane(1);
        for (int row = 0; row < frame.GetPlaneHeight(1); row++)
            for (int col = 0; col < frame.GetPlaneWidth(1); col++)
            {
                uv[row * frame.GetStride(1) + col * 2] = (byte)(col < width / 4 ? 90 : 128 + (row % 60));
                uv[row * frame.GetStride(1) + col * 2 + 1] = (byte)(row < height / 4 ? 240 : 128 - (col % 60));
            }

        return frame;
    }

    [Fact]
    public void NoGpuMeansNoDeviceAndNoCrash()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // Either outcome is fine; what matters is a truthful answer without an exception.
        var shared = D3D11VideoDevice.TryGetShared(out var reason);
        Assert.True(shared is not null || reason.Length > 0);
    }

    [Fact]
    public void ReadsNv12BackUnchanged()
    {
        using var device = Device();
        if (device is null)
            return;
        using var source = Pattern(37, 23);
        var texture = device.UploadNv12(source);
        try
        {
            bool previous = D3D11VideoDevice.GpuConversionEnabled;
            D3D11VideoDevice.GpuConversionEnabled = false;
            try
            {
                using var frame = device.Download(texture, 0, source.Width, source.Height, MediaLimits.Default, source.Timestamp, source.Duration);
                Assert.NotNull(frame);
                Assert.Equal(VideoPixelFormat.Nv12, frame.Format);
                Assert.Equal((37, 23), (frame.Width, frame.Height));
                Assert.Equal(source.Timestamp, frame.Timestamp);
                Assert.Equal(source.Duration, frame.Duration);
                for (int plane = 0; plane < 2; plane++)
                {
                    int rowBytes = frame.GetPlaneWidth(plane) * (plane == 1 ? 2 : 1);
                    for (int row = 0; row < frame.GetPlaneHeight(plane); row++)
                    {
                        Assert.True(source.GetPlane(plane).Slice(row * source.GetStride(plane), rowBytes)
                            .SequenceEqual(frame.GetPlane(plane).Slice(row * frame.GetStride(plane), rowBytes)), $"plane {plane} row {row}");
                    }
                }
            }
            finally
            {
                D3D11VideoDevice.GpuConversionEnabled = previous;
            }
        }
        finally
        {
            D3D11VideoDevice.ReleaseTexture(ref texture);
        }
    }

    [Fact]
    public void ConvertsToBgraLikeTheCpuConverter()
    {
        using var device = Device();
        if (device is null)
            return;
        using var source = Pattern(64, 48);
        var expected = new byte[64 * 4 * 48];
        PixelConverter.ToBgra(source, expected, 64 * 4);

        var texture = device.UploadNv12(source);
        try
        {
            bool previous = D3D11VideoDevice.GpuConversionEnabled;
            D3D11VideoDevice.GpuConversionEnabled = true;
            long convertedBefore = GpuPictureCounters.ConvertedPictures;
            try
            {
                using var frame = device.Download(texture, 0, source.Width, source.Height, MediaLimits.Default, source.Timestamp, source.Duration);
                Assert.NotNull(frame);
                Assert.Equal(VideoPixelFormat.Bgra32, frame.Format);
                Assert.Equal(convertedBefore + 1, GpuPictureCounters.ConvertedPictures);

                // The video processor interpolates the subsampled chroma where the CPU
                // path replicates it, so pixels on a chroma edge differ; the picture as a
                // whole must follow the same BT.601 equations.
                long close = 0, total = 0, absoluteError = 0;
                var actual = frame.GetPlane(0);
                for (int row = 0; row < 48; row++)
                {
                    for (int i = 0; i < 64 * 4; i++)
                    {
                        int a = actual[row * frame.GetStride(0) + i];
                        int e = expected[row * 64 * 4 + i];
                        if (i % 4 == 3)
                        {
                            Assert.Equal(255, a);
                            continue;
                        }

                        total++;
                        absoluteError += Math.Abs(a - e);
                        if (Math.Abs(a - e) <= 6)
                            close++;
                    }
                }

                Assert.True(absoluteError <= total * 2.5, $"mean absolute error {(double)absoluteError / total:F2}");
                Assert.True(close >= total * 0.95, $"{close} of {total} samples within tolerance");
            }
            finally
            {
                D3D11VideoDevice.GpuConversionEnabled = previous;
            }
        }
        finally
        {
            D3D11VideoDevice.ReleaseTexture(ref texture);
        }
    }
}
