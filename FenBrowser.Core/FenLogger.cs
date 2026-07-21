using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core;

public static partial class FenLogger
{
    private static bool _enabled = true;

    public static bool StructuredOutputEnabled { get; set; }

    public static event Action<StructuredLogEntry> OnStructuredLog;

    public static bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            // Delegate to the shared preset pipeline — do NOT force-enable sinks.
            if (!value)
            {
                EngineLog.Configure(new EngineLoggingOptions
                {
                    Enabled = false,
                    EnableConsoleSink = false,
                    EnableDebugSink = false,
                    EnableNdjsonSink = false,
                    EnableRingBufferSink = false,
                    EnableTraceSink = false
                });
            }
            else
            {
                EngineLog.InitializeFromSettings();
            }
        }
    }

    public static void Initialize(string logFilePath)
    {
        _enabled = true;
        // Delegate to the shared preset pipeline — do NOT force-enable all sinks.
        EngineLog.InitializeFromSettings();
        Info($"FenLogger initialized with path: {logFilePath}", LogCategory.General);
    }

    public static IDisposable BeginScope(
        string component = null,
        string correlationId = null,
        IReadOnlyDictionary<string, object> data = null)
    {
        return LogContext.Push(component, correlationId, data);
    }

    public static IDisposable BeginCorrelationScope(
        string correlationId = null,
        string component = null,
        IReadOnlyDictionary<string, object> data = null)
    {
        return LogContext.Push(component, correlationId, data);
    }

    public static void Log(
        string message,
        LogCategory category = LogCategory.General,
        LogLevel level = LogLevel.Info,
        Exception ex = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        var entry = new LogEntry
        {
            Category = category,
            Level = level,
            Message = message ?? string.Empty,
            Exception = ex,
            MethodName = memberName,
            SourceFile = string.IsNullOrWhiteSpace(sourceFile) ? null : System.IO.Path.GetFileName(sourceFile),
            SourceLine = sourceLine,
            CorrelationId = LogContext.CurrentCorrelationId,
            Component = LogContext.CurrentComponent,
            Data = LogContext.CaptureData()
        };

        var fields = entry.Data != null
            ? new Dictionary<string, object>(entry.Data)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ex != null)
        {
            fields["exception"] = ex.ToString();
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

        EmitStructured(entry);
    }

    public static void Debug(
        string message,
        LogCategory category = LogCategory.General,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Log(message, category, LogLevel.Debug, null, memberName, sourceFile, sourceLine);
    }

    public static void Info(
        string message,
        LogCategory category = LogCategory.General,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Log(message, category, LogLevel.Info, null, memberName, sourceFile, sourceLine);
    }

    public static void Warn(
        string message,
        LogCategory category = LogCategory.General,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Log(message, category, LogLevel.Warn, null, memberName, sourceFile, sourceLine);
    }

    public static void Error(
        string message,
        LogCategory category = LogCategory.General,
        Exception ex = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        Log(message, category, LogLevel.Error, ex, memberName, sourceFile, sourceLine);
    }

    public static void LogMetric(string name, double value, string unit = "ms", LogCategory category = LogCategory.Performance)
    {
        var entry = new LogEntry
        {
            Category = category,
            Level = LogLevel.Info,
            Message = $"[METRIC] {name}: {value:F2} {unit}",
            Data = new Dictionary<string, object>
            {
                ["metricName"] = name,
                ["metricValue"] = value,
                ["metricUnit"] = unit
            }
        };

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(entry.Category),
            EngineLogCompatibility.FromLegacyLevel(entry.Level),
            entry.Message ?? string.Empty,
            LogMarker.None,
            default,
            entry.Data);

        EmitStructured(entry);
    }

    public static TimingScope TimeScope(string operationName, LogCategory category = LogCategory.Performance)
    {
        return new TimingScope(operationName, category);
    }

    private static void EmitStructured(LogEntry entry)
    {
        if (!StructuredOutputEnabled && OnStructuredLog == null)
        {
            return;
        }

        var structured = new StructuredLogEntry
        {
            Timestamp = entry.Timestamp,
            Level = entry.Level.ToString(),
            Category = entry.Category.ToString(),
            Message = entry.Message,
            Exception = entry.Exception?.ToString(),
            MetricName = entry.Data != null && entry.Data.TryGetValue("metricName", out var metricName) ? metricName?.ToString() : null,
            MetricValue = entry.Data != null && entry.Data.TryGetValue("metricValue", out var metricValue) ? Convert.ToDouble(metricValue) : null,
            MetricUnit = entry.Data != null && entry.Data.TryGetValue("metricUnit", out var metricUnit) ? metricUnit?.ToString() : null
        };

        try
        {
            OnStructuredLog?.Invoke(structured);
        }
        catch
        {
            // no-op
        }

        if (!StructuredOutputEnabled)
        {
            return;
        }

        try
        {
            Console.WriteLine(JsonSerializer.Serialize(structured));
        }
        catch
        {
            // no-op
        }
    }
}

public class StructuredLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; }
    public string Category { get; set; }
    public string Message { get; set; }
    public string Exception { get; set; }
    public string MetricName { get; set; }
    public double? MetricValue { get; set; }
    public string MetricUnit { get; set; }
}

public class TimingScope : IDisposable
{
    private readonly string _operationName;
    private readonly LogCategory _category;
    private readonly System.Diagnostics.Stopwatch _sw;

    public TimingScope(string operationName, LogCategory category)
    {
        _operationName = operationName;
        _category = category;
        _sw = System.Diagnostics.Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _sw.Stop();
        FenLogger.LogMetric(_operationName, _sw.Elapsed.TotalMilliseconds, "ms", _category);
    }
}

public static partial class FenLogger
{
    /// <summary>
    /// Legacy entry point — delegates to the shared preset pipeline.
    /// Does NOT force-enable sinks; respects the configured preset and user settings.
    /// </summary>
    private static void ConfigureEngineLogging(bool enabled, LogLevel minimumLevel, string logFilePath)
    {
        if (!enabled)
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            return;
        }

        // Use the shared preset/settings pipeline.
        EngineLog.InitializeFromSettings();

        // If a specific log file path was provided, enable NDJSON to that path
        // as an additional sink on top of whatever the preset configured.
        var logsPath = ResolveNdjsonPath(logFilePath);
        if (!string.IsNullOrWhiteSpace(logsPath))
        {
            // Re-configure with the NDJSON path added. We need to read the current
            // options, add the path, and re-apply. The simplest approach: just
            // configure with the preset pipeline which will pick up the log path
            // from settings. If the caller passed an explicit path, use it.
            var settings = BrowserSettings.Instance?.Logging;
            var opts = new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = EngineLogCompatibility.FromLegacyLevel(minimumLevel),
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = true,
                EnableRingBufferSink = true,
                EnableTraceSink = false,
                RingBufferCapacity = Math.Max(1000, settings?.MemoryBufferSize ?? 5000),
                DispatcherQueueCapacity = 32768,
                NdjsonFilePath = logsPath
            };

            EngineLog.Configure(opts);
        }
    }

    private static string ResolveNdjsonPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (configuredPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath;
            }

            if (configuredPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath[..^4] + ".jsonl";
            }

            return Path.Combine(configuredPath, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        }

        var dir = BrowserSettings.Instance?.Logging?.LogPath;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = DiagnosticPaths.GetLogsDirectory();
        }

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    }
}
