using System.Buffers.Binary;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Codecs;
using FenBrowser.Media.Containers.Wav;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

public class WavDemuxerTests
{
    private sealed class Collect : IDecodeOutput<AudioBlock>
    {
        public List<AudioBlock> Blocks { get; } = [];
        public void Emit(AudioBlock item) => Blocks.Add(item);
    }

    [Fact]
    public async Task Fixture_DemuxesAndDecodesToTheManifestShape()
    {
        var fixture = MediaFixtures.Get("sine_pcm16.wav");
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read(fixture.File)), context);

        var info = await demuxer.InitializeAsync(CancellationToken.None);
        var track = Assert.Single(info.Tracks);
        Assert.Equal(MediaCodec.Pcm, track.Config.Codec);
        Assert.Equal(PcmSampleFormat.S16, track.Config.PcmFormat);
        Assert.Equal(fixture.Streams[0].SampleRate, track.Config.SampleRate);
        Assert.Equal(fixture.Streams[0].Channels, track.Config.Channels);
        Assert.Equal(fixture.DurationSeconds!.Value, info.Duration.TotalSeconds, 3);
        Assert.True(info.IsSeekable);

        await using var decoder = PcmDecoderFactory.Instance.Create(context);
        await decoder.ConfigureAsync(track.Config, CancellationToken.None);
        var output = new Collect();
        long frames = 0;
        long packetFrames = 0;
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                // Timestamps come from the frame index, not from summing rounded durations.
                Assert.Equal(MediaTime.FromTimescale(packetFrames, track.Config.SampleRate), packet.Pts);
                packetFrames += packet.Length / 2;
                await decoder.DecodeAsync(packet, output, CancellationToken.None);
            }
        }

        foreach (var block in output.Blocks)
        {
            frames += block.FrameCount;
            Assert.All(block.Samples.ToArray(), s => Assert.InRange(s, -1f, 1f));
        }

        Assert.Equal(48000, packetFrames);
        Assert.Equal(48000, frames);
        // ffmpeg's sine source has amplitude 1/8: the peak is there, and it is not louder.
        float peak = output.Blocks.Max(b => b.Samples.ToArray().Max());
        Assert.InRange(peak, 0.12f, 0.13f);
        foreach (var block in output.Blocks)
            block.Dispose();
    }

    [Fact]
    public async Task Seek_LandsOnTheExactFrame()
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read("sine_pcm16.wav")), context);
        await demuxer.InitializeAsync(CancellationToken.None);

        await demuxer.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.NotNull(packet);
        Assert.Equal(MediaTime.FromSeconds(0.5), packet.Pts);

        await demuxer.SeekAsync(MediaTime.FromSeconds(99), CancellationToken.None);
        Assert.Null(await demuxer.ReadPacketAsync(CancellationToken.None));

        await demuxer.SeekAsync(MediaTime.FromSeconds(-1), CancellationToken.None);
        using var first = await demuxer.ReadPacketAsync(CancellationToken.None);
        Assert.Equal(MediaTime.Zero, first!.Pts);
    }

    [Theory]
    [InlineData(PcmSampleFormat.U8, 1, 8)]
    [InlineData(PcmSampleFormat.S16, 1, 16)]
    [InlineData(PcmSampleFormat.S24, 1, 24)]
    [InlineData(PcmSampleFormat.S32, 1, 32)]
    [InlineData(PcmSampleFormat.F32, 3, 32)]
    [InlineData(PcmSampleFormat.F64, 3, 64)]
    public async Task EveryFormat_RoundTripsFullScale(PcmSampleFormat format, int tag, int bits)
    {
        // Two frames, stereo: +full scale then -full scale on every channel.
        byte[] samples = new byte[2 * 2 * (bits / 8)];
        for (int i = 0; i < 4; i++)
        {
            double value = i < 2 ? 1.0 : -1.0;
            var dest = samples.AsSpan(i * (bits / 8));
            switch (format)
            {
                case PcmSampleFormat.U8: dest[0] = (byte)(value > 0 ? 255 : 0); break;
                case PcmSampleFormat.S16: BinaryPrimitives.WriteInt16LittleEndian(dest, (short)(value > 0 ? 32767 : -32768)); break;
                case PcmSampleFormat.S24: int v24 = value > 0 ? 8388607 : -8388608; dest[0] = (byte)v24; dest[1] = (byte)(v24 >> 8); dest[2] = (byte)(v24 >> 16); break;
                case PcmSampleFormat.S32: BinaryPrimitives.WriteInt32LittleEndian(dest, value > 0 ? int.MaxValue : int.MinValue); break;
                case PcmSampleFormat.F32: BinaryPrimitives.WriteSingleLittleEndian(dest, (float)value); break;
                case PcmSampleFormat.F64: BinaryPrimitives.WriteDoubleLittleEndian(dest, value); break;
            }
        }

        byte[] wav = BuildWav(tag, channels: 2, sampleRate: 8000, bits, samples, extraChunkBeforeFmt: true);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(wav), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        Assert.Equal(format, info.Tracks[0].Config.PcmFormat);
        Assert.Equal(MediaTime.FromTimescale(2, 8000), info.Duration);

        await using var decoder = PcmDecoderFactory.Instance.Create(context);
        await decoder.ConfigureAsync(info.Tracks[0].Config, CancellationToken.None);
        var output = new Collect();
        using var packet = await demuxer.ReadPacketAsync(CancellationToken.None);
        await decoder.DecodeAsync(packet!, output, CancellationToken.None);

        var block = Assert.Single(output.Blocks);
        float[] decoded = block.Samples.ToArray();
        Assert.Equal(4, decoded.Length);
        Assert.True(decoded[0] > 0.99f && decoded[1] > 0.99f, $"{format}: {decoded[0]}, {decoded[1]}");
        Assert.True(decoded[2] < -0.99f && decoded[3] < -0.99f, $"{format}: {decoded[2]}, {decoded[3]}");
        block.Dispose();
    }

    [Fact]
    public async Task TruncatedData_PlaysWholeFramesOnly()
    {
        byte[] wav = BuildWav(1, channels: 2, sampleRate: 8000, bits: 16, new byte[4 * 4 + 1], extraChunkBeforeFmt: false);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(wav), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        Assert.Equal(MediaTime.FromTimescale(4, 8000), info.Duration);

        // The declared data size exceeds what the file holds: whole frames only.
        byte[] lying = (byte[])wav.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(lying.AsSpan(lying.Length - 17 - 4), 1_000_000);
        await using var lyingDemuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(lying), context);
        var lyingInfo = await lyingDemuxer.InitializeAsync(CancellationToken.None);
        Assert.Equal(MediaTime.FromTimescale(4, 8000), lyingInfo.Duration);
    }

    [Theory]
    [InlineData("RIFF....WAVE", "No fmt chunk.")]
    [InlineData("RIFX....WAVE", "Not a RIFF/WAVE resource.")]
    public async Task Malformed_ThrowsMediaFormatException(string header, string expected)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(header);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(bytes), context);
        var ex = await Assert.ThrowsAsync<MediaFormatException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public async Task HostileChannelCount_HitsTheLimitBeforeAllocating()
    {
        byte[] wav = BuildWav(1, channels: 64, sampleRate: 8000, bits: 16, new byte[0], extraChunkBeforeFmt: false);
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = WavDemuxerFactory.Instance.Create(new MemoryByteSource(wav), context);
        await Assert.ThrowsAsync<MediaLimitExceededException>(async () => await demuxer.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public void Probe_AnswersOnlyForRiffWave()
    {
        Assert.Equal(100, WavDemuxerFactory.Instance.Probe("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Equal(0, WavDemuxerFactory.Instance.Probe("RIFF\0\0\0\0AVI "u8));
        Assert.Equal(0, WavDemuxerFactory.Instance.Probe("RIFF"u8));
        Assert.Equal(0, WavDemuxerFactory.Instance.Probe(ReadOnlySpan<byte>.Empty));
    }

    internal static byte[] BuildWav(int formatTag, int channels, int sampleRate, int bits, byte[] samples, bool extraChunkBeforeFmt)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0); // patched below
        writer.Write("WAVE"u8);
        if (extraChunkBeforeFmt)
        {
            writer.Write("LIST"u8);
            writer.Write(5);
            writer.Write("INFO\0"u8);
            writer.Write((byte)0); // pad byte for the odd size
        }

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)formatTag);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * channels * bits / 8));
        writer.Write((ushort)(channels * bits / 8));
        writer.Write((ushort)bits);
        writer.Write("data"u8);
        writer.Write((uint)samples.Length);
        writer.Write(samples);
        writer.Flush();
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }
}
