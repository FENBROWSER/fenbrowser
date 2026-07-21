using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Logging;

public static class EngineLog
{
    private static readonly object Sync = new();
    private static EngineLogger _logger;
    private static EngineLoggingOptions _options;
    private static readonly ConcurrentQueue<LogEntry> CompatibilityBuffer = new();
    private static readonly ConcurrentDictionary<string, MutableDocumentCounter> DocumentCounters = new(StringComparer.Ordinal);
    private static int _compatibilityBufferCap = 5000;
    private static readonly ILogDeduplicator Deduplicator = new EngineLogDeduplicator();
    private static event Action<EngineLogEvent> EngineEventWrittenInternal;

    // Re-entrancy guard: prevents recursive Write() calls from event handlers
    // that could stack-overflow into a native buffer overrun (0xc0000409).
    [ThreadStatic]
    private static bool _isWriting;

    public static event Action<LogEntry> CompatibilityEntryAdded;

    public static event Action<EngineLogEvent> EngineEventWritten
    {
        add
        {
            lock (Sync)
            {
                EngineEventWrittenInternal += value;
            }
        }
        remove
        {
            lock (Sync)
            {
                EngineEventWrittenInternal -= value;
            }
        }
    }

    public static IEngineLogger Current
    {
        get
        {
            EnsureInitialized();
            return _logger;
        }
    }

    public static void InitializeFromSettings()
    {
        var settings = BrowserSettings.Instance?.Logging;
        var loggingEnabled = settings?.EnableLogging ?? true;

        // Start from framework defaults (all sinks off, ring buffer on).
        var opts = new EngineLoggingOptions
        {
            Enabled = loggingEnabled,
            EnabledCategories = settings != null ? (LogCategory)settings.EnabledCategories : LogCategory.All,
            GlobalMinimumSeverity = settings != null
                ? EngineLogCompatibility.FromLegacyLevel((LogLevel)settings.MinimumLevel)
                : LogSeverity.Warn,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = Math.Max(1000, settings?.MemoryBufferSize ?? 5000),
            DispatcherQueueCapacity = 32768
        };

        // Step 1: Apply the selected preset (or Normal as fallback).
        var preset = Environment.GetEnvironmentVariable("FEN_LOG_PRESET");
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = settings?.LoggingPreset;
        }

        if (!EngineLoggingPresets.Apply(preset, opts))
        {
            // Unrecognized or missing preset → fall back to Normal.
            EngineLoggingPresets.Apply(EngineLoggingPresets.Normal, opts);
        }

        // Step 2: Apply explicit user restrictions as gates.
        // User settings may further DISABLE sinks but must NOT re-enable
        // sinks that the preset deliberately disabled.
        ApplySettingsSinkGates(opts, settings);

        // Step 3: Resolve file paths only for sinks that remain enabled.
        if (opts.EnableNdjsonSink)
        {
            opts.NdjsonFilePath = BuildNdjsonPath(settings?.LogPath);
        }

        if (opts.EnableTraceSink)
        {
            opts.TraceFilePath = BuildTracePath(settings?.LogPath);
        }

        Configure(opts);
    }

    public static void Configure(EngineLoggingOptions options)
    {
        lock (Sync)
        {
            _options = options ?? new EngineLoggingOptions();

            _logger?.Dispose();
            _logger = BuildLogger(_options);
            _logger.EventWritten += OnEngineEvent;
            _compatibilityBufferCap = Math.Max(1000, _options.RingBufferCapacity);
            if (!_options.Enabled)
            {
                ClearCompatibilityBuffer();
            }
        }
    }

    public static void ApplyPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return;
        }

        EnsureInitialized();
        lock (Sync)
        {
            if (!EngineLoggingPresets.Apply(presetName, _options))
            {
                return;
            }

            _logger?.Dispose();
            _logger = BuildLogger(_options);
            _logger.EventWritten += OnEngineEvent;
            _compatibilityBufferCap = Math.Max(1000, _options.RingBufferCapacity);
        }
    }

    public static bool IsEnabled(LogSubsystem subsystem, LogSeverity severity)
    {
        EnsureInitialized();
        return _logger.IsEnabled(subsystem, severity);
    }

    public static bool Flush(TimeSpan timeout)
    {
        EnsureInitialized();
        EngineLogger logger;
        lock (Sync)
        {
            logger = _logger;
        }

        return logger?.Flush(timeout) ?? true;
    }

    public static void Write(
        LogSubsystem subsystem,
        LogSeverity severity,
        string message,
        LogMarker marker = LogMarker.None,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        // Re-entrancy guard: if a log write triggers an event handler that
        // calls Write() again on the same thread, drop the recursive call
        // to prevent stack overflow → native buffer overrun (0xc0000409).
        if (_isWriting)
        {
            return;
        }

        _isWriting = true;
        try
        {
            EnsureInitialized();
            _logger.Write(
                subsystem,
                severity,
                message,
                marker,
                context,
                fields,
                string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFileName(sourceFile),
                sourceLine,
                sourceMember);
        }
        finally
        {
            _isWriting = false;
        }
    }

    public static ITraceScope BeginScope(
        LogSubsystem subsystem,
        string name,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null)
    {
        EnsureInitialized();
        return _logger.BeginScope(subsystem, name, context, fields);
    }

    public static List<LogEntry> GetCompatibilityRecentEntries(int count = 1000)
    {
        var all = CompatibilityBuffer.ToArray();
        if (count <= 0 || all.Length <= count)
        {
            return new List<LogEntry>(all);
        }

        var start = all.Length - count;
        var result = new List<LogEntry>(count);
        for (var i = start; i < all.Length; i++)
        {
            result.Add(all[i]);
        }

        return result;
    }

    public static void ClearCompatibilityBuffer()
    {
        while (CompatibilityBuffer.TryDequeue(out _))
        {
        }

        DocumentCounters.Clear();
    }

    public static IReadOnlyList<EngineLogDocumentCounter> GetPerDocumentCounters(int maxCount = 100)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<EngineLogDocumentCounter>();
        }

        var snapshots = new List<EngineLogDocumentCounter>(Math.Min(maxCount, DocumentCounters.Count));
        foreach (var pair in DocumentCounters)
        {
            snapshots.Add(pair.Value.Snapshot(pair.Key));
        }

        return snapshots
            .OrderByDescending(x => x.TotalCount)
            .ThenByDescending(x => x.LastSequence)
            .Take(maxCount)
            .ToList();
    }

    public static bool ShouldLogOncePerDocument(string documentId, string key)
    {
        return Deduplicator.ShouldLogOncePerDocument(documentId, key);
    }

    public static bool ShouldLogOncePerSession(string key)
    {
        return Deduplicator.ShouldLogOncePerSession(key);
    }

    public static bool ShouldLogRateLimited(string key, TimeSpan window)
    {
        return Deduplicator.ShouldLogRateLimited(key, window);
    }

    public static void WriteOncePerDocument(
        string documentId,
        string key,
        LogSubsystem subsystem,
        LogSeverity severity,
        string message,
        LogMarker marker = LogMarker.None,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null)
    {
        if (!ShouldLogOncePerDocument(documentId, key))
        {
            return;
        }

        Write(subsystem, severity, message, marker, context, fields);
    }

    public static void WriteRateLimited(
        string key,
        TimeSpan window,
        LogSubsystem subsystem,
        LogSeverity severity,
        string message,
        LogMarker marker = LogMarker.None,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null)
    {
        if (!ShouldLogRateLimited(key, window))
        {
            return;
        }

        Write(subsystem, severity, message, marker, context, fields);
    }

    public static void EmitSuppressedSummary()
    {
        var summary = Deduplicator.DrainSuppressedCounts();
        foreach (var pair in summary.OrderByDescending(p => p.Value).Take(100))
        {
            Write(
                LogSubsystem.Verification,
                LogSeverity.Info,
                "Suppressed duplicate log entries summary",
                LogMarker.Fallback,
                default,
                new Dictionary<string, object>
                {
                    ["dedupKey"] = pair.Key,
                    ["suppressedCount"] = pair.Value
                });
        }
    }

    public static string ExportFailureBundle(
        string testId = null,
        string url = null,
        string summary = null,
        int maxEntries = 2000)
    {
        EnsureInitialized();
        return EngineFailureBundleExporter.CreateBundle(
            GetCompatibilityRecentEntries(maxEntries),
            testId,
            url,
            summary);
    }

    public static void PublishExternalEvent(in EngineLogEvent evt)
    {
        EnsureInitialized();
        _logger.WriteRaw(evt);
    }

    private static void EnsureInitialized()
    {
        if (_logger != null)
        {
            return;
        }

        InitializeFromSettings();
    }

    private static EngineLogger BuildLogger(EngineLoggingOptions options)
    {
        var sinks = new List<ILogSink>();
        RingBufferEngineLogSink ring = null;

        if (options.EnableRingBufferSink)
        {
            ring = new RingBufferEngineLogSink(options.RingBufferCapacity);
            sinks.Add(ring);
        }

        // Console sink writes ONLY to Console.WriteLine.
        if (options.EnableConsoleSink)
        {
            sinks.Add(new ConsoleEngineLogSink());
        }

        // Debug sink writes ONLY to System.Diagnostics.Debug.WriteLine.
        if (options.EnableDebugSink)
        {
            sinks.Add(new DebugEngineLogSink());
        }

        if (options.EnableNdjsonSink && !string.IsNullOrWhiteSpace(options.NdjsonFilePath))
        {
            sinks.Add(new NdjsonEngineLogSink(options.NdjsonFilePath));
        }

        if (options.EnableTraceSink && !string.IsNullOrWhiteSpace(options.TraceFilePath))
        {
            sinks.Add(new DiagnosticTraceEngineLogSink(options.TraceFilePath));
        }

        var dispatcher = new EngineLogDispatcher(options.DispatcherQueueCapacity, sinks);
        return new EngineLogger(options, dispatcher, ring);
    }

    /// <summary>
    /// Applies explicit user settings as restrictive gates over the preset.
    /// User settings may further DISABLE a sink the preset selected,
    /// but must NOT re-enable a sink the preset deliberately disabled.
    /// Ring buffer is always gated by the global EnableLogging switch.
    /// </summary>
    private static void ApplySettingsSinkGates(EngineLoggingOptions options, LogSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        // Global on/off gate: if logging is disabled entirely, turn off everything.
        if (!settings.EnableLogging)
        {
            options.Enabled = false;
            options.EnableConsoleSink = false;
            options.EnableDebugSink = false;
            options.EnableNdjsonSink = false;
            options.EnableTraceSink = false;
            options.EnableRingBufferSink = false;
            options.NdjsonFilePath = null;
            options.TraceFilePath = null;
            return;
        }

        // When logging is enabled, apply category filter.
        options.EnabledCategories = (LogCategory)settings.EnabledCategories;

        // Console and Debug are now separate sinks controlled independently.
        // LogToDebug only gates the debugger sink (not console).
        if (!settings.LogToDebug)
        {
            options.EnableDebugSink = false;
        }

        // LogToFile only gates file sinks (NDJSON + trace).
        if (!settings.LogToFile)
        {
            options.EnableNdjsonSink = false;
            options.EnableTraceSink = false;
        }

        // Ring buffer is always enabled when logging is on.
        // It's gated ONLY by EnableLogging — individual settings cannot disable it.
        // (The preset controls it.)

        // Clean up file paths for disabled sinks.
        if (!options.EnableNdjsonSink)
        {
            options.NdjsonFilePath = null;
        }

        if (!options.EnableTraceSink)
        {
            options.TraceFilePath = null;
        }
    }

    private static string BuildNdjsonPath(string configuredPath)
    {
        var logsDir = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : DiagnosticPaths.GetLogsDirectory();

        if (!Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }

        return Path.Combine(logsDir, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    }

    private static string BuildTracePath(string configuredPath)
    {
        var logsDir = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : DiagnosticPaths.GetLogsDirectory();

        if (!Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }

        return Path.Combine(logsDir, $"fenbrowser_trace_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    }

    private static void OnEngineEvent(EngineLogEvent evt)
    {
        UpdatePerDocumentCounter(evt);

        try
        {
            EngineEventWrittenInternal?.Invoke(evt);
        }
        catch
        {
            // no-op
        }

        var entry = new LogEntry
        {
            Timestamp = evt.Header.TimestampUtc.LocalDateTime,
            Category = EngineLogCompatibility.ToLegacyCategory(evt.Header.Subsystem),
            Level = EngineLogCompatibility.ToLegacyLevel(evt.Header.Severity),
            Message = evt.Payload?.MessageTemplate ?? string.Empty,
            CorrelationId = evt.Header.CorrelationId?.ToString(),
            Component = evt.Header.Subsystem.ToString(),
            SourceFile = evt.Payload?.SourceFile,
            SourceLine = evt.Payload?.SourceLine ?? 0,
            MethodName = evt.Payload?.SourceMember,
            Data = evt.Payload?.Fields != null
                ? new Dictionary<string, object>(evt.Payload.Fields)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        };

        if (entry.Data.TryGetValue("durationMs", out var durationValue) &&
            long.TryParse(durationValue?.ToString(), out var durationMs))
        {
            entry.DurationMs = durationMs;
        }

        if (entry.Data.TryGetValue("memoryBytes", out var memoryValue) &&
            long.TryParse(memoryValue?.ToString(), out var memoryBytes))
        {
            entry.MemoryBytes = memoryBytes;
        }

        entry.Data["marker"] = evt.Header.Marker.ToString();
        if (!string.IsNullOrWhiteSpace(evt.Header.Context.Url))
        {
            entry.Data["url"] = evt.Header.Context.Url;
        }

        CompatibilityBuffer.Enqueue(entry);
        while (CompatibilityBuffer.Count > _compatibilityBufferCap)
        {
            CompatibilityBuffer.TryDequeue(out _);
        }

        try
        {
            CompatibilityEntryAdded?.Invoke(entry);
        }
        catch
        {
            // no-op
        }
    }

    private static void UpdatePerDocumentCounter(in EngineLogEvent evt)
    {
        string key = evt.Header.Context.DocumentId;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = evt.Header.Context.NavigationId;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            key = evt.Header.Context.Url;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var counter = DocumentCounters.GetOrAdd(key, _ => new MutableDocumentCounter());
        counter.Update(evt);
    }

    private sealed class MutableDocumentCounter
    {
        private readonly object _sync = new();
        private long _total;
        private long _warn;
        private long _error;
        private long _fatal;
        private long _lastSequence;
        private DateTimeOffset _lastTimestampUtc;
        private string _lastUrl;
        private string _lastTabId;
        private string _lastFrameId;

        public void Update(in EngineLogEvent evt)
        {
            lock (_sync)
            {
                _total++;
                if (evt.Header.Severity >= LogSeverity.Warn)
                {
                    _warn++;
                }

                if (evt.Header.Severity >= LogSeverity.Error)
                {
                    _error++;
                }

                if (evt.Header.Severity == LogSeverity.Fatal)
                {
                    _fatal++;
                }

                _lastSequence = evt.Header.Sequence;
                _lastTimestampUtc = evt.Header.TimestampUtc;
                _lastUrl = evt.Header.Context.Url;
                _lastTabId = evt.Header.Context.TabId;
                _lastFrameId = evt.Header.Context.FrameId;
            }
        }

        public EngineLogDocumentCounter Snapshot(string key)
        {
            lock (_sync)
            {
                return new EngineLogDocumentCounter(
                    key,
                    _total,
                    _warn,
                    _error,
                    _fatal,
                    _lastSequence,
                    _lastTimestampUtc,
                    _lastUrl,
                    _lastTabId,
                    _lastFrameId);
            }
        }
    }
}
