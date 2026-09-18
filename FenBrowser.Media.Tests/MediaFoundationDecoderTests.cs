using FenBrowser.Media.Buffers;
using FenBrowser.Media.Codecs.MediaFoundation;
using FenBrowser.Media.Containers.Mp4;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The Media Foundation adapter (ADR-0002) on Windows. The OS H.264 decoder rounds a little
/// differently from libavcodec (about 54 dB PSNR on the fixture), so its NV12 pictures are
/// compared to ffmpeg's decode by PSNR and structure rather than by digest; AAC is checked
/// as the 440 Hz sine the fixture encodes.
/// </summary>
public class MediaFoundationDecoderTests
{
    private sealed class CollectVideo : IDecodeOutput<VideoFrame>
    {
        public List<VideoFrame> Frames { get; } = [];
        public void Emit(VideoFrame item) => Frames.Add(item);
    }

    private sealed class CollectAudio : IDecodeOutput<AudioBlock>
    {
        public List<AudioBlock> Blocks { get; } = [];
        public void Emit(AudioBlock item) => Blocks.Add(item);
    }

    private static DecoderRegistry Registry()
    {
        var decoders = new DecoderRegistry();
        Assert.True(MediaFoundationDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "Media Foundation decoders must register on Windows");
        return decoders;
    }

    [Fact]
    public void Registers_AacAndH264OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var decoders = Registry();
        Assert.Equal(DecoderSupport.Supported, decoders.GetSupport(new CodecConfig(MediaTrackKind.Audio, MediaCodec.Aac, "mp4a.40.2", SampleRate: 48000, Channels: 2)));
        Assert.Equal(DecoderSupport.Supported, decoders.GetSupport(new CodecConfig(MediaTrackKind.Video, MediaCodec.H264, "avc1.64000A", Width: 64, Height: 48)));
    }

    [Theory]
    [InlineData("pattern_h264_aac.mp4")]
    [InlineData("pattern_h264_fragmented.mp4")]
    public async Task H264_DecodesToNv12WithinRoundingOfTheReference(string fixture)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read(fixture)), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = info.Tracks.Single(t => t.Kind == MediaTrackKind.Video);
        var decoder = await DecoderSelector.SelectAsync(Registry().GetVideoCandidates(track.Config), track.Config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using var _ = decoder;

        var output = new CollectVideo();
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

        // ffmpeg's decode of the same pictures, NV12 planes end to end (scripts/media/gen_fixtures.py --references).
        var reference = MediaFixtures.Read("pattern_h264_ref.nv12");
        const int frameBytes = 64 * 48 * 3 / 2;
        Assert.Equal(10 * frameBytes, reference.Length);

        MediaTime previous = MediaTime.NegativeInfinity;
        double squaredError = 0;
        long samples = 0;
        for (int i = 0; i < output.Frames.Count; i++)
        {
            var frame = output.Frames[i];
            Assert.Equal(VideoPixelFormat.Nv12, frame.Format);
            Assert.Equal(64, frame.Width);
            Assert.Equal(48, frame.Height);
            Assert.True(frame.Timestamp > previous, "pictures come out in presentation order");
            previous = frame.Timestamp;

            int at = i * frameBytes;
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                int rowBytes = frame.GetPlaneWidth(plane) * (plane == 1 ? 2 : 1);
                for (int y = 0; y < frame.GetPlaneHeight(plane); y++)
                {
                    var row = frame.GetPlane(plane).Slice(y * frame.GetStride(plane), rowBytes);
                    for (int x = 0; x < rowBytes; x++)
                    {
                        int d = row[x] - reference[at + x];
                        squaredError += d * d;
                        samples++;
                    }

                    at += rowBytes;
                }
            }
        }

        double psnr = squaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * samples / squaredError);
        Assert.True(psnr >= 45, $"PSNR against ffmpeg's decode is {psnr:F1} dB");

        // The edit list put the first picture at 0 in the progressive file; the fragmented one starts at 0.2 s.
        Assert.Equal(fixture == "pattern_h264_fragmented.mp4" ? MediaTime.FromSeconds(0.2) : MediaTime.Zero, output.Frames[0].Timestamp);
        Assert.Equal(MediaTime.FromSeconds(0.1), output.Frames[0].Duration);
        foreach (var frame in output.Frames)
            frame.Dispose();
    }

    [Fact]
    public async Task Aac_DecodesTheSine()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_aac.m4a")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = info.Tracks[0];
        var decoder = await DecoderSelector.SelectAsync(Registry().GetAudioCandidates(track.Config), track.Config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using var _ = decoder;

        var output = new CollectAudio();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
                await decoder.DecodeAsync(packet, output, CancellationToken.None);
        }

        await decoder.DrainAsync(output, CancellationToken.None);

        Assert.NotEmpty(output.Blocks);
        Assert.All(output.Blocks, b => Assert.Equal(48000, b.SampleRate));
        Assert.All(output.Blocks, b => Assert.Equal(1, b.Channels));
        var samples = new List<float>();
        foreach (var block in output.Blocks)
        {
            samples.AddRange(block.Samples.ToArray());
            block.Dispose();
        }

        // 48 frames of 1024 samples, the first starting at the priming offset.
        Assert.InRange(samples.Count / 48000.0, 0.95, 1.1);
        Assert.Equal(MediaTime.FromTimescale(-1024, 48000), output.Blocks[0].Timestamp);

        int from = 12000;
        int to = Math.Min(samples.Count, 36000);
        float peak = 0;
        int crossings = 0;
        for (int i = from + 1; i < to; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
            if ((samples[i - 1] < 0) != (samples[i] < 0))
                crossings++;
        }

        Assert.InRange(peak, 0.10f, 0.15f);
        Assert.InRange(crossings, 400, 480);
    }

    [Fact]
    public void AnnexB_RewritesLengthPrefixesAndPrependsParameterSets()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // avcC: version 1, profile/compat/level, lengthSizeMinusOne=3, one SPS (2 bytes), one PPS (1 byte).
        byte[] avcC = [1, 0x64, 0, 0x0A, 0xFF, 0xE1, 0, 2, 0x67, 0xAA, 1, 0, 1, 0x68];
        var (sets, lengthSize) = MfVideoDecoder.ParseAvcC(avcC);
        Assert.Equal(4, lengthSize);
        Assert.Equal([0, 0, 0, 1, 0x67, 0xAA, 0, 0, 0, 1, 0x68], sets);

        var decoder = new MfVideoDecoder(MediaCodec.H264, MfInterop.ClsidMsH264Decoder, "mf-h264", MediaPipelineContext.ForTests());
        // Two NAL units of 2 and 1 bytes with 4-byte length prefixes.
        byte[] accessUnit = [0, 0, 0, 2, 0x65, 0x11, 0, 0, 0, 1, 0x41];
        typeof(MfVideoDecoder).GetField("_parameterSets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(decoder, sets);
        var annexB = decoder.ToAnnexB(accessUnit, keyframe: true);
        Assert.Equal([0, 0, 0, 1, 0x67, 0xAA, 0, 0, 0, 1, 0x68, 0, 0, 0, 1, 0x65, 0x11, 0, 0, 0, 1, 0x41], annexB);
        var inter = decoder.ToAnnexB(accessUnit, keyframe: false);
        Assert.Equal([0, 0, 0, 1, 0x65, 0x11, 0, 0, 0, 1, 0x41], inter);
    }
}
