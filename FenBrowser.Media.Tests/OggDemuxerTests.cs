using System.Buffers.Binary;
using FenBrowser.Media.Containers.Ogg;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

public class OggDemuxerTests
{
    [Fact]
    public async Task Opus_DemuxesWithExactTimestamps()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = OggDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_opus.ogg")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Opus, track.Config.Codec);
        Assert.Equal(48000, track.Config.SampleRate);
        Assert.Equal(1, track.Config.Channels);
        Assert.Equal("OpusHead"u8.ToArray(), track.Config.Extradata.ToArray()[..8]);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 1);
        Assert.True(info.IsSeekable);

        int preSkip = BinaryPrimitives.ReadUInt16LittleEndian(track.Config.Extradata.Span[10..]);
        Assert.True(preSkip > 0);

        long samples = 0;
        int packets = 0;
        MediaTime lastEnd = MediaTime.NegativeInfinity;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                Assert.True(packet.HasPts);
                // The first packet starts before zero by the pre-skip; every packet is 20 ms.
                Assert.Equal(MediaTime.FromTimescale(samples - preSkip, 48000), packet.Pts);
                Assert.Equal(MediaTime.FromTimescale(960, 48000), packet.Duration);
                samples += 960;
                lastEnd = packet.Pts + packet.Duration;
                packets++;
            }
        }

        Assert.InRange(packets, 48, 52);
        Assert.InRange(lastEnd.TotalSeconds, 0.95, 1.05);
    }

    [Fact]
    public async Task Vorbis_DemuxesAndStampsTheFirstPacketOfEachPage()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = OggDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_vorbis.ogg")), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);

        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Vorbis, track.Config.Codec);
        Assert.Equal(48000, track.Config.SampleRate);
        Assert.Equal(1, track.Config.Channels);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 2);

        // Xiph lacing: 0x02, the two lacing lengths, then the three headers in order.
        var extradata = track.Config.Extradata.Span;
        Assert.Equal(2, extradata[0]);
        int at = 1;
        int idLength = 0;
        while (extradata[at] == 255) { idLength += 255; at++; }
        idLength += extradata[at++];
        int commentLength = 0;
        while (extradata[at] == 255) { commentLength += 255; at++; }
        commentLength += extradata[at++];
        Assert.Equal(1, extradata[at]);
        Assert.Equal("vorbis"u8.ToArray(), extradata.Slice(at + 1, 6).ToArray());
        Assert.Equal(3, extradata[at + idLength]);
        Assert.Equal(5, extradata[at + idLength + commentLength]);

        int packets = 0;
        int stamped = 0;
        MediaTime lastStamp = MediaTime.Zero;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                packets++;
                if (packet.HasPts)
                {
                    stamped++;
                    Assert.True(packet.Pts >= lastStamp, "stamps must not go backwards");
                    lastStamp = packet.Pts;
                }
            }
        }

        Assert.True(packets > 40, $"{packets} packets");
        // At least the first packet of the first page is stamped; a page whose first
        // packet continues from the previous page is not.
        Assert.True(stamped >= 1 && stamped <= packets, $"{stamped} stamped of {packets}");
        Assert.InRange(lastStamp.TotalSeconds, 0.0, 1.0);
    }

    [Fact]
    public async Task Seek_ResumesAtAPageAtOrBeforeTheTarget()
    {
        // The fixture is one page; repaginated at five packets (100 ms) a page the
        // bisection has something to bisect.
        byte[] bytes = Repaginate(MediaFixtures.Read("sine_opus.ogg"), packetsPerPage: 5);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = OggDemuxerFactory.Instance.Create(new MemoryByteSource(bytes), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 1);

        await demuxer.SeekAsync(MediaTime.FromSeconds(0.6), CancellationToken.None);
        using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.InRange(packet.Pts.TotalSeconds, 0.4, 0.6);

        await demuxer.SeekAsync(MediaTime.Zero, CancellationToken.None);
        using var first = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.True(first!.Pts <= MediaTime.Zero);

        await demuxer.SeekAsync(MediaTime.FromSeconds(100), CancellationToken.None);
        int rest = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } p)
        {
            p.Dispose();
            rest++;
        }

        Assert.True(rest < 10, $"{rest} packets after seeking past the end");
    }

    [Fact]
    public async Task BadCrcPage_IsSkipped()
    {
        byte[] bytes = MediaFixtures.Read("sine_opus.ogg");
        // Corrupt a byte inside the body of the fourth page.
        int page = 0;
        int at = 0;
        while (page < 3)
        {
            at = bytes.AsSpan(at + 1).IndexOf("OggS"u8) + at + 1;
            page++;
        }

        bytes[at + 40] ^= 0xFF;
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = OggDemuxerFactory.Instance.Create(new MemoryByteSource(bytes), context);
        await demuxer.InitializeAsync(CancellationToken.None);
        int packets = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            packet.Dispose();
            packets++;
        }

        Assert.InRange(packets, 1, 51);
    }

    [Theory]
    [InlineData(new byte[] { 0x0C }, 960)]    // config 1 (SILK NB 20 ms), code 0
    [InlineData(new byte[] { 0xFC }, 960)]    // config 31 (CELT FB 20 ms), code 0
    [InlineData(new byte[] { 0xE1 }, 240)]    // config 28 (CELT FB 2.5 ms), code 1: two frames
    [InlineData(new byte[] { 0x1B, 0x03 }, 8640)] // config 3 (60 ms), code 3, 3 frames → capped to 120 ms
    [InlineData(new byte[] { }, 0)]
    public void OpusPacketSamples_FollowTheToc(byte[] packet, int expected)
    {
        int samples = OggDemuxer.OpusPacketSamples(packet);
        Assert.Equal(Math.Min(expected, 5760), samples);
    }

    /// <summary>Rewrites an Ogg Opus file with the audio packets spread over pages of a given size.</summary>
    internal static byte[] Repaginate(byte[] input, int packetsPerPage)
    {
        var output = new List<byte>();
        var audio = new List<byte[]>();
        uint serial = 0;
        int at = 0;
        int pageIndex = 0;
        var partial = new List<byte>();
        while (at < input.Length)
        {
            Assert.True(OggPageHeader.TryParse(input.AsSpan(at), out var header, out var table));
            serial = header.Serial;
            int bodyAt = at + header.HeaderLength;
            if (pageIndex < 2)
            {
                output.AddRange(input.AsSpan(at, header.TotalLength).ToArray());
            }
            else
            {
                int offset = 0;
                foreach (byte lacing in table)
                {
                    partial.AddRange(input.AsSpan(bodyAt + offset, lacing).ToArray());
                    offset += lacing;
                    if (lacing < 255)
                    {
                        audio.Add([.. partial]);
                        partial.Clear();
                    }
                }
            }

            at += header.TotalLength;
            pageIndex++;
        }

        long granule = 0;
        uint sequence = 2;
        for (int i = 0; i < audio.Count; i += packetsPerPage)
        {
            var packets = audio.Skip(i).Take(packetsPerPage).ToList();
            foreach (var p in packets)
                granule += OggDemuxer.OpusPacketSamples(p);
            bool last = i + packetsPerPage >= audio.Count;
            output.AddRange(BuildPage(serial, sequence++, granule, packets, last));
        }

        return [.. output];
    }

    private static byte[] BuildPage(uint serial, uint sequence, long granule, List<byte[]> packets, bool endOfStream)
    {
        var table = new List<byte>();
        var body = new List<byte>();
        foreach (var packet in packets)
        {
            int remaining = packet.Length;
            while (remaining >= 255)
            {
                table.Add(255);
                remaining -= 255;
            }

            table.Add((byte)remaining);
            body.AddRange(packet);
        }

        var page = new byte[27 + table.Count + body.Count];
        "OggS"u8.CopyTo(page);
        page[5] = (byte)(endOfStream ? 0x04 : 0x00);
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)table.Count;
        table.CopyTo(page, 27);
        body.CopyTo(page, 27 + table.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc.Compute(page));
        return page;
    }

    [Fact]
    public void PageCrc_MatchesTheFixture()
    {
        byte[] bytes = MediaFixtures.Read("sine_vorbis.ogg");
        Assert.True(OggPageHeader.TryParse(bytes, out var header, out _));
        Assert.Equal(header.Crc, OggCrc.Compute(bytes.AsSpan(0, header.TotalLength)));
        Assert.True(header.IsBeginningOfStream);
    }

    [Fact]
    public async Task NotOgg_Throws()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = OggDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_pcm16.wav")), context);
        await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }
}
