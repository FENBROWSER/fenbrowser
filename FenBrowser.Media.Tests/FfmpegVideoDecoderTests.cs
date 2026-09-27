using System.Security.Cryptography;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Containers.Matroska;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The libavcodec video adapter against the WebM fixtures. The expected digests are of
/// ffmpeg's own <c>-f rawvideo -pix_fmt yuv420p</c> output for the same files: VP8, VP9
/// and AV1 decoding is bit-exact, so the planes must match byte for byte.
/// </summary>
public class FfmpegVideoDecoderTests
{
    private sealed class Collect : IDecodeOutput<VideoFrame>
    {
        public List<VideoFrame> Frames { get; } = [];
        public void Emit(VideoFrame item) => Frames.Add(item);
    }

    private static DecoderRegistry Registry()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "libavcodec must be available on the development machine (winget Gyan.FFmpeg.Shared)");
        return decoders;
    }

    [Theory]
    [InlineData("pattern_vp9.webm", MediaCodec.Vp9, "1fc5acb40b7d1cc57be2bb749c630bcc")]
    [InlineData("pattern_vp8_vorbis.webm", MediaCodec.Vp8, "bf3c481a48e8c107f91c51ddd8b3cffb")]
    [InlineData("pattern_av1.webm", MediaCodec.Av1, "02f2577c853310ef56f31a24409d774a")]
    public async Task DecodesTheFixtureBitExactly(string fixture, MediaCodec codec, string expectedMd5)
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read(fixture)), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = info.Tracks.Single(t => t.Kind == MediaTrackKind.Video);
        Assert.Equal(codec, track.Config.Codec);

        var decoder = await DecoderSelector.SelectAsync(Registry().GetVideoCandidates(track.Config), track.Config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using var _ = decoder;

        var output = new Collect();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                if (packet.TrackId == track.Id)
                    await decoder.DecodeAsync(packet, output, CancellationToken.None);
            }
        }

        await decoder.DrainAsync(output, CancellationToken.None);

        Assert.Equal(10, output.Frames.Count);
        using var md5 = MD5.Create();
        for (int i = 0; i < output.Frames.Count; i++)
        {
            var frame = output.Frames[i];
            Assert.Equal(VideoPixelFormat.I420, frame.Format);
            Assert.Equal(64, frame.Width);
            Assert.Equal(48, frame.Height);
            Assert.Equal(MediaTime.FromSeconds(i * 0.1), frame.Timestamp);
            Assert.Equal(MediaTime.FromSeconds(0.1), frame.Duration);
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                int rowBytes = frame.GetPlaneWidth(plane);
                for (int y = 0; y < frame.GetPlaneHeight(plane); y++)
                    md5.TransformBlock(frame.GetPlane(plane).Slice(y * frame.GetStride(plane), rowBytes).ToArray(), 0, rowBytes, null, 0);
            }
        }

        md5.TransformFinalBlock([], 0, 0);
        Assert.Equal(expectedMd5, Convert.ToHexStringLower(md5.Hash!));
        foreach (var frame in output.Frames)
            frame.Dispose();
    }

    [Fact]
    public async Task Reset_DropsStateAndDecodesFromTheNextKeyframe()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_vp9.webm")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = info.Tracks[0];
        var decoder = await DecoderSelector.SelectAsync(Registry().GetVideoCandidates(track.Config), track.Config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using var _ = decoder;

        // Frame threading holds pictures back until the pipeline fills, so feed a few
        // packets without draining: the reset must drop them without a trace.
        var first = new Collect();
        for (int i = 0; i < 3; i++)
        {
            using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
            await decoder.DecodeAsync(packet!, first, CancellationToken.None);
        }

        await decoder.ResetAsync(CancellationToken.None);
        foreach (var frame in first.Frames)
            frame.Dispose();
        await demuxer.SeekAsync(MediaTime.Zero, CancellationToken.None);

        var second = new Collect();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
                await decoder.DecodeAsync(packet, second, CancellationToken.None);
        }

        await decoder.DrainAsync(second, CancellationToken.None);
        Assert.Equal(10, second.Frames.Count);
        for (int i = 0; i < second.Frames.Count; i++)
            Assert.Equal(MediaTime.FromSeconds(i * 0.1), second.Frames[i].Timestamp);
        foreach (var frame in second.Frames)
            frame.Dispose();
    }

    [Fact]
    public void Registry_AnswersCanPlayTypeForWebmVideo()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance));
        var support = new MediaTypeSupport(demuxers, decoders);
        Assert.Equal("maybe", MediaTypeSupport.ToDomString(support.CanPlayType("video/webm")));
        Assert.Equal("probably", MediaTypeSupport.ToDomString(support.CanPlayType("video/webm; codecs=\"vp8\"")));
        Assert.Equal("probably", MediaTypeSupport.ToDomString(support.CanPlayType("video/webm; codecs=\"vp9, opus\"")));
        Assert.Equal("probably", MediaTypeSupport.ToDomString(support.CanPlayType("video/webm; codecs=\"av01.0.04M.08\"")));
        Assert.Equal("maybe", MediaTypeSupport.ToDomString(support.CanPlayType("video/webm; codecs=\"vp09.02.10.10\"")));
        Assert.Equal("", MediaTypeSupport.ToDomString(support.CanPlayType("video/webm; codecs=\"avc1.42E01E\"")));
    }
}
