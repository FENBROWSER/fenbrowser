using FenBrowser.Media.Containers.Flac;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

public class FlacDemuxerTests
{
    [Fact]
    public async Task Fixture_DemuxesWholeFramesWithExactTimestamps()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = FlacDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_flac.flac")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Flac, track.Config.Codec);
        Assert.Equal(48000, track.Config.SampleRate);
        Assert.Equal(1, track.Config.Channels);
        Assert.Equal(34, track.Config.Extradata.Length);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 3);

        long samples = 0;
        int frames = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                Assert.True(FlacFrameHeader.TryParse(packet.Span, out var header), $"frame {frames} has no header");
                Assert.Equal(MediaTime.FromTimescale(samples, 48000), packet.Pts);
                Assert.Equal(MediaTime.FromTimescale(header.BlockSize, 48000), packet.Duration);
                // The frame ends with its CRC-16.
                ushort crc = (ushort)((packet.Span[^2] << 8) | packet.Span[^1]);
                Assert.Equal(crc, FlacFrameHeader.Crc16(packet.Span[..^2]));
                samples += header.BlockSize;
                frames++;
            }
        }

        Assert.Equal(48000, samples);
        Assert.True(frames >= 10, $"only {frames} frames");
    }

    [Fact]
    public async Task Seek_LandsOnTheFrameContainingTheTarget()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = FlacDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_flac.flac")), context);
        await demuxer.InitializeAsync(CancellationToken.None);

        await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.True(packet.Pts <= MediaTime.FromSeconds(0.5), $"landed after the target at {packet.Pts}");
        Assert.True(packet.Pts + packet.Duration > MediaTime.FromSeconds(0.5), $"landed too early at {packet.Pts}");

        await demuxer.SeekAsync(MediaTime.Zero, CancellationToken.None);
        using var first = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.Equal(MediaTime.Zero, first!.Pts);

        await demuxer.SeekAsync(MediaTime.FromSeconds(50), CancellationToken.None);
        int rest = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } p)
        {
            p.Dispose();
            rest++;
        }

        Assert.True(rest <= 1, $"{rest} frames after seeking past the end");
    }

    [Fact]
    public void FrameHeader_ParsesAndChecksItsCrc()
    {
        byte[] flac = MediaFixtures.Read("sine_flac.flac");
        // First frame follows the metadata: find the first sync that parses.
        int at = flac.AsSpan().IndexOf(new byte[] { 0xFF, 0xF8 });
        Assert.True(at > 0);
        Assert.True(FlacFrameHeader.TryParse(flac.AsSpan(at), out var header));
        Assert.Equal(0, header.Number);
        Assert.True(header.BlockSize > 0);

        byte[] corrupt = flac.AsSpan(at, 16).ToArray();
        corrupt[3] ^= 0x10; // channel assignment changes, CRC-8 no longer matches
        Assert.False(FlacFrameHeader.TryParse(corrupt, out _));
    }

    [Fact]
    public void Crcs_MatchKnownVectors()
    {
        Assert.Equal(0xF4, FlacFrameHeader.Crc8("123456789"u8));
        Assert.Equal(0xFEE8, FlacFrameHeader.Crc16("123456789"u8)); // CRC-16/BUYPASS, the non-reflected 0x8005
    }

    [Theory]
    [InlineData("fLaC")]
    [InlineData("fLaC\x80\x00\x00\x22")]
    [InlineData("RIFF....WAVE")]
    public async Task Malformed_Throws(string text)
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = FlacDemuxerFactory.Instance.Create(new MemoryByteSource(System.Text.Encoding.Latin1.GetBytes(text)), context);
        await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }
}
