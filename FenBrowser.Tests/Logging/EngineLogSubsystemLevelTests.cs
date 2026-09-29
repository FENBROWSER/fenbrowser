using FenBrowser.Core.Logging;

namespace FenBrowser.Tests.Logging;

[Collection(EngineLogTestCollection.Name)]
public sealed class EngineLogSubsystemLevelTests
{
    [Fact]
    public void EnsureSubsystemEnabled_WidensOnlyThatSubsystem()
    {
        Isolated(() =>
        {
            Assert.False(EngineLog.IsEnabled(LogSubsystem.Svg, LogSeverity.Info));

            EngineLog.EnsureSubsystemEnabled(LogSubsystem.Svg, LogSeverity.Info);

            Assert.True(EngineLog.IsEnabled(LogSubsystem.Svg, LogSeverity.Info));
            Assert.False(EngineLog.IsEnabled(LogSubsystem.Svg, LogSeverity.Debug));
            Assert.False(EngineLog.IsEnabled(LogSubsystem.Layout, LogSeverity.Info));
        });
    }

    [Fact]
    public void EnsureSubsystemEnabled_SurvivesReconfiguration()
    {
        Isolated(() =>
        {
            EngineLog.EnsureSubsystemEnabled(LogSubsystem.Svg, LogSeverity.Info);

            EngineLog.Configure(WarnOnly());

            Assert.True(EngineLog.IsEnabled(LogSubsystem.Svg, LogSeverity.Info));
        });
    }

    [Fact]
    public void EnsureSubsystemEnabled_NeverNarrowsAMoreVerboseLevel()
    {
        Isolated(() =>
        {
            var options = WarnOnly();
            options.SubsystemOverrides[LogSubsystem.Svg] = LogSeverity.Debug;
            EngineLog.Configure(options);

            EngineLog.EnsureSubsystemEnabled(LogSubsystem.Svg, LogSeverity.Warn);

            Assert.True(EngineLog.IsEnabled(LogSubsystem.Svg, LogSeverity.Debug));
        });
    }

    [Fact]
    public void EnsureSubsystemEnabled_DoesNotOverrideDisabledLogging()
    {
        Isolated(() =>
        {
            var options = WarnOnly();
            options.Enabled = false;
            EngineLog.Configure(options);

            EngineLog.EnsureSubsystemEnabled(LogSubsystem.Svg, LogSeverity.Trace);

            Assert.False(EngineLog.IsEnabled(LogSubsystem.Svg, LogSeverity.Error));
        });
    }

    private static void Isolated(Action test)
    {
        EngineLog.ClearEnsuredSubsystemLevels();
        EngineLog.Configure(WarnOnly());
        try
        {
            test();
        }
        finally
        {
            EngineLog.ClearEnsuredSubsystemLevels();
            EngineLog.Configure(WarnOnly());
        }
    }

    private static EngineLoggingOptions WarnOnly() => new()
    {
        Enabled = true,
        GlobalMinimumSeverity = LogSeverity.Warn,
        EnableConsoleSink = false,
        EnableDebugSink = false,
        EnableNdjsonSink = false,
        EnableRingBufferSink = true,
        EnableTraceSink = false
    };
}
