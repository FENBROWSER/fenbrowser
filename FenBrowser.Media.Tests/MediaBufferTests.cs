using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Tests;

public class MediaBufferTests
{
    private static readonly MediaLimits Limits = MediaLimits.Default;

    [Fact]
    public void EncodedPacket_CopyKeepsBytesAndTiming()
    {
        using var packet = EncodedPacket.Copy(Limits, MediaTrackKind.Audio, trackId: 2, [1, 2, 3],
            MediaTime.FromSeconds(1), MediaTime.FromSeconds(0.9), MediaTime.FromSeconds(0.02), isKeyframe: true);

        Assert.Equal([1, 2, 3], packet.Span.ToArray());
        Assert.Equal(3, packet.Memory.Length);
        Assert.Equal(2, packet.TrackId);
        Assert.Equal(MediaTime.FromSeconds(0.9), packet.Dts);
        Assert.True(packet.IsKeyframe);
    }

    [Fact]
    public void EncodedPacket_UseAfterDisposeThrows_AndDoubleDisposeIsSafe()
    {
        var packet = EncodedPacket.Rent(Limits, MediaTrackKind.Video, 1, 16, default, default, default, false);
        packet.Dispose();
        packet.Dispose();
        Assert.Throws<ObjectDisposedException>(() => packet.Memory);
    }

    [Fact]
    public void EncodedPacket_EnforcesPacketCeiling()
    {
        Assert.Throws<MediaLimitExceededException>(() =>
            EncodedPacket.Rent(Limits, MediaTrackKind.Audio, 1, Limits.MaxAudioPacketBytes + 1, default, default, default, false));
    }

    [Theory]
    [InlineData(VideoPixelFormat.I420, 1, 3)]
    [InlineData(VideoPixelFormat.Nv12, 1, 2)]
    [InlineData(VideoPixelFormat.Bgra32, 1, 1)]
    public void VideoFrame_PlaneCountFollowsFormat(VideoPixelFormat format, int alignment, int planes)
    {
        using var frame = VideoFrame.Allocate(Limits, format, 4, 4, default, default, alignment);
        Assert.Equal(planes, frame.PlaneCount);
    }

    [Fact]
    public void VideoFrame_I420_OddSizeRoundsChromaUp()
    {
        using var frame = VideoFrame.Allocate(Limits, VideoPixelFormat.I420, 5, 3, default, default, alignment: 1);

        Assert.Equal((5, 3, 5), (frame.GetPlaneWidth(0), frame.GetPlaneHeight(0), frame.GetStride(0)));
        Assert.Equal((3, 2, 3), (frame.GetPlaneWidth(1), frame.GetPlaneHeight(1), frame.GetStride(1)));
        Assert.Equal((3, 2, 3), (frame.GetPlaneWidth(2), frame.GetPlaneHeight(2), frame.GetStride(2)));
        Assert.Equal(15, frame.GetPlane(0).Length);
        Assert.Equal(6, frame.GetPlane(2).Length);
    }

    [Fact]
    public void VideoFrame_StridesAreAligned()
    {
        using var frame = VideoFrame.Allocate(Limits, VideoPixelFormat.Nv12, 1919, 1081, default, default, alignment: 32);

        Assert.Equal(1920, frame.GetStride(0));
        Assert.Equal(1920, frame.GetStride(1));         // 960 chroma pixels × 2 bytes, already aligned
        Assert.Equal(541, frame.GetPlaneHeight(1));
        Assert.Equal(1920 * 1081, frame.GetPlane(0).Length);
    }

    [Fact]
    public void VideoFrame_PlanesDoNotOverlap()
    {
        using var frame = VideoFrame.Allocate(Limits, VideoPixelFormat.I420, 7, 7, default, default, alignment: 8);
        frame.GetPlane(0).Fill(1);
        frame.GetPlane(1).Fill(2);
        frame.GetPlane(2).Fill(3);

        Assert.All(frame.GetPlane(0).ToArray(), b => Assert.Equal(1, b));
        Assert.All(frame.GetPlane(1).ToArray(), b => Assert.Equal(2, b));
        Assert.All(frame.GetPlane(2).ToArray(), b => Assert.Equal(3, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(128)]
    public void VideoFrame_RejectsBadAlignment(int alignment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoFrame.Allocate(Limits, VideoPixelFormat.I420, 4, 4, default, default, alignment));
    }

    [Fact]
    public void VideoFrame_EnforcesDimensionLimits_AndDisposes()
    {
        Assert.Throws<MediaLimitExceededException>(() =>
            VideoFrame.Allocate(Limits, VideoPixelFormat.Bgra32, 16384, 16, default, default));

        var frame = VideoFrame.Allocate(Limits, VideoPixelFormat.Bgra32, 2, 2, default, default);
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.GetPlane(0));
    }

    [Fact]
    public void AudioBlock_DurationComesFromFrameCount()
    {
        using var block = AudioBlock.Allocate(Limits, 48_000, 2, 960, MediaTime.FromSeconds(1));

        Assert.Equal(1920, block.Samples.Length);
        Assert.Equal(MediaTime.FromMicroseconds(20_000), block.Duration);
        Assert.Equal(MediaTime.FromMicroseconds(1_020_000), block.EndTime);
    }

    [Fact]
    public void AudioBlock_EnforcesFormatAndSizeLimits()
    {
        Assert.Throws<MediaLimitExceededException>(() => AudioBlock.Allocate(Limits, 48_000, 0, 10, default));
        Assert.Throws<MediaLimitExceededException>(() => AudioBlock.Allocate(Limits, 48_000, 2, -1, default));
        Assert.Throws<MediaLimitExceededException>(() =>
            AudioBlock.Allocate(Limits, 48_000, 2, Limits.MaxAudioBlockFrames + 1, default));
    }

    [Fact]
    public void AudioBlock_UseAfterDisposeThrows()
    {
        var block = AudioBlock.Allocate(Limits, 44_100, 1, 1, default);
        block.Dispose();
        Assert.Throws<ObjectDisposedException>(() => block.Samples.Length);
    }

    [Fact]
    public void PooledBuffers_AreZeroedBeforeReuse()
    {
        // Leave dirty arrays in the shared pool, as other subsystems do. Media must not see them.
        for (int i = 0; i < 8; i++)
        {
            byte[] dirty = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
            dirty.AsSpan().Fill(0xCD);
            System.Buffers.ArrayPool<byte>.Shared.Return(dirty, clearArray: false);
        }

        // Bytes written by an earlier media owner never reappear either.
        for (int i = 0; i < 64; i++)
        {
            using var packet = EncodedPacket.Rent(Limits, MediaTrackKind.Video, 1, 4096, default, default, default, false);
            Assert.All(packet.Span.ToArray(), b => Assert.Equal(0, b));
            packet.Memory.Span.Fill(0xAB);
        }
    }
}
