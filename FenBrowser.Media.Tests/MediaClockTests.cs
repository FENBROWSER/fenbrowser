using FenBrowser.Media.Clock;

namespace FenBrowser.Media.Tests;

public class MediaClockTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class FakeAudioPosition(int sampleRate) : IAudioPlaybackPosition
    {
        public int SampleRate => sampleRate;
        public long FramesPlayed { get; set; }
    }

    private static MediaTime Ms(long ms) => MediaTime.FromMicroseconds(ms * 1000);

    [Fact]
    public void Monotonic_OnlyAdvancesWhileRunning()
    {
        var time = new ManualTimeProvider();
        var clock = new MonotonicMediaClock(time);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(MediaTime.Zero, clock.CurrentTime);

        clock.Start();
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(Ms(250), clock.CurrentTime);

        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(Ms(250), clock.CurrentTime);
        Assert.False(clock.IsRunning);

        clock.Start();
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Ms(260), clock.CurrentTime);
    }

    [Fact]
    public void Monotonic_RateChangeAppliesFromNow()
    {
        var time = new ManualTimeProvider();
        var clock = new MonotonicMediaClock(time);
        clock.Start();

        time.Advance(TimeSpan.FromSeconds(1));
        clock.SetPlaybackRate(2.0);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Ms(3000), clock.CurrentTime);
        Assert.Equal(2.0, clock.PlaybackRate);

        clock.SetPlaybackRate(0.5);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Ms(3500), clock.CurrentTime);
    }

    [Fact]
    public void Monotonic_SeekKeepsRunningState()
    {
        var time = new ManualTimeProvider();
        var clock = new MonotonicMediaClock(time);
        clock.Start();
        time.Advance(TimeSpan.FromSeconds(1));

        clock.SetTime(Ms(60_000));
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(Ms(60_100), clock.CurrentTime);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void Monotonic_RejectsUnsupportedRates(double rate)
    {
        var clock = new MonotonicMediaClock(new ManualTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetPlaybackRate(rate));
    }

    [Fact]
    public void AudioMaster_FollowsPlayedFrames()
    {
        var device = new FakeAudioPosition(48_000);
        var clock = new AudioMasterClock(device);
        clock.Reset(Ms(5000));
        Assert.Equal(Ms(5000), clock.CurrentTime);

        clock.AppendSegment(outputStartFrame: 0, frameCount: 48_000, mediaStart: Ms(5000), rate: 1.0);
        device.FramesPlayed = 12_000;

        Assert.Equal(Ms(5250), clock.CurrentTime);
        Assert.False(clock.IsStarved);
    }

    [Fact]
    public void AudioMaster_RateChangeAtSegmentBoundaryIsExact()
    {
        var device = new FakeAudioPosition(48_000);
        var clock = new AudioMasterClock(device);

        // One second at 1x, then one output second at 2x (two media seconds).
        clock.AppendSegment(0, 48_000, Ms(0), 1.0);
        clock.AppendSegment(48_000, 48_000, Ms(1000), 2.0);

        device.FramesPlayed = 48_000 + 24_000;
        Assert.Equal(Ms(2000), clock.CurrentTime);
        Assert.Equal(2.0, clock.PlaybackRate);
    }

    [Fact]
    public void AudioMaster_HoldsDuringUnderrunAndGaps()
    {
        var device = new FakeAudioPosition(1000);
        var clock = new AudioMasterClock(device);
        clock.AppendSegment(0, 1000, Ms(0), 1.0);
        clock.AppendSegment(1500, 500, Ms(1000), 1.0);

        device.FramesPlayed = 1200;               // in the gap
        Assert.Equal(Ms(1000), clock.CurrentTime);

        device.FramesPlayed = 1750;
        Assert.Equal(Ms(1250), clock.CurrentTime);

        device.FramesPlayed = 9000;               // ran out of written audio
        Assert.Equal(Ms(1500), clock.CurrentTime);
        Assert.True(clock.IsStarved);
    }

    [Fact]
    public void AudioMaster_SilenceSegmentDoesNotAdvanceTime()
    {
        var device = new FakeAudioPosition(1000);
        var clock = new AudioMasterClock(device);
        clock.AppendSegment(0, 500, Ms(3000), rate: 0);
        clock.AppendSegment(500, 1000, Ms(3000), rate: 1.0);

        device.FramesPlayed = 400;
        Assert.Equal(Ms(3000), clock.CurrentTime);
        device.FramesPlayed = 600;
        Assert.Equal(Ms(3100), clock.CurrentTime);
    }

    [Fact]
    public void AudioMaster_BeforeFirstWrittenFrameReportsItsStart()
    {
        var device = new FakeAudioPosition(1000) { FramesPlayed = 10 };
        var clock = new AudioMasterClock(device);
        clock.AppendSegment(100, 100, Ms(7000), 1.0);

        Assert.Equal(Ms(7000), clock.CurrentTime);
    }

    [Fact]
    public void AudioMaster_PruningKeepsTimeStable()
    {
        var device = new FakeAudioPosition(1000);
        var clock = new AudioMasterClock(device);
        for (int i = 0; i < 100; i++)
            clock.AppendSegment(i * 10, 10, Ms(i * 10), 1.0);

        for (long played = 0; played < 1000; played += 7)
        {
            device.FramesPlayed = played;
            Assert.Equal(Ms(played), clock.CurrentTime);
        }

        // Late reads after pruning still land at the end.
        device.FramesPlayed = 5000;
        Assert.Equal(Ms(1000), clock.CurrentTime);
    }

    [Fact]
    public void AudioMaster_RejectsOverlapAndBadInput()
    {
        var clock = new AudioMasterClock(new FakeAudioPosition(1000));
        clock.AppendSegment(0, 100, Ms(0), 1.0);

        Assert.Throws<InvalidOperationException>(() => clock.AppendSegment(50, 100, Ms(50), 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AppendSegment(200, 0, Ms(0), 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AppendSegment(200, 10, Ms(0), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMasterClock(new FakeAudioPosition(0)));
    }

    [Fact]
    public void AudioMaster_ResetDropsQueuedAudio()
    {
        var device = new FakeAudioPosition(1000);
        var clock = new AudioMasterClock(device);
        clock.AppendSegment(0, 1000, Ms(0), 1.0);
        device.FramesPlayed = 500;

        clock.Reset(Ms(42_000));
        Assert.Equal(Ms(42_000), clock.CurrentTime);

        clock.AppendSegment(600, 1000, Ms(42_000), 1.0);
        device.FramesPlayed = 700;
        Assert.Equal(Ms(42_100), clock.CurrentTime);
    }

    /// <summary>A block the device thread was copying when the seek reset the clock is audio from before the seek: it must not move time back.</summary>
    [Fact]
    public void AudioMaster_SegmentRenderedBeforeAReset_IsIgnored()
    {
        var device = new FakeAudioPosition(1000);
        var clock = new AudioMasterClock(device);
        long epoch = clock.ResetCount;
        clock.Reset(Ms(1000));
        clock.AppendSegment(0, 1000, Ms(46), 1.0, epoch);
        device.FramesPlayed = 500;
        Assert.Equal(Ms(1000), clock.CurrentTime);

        clock.AppendSegment(1000, 1000, Ms(1000), 1.0, clock.ResetCount);
        device.FramesPlayed = 1100;
        Assert.Equal(Ms(1100), clock.CurrentTime);
    }
}
