using FenBrowser.Media.Clock;
using Xunit;

namespace FenBrowser.Media.Fuzz;

// Property fuzzing for the time model: MediaTimeRanges set algebra and the audio clock.
//
// MediaTimeRanges is checked against a brute-force oracle over a small integer domain:
// a range [a, b) covers the unit cells a..b-1, and a set is the union of its cells.
// The oracle ignores the end-inclusive Contains rule, so containment is checked on
// cell midpoints only.
//
// AudioMasterClock is checked for: no exceptions on any legal call sequence, media time
// never going backwards while the device position only moves forward and segments are
// appended in order at non-negative rates, and time never exceeding the written media.
public sealed class TimeModelFuzz
{
    private const int Domain = 48;

    private static MediaTime T(int unit) => MediaTime.FromMicroseconds(unit * 1_000L);

    private static (MediaTimeRanges Ranges, bool[] Cells) RandomSet(Random random)
    {
        var cells = new bool[Domain];
        var list = new List<(MediaTime, MediaTime)>();
        int count = random.Next(0, 6);
        for (int i = 0; i < count; i++)
        {
            int a = random.Next(-4, Domain + 4);
            int b = random.Next(-4, Domain + 4);
            list.Add((T(a), T(b)));
            for (int c = Math.Max(a, 0); c < Math.Min(b, Domain); c++)
                cells[c] = true;
        }

        return (MediaTimeRanges.From(list), cells);
    }

    private static bool[] CellsOf(MediaTimeRanges ranges)
    {
        var cells = new bool[Domain];
        for (int c = 0; c < Domain; c++)
            cells[c] = ranges.Contains(MediaTime.FromMicroseconds((c * 1_000L) + 500));
        return cells;
    }

    private static void AssertNormalized(MediaTimeRanges ranges)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            Assert.True(ranges.Start(i) < ranges.End(i), $"empty range at {i}: {ranges}");
            if (i > 0)
                Assert.True(ranges.End(i - 1) < ranges.Start(i), $"touching or overlapping ranges at {i}: {ranges}");
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void TimeRanges_MatchTheCellOracle(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < 5_000; i++)
        {
            var (a, aCells) = RandomSet(random);
            var (b, bCells) = RandomSet(random);

            AssertNormalized(a);
            Assert.Equal(aCells, CellsOf(a));

            var union = a.Union(b);
            var intersect = a.Intersect(b);
            AssertNormalized(union);
            AssertNormalized(intersect);
            Assert.Equal(aCells.Zip(bCells, (x, y) => x || y).ToArray(), CellsOf(union));
            Assert.Equal(aCells.Zip(bCells, (x, y) => x && y).ToArray(), CellsOf(intersect));

            // Nearest always lands inside the set (end-inclusive), or is null for an empty set.
            var probe = MediaTime.FromMicroseconds(random.Next(-10_000, (Domain + 10) * 1_000));
            var nearest = a.Nearest(probe);
            Assert.Equal(a.Count == 0, nearest is null);
            if (nearest is { } n)
                Assert.True(a.Contains(n), $"Nearest({probe}) = {n} is outside {a}");
        }
    }

    private sealed class Device(int sampleRate) : IAudioPlaybackPosition
    {
        public int SampleRate => sampleRate;
        public long FramesPlayed { get; set; }
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    public void AudioClock_IsMonotonicAndBounded(int seed)
    {
        var random = new Random(seed);
        for (int run = 0; run < 200; run++)
        {
            int sampleRate = random.Next(2) == 0 ? 48_000 : random.Next(1, 400_000);
            var device = new Device(sampleRate);
            var clock = new AudioMasterClock(device);
            var start = T(random.Next(0, 100_000));
            clock.Reset(start);

            long writeFrame = 0;
            var mediaCursor = start;
            var last = clock.CurrentTime;
            var maxMedia = start;

            for (int step = 0; step < 100; step++)
            {
                if (random.Next(3) > 0)
                {
                    writeFrame += random.Next(0, 3) == 0 ? random.Next(0, 5_000) : 0; // optional gap
                    long frames = random.Next(1, 20_000);
                    double rate = random.Next(6) switch
                    {
                        0 => 0,
                        1 => 0.25,
                        2 => 2.0,
                        3 => random.NextDouble() * 4,
                        _ => 1.0,
                    };
                    clock.AppendSegment(writeFrame, frames, mediaCursor, rate);
                    writeFrame += frames;
                    mediaCursor += MediaTime.FromMicroseconds((long)Math.Round(frames * rate * 1_000_000.0 / sampleRate));
                    maxMedia = MediaTime.Max(maxMedia, mediaCursor);
                }

                device.FramesPlayed += random.Next(0, 8_000);

                var now = clock.CurrentTime;
                Assert.True(now >= last, $"time went backwards: {last} -> {now} (seed {seed}, run {run}, step {step})");
                Assert.True(now <= maxMedia, $"time {now} ran past the written media {maxMedia} (seed {seed}, run {run}, step {step})");
                Assert.True(double.IsFinite(clock.PlaybackRate) && clock.PlaybackRate >= 0);
                last = now;
            }
        }
    }
}
