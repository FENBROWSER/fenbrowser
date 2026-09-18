using FenBrowser.Media.Buffers;
using FenBrowser.Media.Containers.Matroska;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// Expected values come from ffprobe over the fixtures (packet counts, timestamps, key
/// flags), not from running the demuxer.
/// </summary>
public class MatroskaDemuxerTests
{
    [Fact]
    public async Task Vp9_DemuxesTenKeyAndInterFrames()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_vp9.webm")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaTrackKind.Video, track.Kind);
        Assert.Equal(MediaCodec.Vp9, track.Config.Codec);
        Assert.Equal("vp9", track.Config.CodecString);
        Assert.Equal(64, track.Config.Width);
        Assert.Equal(48, track.Config.Height);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 3);
        Assert.True(info.IsSeekable);

        var packets = await ReadAll(demuxer);
        Assert.Equal(10, packets.Count);
        Assert.True(packets[0].IsKeyframe);
        Assert.All(packets.Skip(1), p => Assert.False(p.IsKeyframe));
        for (int i = 0; i < packets.Count; i++)
        {
            Assert.Equal(0, packets[i].TrackId);
            Assert.Equal(MediaTime.FromSeconds(i * 0.1), packets[i].Pts);
            Assert.Equal(MediaTime.FromSeconds(0.1), packets[i].Duration);
            Assert.True(packets[i].Length > 0);
        }
    }

    [Fact]
    public async Task Vp8Vorbis_InterleavesBothTracksInFileOrder()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_vp8_vorbis.webm")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, info.Tracks.Count);
        var video = info.Tracks.Single(t => t.Kind == MediaTrackKind.Video);
        var audio = info.Tracks.Single(t => t.Kind == MediaTrackKind.Audio);
        Assert.Equal(MediaCodec.Vp8, video.Config.Codec);
        Assert.Equal(MediaCodec.Vorbis, audio.Config.Codec);
        Assert.Equal(48000, audio.Config.SampleRate);
        Assert.Equal(1, audio.Config.Channels);
        // The three Vorbis headers arrive Xiph-laced in CodecPrivate, as libavcodec wants them.
        Assert.Equal(2, audio.Config.Extradata.Span[0]);

        var packets = await ReadAll(demuxer);
        var videoPackets = packets.Where(p => p.TrackId == video.Id).ToList();
        var audioPackets = packets.Where(p => p.TrackId == audio.Id).ToList();
        Assert.Equal(10, videoPackets.Count);
        Assert.Equal(49, audioPackets.Count);

        // The first audio packet starts one CodecDelay (128 samples, 2.667 ms; ffprobe shows
        // -3 ms in its 1 ms timebase) before zero, the first video frame at 0.
        Assert.Equal(audio.Id, packets[0].TrackId);
        Assert.Equal(MediaTime.FromMicroseconds(-2667), packets[0].Pts);
        Assert.Equal(video.Id, packets[1].TrackId);
        Assert.Equal(MediaTime.Zero, packets[1].Pts);
        Assert.All(audioPackets, p => Assert.True(p.IsKeyframe));
        Assert.Equal(MediaTime.FromMicroseconds(993_333), audioPackets[^1].Pts);
        Assert.Equal(MediaTime.FromSeconds(0.9), videoPackets[^1].Pts);
        Assert.True(audioPackets.Zip(audioPackets.Skip(1)).All(pair => pair.First.Pts < pair.Second.Pts), "audio timestamps rise");
    }

    [Fact]
    public async Task Opus_StampsPacketsFromTheTocAndCodecDelay()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_opus.webm")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Opus, track.Config.Codec);
        Assert.Equal(48000, track.Config.SampleRate);
        Assert.Equal("OpusHead"u8.ToArray(), track.Config.Extradata.ToArray()[..8]);

        var packets = await ReadAll(demuxer);
        Assert.Equal(51, packets.Count);
        // CodecDelay is the 312-sample pre-skip, 6.5 ms (ffprobe rounds it to -7 ms).
        Assert.Equal(MediaTime.FromMicroseconds(-6500), packets[0].Pts);
        Assert.Equal(MediaTime.FromTimescale(960, 48000), packets[0].Duration);
        Assert.Equal(MediaTime.FromMicroseconds(994_500), packets[^1].Pts);
    }

    [Fact]
    public async Task Av1_IsNamedForTheDecoderRegistry()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_av1.webm")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Av1, track.Config.Codec);
        Assert.Equal("av01", track.Config.CodecString);
        Assert.Equal(10, (await ReadAll(demuxer)).Count);
    }

    [Fact]
    public async Task Matroska_DocTypeIsAcceptedToo()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_vp9.mkv")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        Assert.Equal(MediaCodec.Vp9, Assert.Single(info.Tracks).Config.Codec);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public async Task Seek_LandsOnTheCuedClusterAndReplaysFromItsKeyframe()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("pattern_vp8_vorbis.webm")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var video = info.Tracks.Single(t => t.Kind == MediaTrackKind.Video);

        var all = await ReadAll(demuxer);
        Assert.Null(await demuxer.ReadPacketAsync(CancellationToken.None));

        // The fixture has one Cluster, so any seek replays it from the keyframe.
        await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        var again = await ReadAll(demuxer);
        Assert.Equal(all.Count, again.Count);
        var firstVideo = again.First(p => p.TrackId == video.Id);
        Assert.True(firstVideo.IsKeyframe);
        Assert.Equal(MediaTime.Zero, firstVideo.Pts);

        await demuxer.SeekAsync(MediaTime.Zero, CancellationToken.None);
        Assert.Equal(all.Count, (await ReadAll(demuxer)).Count);
    }

    [Fact]
    public void Ebml_ReadsIdsAndSizes()
    {
        Assert.True(Ebml.TryReadId([0x1A, 0x45, 0xDF, 0xA3], out uint id, out int idLength));
        Assert.Equal(EbmlId.EbmlHeader, id);
        Assert.Equal(4, idLength);
        Assert.True(Ebml.TryReadId([0xAE], out id, out idLength));
        Assert.Equal(EbmlId.TrackEntry, id);
        Assert.False(Ebml.TryReadId([0xFF], out _, out _));          // reserved
        Assert.False(Ebml.TryReadId([0x08, 0x00, 0x00, 0x00, 0x01], out _, out _)); // five bytes
        Assert.False(Ebml.TryReadId([0x00], out _, out _));

        Assert.True(Ebml.TryReadSize([0x81], out long? size, out int sizeLength));
        Assert.Equal(1, size);
        Assert.Equal(1, sizeLength);
        Assert.True(Ebml.TryReadSize([0x40, 0x02], out size, out sizeLength));
        Assert.Equal(2, size);
        Assert.Equal(2, sizeLength);
        Assert.True(Ebml.TryReadSize([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], out size, out sizeLength));
        Assert.Null(size); // unknown size
        Assert.Equal(8, sizeLength);
        Assert.False(Ebml.TryReadSize([0x40], out _, out _));       // truncated
    }

    [Theory]
    [InlineData("V_VP8", MediaTrackKind.Video, MediaCodec.Vp8)]
    [InlineData("V_VP9", MediaTrackKind.Video, MediaCodec.Vp9)]
    [InlineData("V_AV1", MediaTrackKind.Video, MediaCodec.Av1)]
    [InlineData("V_MPEG4/ISO/AVC", MediaTrackKind.Video, MediaCodec.H264)]
    [InlineData("V_THEORA", MediaTrackKind.Video, MediaCodec.Unknown)]
    [InlineData("A_OPUS", MediaTrackKind.Audio, MediaCodec.Opus)]
    [InlineData("A_VORBIS", MediaTrackKind.Audio, MediaCodec.Vorbis)]
    [InlineData("A_FLAC", MediaTrackKind.Audio, MediaCodec.Flac)]
    [InlineData("A_MPEG/L3", MediaTrackKind.Audio, MediaCodec.Mp3)]
    [InlineData("A_AAC/MPEG4/LC", MediaTrackKind.Audio, MediaCodec.Aac)]
    [InlineData("A_PCM/INT/LIT", MediaTrackKind.Audio, MediaCodec.Pcm)]
    [InlineData("A_AC3", MediaTrackKind.Audio, MediaCodec.Unknown)]
    public void CodecIds_MapToEngineNames(string codecId, MediaTrackKind kind, MediaCodec expected) =>
        Assert.Equal(expected, MatroskaDemuxer.MapCodec(codecId, kind).Codec);

    [Fact]
    public async Task TruncatedHeader_IsAFormatError()
    {
        var bytes = MediaFixtures.Read("pattern_vp9.webm").AsMemory(0, 60);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(bytes), context);
        await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OversizedVideoDimensions_HitTheLimit()
    {
        var bytes = MediaFixtures.Read("pattern_vp9.webm");
        var context = MediaPipelineContext.ForTests() with { Limits = MediaLimits.Default with { MaxVideoWidth = 32 } };
        await using var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(bytes), context);
        var error = await Assert.ThrowsAsync<MediaLimitExceededException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
        Assert.Equal("MaxVideoWidth", error.Limit);
    }

    [Fact]
    public void Probe_WantsTheEbmlMagic()
    {
        Assert.Equal(100, MatroskaDemuxerFactory.Instance.Probe([0x1A, 0x45, 0xDF, 0xA3, 0x9F]));
        Assert.Equal(0, MatroskaDemuxerFactory.Instance.Probe("OggS\0"u8));
        Assert.Equal(0, MatroskaDemuxerFactory.Instance.Probe([0x1A, 0x45]));
    }

    private static async Task<List<EncodedPacket>> ReadAll(IDemuxer demuxer)
    {
        var packets = new List<EncodedPacket>();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
            packets.Add(packet);
        return packets;
    }
}
