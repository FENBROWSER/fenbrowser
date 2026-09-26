using FenBrowser.Media.Containers.Matroska;
using Xunit;

namespace FenBrowser.Media.Fuzz;

public sealed class MatroskaDemuxerFuzz
{
    private const int IterationsPerSeed = 2_000;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    {
        byte[] vp9 = MediaFuzzCorpus.ReadFixture("pattern_vp9.webm");
        byte[] both = MediaFuzzCorpus.ReadFixture("pattern_vp8_vorbis.webm");
        byte[] opus = MediaFuzzCorpus.ReadFixture("sine_opus.webm");
        return
        [
            vp9,
            both,
            opus,
            // Segment and Cluster of unknown size, as a live stream writes them.
            WithUnknownSizes(vp9),
            // A Block whose lacing head claims 256 frames.
            WithLacingFlag(both),
            vp9.AsSpan(0, 200).ToArray(),
        ];
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public Task Matroska_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(MatroskaDemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 24_000);

    [Fact]
    public void Matroska_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(5 * IterationsPerSeed >= 10_000);

    [Fact]
    public void ElementHeader_NeverThrowsOnRandomBytes()
    {
        var random = new Random(23);
        var bytes = new byte[Ebml.MaxHeaderLength];
        for (int i = 0; i < 200_000; i++)
        {
            random.NextBytes(bytes);
            int length = random.Next(bytes.Length + 1);
            if (Ebml.TryReadElement(bytes.AsSpan(0, length), 0, out var element))
            {
                Assert.InRange(element.HeaderLength, 2, Ebml.MaxHeaderLength);
                Assert.True(element.DataLength is null or >= 0);
            }

            _ = MatroskaDemuxer.TryParseBlockHead(bytes.AsSpan(0, length), out _);
        }
    }

    /// <summary>Rewrites the Segment's and every Cluster's size to the reserved "unknown" value.</summary>
    private static byte[] WithUnknownSizes(byte[] input)
    {
        byte[] bytes = (byte[])input.Clone();
        int at = 0;
        // EBML header, then the Segment.
        Ebml.TryReadElement(bytes, 0, out var header);
        at = (int)header.End!.Value;
        Ebml.TryReadElement(bytes.AsSpan(at), at, out var segment);
        int idLength = Ebml.TryReadId(bytes.AsSpan(at), out _, out int n) ? n : 4;
        MakeUnknown(bytes, at + idLength, segment.HeaderLength - idLength);
        int position = (int)segment.DataStart;
        while (position < bytes.Length && Ebml.TryReadElement(bytes.AsSpan(position), position, out var child) && child.DataLength is { } length)
        {
            if (child.Id == EbmlId.Cluster)
            {
                int clusterIdLength = Ebml.TryReadId(bytes.AsSpan(position), out _, out int m) ? m : 4;
                MakeUnknown(bytes, position + clusterIdLength, child.HeaderLength - clusterIdLength);
            }

            position = (int)(child.DataStart + length);
        }

        return bytes;
    }

    private static void MakeUnknown(byte[] bytes, int at, int sizeLength)
    {
        bytes[at] = (byte)((0x80 >> (sizeLength - 1)) | (0xFF >> sizeLength));
        for (int i = 1; i < sizeLength; i++)
            bytes[at + i] = 0xFF;
    }

    /// <summary>Sets the EBML-lacing flag on the first SimpleBlock so its first payload byte reads as a frame count.</summary>
    private static byte[] WithLacingFlag(byte[] input)
    {
        byte[] bytes = (byte[])input.Clone();
        int index = bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x1F, 0x43, 0xB6, 0x75]);
        if (index < 0)
            return bytes;
        int position = index;
        Ebml.TryReadElement(bytes.AsSpan(position), position, out var cluster);
        position = (int)cluster.DataStart;
        while (position < bytes.Length && Ebml.TryReadElement(bytes.AsSpan(position), position, out var child) && child.DataLength is { } length)
        {
            if (child.Id == EbmlId.SimpleBlock)
            {
                int flags = (int)child.DataStart + 3; // one-byte track number, int16 timestamp, flags
                bytes[flags] |= 0x06;
                bytes[flags + 1] = 0xFF;
                break;
            }

            position = (int)(child.DataStart + length);
        }

        return bytes;
    }
}
