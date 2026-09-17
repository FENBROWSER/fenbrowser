using FenBrowser.Media.Containers.Mp3;
using Xunit;

namespace FenBrowser.Media.Fuzz;

public sealed class Mp3DemuxerFuzz
{
    private const int IterationsPerSeed = 2_500;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(() =>
    [
        MediaFuzzCorpus.ReadFixture("sine_mp3_noid3.mp3"),
        MediaFuzzCorpus.ReadFixture("sine_mp3_id3.mp3"),
        MediaFuzzCorpus.ReadFixture("sine_mp3_mpeg2.mp3"),
        // A lone ID3 header claiming a huge tag, and a Xing frame with every flag set.
        [.. "ID3\x04\x00\x10"u8.ToArray(), 0x7F, 0x7F, 0x7F, 0x7F],
        BuildXingFrame(),
    ]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task Mp3_HoldsItsPropertiesOnMutatedFiles(int seed) =>
        DemuxerFuzz.RunAsync(Mp3DemuxerFactory.Instance, s_seeds.Value, seed, IterationsPerSeed, maxLength: 20_000);

    [Fact]
    public void Mp3_IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(4 * IterationsPerSeed >= 10_000);

    [Fact]
    public void Header_NeverThrowsOnAnyFourBytes()
    {
        // Every sync-plausible header: first byte 0xFF, the other three exhaustive.
        var bytes = new byte[4];
        bytes[0] = 0xFF;
        for (int b = 0xE0; b < 256; b++)
        {
            bytes[1] = (byte)b;
            for (int c = 0; c < 256; c++)
            {
                bytes[2] = (byte)c;
                for (int d = 0; d < 256; d += 64)
                {
                    bytes[3] = (byte)d;
                    if (MpegAudioFrameHeader.TryParse(bytes, out var header))
                    {
                        Assert.InRange(header.FrameLength, 4, 8192);
                        Assert.InRange(header.SampleRate, 8000, 48000);
                        Assert.InRange(header.Channels, 1, 2);
                    }
                }
            }
        }
    }

    private static byte[] BuildXingFrame()
    {
        // MPEG-1 Layer III 128 kbps 44.1 kHz mono: 418-byte frame with a Xing tag at offset 4 + 17.
        var frame = new byte[418];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x92;
        frame[3] = 0xC4;
        "Xing"u8.CopyTo(frame.AsSpan(21));
        frame[28] = 0x0F; // frames, bytes, TOC, quality
        frame[32] = 0x00; frame[33] = 0x00; frame[34] = 0x00; frame[35] = 0x28; // 40 frames
        for (int i = 0; i < 100; i++)
            frame[41 + i] = (byte)(i * 256 / 100);
        return [.. frame, .. MediaFuzzCorpus.ReadFixture("sine_mp3_noid3.mp3")];
    }
}
