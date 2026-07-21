using System;

namespace FenBrowser.Core.Logging;

public static class EngineLoggingPresets
{
    public const string Normal = "normal";
    public const string Developer = "developer";
    public const string TestRun = "testrun";
    public const string Perf = "perf";
    public const string Ci = "ci";

    /// <summary>
    /// Applies a named preset to the given options object.
    /// Returns true if the preset was recognized and applied.
    /// Unknown/blank preset names return false (caller should fall back to Normal).
    /// </summary>
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
            case Normal:
                // Normal browsing: retain recent diagnostics in memory only.
                // No continuous writes to terminal, debugger, or filesystem.
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.EnableConsoleSink = false;
                options.EnableDebugSink = false;
                options.EnableNdjsonSink = false;
                options.EnableTraceSink = false;
                options.EnableRingBufferSink = true;
                return true;

            case Developer:
                // Developer mode: prefer debugger when attached, else console.
                // Never duplicate to both. Trace file for detailed diagnostics.
                options.GlobalMinimumSeverity = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Style] = LogSeverity.Debug;
                options.SubsystemOverrides[LogSubsystem.Layout] = LogSeverity.Debug;
                options.SubsystemOverrides[LogSubsystem.Js] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Fetch] = LogSeverity.Info;
                options.EnableTraceSink = true;
                options.EnableRingBufferSink = true;
                options.EnableConsoleSink = !System.Diagnostics.Debugger.IsAttached;
                options.EnableDebugSink = System.Diagnostics.Debugger.IsAttached;
                return true;

            case TestRun:
                // Test runs: structured NDJSON artifacts + ring buffer.
                // No console/debugger spam.
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.SubsystemOverrides[LogSubsystem.Verification] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Style] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Layout] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Html] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.CssParse] = LogSeverity.Info;
                options.EnableRingBufferSink = true;
                options.EnableNdjsonSink = true;
                options.EnableTraceSink = false;
                options.EnableConsoleSink = false;
                options.EnableDebugSink = false;
                return true;

            case Perf:
                // Performance profiling: trace file for analysis + ring buffer.
                // Console off to avoid I/O stalls. NDJSON for structured analysis.
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.SubsystemOverrides[LogSubsystem.Nav] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Style] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Layout] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Paint] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Js] = LogSeverity.Info;
                options.SubsystemOverrides[LogSubsystem.Fetch] = LogSeverity.Info;
                options.EnableConsoleSink = false;
                options.EnableDebugSink = false;
                options.EnableTraceSink = true;
                options.EnableNdjsonSink = true;
                options.EnableRingBufferSink = true;
                return true;

            case Ci:
                // CI: structured NDJSON artifacts + ring buffer.
                // Zero console/debug output.
                options.GlobalMinimumSeverity = LogSeverity.Warn;
                options.EnableRingBufferSink = true;
                options.EnableNdjsonSink = true;
                options.EnableTraceSink = false;
                options.EnableConsoleSink = false;
                options.EnableDebugSink = false;
                return true;
        }

        return false;
    }
}

