using FenBrowser.Media.Containers.Adts;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

public class AdtsDemuxerTests
{
    [Fact]
    public async Task Fixture_DemuxesWholeFramesOf1024Samples()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = AdtsDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_aac.aac")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Aac, track.Config.Codec);
        Assert.Equal("mp4a.40.2", track.Config.CodecString);
        Assert.Equal(48000, track.Config.SampleRate);
        Assert.Equal(1, track.Config.Channels);
        // AudioSpecificConfig: AAC LC (2), 48 kHz (index 3), mono (1) → 0001 0001 1000 1000.
        Assert.Equal(new byte[] { 0x11, 0x88 }, track.Config.Extradata.ToArray());
        Assert.True(info.IsSeekable);

        long samples = 0;
        int frames = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                Assert.True(AdtsFrameHeader.TryParse(packet.Span, out var header));
                Assert.Equal(header.FrameLength, packet.Length);
                Assert.Equal(MediaTime.FromTimescale(samples, 48000), packet.Pts);
                samples += header.Samples;
                frames++;
            }
        }

        Assert.Equal(1.024, MediaTime.FromTimescale(samples, 48000).TotalSeconds, 3);
        Assert.True(frames >= 40, $"{frames} frames");
        // The duration is an estimate from the first frame's length; it must be in the right ballpark.
        Assert.InRange(info.Duration.TotalSeconds, 0.7, 1.4);
    }

    [Fact]
    public async Task Seek_ResyncsToAFrame()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = AdtsDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_aac.aac")), context);
        await demuxer.InitializeAsync(CancellationToken.None);

        await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.True(AdtsFrameHeader.TryParse(packet.Span, out _));
        Assert.InRange(packet.Pts.TotalSeconds, 0.3, 0.7);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xF1, 0x4C, 0x80, 0x0B, 0x9F, 0xFC }, false, 1, 3, 48000, 2, 92, 7, 1)]
    [InlineData(new byte[] { 0xFF, 0xF8, 0x50, 0x40, 0x01, 0x3F, 0xFD }, true, 1, 4, 44100, 1, 9, 9, 2)]
    public void Header_Parses(byte[] bytes, bool mpeg2, int profile, int sri, int rate, int channelConfig, int length, int headerLength, int blocks)
    {
        Assert.True(AdtsFrameHeader.TryParse(bytes, out var header));
        Assert.Equal((mpeg2, profile, sri, rate, channelConfig, length, headerLength, blocks),
            (header.Mpeg2, header.Profile, header.SampleRateIndex, header.SampleRate, header.ChannelConfiguration, header.FrameLength, header.HeaderLength, header.RawDataBlocks));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xF3, 0x4C, 0x80, 0x0B, 0x9F, 0xFC })] // layer bits set
    [InlineData(new byte[] { 0xFF, 0xF1, 0x7C, 0x80, 0x0B, 0x9F, 0xFC })] // sampling index 15
    [InlineData(new byte[] { 0xFF, 0xF1, 0x4C, 0x80, 0x00, 0x1F, 0xFC })] // frame shorter than its header
    [InlineData(new byte[] { 0xFF, 0xF1, 0x4C })]
    public void Header_RejectsInvalid(byte[] bytes) => Assert.False(AdtsFrameHeader.TryParse(bytes, out _));

    [Fact]
    public async Task NotAdts_Throws()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = AdtsDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_pcm16.wav")), context);
        await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public void Probe_NeedsTwoAgreeingFrames()
    {
        byte[] adts = MediaFixtures.Read("sine_aac.aac");
        Assert.Equal(70, AdtsDemuxerFactory.Instance.Probe(adts));
        Assert.Equal(0, AdtsDemuxerFactory.Instance.Probe(MediaFixtures.Read("sine_mp3_noid3.mp3")));
        Assert.Equal(0, AdtsDemuxerFactory.Instance.Probe(ReadOnlySpan<byte>.Empty));
    }
}
