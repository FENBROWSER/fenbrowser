using FenBrowser.Media.Containers.Flac;
using Xunit;

namespace FenBrowser.Media.Fuzz;

public sealed class FlacDemuxerFuzz
{
    private const int IterationsPerSeed = 2_500;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    {
        byte[] flac = MediaFuzzCorpus.ReadFixture("sine_flac.flac");
        // STREAMINFO claiming 8 channels of 32 bits and a huge block size, then the real frames.
        byte[] hostile = (byte[])flac.Clone();
        hostile[4 + 4 + 2] = 0xFF; hostile[4 + 4 + 3] = 0xFF;                 // max block size 65535
        hostile[4 + 4 + 12] |= 0x0E; hostile[4 + 4 + 13] |= 0xF0;              // channels 8, 32 bits
        return [flac, hostile, flac.AsSpan(0, 42).ToArray()];
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task Flac_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(FlacDemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 40_000);

    [Fact]
    public void Flac_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(4 * IterationsPerSeed >= 10_000);

    [Fact]
    public void FrameHeader_NeverThrowsOnRandomBytes()
    {
        var random = new Random(11);
        var bytes = new byte[FlacFrameHeader.MaxHeaderLength];
        for (int i = 0; i < 100_000; i++)
        {
            random.NextBytes(bytes);
            bytes[0] = 0xFF;
            bytes[1] = (byte)(0xF8 | (bytes[1] & 0x03));
            if (FlacFrameHeader.TryParse(bytes.AsSpan(0, random.Next(bytes.Length + 1)), out var header))
            {
                Assert.InRange(header.BlockSize, 1, 65536);
                Assert.InRange(header.Channels, 1, 8);
                Assert.InRange(header.HeaderLength, 6, FlacFrameHeader.MaxHeaderLength);
            }
        }
    }
}
