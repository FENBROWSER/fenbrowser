using System.Buffers.Binary;
using FenBrowser.Media.Containers.Mp4;
using Xunit;

namespace FenBrowser.Media.Fuzz;

public sealed class Mp4DemuxerFuzz
{
    private const int IterationsPerSeed = 2_000;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    {
        byte[] progressive = MediaFuzzCorpus.ReadFixture("pattern_h264_aac.mp4");
        byte[] fragmented = MediaFuzzCorpus.ReadFixture("pattern_h264_fragmented.mp4");
        byte[] m4a = MediaFuzzCorpus.ReadFixture("sine_aac.m4a");
        return
        [
            progressive,
            fragmented,
            m4a,
            // Sample counts and chunk offsets pointing everywhere.
            WithTableCount(progressive, "stsz", 12, 0x7FFFFFFF),
            WithTableCount(progressive, "stco", 4, 0x00FFFFFF),
            // A moof whose run claims a data offset past the file.
            WithTableCount(fragmented, "trun", 4, 0x0FFFFFFF),
            progressive.AsSpan(0, 40).ToArray(),
        ];
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public Task Mp4_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(Mp4DemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 24_000);

    [Fact]
    public void Mp4_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(5 * IterationsPerSeed >= 10_000);

    [Fact]
    public void BoxHeader_NeverThrowsOnRandomBytes()
    {
        var random = new Random(31);
        var bytes = new byte[40];
        for (int i = 0; i < 200_000; i++)
        {
            random.NextBytes(bytes);
            int length = random.Next(bytes.Length + 1);
            if (Box.TryReadHeader(bytes.AsSpan(0, length), 0, long.MaxValue, out var header))
            {
                Assert.InRange(header.HeaderLength, 8, 32);
                Assert.True(header.Size >= header.HeaderLength);
            }
        }
    }

    /// <summary>Overwrites the entry count field of the first box named <paramref name="type"/>.</summary>
    private static byte[] WithTableCount(byte[] input, string type, int countOffsetInBody, uint count)
    {
        byte[] bytes = (byte[])input.Clone();
        int index = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(type));
        if (index >= 0 && index + 4 + countOffsetInBody + 4 <= bytes.Length)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(index + 4 + countOffsetInBody), count);
        return bytes;
    }
}
