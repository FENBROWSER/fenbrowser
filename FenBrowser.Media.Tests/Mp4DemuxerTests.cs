using FenBrowser.Media.Buffers;
using FenBrowser.Media.Containers.Mp4;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// Expected values come from ffprobe over the fixtures (packet counts, timestamps after the
/// edit list, key flags, interleaving) and from the boxes read by hand.
/// </summary>
public class Mp4DemuxerTests
{
    [Fact]
    public async Task H264Aac_DemuxesBothTracksInDecodeOrder()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_h264_aac.mp4")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, info.Tracks.Count);
        var video = info.Tracks.Single(t => t.Kind == MediaTrackKind.Video);
        var audio = info.Tracks.Single(t => t.Kind == MediaTrackKind.Audio);
        Assert.Equal(MediaCodec.H264, video.Config.Codec);
        Assert.Equal("avc1.64000A", video.Config.CodecString);
        Assert.Equal(64, video.Config.Width);
        Assert.Equal(48, video.Config.Height);
        Assert.Equal(1, video.Config.Extradata.Span[0]);            // avcC configurationVersion
        Assert.Equal(MediaCodec.Aac, audio.Config.Codec);
        Assert.Equal("mp4a.40.2", audio.Config.CodecString);
        Assert.Equal(48000, audio.Config.SampleRate);
        Assert.Equal(1, audio.Config.Channels);
        Assert.Equal([0x11, 0x88, 0x56, 0xE5, 0x00], audio.Config.Extradata.ToArray()); // AudioSpecificConfig
        Assert.InRange(info.Duration.TotalSeconds, 0.99, 1.03);
        Assert.True(info.IsSeekable);

        var packets = await ReadAll(demuxer);
        var videoPackets = packets.Where(p => p.TrackId == video.Id).ToList();
        var audioPackets = packets.Where(p => p.TrackId == audio.Id).ToList();
        Assert.Equal(10, videoPackets.Count);
        Assert.Equal(48, audioPackets.Count);

        // The edit list puts the first frame at 0 with its decode time 0.2 s earlier (B-frames).
        Assert.Equal(MediaTime.Zero, videoPackets[0].Pts);
        Assert.Equal(MediaTime.FromSeconds(-0.2), videoPackets[0].Dts);
        Assert.True(videoPackets[0].IsKeyframe);
        Assert.Equal(MediaTime.FromSeconds(0.4), videoPackets[1].Pts);
        Assert.Equal(MediaTime.FromSeconds(-0.1), videoPackets[1].Dts);
        Assert.False(videoPackets[1].IsKeyframe);
        Assert.Equal(MediaTime.FromSeconds(0.9), videoPackets[^1].Pts);
        Assert.All(videoPackets, p => Assert.Equal(MediaTime.FromSeconds(0.1), p.Duration));
        Assert.Equal(1677, videoPackets[0].Length);

        // AAC priming: the first frame starts 1024 samples before zero.
        Assert.Equal(MediaTime.FromTimescale(-1024, 48000), audioPackets[0].Pts);
        Assert.Equal(MediaTime.FromTimescale(1024, 48000), audioPackets[0].Duration);
        Assert.Equal(MediaTime.FromTimescale(47 * 1024 - 1024, 48000), audioPackets[^1].Pts);
        Assert.All(audioPackets, p => Assert.True(p.IsKeyframe));

        // Interleaved by decode time: two video frames, then audio and video alternate.
        Assert.Equal(video.Id, packets[0].TrackId);
        Assert.Equal(video.Id, packets[1].TrackId);
        Assert.Equal(audio.Id, packets[2].TrackId);
        Assert.Equal(video.Id, packets[3].TrackId);
        for (int i = 1; i < packets.Count; i++)
            Assert.True(packets[i].Dts >= packets[i - 1].Dts, $"decode order broken at {i}");
    }

    [Fact]
    public async Task FragmentedH264_IndexesTheMoof()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_h264_fragmented.mp4")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.H264, track.Config.Codec);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 3);   // mvhd, as ffprobe reports

        var packets = await ReadAll(demuxer);
        Assert.Equal(10, packets.Count);
        // No edit list: the composition offsets stand as they are.
        Assert.Equal(MediaTime.FromSeconds(0.2), packets[0].Pts);
        Assert.Equal(MediaTime.Zero, packets[0].Dts);
        Assert.True(packets[0].IsKeyframe);
        Assert.All(packets.Skip(1), p => Assert.False(p.IsKeyframe));
        Assert.Equal(MediaTime.FromSeconds(1.1), packets[^1].Pts);
        Assert.Equal(1677, packets[0].Length);
    }

    [Fact]
    public async Task M4a_DemuxesAac()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_aac.m4a")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Aac, track.Config.Codec);
        Assert.Equal(48, (await ReadAll(demuxer)).Count);
    }

    [Fact]
    public async Task Faststart_MoovBeforeMdatReadsTheSame()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_h264_faststart.mp4")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        Assert.Equal(MediaCodec.H264, Assert.Single(info.Tracks).Config.Codec);
        var packets = await ReadAll(demuxer);
        Assert.Equal(10, packets.Count);
        Assert.Equal(MediaTime.Zero, packets[0].Pts);
    }

    [Fact]
    public async Task Seek_ResumesAtTheSyncSampleBeforeTheTarget()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_h264_aac.mp4")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var video = info.Tracks.Single(t => t.Kind == MediaTrackKind.Video);
        var audio = info.Tracks.Single(t => t.Kind == MediaTrackKind.Audio);
        _ = await ReadAll(demuxer);

        await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        var packets = await ReadAll(demuxer);
        var firstVideo = packets.First(p => p.TrackId == video.Id);
        Assert.True(firstVideo.IsKeyframe);
        Assert.Equal(MediaTime.Zero, firstVideo.Pts);              // the only sync sample
        var firstAudio = packets.First(p => p.TrackId == audio.Id);
        Assert.True(firstAudio.Pts <= MediaTime.FromSeconds(0.5) && firstAudio.Pts + firstAudio.Duration > MediaTime.FromSeconds(0.5));
        Assert.Equal(10, packets.Count(p => p.TrackId == video.Id));
    }

    [Fact]
    public void Probe_PrefersFtyp()
    {
        Assert.Equal(100, Mp4DemuxerFactory.Instance.Probe(MediaFixtures.Read("pattern_h264_aac.mp4").AsSpan(0, 16)));
        Assert.Equal(0, Mp4DemuxerFactory.Instance.Probe("OggS\0\0\0\0"u8));
        Assert.Equal(0, Mp4DemuxerFactory.Instance.Probe([0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]));
    }

    [Fact]
    public async Task TruncatedMoov_IsAFormatError()
    {
        var bytes = MediaFixtures.Read("pattern_h264_faststart.mp4");
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(bytes.AsMemory(0, 300)), context);
        await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OversizedVideo_HitsTheLimit()
    {
        var context = MediaPipelineContext.ForTests() with { Limits = MediaLimits.Default with { MaxVideoHeight = 32 } };
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_h264_aac.mp4")), context);
        var error = await Assert.ThrowsAsync<MediaLimitExceededException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
        Assert.Equal("MaxVideoHeight", error.Limit);
    }

    private static async Task<List<EncodedPacket>> ReadAll(IDemuxer demuxer)
    {
        var packets = new List<EncodedPacket>();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
            packets.Add(packet);
        return packets;
    }
}
