using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Sniffing;
using Xunit;

namespace FenBrowser.Media.Fuzz;

// Fuzz target for the MIME sniffing parser and the demuxer probe path
// (docs/DEFINITION_OF_DONE.md: every parser runs 10,000 fuzz iterations clean).
//
// Properties enforced on every input:
//   * Sniff never throws, whatever the bytes.
//   * The result is null or one of the eight MIME types the spec table can produce.
//   * The result is deterministic, and depends only on the resource header.
//   * A result that names a signature agrees with that signature's own predicate.
//   * DemuxerRegistry.Select never throws, even when a probe does.
public sealed class SnifferFuzz
{
    private const int IterationsPerSeed = 2_500;

    private static readonly HashSet<string?> s_allowed =
    [
        null,
        MediaSniffer.Aiff, MediaSniffer.Mpeg, MediaSniffer.Ogg, MediaSniffer.Midi,
        MediaSniffer.Avi, MediaSniffer.Wave, MediaSniffer.Mp4, MediaSniffer.WebM,
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Sniff_HoldsItsPropertiesOnMutatedHeaders(int seed)
    {
        var random = new Random(seed);
        var seen = new HashSet<string>();
        for (int i = 0; i < IterationsPerSeed; i++)
        {
            byte[] parent = MediaFuzzCorpus.Seeds[random.Next(MediaFuzzCorpus.Seeds.Count)];
            byte[] input = MediaFuzzCorpus.Mutate(parent, random);
            seen.Add(CheckSniff(input) ?? "undefined");
        }

        // The mutations must keep reaching every signature branch, or the run proves little.
        foreach (string expected in (string[])["undefined", MediaSniffer.Mp4, MediaSniffer.WebM, MediaSniffer.Mpeg, MediaSniffer.Ogg, MediaSniffer.Wave])
            Assert.Contains(expected, seen);
    }

    [Fact]
    public void Sniff_IterationBudgetMeetsTheDefinitionOfDone()
    {
        // Four seeds × IterationsPerSeed must stay at or above the 10,000 required for parsers.
        Assert.True(4 * IterationsPerSeed >= 10_000);
    }

    [Fact]
    public void Sniff_HandlesEveryShortInputExhaustively()
    {
        // Every 0-, 1- and 2-byte input, plus every 3-byte input whose first byte is a lead byte.
        CheckSniff([]);
        var buffer = new byte[3];
        for (int a = 0; a < 256; a++)
        {
            buffer[0] = (byte)a;
            CheckSniff(buffer.AsSpan(0, 1).ToArray());
            for (int b = 0; b < 256; b++)
            {
                buffer[1] = (byte)b;
                CheckSniff(buffer.AsSpan(0, 2).ToArray());
            }
        }

        foreach (byte lead in (byte[])[0xFF, 0x1A, 0x49, 0x4F, 0x52, 0x46, 0x4D])
        {
            buffer[0] = lead;
            for (int b = 0; b < 256; b++)
            {
                for (int c = 0; c < 256; c++)
                {
                    buffer[1] = (byte)b;
                    buffer[2] = (byte)c;
                    CheckSniff(buffer);
                }
            }
        }
    }

    [Fact]
    public void Sniff_IgnoresBytesPastTheResourceHeader()
    {
        var random = new Random(99);
        for (int i = 0; i < 500; i++)
        {
            byte[] header = MediaFuzzCorpus.Mutate(MediaFuzzCorpus.Seeds[random.Next(MediaFuzzCorpus.Seeds.Count)], random);
            if (header.Length < MediaSniffer.ResourceHeaderLength)
                continue;

            byte[] longer = new byte[header.Length + 4096];
            header.CopyTo(longer, 0);
            random.NextBytes(longer.AsSpan(header.Length));

            Assert.Equal(
                MediaSniffer.Sniff(header),
                MediaSniffer.Sniff(longer.AsSpan(0, MediaSniffer.ResourceHeaderLength)));
        }
    }

    private sealed class ProbeFactory(string name, Func<byte[], int> probe) : IDemuxerFactory
    {
        public string Name => name;
        public IReadOnlyList<string> MimeTypes => ["video/webm", "video/mp4"];
        public int Probe(ReadOnlySpan<byte> header) => probe(header.ToArray());
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    [Fact]
    public void DemuxerSelect_NeverThrowsOnMutatedHeaders()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new ProbeFactory("sniff-webm", h => MediaSniffer.MatchesWebMSignature(h) ? 100 : 0));
        registry.Register(new ProbeFactory("sniff-mp4", h => MediaSniffer.MatchesMp4Signature(h) ? 100 : 0));
        registry.Register(new ProbeFactory("reads-past-end", h => h[h.Length / 2 + 8] == 0 ? 10 : 0));
        registry.Register(new ProbeFactory("out-of-range", h => h.Length * 1000 - 5000));

        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, NullMediaLogSink.Instance);
        var random = new Random(7);
        for (int i = 0; i < 10_000; i++)
        {
            byte[] input = MediaFuzzCorpus.Mutate(MediaFuzzCorpus.Seeds[random.Next(MediaFuzzCorpus.Seeds.Count)], random);
            try
            {
                var chosen = registry.Select(input, random.Next(2) == 0 ? "video/mp4" : null, context);
                if (chosen is not null)
                    Assert.Contains(chosen, registry.Factories);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Select threw {ex.GetType().Name} for input {MediaFuzzCorpus.Hex(input)}");
            }
        }
    }

    private static string? CheckSniff(byte[] input)
    {
        string? first;
        try
        {
            first = MediaSniffer.Sniff(input);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Sniff threw {ex.GetType().Name} for input {MediaFuzzCorpus.Hex(input)}");
            return null;
        }

        if (!s_allowed.Contains(first))
            Assert.Fail($"Sniff returned '{first}' for input {MediaFuzzCorpus.Hex(input)}");

        if (MediaSniffer.Sniff(input) != first)
            Assert.Fail($"Sniff is not deterministic for input {MediaFuzzCorpus.Hex(input)}");

        // The signature rows run after the pattern table, so a signature result must match
        // its predicate; a pattern-table result may shadow a signature, never the reverse.
        bool consistent = first switch
        {
            MediaSniffer.Mp4 => MediaSniffer.MatchesMp4Signature(input),
            MediaSniffer.WebM => MediaSniffer.MatchesWebMSignature(input) && !MediaSniffer.MatchesMp4Signature(input),
            null => !MediaSniffer.MatchesMp4Signature(input)
                && !MediaSniffer.MatchesWebMSignature(input)
                && !MediaSniffer.MatchesMp3WithoutId3Signature(input),
            _ => true,
        };
        if (!consistent)
            Assert.Fail($"Sniff result '{first}' disagrees with the signature predicates for input {MediaFuzzCorpus.Hex(input)}");
        return first;
    }
}
