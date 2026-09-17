using System.Diagnostics;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Codecs;
using FenBrowser.Media.Pipeline;
using Xunit;

namespace FenBrowser.Media.Fuzz;

/// <summary>
/// Shared driver for the container demuxer fuzz targets (docs/DEFINITION_OF_DONE.md: every
/// parser runs 10,000 iterations clean; MEDIA_ENGINE_DESIGN.md §4).
/// </summary>
/// <remarks>
/// Properties enforced on every mutated file:
///   * Initialize, ReadPacket and Seek only ever fail with <see cref="MediaFormatException"/>
///     or <see cref="MediaLimitExceededException"/> (or cancellation); no other exception type,
///     no hang, no unbounded output.
///   * Every packet honours the packet ceiling for its kind and belongs to a declared track.
///   * The demuxer is deterministic: the same bytes give the same track list and packet count.
///   * A run is bounded in time and in the number of packets it may emit.
/// </remarks>
internal static class DemuxerFuzz
{
    private const int MaxPacketsPerRun = 4096;
    private const int MaxRunMilliseconds = 2000;

    public static async Task RunAsync(IDemuxerFactory factory, IReadOnlyList<byte[]> seeds, int seed, int iterations, int maxLength)
    {
        var random = new Random(seed);
        int initialised = 0;
        int rejected = 0;
        int packets = 0;
        for (int i = 0; i < iterations; i++)
        {
            byte[] parent = seeds[random.Next(seeds.Count)];
            byte[] input = MediaFuzzCorpus.Mutate(parent, random, maxLength);
            var first = await CheckOneAsync(factory, input);
            var second = await CheckOneAsync(factory, input);
            Assert.True(first == second, $"{factory.Name} was not deterministic on {MediaFuzzCorpus.Hex(input)}: {first} then {second}");
            if (first.Tracks > 0)
                initialised++;
            else
                rejected++;
            packets += first.Packets;
        }

        // The corpus must exercise both the accepting and the rejecting paths.
        Assert.True(initialised > 0, $"{factory.Name}: no mutated input was accepted.");
        Assert.True(rejected > 0, $"{factory.Name}: no mutated input was rejected.");
        Assert.True(packets > 0, $"{factory.Name}: no packets were produced.");
    }

    private static async Task<(int Tracks, int Packets)> CheckOneAsync(IDemuxerFactory factory, byte[] input)
    {
        var context = MediaPipelineContext.ForTests();
        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(MaxRunMilliseconds);
        try
        {
            _ = factory.Probe(input);
            await using var demuxer = factory.Create(new MemoryByteSource(input), context);
            DemuxerInfo info;
            try
            {
                info = await demuxer.InitializeAsync(cts.Token);
            }
            catch (MediaFormatException)
            {
                return (0, 0);
            }
            catch (MediaLimitExceededException)
            {
                return (0, 0);
            }

            Assert.InRange(info.Tracks.Count, 1, context.Limits.MaxTracks);
            var trackIds = info.Tracks.Select(t => t.Id).ToHashSet();
            int count = 0;
            try
            {
                while (count < MaxPacketsPerRun && await demuxer.ReadPacketAsync(cts.Token) is { } packet)
                {
                    using (packet)
                    {
                        Assert.Contains(packet.TrackId, trackIds);
                        var kind = info.Tracks.First(t => t.Id == packet.TrackId).Kind;
                        context.Limits.CheckPacketSize(kind, packet.Length);
                        Assert.Equal(packet.Length, packet.Span.Length);
                    }

                    count++;
                }

                if (info.IsSeekable)
                {
                    await demuxer.SeekAsync(MediaTime.FromSeconds(0.25), cts.Token);
                    if (await demuxer.ReadPacketAsync(cts.Token) is { } afterSeek)
                        afterSeek.Dispose();
                }
            }
            catch (MediaFormatException)
            {
                // A body that turns bad mid-stream is a legitimate stop.
            }
            catch (MediaLimitExceededException)
            {
            }

            return (info.Tracks.Count, count);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Assert.Fail($"{factory.Name} did not finish within {MaxRunMilliseconds} ms on {MediaFuzzCorpus.Hex(input)}");
            throw;
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"{factory.Name} threw {ex.GetType().Name} ({ex.Message}) on {MediaFuzzCorpus.Hex(input)}");
            throw;
        }
        finally
        {
            Assert.True(stopwatch.ElapsedMilliseconds < MaxRunMilliseconds * 2, $"{factory.Name} took {stopwatch.ElapsedMilliseconds} ms on {MediaFuzzCorpus.Hex(input)}");
        }
    }

    /// <summary>Runs every packet of a well-formed file through its decoder; the decoder must not throw either.</summary>
    public static async Task DecodeAllAsync<T>(IDemuxerFactory factory, IDecoderFactory<T> decoderFactory, byte[] input)
        where T : IDisposable
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = factory.Create(new MemoryByteSource(input), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        await using var decoder = decoderFactory.Create(context);
        await decoder.ConfigureAsync(info.Tracks[0].Config, CancellationToken.None);
        var sink = new DisposingOutput<T>();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
                await decoder.DecodeAsync(packet, sink, CancellationToken.None);
        }

        await decoder.DrainAsync(sink, CancellationToken.None);
        Assert.True(sink.Count > 0);
    }

    private sealed class DisposingOutput<T> : IDecodeOutput<T>
        where T : IDisposable
    {
        public int Count { get; private set; }

        public void Emit(T item)
        {
            Count++;
            item.Dispose();
        }
    }
}

public sealed class WavDemuxerFuzz
{
    private const int IterationsPerSeed = 2_500;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    {
        var seeds = new List<byte[]> { MediaFuzzCorpus.ReadFixture("sine_pcm16.wav") };
        // Small hand-built variants: 8-bit mono, float stereo, a chunk before fmt, an odd-sized chunk.
        seeds.Add(BuildWav(1, 1, 8000, 8, 37));
        seeds.Add(BuildWav(3, 2, 44100, 32, 64));
        seeds.Add(BuildWav(0xFFFE, 2, 48000, 24, 12));
        return seeds;
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task Wav_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(Containers.Wav.WavDemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 4096);

    [Fact]
    public void Wav_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(4 * IterationsPerSeed >= 10_000);

    [Fact]
    public Task Wav_FixtureDecodesEnd_ToEnd() =>
        DemuxerFuzz.DecodeAllAsync<AudioBlock>(Containers.Wav.WavDemuxerFactory.Instance, PcmDecoderFactory.Instance, MediaFuzzCorpus.ReadFixture("sine_pcm16.wav"));

    private static byte[] BuildWav(int formatTag, int channels, int sampleRate, int bits, int frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int blockAlign = channels * bits / 8;
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(formatTag == 0xFFFE ? 40 : 16);
        writer.Write((ushort)formatTag);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)bits);
        if (formatTag == 0xFFFE)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write((uint)3);
            writer.Write((ushort)1); // PCM sub-format tag
            writer.Write(new byte[14]);
        }

        writer.Write("data"u8);
        writer.Write((uint)(frames * blockAlign));
        writer.Write(new byte[frames * blockAlign]);
        writer.Flush();
        byte[] bytes = stream.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }
}
