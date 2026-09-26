using FenBrowser.Media.Containers.Adts;
using Xunit;

namespace FenBrowser.Media.Fuzz;

public sealed class AdtsDemuxerFuzz
{
    private const int IterationsPerSeed = 2_500;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    [
        MediaFuzzCorpus.ReadFixture("sine_aac.aac"),
        [.. "ID3\x04\x00\x00\x00\x00\x00\x0A"u8.ToArray(), .. new byte[10], .. MediaFuzzCorpus.ReadFixture("sine_aac.aac")],
        // A frame claiming the maximum length with nothing behind it.
        [0xFF, 0xF1, 0x4C, 0x83, 0xFF, 0xFF, 0xFC, 0, 0, 0],
    ]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task Adts_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(AdtsDemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 12_000);

    [Fact]
    public void Adts_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(4 * IterationsPerSeed >= 10_000);

    [Fact]
    public void Header_NeverThrowsOnAnySevenBytes()
    {
        var random = new Random(5);
        var bytes = new byte[9];
        for (int i = 0; i < 200_000; i++)
        {
            random.NextBytes(bytes);
            bytes[0] = 0xFF;
            bytes[1] = (byte)(0xF0 | (bytes[1] & 0x0F));
            if (AdtsFrameHeader.TryParse(bytes.AsSpan(0, random.Next(7, 10)), out var header))
            {
                Assert.InRange(header.FrameLength, header.HeaderLength, 8191);
                Assert.InRange(header.Channels, 1, 8);
                Assert.InRange(header.RawDataBlocks, 1, 4);
            }
        }
    }
}
