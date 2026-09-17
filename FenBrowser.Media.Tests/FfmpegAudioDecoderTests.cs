using FenBrowser.Media.Buffers;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The libavcodec adapter against the generated fixtures. Every fixture is a 440 Hz sine
/// at amplitude 1/8 for one second, so a correct decode has about a second of samples
/// whose peak sits near 0.125 and whose zero crossings come 440 times a second.
/// </summary>
public class FfmpegAudioDecoderTests
{
    private sealed class Collect : IDecodeOutput<AudioBlock>
    {
        public List<AudioBlock> Blocks { get; } = [];
        public void Emit(AudioBlock item) => Blocks.Add(item);
    }

    private static DecoderRegistry Registry()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "libavcodec must be available on the development machine (winget Gyan.FFmpeg.Shared)");
        return decoders;
    }

    private static DemuxerRegistry Demuxers()
    {
        var demuxers = new DemuxerRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, new DecoderRegistry());
        return demuxers;
    }

    [Fact]
    public void Library_LoadsAndPinsTheMajor()
    {
        Assert.True(FfmpegLibrary.TryLoad(out string reason), reason);
        Assert.True(FfmpegLibrary.Location.Contains("libavcodec 63.", StringComparison.Ordinal), FfmpegLibrary.Location);
    }

    [Theory]
    [InlineData("sine_opus.ogg", 48000, 1)]
    [InlineData("sine_vorbis.ogg", 48000, 1)]
    [InlineData("sine_flac.flac", 48000, 1)]
    [InlineData("sine_mp3_noid3.mp3", 44100, 1)]
    [InlineData("sine_mp3_id3.mp3", 44100, 1)]
    [InlineData("sine_mp3_mpeg2.mp3", 22050, 1)]
    public async Task Fixture_DecodesToOneSecondOfSine(string file, int sampleRate, int channels)
    {
        var context = MediaPipelineContext.ForTests();
        byte[] bytes = MediaFixtures.Read(file);
        var factory = Demuxers().Select(bytes.AsSpan(0, Math.Min(bytes.Length, 1445)), null, context);
        Assert.NotNull(factory);
        await using var demuxer = factory.Create(new MemoryByteSource(bytes), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var config = info.Tracks[0].Config;

        var decoder = await DecoderSelector.SelectAsync(Registry().GetAudioCandidates(config), config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using (decoder)
        {
            var output = new Collect();
            while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
            {
                using (packet)
                    await decoder.DecodeAsync(packet, output, CancellationToken.None);
            }

            await decoder.DrainAsync(output, CancellationToken.None);

            Assert.NotEmpty(output.Blocks);
            Assert.All(output.Blocks, b => Assert.Equal(sampleRate, b.SampleRate));
            Assert.All(output.Blocks, b => Assert.Equal(channels, b.Channels));

            var samples = new List<float>();
            MediaTime? expectedNext = null;
            foreach (var block in output.Blocks)
            {
                if (expectedNext is { } next)
                    Assert.True(Math.Abs((block.Timestamp - next).Microseconds) <= 2, $"gap before block at {block.Timestamp}, expected {next}");
                expectedNext = block.EndTime;
                samples.AddRange(block.Samples.ToArray());
                block.Dispose();
            }

            // About a second: encoders pad the last frame, MP3 adds decoder delay.
            Assert.InRange(samples.Count / (double)sampleRate, 0.95, 1.15);
            Assert.True(output.Blocks[0].Timestamp >= MediaTime.FromMicroseconds(-100), $"first block at {output.Blocks[0].Timestamp}");

            // The middle half-second is steady state: check amplitude and frequency there.
            int from = sampleRate / 4;
            int to = Math.Min(samples.Count, sampleRate * 3 / 4);
            float peak = 0;
            int crossings = 0;
            for (int i = from + 1; i < to; i++)
            {
                peak = Math.Max(peak, Math.Abs(samples[i]));
                if ((samples[i - 1] < 0) != (samples[i] < 0))
                    crossings++;
            }

            Assert.InRange(peak, 0.10f, 0.15f);
            // 440 Hz over half a second: 440 cycles, two crossings each.
            Assert.InRange(crossings, 400, 480);
        }
    }

    [Fact]
    public async Task Reset_AllowsDecodingFromASeekPoint()
    {
        var context = MediaPipelineContext.ForTests();
        byte[] bytes = MediaFixtures.Read("sine_flac.flac");
        await using var demuxer = Demuxers().Select(bytes, null, context)!.Create(new MemoryByteSource(bytes), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var config = info.Tracks[0].Config;
        var decoder = await DecoderSelector.SelectAsync(Registry().GetAudioCandidates(config), config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using (decoder)
        {
            var first = new Collect();
            using (var packet = await demuxer.ReadPacketAsync(CancellationToken.None))
                await decoder.DecodeAsync(packet!, first, CancellationToken.None);
            Assert.NotEmpty(first.Blocks);
            first.Blocks.ForEach(b => b.Dispose());

            await decoder.ResetAsync(CancellationToken.None);
            await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
            var after = new Collect();
            using (var packet = await demuxer.ReadPacketAsync(CancellationToken.None))
                await decoder.DecodeAsync(packet!, after, CancellationToken.None);
            var block = Assert.Single(after.Blocks);
            Assert.InRange(block.Timestamp.TotalSeconds, 0.4, 0.5);
            block.Dispose();
        }
    }

    [Fact]
    public async Task Garbage_IsRejectedNotFatal()
    {
        var context = MediaPipelineContext.ForTests();
        var config = CodecConfig.Audio(MediaCodec.Mp3, 44100, 2, "mp3");
        var decoder = await DecoderSelector.SelectAsync(Registry().GetAudioCandidates(config), config, context, CancellationToken.None);
        Assert.NotNull(decoder);
        await using (decoder)
        {
            var output = new Collect();
            using var junk = EncodedPacket.Copy(MediaLimits.Default, MediaTrackKind.Audio, 0, new byte[500], MediaTime.Zero, MediaTime.Zero, MediaTime.Zero, true);
            await decoder.DecodeAsync(junk, output, CancellationToken.None);
            await decoder.DrainAsync(output, CancellationToken.None);
            output.Blocks.ForEach(b => b.Dispose());
        }
    }

    [Fact]
    public void CanPlayType_AnswersProbablyForRegisteredCodecs()
    {
        var demuxers = Demuxers();
        var support = new Types.MediaTypeSupport(demuxers, Registry());
        Assert.Equal(Types.CanPlayTypeResult.Probably, support.CanPlayType("audio/ogg; codecs=opus"));
        Assert.Equal(Types.CanPlayTypeResult.Probably, support.CanPlayType("audio/ogg; codecs=vorbis"));
        Assert.Equal(Types.CanPlayTypeResult.Probably, support.CanPlayType("audio/flac; codecs=flac"));
        Assert.Equal(Types.CanPlayTypeResult.Probably, support.CanPlayType("audio/mpeg; codecs=mp3"));
        // Containers that imply their codec answer for it; Ogg could hold either.
        Assert.Equal(Types.CanPlayTypeResult.Probably, support.CanPlayType("audio/flac"));
        Assert.Equal(Types.CanPlayTypeResult.Probably, support.CanPlayType("audio/mpeg"));
        Assert.Equal(Types.CanPlayTypeResult.Maybe, support.CanPlayType("audio/ogg"));
        Assert.Equal(Types.CanPlayTypeResult.No, support.CanPlayType("audio/aac; codecs=mp4a.40.2"));
        Assert.Equal(Types.CanPlayTypeResult.No, support.CanPlayType("video/webm; codecs=vp9"));
    }
}
