using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Logging;

#pragma warning disable CS8632

namespace FenBrowser.Core;

public static class EngineLogCompat
{
    private static bool _structuredOutputEnabled;
    private static bool _isEnabled = true;

    public static bool StructuredOutputEnabled
    {
        get => _structuredOutputEnabled;
        set => _structuredOutputEnabled = value;
    }

    public static bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            ConfigureEngineLogging(value, null);
        }
    }

    public static void Initialize(string logFilePath)
    {
        _isEnabled = true;
        ConfigureEngineLogging(true, logFilePath);
        Log("EngineLogCompat initialized", LogCategory.General, LogLevel.Info);
    }

    public static IDisposable BeginScope(
        string component = null,
        string correlationId = null,
        IReadOnlyDictionary<string, object> data = null)
        => LogContext.Push(component, correlationId, data);

    public static IDisposable BeginCorrelationScope(
        string correlationId = null,
        string component = null,
        IReadOnlyDictionary<string, object> data = null)
        => LogContext.Push(component, correlationId, data);

    public static void Log(
        LogCategory category,
        LogLevel level,
        [InterpolatedStringHandlerArgument(nameof(category), nameof(level))]
        ref EngineLogInterpolatedStringHandler message,
        Exception ex = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0)
    {
        if (!message.IsEnabled)
        {
            return;
        }

        Log(message.GetFormattedText(), category, level, ex, memberName, sourceFile, sourceLine);
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
        if (!LogManager.IsEnabled(category, level))
        {
            return;
        }

        var fields = LogContext.CaptureData() ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ex != null)
        {
            fields["exception"] = ex.ToString();
        }

        string? GetField(string key)
        {
            return fields.TryGetValue(key, out var value) ? value?.ToString() : null;
        }

        var context = new EngineLogContext(
            NavigationId: LogContext.CurrentCorrelationId ?? GetField("navigationId"),
            DocumentId: GetField("documentId"),
            FrameId: GetField("frameId"),
            TabId: GetField("tabId"),
            Url: GetField("url"),
            TestId: GetField("testId"),
            RequestId: GetField("requestId"),
            NodeDescription: GetField("node"),
            CssSelector: GetField("selector"),
            ResourceUrl: GetField("resourceUrl"),
            SpecArea: GetField("specArea"),
            Source: LogContext.CurrentComponent);

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(category),
            EngineLogCompatibility.FromLegacyLevel(level),
            message ?? string.Empty,
            LogMarker.None,
            context,
            fields,
            sourceFile: string.IsNullOrWhiteSpace(sourceFile) ? null : System.IO.Path.GetFileName(sourceFile),
            sourceLine: sourceLine,
            sourceMember: memberName);
    }

    public static void Debug(string message, LogCategory category = LogCategory.General)
        => Log(message, category, LogLevel.Debug);

    public static void Info(string message, LogCategory category = LogCategory.General)
        => Log(message, category, LogLevel.Info);

    public static void Warn(string message, LogCategory category = LogCategory.General)
        => Log(message, category, LogLevel.Warn);

    public static void Warn(string message, LogCategory category, Exception ex)
        => Log(message, category, LogLevel.Warn, ex);

    public static void Error(string message, LogCategory category = LogCategory.General, Exception ex = null)
        => Log(message, category, LogLevel.Error, ex);

    public static void Trace(string message, LogCategory category = LogCategory.General)
        => Log(message, category, LogLevel.Trace);

    public static void LogMetric(string name, double value, string unit = "ms", LogCategory category = LogCategory.Performance)
    {
        var fields = new Dictionary<string, object>
        {
            ["metricName"] = name,
            ["metricValue"] = value,
            ["metricUnit"] = unit
        };

        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(category),
            LogSeverity.Info,
            $"[METRIC] {name}: {value:F2} {unit}",
            LogMarker.None,
            default,
            fields);
    }

    public static TimingScope TimeScope(string operationName, LogCategory category = LogCategory.Performance)
        => new(operationName, category);

    public static string DumpRawSource(string url, string htmlContent)
    {
        var logsDir = DiagnosticPaths.GetLogsDirectory();
        var filePath = System.IO.Path.Combine(logsDir, $"raw_source_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        ResilientFileWriter.WriteAllText(filePath, $"<!-- URL: {url} -->\n<!-- Type: Network Fetch (Raw) -->\n{htmlContent ?? string.Empty}");
        Info($"[Network] Raw source dumped to: {filePath}", LogCategory.Network);
        return filePath;
    }

    public static string DumpEngineSource(string url, string htmlContent)
    {
        var logsDir = DiagnosticPaths.GetLogsDirectory();
        var filePath = System.IO.Path.Combine(logsDir, $"engine_source_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        ResilientFileWriter.WriteAllText(filePath, $"<!-- URL: {url} -->\n<!-- Type: Fen Engine Processed DOM -->\n{htmlContent ?? string.Empty}");
        Info($"[Rendering] Engine source dumped to: {filePath}", LogCategory.Rendering);
        return filePath;
    }

    public static string DumpRenderedText(string url, string textContent)
    {
        var logsDir = DiagnosticPaths.GetLogsDirectory();
        var filePath = System.IO.Path.Combine(logsDir, $"rendered_text_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        ResilientFileWriter.WriteAllText(filePath, $"URL: {url}\nDumped: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n----------------------------------\n{textContent ?? string.Empty}");
        Info($"[Rendering] Rendered text dumped to: {filePath}", LogCategory.Rendering);
        return filePath;
    }

    private static void ConfigureEngineLogging(bool enabled, string? logFilePath)
    {
        var loggingSettings = BrowserSettings.Instance?.Logging;
        var settingsEnabled = loggingSettings?.EnableLogging ?? true;
        var effectiveEnabled = enabled && settingsEnabled;
        var logToFile = loggingSettings?.LogToFile ?? false;
        var minimumLevel = loggingSettings?.MinimumLevel ?? (int)LogLevel.Warn;

        // Start from framework defaults (quiet).
        var options = new EngineLoggingOptions
        {
            Enabled = effectiveEnabled,
            EnabledCategories = loggingSettings != null ? (LogCategory)loggingSettings.EnabledCategories : LogCategory.All,
            GlobalMinimumSeverity = EngineLogCompatibility.FromLegacyLevel((LogLevel)minimumLevel),
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = effectiveEnabled,
            EnableTraceSink = false,
            RingBufferCapacity = Math.Max(1000, loggingSettings?.MemoryBufferSize ?? 5000),
            DispatcherQueueCapacity = 32768
        };

        // Step 1: Apply the configured preset (or Normal fallback).
        var preset = Environment.GetEnvironmentVariable("FEN_LOG_PRESET");
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = loggingSettings?.LoggingPreset;
        }

        if (!EngineLoggingPresets.Apply(preset, options))
        {
            EngineLoggingPresets.Apply(EngineLoggingPresets.Normal, options);
        }

        // Step 2: Apply user settings as restrictive gates.
        // User settings may DISABLE sinks the preset enabled,
        // but must NOT re-enable sinks the preset deliberately disabled.
        if (!effectiveEnabled)
        {
            options.EnableRingBufferSink = false;
        }

        // LogToDebug only gates the debugger sink (not console).
        if (loggingSettings != null && !loggingSettings.LogToDebug)
        {
            options.EnableDebugSink = false;
        }

        // LogToFile only gates file sinks.
        if (!logToFile)
        {
            options.EnableNdjsonSink = false;
            options.EnableTraceSink = false;
        }

        // Step 3: Resolve file paths only for sinks that remain enabled.
        if (options.EnableNdjsonSink || options.EnableTraceSink)
        {
            var logsPath = ResolveNdjsonPath(logFilePath);
            if (!string.IsNullOrWhiteSpace(logsPath))
            {
                if (options.EnableNdjsonSink)
                {
                    options.NdjsonFilePath = logsPath;
                }

                if (options.EnableTraceSink)
                {
                    options.TraceFilePath = logsPath.Replace(".jsonl", "_trace.jsonl", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        EngineLog.Configure(options);
    }

    private static string ResolveNdjsonPath(string? configuredPath)
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

            return System.IO.Path.Combine(configuredPath, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        }

        var dir = BrowserSettings.Instance?.Logging?.LogPath;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = DiagnosticPaths.GetLogsDirectory();
        }

        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    }
}
