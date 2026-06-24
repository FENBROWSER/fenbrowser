using FenBrowser.Core.Logging;

namespace FenBrowser.Tests.Logging;

public class EngineLogSettingsTests
{
    [Fact]
    public void DisabledLogging_BlocksWritesAndClearsCompatibilityBuffer()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Info,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });

        EngineLog.Write(LogSubsystem.General, LogSeverity.Info, "before-off");
        Assert.NotEmpty(EngineLog.GetCompatibilityRecentEntries());

        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = false,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = false,
            EnableTraceSink = false
        });

        EngineLog.Write(LogSubsystem.General, LogSeverity.Error, "after-off");

        Assert.False(EngineLog.IsEnabled(LogSubsystem.General, LogSeverity.Error));
        Assert.Empty(EngineLog.GetCompatibilityRecentEntries());
    }

    [Fact]
    public void EnabledCategories_FilterStructuredWrites()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            EnabledCategories = LogCategory.Network,
            GlobalMinimumSeverity = LogSeverity.Info,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });

        EngineLog.ClearCompatibilityBuffer();

        EngineLog.Write(LogSubsystem.Layout, LogSeverity.Info, "layout-filtered");
        EngineLog.Write(LogSubsystem.Net, LogSeverity.Info, "network-visible");

        var entries = EngineLog.GetCompatibilityRecentEntries();
        Assert.Single(entries);
        Assert.Equal(LogCategory.Network, entries[0].Category);
        Assert.Equal("network-visible", entries[0].Message);
    }
}
