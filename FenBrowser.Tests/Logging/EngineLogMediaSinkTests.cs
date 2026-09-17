using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Media;
using FenBrowser.Media.Diagnostics;

namespace FenBrowser.Tests.Logging;

// ADR-0006: media events reach the engine log as LogCategory.Media with structured fields.
[Collection(EngineLogTestCollection.Name)]
public class EngineLogMediaSinkTests
{
    private static void Configure(LogSeverity minimum, LogCategory categories = LogCategory.All)
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            EnabledCategories = categories,
            GlobalMinimumSeverity = minimum,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000,
        });
        EngineLog.ClearCompatibilityBuffer();
    }

    private static List<LogEntry> MediaEntries() =>
        EngineLog.GetCompatibilityRecentEntries().Where(e => e.Category == LogCategory.Media).ToList();

    [Fact]
    public void Event_IsWrittenWithCategoryLevelAndFields()
    {
        Configure(LogSeverity.Debug);
        var player = new PlayerId(4242);

        EngineLogMediaSink.Instance.Emit(player, MediaEventKind.DecoderChosen, MediaLogLevel.Info,
            "Decoding Vp9 with sw.", ("decoder", "sw"), ("codec", "Vp9"));

        var entry = Assert.Single(MediaEntries());
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal("[Media] player-4242 DecoderChosen: Decoding Vp9 with sw.", entry.Message);
        Assert.Equal("DecoderChosen", entry.Data["mediaEvent"]);
        Assert.Equal(4242L, entry.Data["playerId"]);
        Assert.Equal("sw", entry.Data["decoder"]);
        Assert.Equal("Vp9", entry.Data["codec"]);
    }

    [Theory]
    [InlineData(MediaLogLevel.Debug, LogLevel.Debug)]
    [InlineData(MediaLogLevel.Info, LogLevel.Info)]
    [InlineData(MediaLogLevel.Warn, LogLevel.Warn)]
    [InlineData(MediaLogLevel.Error, LogLevel.Error)]
    public void Levels_MapOneToOne(MediaLogLevel level, LogLevel expected)
    {
        Configure(LogSeverity.Debug);

        EngineLogMediaSink.Instance.Emit(PlayerId.Next(), MediaEventKind.Stall, level, "stalled");

        Assert.Equal(expected, Assert.Single(MediaEntries()).Level);
    }

    [Fact]
    public void BelowMinimumSeverity_IsDisabledAndWritesNothing()
    {
        Configure(LogSeverity.Warn);

        Assert.False(EngineLogMediaSink.Instance.IsEnabled(MediaLogLevel.Info));
        Assert.True(EngineLogMediaSink.Instance.IsEnabled(MediaLogLevel.Error));

        EngineLogMediaSink.Instance.Emit(PlayerId.Next(), MediaEventKind.ReadyStateChanged, MediaLogLevel.Info, "quiet");
        Assert.Empty(MediaEntries());
    }

    [Fact]
    public void CategoryFilter_ExcludingMedia_WritesNothing()
    {
        Configure(LogSeverity.Debug, LogCategory.Network);

        Assert.False(EngineLogMediaSink.Instance.IsEnabled(MediaLogLevel.Error));
        EngineLogMediaSink.Instance.Emit(PlayerId.Next(), MediaEventKind.Error, MediaLogLevel.Error, "filtered");

        Assert.Empty(MediaEntries());
    }

    [Fact]
    public void EventFields_CannotOverwriteIdentityKeys()
    {
        Configure(LogSeverity.Debug);

        EngineLogMediaSink.Instance.Emit(new PlayerId(7), MediaEventKind.Error, MediaLogLevel.Error, "spoof",
            ("playerId", "999"), ("mediaEvent", "PlayerCreated"));

        var entry = Assert.Single(MediaEntries());
        Assert.Equal(7L, entry.Data["playerId"]);
        Assert.Equal("Error", entry.Data["mediaEvent"]);
    }

    [Fact]
    public void LimitBreach_ReachesTheEngineLog()
    {
        Configure(LogSeverity.Debug);

        new MediaLimitExceededException(nameof(MediaLimits.MaxTracks), 65, 64)
            .Report(EngineLogMediaSink.Instance, new PlayerId(1));

        var entry = Assert.Single(MediaEntries());
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("LimitExceeded", entry.Data["mediaEvent"]);
        Assert.Equal("MaxTracks", entry.Data["limit"]);
    }

    [Fact]
    public void DisabledLogging_SkipsFieldAllocation()
    {
        Configure(LogSeverity.Error);
        var sink = EngineLogMediaSink.Instance;
        var player = new PlayerId(1);
        sink.Emit(player, MediaEventKind.Buffering, MediaLogLevel.Debug, "warm-up");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            sink.Emit(player, MediaEventKind.Buffering, MediaLogLevel.Debug, "suppressed");

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
