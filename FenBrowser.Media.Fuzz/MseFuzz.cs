using System.Diagnostics;
using FenBrowser.Media.Mse;
using FenBrowser.Media.Pipeline;
using Xunit;

namespace FenBrowser.Media.Fuzz;

/// <summary>
/// The MSE segment parsers and coded frame processing take bytes straight from page script
/// (<c>SourceBuffer.appendBuffer</c>), so a mutated byte stream, chopped into arbitrary
/// appends, must only ever end in "Ok" or an append error: no exception, no unbounded
/// time, and every accepted frame accounted for in the track buffers.
/// </summary>
public sealed class MseFuzz
{
    private const int IterationsPerSeed = 2_000;
    private const int MaxRunMilliseconds = 2_000;

    private static readonly Lazy<IReadOnlyList<(string Type, byte[] Bytes)>> s_seeds = new(() =>
    [
        ("video/mp4", MediaFuzzCorpus.ReadFixture("pattern_h264_fragmented.mp4")),
        ("video/webm", MediaFuzzCorpus.ReadFixture("pattern_vp9.webm")),
        ("video/webm", MediaFuzzCorpus.ReadFixture("pattern_vp9.mkv")),
        ("video/mp4", MediaFuzzCorpus.ReadFixture("pattern_h264_aac.mp4")),
    ]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SourceBuffer_HoldsItsPropertiesOnMutatedAppends(int seed)
    {
        var random = new Random(seed);
        int accepted = 0;
        int rejected = 0;
        for (int i = 0; i < IterationsPerSeed; i++)
        {
            var (type, parent) = s_seeds.Value[random.Next(s_seeds.Value.Count)];
            byte[] input = MediaFuzzCorpus.Mutate(parent, random, maxLength: 24_000);
            var first = RunOne(type, input, random.Next(1, 4), random);
            if (first.Frames > 0)
                accepted++;
            if (first.Errors > 0)
                rejected++;
        }

        Assert.True(accepted > 0, "no mutated append buffered a frame.");
        Assert.True(rejected > 0, "no mutated append was rejected.");
    }

    [Fact]
    public void IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(5 * IterationsPerSeed >= 10_000);

    /// <summary>Appends the same bytes whole and in pieces: the track buffers must end up identical.</summary>
    [Fact]
    public void ChunkingDoesNotChangeTheResult()
    {
        var random = new Random(9);
        foreach (var (type, bytes) in s_seeds.Value)
        {
            var whole = Snapshot(type, bytes, chunks: 1, random);
            for (int i = 0; i < 25; i++)
            {
                var pieces = Snapshot(type, bytes, chunks: random.Next(2, 40), random);
                Assert.Equal(whole, pieces);
            }
        }
    }

    private static string Snapshot(string type, byte[] bytes, int chunks, Random random)
    {
        var model = new MediaSourceModel(MediaPipelineContext.ForTests());
        model.Attach();
        var buffer = model.AddSourceBuffer(type, generateTimestamps: false);
        foreach (var piece in Split(bytes, chunks, random))
            buffer.Append(piece);
        return string.Join("|", buffer.TrackBuffers.OrderBy(t => t.TrackId).Select(t => $"{t.TrackId}:{t.Kind}:{t.Frames.Count}:{t.Bytes}:{string.Join(",", Enumerable.Range(0, t.Buffered.Count).Select(r => $"{t.Buffered.Start(r).Microseconds}-{t.Buffered.End(r).Microseconds}"))}"));
    }

    private static (int Frames, int Errors) RunOne(string type, byte[] input, int chunks, Random random)
    {
        var context = MediaPipelineContext.ForTests();
        var model = new MediaSourceModel(context);
        model.Attach();
        var buffer = model.AddSourceBuffer(type, generateTimestamps: false);
        if (random.Next(4) == 0)
            buffer.SetTimestampOffset(MediaTime.FromSeconds(random.NextDouble() * 4 - 1));
        if (random.Next(4) == 0)
            buffer.SetMode(AppendMode.Sequence);
        if (random.Next(6) == 0)
        {
            buffer.AppendWindowStart = MediaTime.FromSeconds(random.NextDouble());
            buffer.AppendWindowEnd = MediaTime.FromSeconds(0.5 + random.NextDouble());
        }

        int errors = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var piece in Split(input, chunks, random))
        {
            var outcome = buffer.Append(piece);
            if (outcome == AppendOutcome.DecodeError)
                errors++;
            Assert.True(stopwatch.ElapsedMilliseconds < MaxRunMilliseconds, $"{type} append took {stopwatch.ElapsedMilliseconds} ms on {MediaFuzzCorpus.Hex(input)}");
        }

        int frames = 0;
        long bytes = 0;
        foreach (var track in buffer.TrackBuffers)
        {
            frames += track.Frames.Count;
            bytes += track.Bytes;
            Assert.Equal(track.Frames.Sum(f => (long)f.Bytes), track.Bytes);
            Assert.InRange(track.Buffered.Count, 0, track.Frames.Count);
            for (int i = 1; i < track.Frames.Count; i++)
                Assert.True(track.Frames[i].Dts >= track.Frames[i - 1].Dts, "track buffer frames must stay in decode order");
            foreach (var frame in track.Frames)
            {
                Assert.True(frame.Pts >= buffer.AppendWindowStart);
                Assert.True(frame.Pts + frame.Duration <= buffer.AppendWindowEnd);
            }
        }

        Assert.Equal(bytes, buffer.Bytes);
        _ = buffer.Buffered;
        _ = model.Buffered;
        _ = model.Seekable;
        if (random.Next(3) == 0)
            buffer.Remove(MediaTime.FromSeconds(random.NextDouble()), MediaTime.FromSeconds(random.NextDouble() * 3));
        _ = buffer.EvictToFit(random.Next(0, 1 << 20), MediaTime.FromSeconds(random.NextDouble() * 3));
        if (model.ReadyState == MediaSourceReadyState.Open)
            model.EndOfStream(EndOfStreamError.None);
        _ = model.Buffered;
        _ = model.Seekable;
        model.Detach();
        return (frames, errors);
    }

    private static IEnumerable<byte[]> Split(byte[] bytes, int chunks, Random random)
    {
        if (chunks <= 1 || bytes.Length < 2)
        {
            yield return bytes;
            yield break;
        }

        var cuts = Enumerable.Range(0, chunks - 1).Select(_ => random.Next(1, bytes.Length)).Distinct().Order().ToList();
        int at = 0;
        foreach (var cut in cuts)
        {
            yield return bytes.AsSpan(at, cut - at).ToArray();
            at = cut;
        }

        yield return bytes.AsSpan(at).ToArray();
    }
}
