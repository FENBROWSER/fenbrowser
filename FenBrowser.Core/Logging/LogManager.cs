using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Logging;

public sealed class LogManager
{
    private static readonly Lazy<LogManager> _instance = new(() => new LogManager());

    public static bool UseJsonFormat { get; set; } = true;

    public static event Action<LogEntry> LogEntryAdded
    {
        add => EngineLog.CompatibilityEntryAdded += value;
        remove => EngineLog.CompatibilityEntryAdded -= value;
    }

    public static LogManager Instance => _instance.Value;

    private LogManager()
    {
    }

    public static void InitializeFromSettings()
    {
        EngineLog.InitializeFromSettings();
    }

    public static void Initialize(bool enabled, LogCategory categories, LogLevel minLevel)
    {
        var options = new EngineLoggingOptions
        {
            Enabled = enabled,
            GlobalMinimumSeverity = EngineLogCompatibility.FromLegacyLevel(minLevel),
            EnableConsoleSink = true,
            EnableNdjsonSink = true,
            EnableRingBufferSink = true,
            EnableTraceSink = true,
            RingBufferCapacity = Math.Max(1000, BrowserSettings.Instance?.Logging?.MemoryBufferSize ?? 5000)
        };

        options.NdjsonFilePath = BuildNdjsonPath(BrowserSettings.Instance?.Logging?.LogPath);
        options.TraceFilePath = options.NdjsonFilePath.Replace(".jsonl", "_trace.jsonl", StringComparison.OrdinalIgnoreCase);
        EngineLog.Configure(options);
    }

    public void SetLogFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var options = new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Debug,
            EnableConsoleSink = true,
            EnableNdjsonSink = true,
            EnableRingBufferSink = true,
            EnableTraceSink = true,
            RingBufferCapacity = Math.Max(1000, BrowserSettings.Instance?.Logging?.MemoryBufferSize ?? 5000),
            NdjsonFilePath = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? path
                : path.Replace(".log", ".jsonl", StringComparison.OrdinalIgnoreCase)
        };
        options.TraceFilePath = options.NdjsonFilePath.Replace(".jsonl", "_trace.jsonl", StringComparison.OrdinalIgnoreCase);

        EngineLog.Configure(options);
    }

    public static bool IsEnabled(LogCategory category, LogLevel level)
    {
        return EngineLog.IsEnabled(
            EngineLogCompatibility.FromLegacyCategory(category),
            EngineLogCompatibility.FromLegacyLevel(level));
    }

    public static void Log(LogCategory category, LogLevel level, string message, Exception exception = null)
    {
        var fields = exception != null
            ? new Dictionary<string, object> { ["exception"] = exception.ToString() }
            : null;

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(category),
            EngineLogCompatibility.FromLegacyLevel(level),
            message ?? string.Empty,
            LogMarker.None,
            default,
            fields);
    }

    public static void Log(LogEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        var fields = entry.Data != null
            ? new Dictionary<string, object>(entry.Data)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (entry.DurationMs.HasValue)
        {
            fields["durationMs"] = entry.DurationMs.Value;
        }

        if (entry.MemoryBytes.HasValue)
        {
            fields["memoryBytes"] = entry.MemoryBytes.Value;
        }

        if (entry.Exception != null)
        {
            fields["exception"] = entry.Exception.ToString();
        }

        var context = new EngineLogContext(
            NavigationId: entry.CorrelationId,
            Url: fields.TryGetValue("url", out var url) ? url?.ToString() : null,
            Source: entry.Component);

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(entry.Category),
            EngineLogCompatibility.FromLegacyLevel(entry.Level),
            entry.Message ?? string.Empty,
            LogMarker.None,
            context,
            fields,
            sourceFile: entry.SourceFile,
            sourceLine: entry.SourceLine,
            sourceMember: entry.MethodName);
    }

    public static void LogError(LogCategory category, string message, Exception exception = null)
    {
        Log(category, LogLevel.Error, message, exception);
    }

    public static void LogPerf(LogCategory category, string operation, long milliseconds)
    {
        var fields = new Dictionary<string, object>
        {
            ["durationMs"] = milliseconds,
            ["operation"] = operation
        };

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(category),
            LogSeverity.Debug,
            $"[PERF] {operation}: {milliseconds}ms",
            LogMarker.None,
            default,
            fields);
    }

    public static void LogHtmlFailure(string tagName, string reason = null, string suggestion = null)
    {
        EngineCapabilities.LogUnsupportedHtml(tagName, reason, suggestion);
    }

    public static void LogCssFailure(string property, string value = null, string reason = null)
    {
        EngineCapabilities.LogUnsupportedCss(property, value, reason);
    }

    public static void LogJsFailure(string api, string method = null, string reason = null)
    {
        EngineCapabilities.LogUnsupportedJs(api, method, reason);
    }

    public static string GetFailureSummary()
    {
        return EngineCapabilities.GetFailureSummary();
    }

    public static void LogFeature(LogCategory category, string message)
    {
        Log(category, LogLevel.Info, message);
    }

    public static List<LogEntry> GetRecentLogs(int count = 1000)
    {
        return EngineLog.GetCompatibilityRecentEntries(count);
    }

    public static void ClearLogs(bool deleteFile = false)
    {
        EngineLog.ClearCompatibilityBuffer();
    }

    public static string GetLogFilePath()
    {
        return BrowserSettings.Instance?.Logging?.LogPath ?? DiagnosticPaths.GetLogsDirectory();
    }

    private static string BuildNdjsonPath(string configuredPath)
    {
        var logsDir = string.IsNullOrWhiteSpace(configuredPath)
            ? DiagnosticPaths.GetLogsDirectory()
            : configuredPath;

        System.IO.Directory.CreateDirectory(logsDir);
        return System.IO.Path.Combine(logsDir, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    }
}
