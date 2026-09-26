using FenBrowser.Media.Containers.Mp3;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

public class Mp3DemuxerTests
{
    [Theory]
    [InlineData("sine_mp3_noid3.mp3", 44100, 1, 1152)]
    [InlineData("sine_mp3_id3.mp3", 44100, 1, 1152)]
    [InlineData("sine_mp3_mpeg2.mp3", 22050, 1, 576)]
    public async Task Fixtures_DemuxToWholeFramesWithContinuousTimestamps(string file, int sampleRate, int channels, int samplesPerFrame)
    {
        var fixture = MediaFixtures.Get(file);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp3DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read(file)), context);

        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Mp3, track.Config.Codec);
        Assert.Equal(sampleRate, track.Config.SampleRate);
        Assert.Equal(channels, track.Config.Channels);
        Assert.True(info.IsSeekable);
        // ffprobe's duration applies the LAME gapless trim; ours is whole frames.
        Assert.InRange(info.Duration.TotalSeconds, fixture.DurationSeconds!.Value - 0.05, fixture.DurationSeconds.Value + 0.06);

        long samples = 0;
        int frames = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                Assert.True(MpegAudioFrameHeader.TryParse(packet.Span, out var header), $"frame {frames} is not a frame header");
                Assert.Equal(header.FrameLength, packet.Length);
                Assert.Equal(MediaTime.FromTimescale(samples, sampleRate), packet.Pts);
                Assert.Equal(MediaTime.FromTimescale(samplesPerFrame, sampleRate), packet.Duration);
                samples += samplesPerFrame;
                frames++;
            }
        }

        Assert.True(frames >= 30, $"only {frames} frames");
        Assert.Equal(info.Duration, MediaTime.FromTimescale(samples, sampleRate));
    }

    [Fact]
    public async Task Id3v2Tag_IsSkippedAndTheXingFrameIsNotAudio()
    {
        var context = MediaPipelineContext.ForTests();
        byte[] bytes = MediaFixtures.Read("sine_mp3_id3.mp3");
        Assert.Equal("ID3"u8.ToArray(), bytes[..3]);
        await using var demuxer = Mp3DemuxerFactory.Instance.Create(new MemoryByteSource(bytes), context);
        await demuxer.InitializeAsync(CancellationToken.None);

        using var first = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(first);
        // The Xing/Info frame is a silent frame; the first packet is the frame after it.
        Assert.True(first.Span.IndexOf("Info"u8) < 0 && first.Span.IndexOf("Xing"u8) < 0, "the Xing frame was emitted as audio");
        Assert.Equal(MediaTime.Zero, first.Pts);
    }

    [Fact]
    public async Task Seek_LandsOnAFrameNearTheTarget()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp3DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_mp3_noid3.mp3")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.True(MpegAudioFrameHeader.TryParse(packet.Span, out _));
        Assert.InRange(packet.Pts.TotalSeconds, 0.4, 0.6);

        await demuxer.SeekAsync(MediaTime.Zero, CancellationToken.None);
        using var start = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.Equal(MediaTime.Zero, start!.Pts);

        await demuxer.SeekAsync(info.Duration, CancellationToken.None);
        var last = await demuxer.ReadPacketAsync(CancellationToken.None);
        last?.Dispose();
    }

    [Fact]
    public async Task GarbageBetweenFrames_IsResynced()
    {
        byte[] clean = MediaFixtures.Read("sine_mp3_noid3.mp3");
        MpegAudioFrameHeader.TryParse(clean, out var header);
        int cut = header.FrameLength * 5;
        byte[] junk = "this is not audio at all, resync please"u8.ToArray();
        byte[] dirty = [.. clean[..cut], .. junk, .. clean[cut..]];

        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp3DemuxerFactory.Instance.Create(new MemoryByteSource(dirty), context);
        await demuxer.InitializeAsync(CancellationToken.None);
        int frames = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
                Assert.True(MpegAudioFrameHeader.TryParse(packet.Span, out _));
            frames++;
        }

        await using var reference = Mp3DemuxerFactory.Instance.Create(new MemoryByteSource(clean), context);
        await reference.InitializeAsync(CancellationToken.None);
        int expected = 0;
        while (await reference.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            packet.Dispose();
            expected++;
        }

        Assert.Equal(expected, frames);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, 1, 3, 128, 44100, 2, 1152, 417)]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x92, 0xC4 }, 1, 3, 128, 44100, 1, 1152, 418)]
    [InlineData(new byte[] { 0xFF, 0xF3, 0x80, 0xC4 }, 2, 3, 64, 22050, 1, 576, 208)]
    [InlineData(new byte[] { 0xFF, 0xE3, 0x80, 0xC4 }, 25, 3, 64, 11025, 1, 576, 417)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0x90, 0x64 }, 1, 1, 288, 44100, 2, 384, 312)]
    public void Header_Parses(byte[] bytes, int version, int layer, int kbps, int rate, int channels, int spf, int length)
    {
        Assert.True(MpegAudioFrameHeader.TryParse(bytes, out var header));
        Assert.Equal((version, layer, kbps, rate, channels, spf, length), (header.Version, header.Layer, header.BitrateKbps, header.SampleRate, header.Channels, header.SamplesPerFrame, header.FrameLength));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xEB, 0x90, 0x64 })] // reserved version
    [InlineData(new byte[] { 0xFF, 0xF9, 0x90, 0x64 })] // reserved layer
    [InlineData(new byte[] { 0xFF, 0xFB, 0xF0, 0x64 })] // bad bitrate
    [InlineData(new byte[] { 0xFF, 0xFB, 0x00, 0x64 })] // free-format bitrate
    [InlineData(new byte[] { 0xFF, 0xFB, 0x9C, 0x64 })] // reserved sample rate
    [InlineData(new byte[] { 0xFE, 0xFB, 0x90, 0x64 })] // no sync
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90 })]       // short
    public void Header_RejectsInvalid(byte[] bytes) => Assert.False(MpegAudioFrameHeader.TryParse(bytes, out _));

    [Fact]
    public async Task NotMp3_Throws()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp3DemuxerFactory.Instance.Create(new MemoryByteSource(new byte[2000]), context);
        await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }
}
