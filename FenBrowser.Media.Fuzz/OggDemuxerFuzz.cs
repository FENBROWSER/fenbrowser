using System.Buffers.Binary;
using FenBrowser.Media.Containers.Ogg;
using Xunit;

namespace FenBrowser.Media.Fuzz;

public sealed class OggDemuxerFuzz
{
    private const int IterationsPerSeed = 2_500;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    {
        byte[] opus = MediaFuzzCorpus.ReadFixture("sine_opus.ogg");
        byte[] vorbis = MediaFuzzCorpus.ReadFixture("sine_vorbis.ogg");
        return
        [
            opus,
            vorbis,
            WithCrcFixed(opus, FlipHeaderByte),
            // A page claiming 255 segments of 255 bytes with nothing behind it.
            BuildHostilePage(),
        ];
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task Ogg_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(OggDemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 24_000);

    [Fact]
    public void Ogg_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(4 * IterationsPerSeed >= 10_000);

    [Fact]
    public void PageHeader_NeverThrowsOnShortInput()
    {
        var page = new byte[300];
        "OggS"u8.CopyTo(page);
        for (int length = 0; length < page.Length; length++)
        {
            page[26] = (byte)(length % 256);
            _ = OggPageHeader.TryParse(page.AsSpan(0, length), out _, out _);
        }
    }

    /// <summary>Mutations that keep the CRC valid reach past the CRC check into the packet layer.</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Ogg_MutatedBodiesWithValidCrcs(int seed)
    {
        var random = new Random(seed);
        byte[] opus = MediaFuzzCorpus.ReadFixture("sine_opus.ogg");
        byte[] vorbis = MediaFuzzCorpus.ReadFixture("sine_vorbis.ogg");
        var seeds = new List<byte[]>();
        for (int i = 0; i < 40; i++)
        {
            byte[] parent = (i & 1) == 0 ? opus : vorbis;
            seeds.Add(WithCrcFixed(parent, bytes =>
            {
                for (int m = 0; m < 4; m++)
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }));
        }

        await DemuxerFuzz.RunAsync(OggDemuxerFactory.Instance, seeds, seed, 250, maxLength: 24_000);
    }

    private static void FlipHeaderByte(byte[] bytes)
    {
        // The Opus channel count in the first page's body.
        bytes[28 + 9] = 0;
    }

    private static byte[] WithCrcFixed(byte[] input, Action<byte[]> mutate)
    {
        byte[] bytes = (byte[])input.Clone();
        mutate(bytes);
        int at = 0;
        while (at < bytes.Length && OggPageHeader.TryParse(bytes.AsSpan(at), out var header, out _))
        {
            int total = Math.Min(header.TotalLength, bytes.Length - at);
            if (total < header.TotalLength)
                break;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 22), OggCrc.Compute(bytes.AsSpan(at, total)));
            at += total;
        }

        return bytes;
    }

    private static byte[] BuildHostilePage()
    {
        var page = new byte[27 + 255 + 40];
        "OggS"u8.CopyTo(page);
        page[5] = 0x02;
        page[26] = 255;
        Array.Fill(page, (byte)255, 27, 255);
        "OpusHead"u8.CopyTo(page.AsSpan(27 + 255));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc.Compute(page));
        return page;
    }
}
