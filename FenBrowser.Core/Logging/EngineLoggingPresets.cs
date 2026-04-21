using System;

namespace FenBrowser.Core.Logging;

public static class EngineLoggingPresets
{
    public const string Developer = "developer";
    public const string TestRun = "testrun";
    public const string Perf = "perf";
    public const string Ci = "ci";

    public static bool Apply(string presetName, EngineLoggingOptions options)
    {
        if (options == null || string.IsNullOrWhiteSpace(presetName))
        {
            return false;
        }

        var normalized = presetName.Trim().ToLowerInvariant();
        options.SubsystemOverrides.Clear();

        switch (normalized)
        {
            case Developer:
                options.GlobalMinimumSeverity = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Style] = LogSeverity.Debug;
                options.SubsystemOverrides[LogSubsystem.Layout] = LogSeverity.Debug;
                options.SubsystemOverrides[LogSubsystem.Js] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Fetch] = LogSeverity.Info;
                options.EnableTraceSink = true;
                options.EnableRingBufferSink = true;
                options.EnableConsoleSink = true;
                return true;

            case TestRun:
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.SubsystemOverrides[LogSubsystem.Verification] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Style] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Layout] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Html] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.CssParse] = LogSeverity.Info;
                options.EnableRingBufferSink = true;
                options.EnableNdjsonSink = true;
                options.EnableTraceSink = false;
                return true;

            case Perf:
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.SubsystemOverrides[LogSubsystem.Nav] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Style] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Layout] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Paint] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Js] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Fetch] = LogSeverity.Info;
                options.EnableConsoleSink = false;
                options.EnableTraceSink = true;
                options.EnableNdjsonSink = true;
                return true;

            case Ci:
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.EnableRingBufferSink = true;
                options.EnableNdjsonSink = true;
                options.EnableTraceSink = false;
                options.EnableConsoleSink = false;
                return true;
        }

        return false;
    }
}

