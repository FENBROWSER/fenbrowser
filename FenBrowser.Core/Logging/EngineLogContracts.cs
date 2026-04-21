using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace FenBrowser.Core.Logging;

public enum LogSeverity
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5
}

public enum LogMarker
{
    None = 0,
    Unimplemented,
    Partial,
    Stub,
    Fallback,
    Recovered,
    SpecGap,
    EngineBug,
    Invariant,
    Unexpected
}

public enum LogSubsystem
{
    General = 0,
    Doc,
    Html,
    Dom,
    CssParse,
    Style,
    Layout,
    Paint,
    Compositor,
    Img,
    Font,
    Js,
    Net,
    Fetch,
    Url,
    Nav,
    Event,
    Storage,
    Ipc,
    DevTools,
    Verification,
    Security,
    Accessibility,
    ProcessIsolation
}

public readonly record struct EngineLogContext(
    string BrowserSessionId = null,
    string NavigationId = null,
    string DocumentId = null,
    string FrameId = null,
    string TabId = null,
    string Url = null,
    string Referrer = null,
    string TestId = null,
    string RequestId = null,
    string NodeDescription = null,
    string CssSelector = null,
    string ResourceUrl = null,
    string SpecArea = null,
    string Source = null);

public readonly record struct EngineLogHeader(
    DateTimeOffset TimestampUtc,
    long Sequence,
    int ProcessId,
    int ThreadId,
    LogSubsystem Subsystem,
    LogSeverity Severity,
    LogMarker Marker,
    Guid EventId,
    Guid? ParentEventId,
    Guid? CorrelationId,
    EngineLogContext Context);

public sealed class EngineLogPayload
{
    public string MessageTemplate { get; init; }
    public IReadOnlyDictionary<string, object> Fields { get; init; }
    public string SourceFile { get; init; }
    public int SourceLine { get; init; }
    public string SourceMember { get; init; }
}

public readonly record struct EngineLogEvent(EngineLogHeader Header, EngineLogPayload Payload);

public readonly record struct EngineLogDocumentCounter(
    string DocumentKey,
    long TotalCount,
    long WarnCount,
    long ErrorCount,
    long FatalCount,
    long LastSequence,
    DateTimeOffset LastTimestampUtc,
    string LastUrl,
    string LastTabId,
    string LastFrameId);

public interface ITraceScope : IDisposable
{
    Guid EventId { get; }
    void AddField(string key, object value);
    void MarkSuccess();
    void MarkFailure(string reason, LogMarker marker = LogMarker.None);
}

public interface IEngineLogger
{
    bool IsEnabled(LogSubsystem subsystem, LogSeverity severity);
    void Write(
        LogSubsystem subsystem,
        LogSeverity severity,
        string messageTemplate,
        LogMarker marker = LogMarker.None,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null,
        string sourceFile = null,
        int sourceLine = 0,
        string sourceMember = null,
        Guid? parentEventId = null,
        Guid? correlationId = null);

    ITraceScope BeginScope(
        LogSubsystem subsystem,
        string name,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null,
        Guid? parentEventId = null,
        Guid? correlationId = null);
}

public interface ILogSink : IDisposable
{
    void Write(in EngineLogEvent evt);
}

public sealed class EngineLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public LogSeverity GlobalMinimumSeverity { get; set; } = LogSeverity.Info;
    public Dictionary<LogSubsystem, LogSeverity> SubsystemOverrides { get; } = new();
    public int DispatcherQueueCapacity { get; set; } = 32768;
    public bool EnableConsoleSink { get; set; } = true;
    public bool EnableNdjsonSink { get; set; } = true;
    public bool EnableRingBufferSink { get; set; } = true;
    public int RingBufferCapacity { get; set; } = 20000;
    public string NdjsonFilePath { get; set; }
    public bool EnableTraceSink { get; set; }
    public string TraceFilePath { get; set; }
}

public interface ILogDeduplicator
{
    bool ShouldLogOncePerDocument(string documentId, string key);
    bool ShouldLogOncePerSession(string key);
    bool ShouldLogRateLimited(string key, TimeSpan window);
    IReadOnlyDictionary<string, int> DrainSuppressedCounts();
}

public sealed class EngineLogDeduplicator : ILogDeduplicator
{
    private readonly Dictionary<string, byte> _perDocument = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte> _perSession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _rateLimit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _suppressedCounts = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public bool ShouldLogOncePerDocument(string documentId, string key)
    {
        if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        lock (_sync)
        {
            var combined = string.Concat(documentId, "::", key);
            if (_perDocument.ContainsKey(combined))
            {
                IncrementSuppressedLocked(combined);
                return false;
            }

            _perDocument[combined] = 1;
            return true;
        }
    }

    public bool ShouldLogOncePerSession(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        lock (_sync)
        {
            if (_perSession.ContainsKey(key))
            {
                IncrementSuppressedLocked(key);
                return false;
            }

            _perSession[key] = 1;
            return true;
        }
    }

    public bool ShouldLogRateLimited(string key, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        if (window <= TimeSpan.Zero)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (_rateLimit.TryGetValue(key, out var last) && (now - last) < window)
            {
                IncrementSuppressedLocked(key);
                return false;
            }

            _rateLimit[key] = now;
            return true;
        }
    }

    public IReadOnlyDictionary<string, int> DrainSuppressedCounts()
    {
        lock (_sync)
        {
            var snapshot = new Dictionary<string, int>(_suppressedCounts, StringComparer.Ordinal);
            _suppressedCounts.Clear();
            return snapshot;
        }
    }

    private void IncrementSuppressedLocked(string key)
    {
        if (_suppressedCounts.TryGetValue(key, out var count))
        {
            _suppressedCounts[key] = count + 1;
            return;
        }

        _suppressedCounts[key] = 1;
    }
}

public static class EngineLogCompatibility
{
    public static LogSeverity FromLegacyLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => LogSeverity.Trace,
            LogLevel.Debug => LogSeverity.Debug,
            LogLevel.Info => LogSeverity.Info,
            LogLevel.Warn => LogSeverity.Warn,
            LogLevel.Error => LogSeverity.Error,
            _ => LogSeverity.Info
        };
    }

    public static LogLevel ToLegacyLevel(LogSeverity level)
    {
        return level switch
        {
            LogSeverity.Trace => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            LogSeverity.Info => LogLevel.Info,
            LogSeverity.Warn => LogLevel.Warn,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Fatal => LogLevel.Error,
            _ => LogLevel.Info
        };
    }

    public static LogSubsystem FromLegacyCategory(LogCategory category)
    {
        if (category.HasFlag(LogCategory.HtmlParsing)) return LogSubsystem.Html;
        if (category.HasFlag(LogCategory.DOM)) return LogSubsystem.Dom;
        if (category.HasFlag(LogCategory.CssParsing)) return LogSubsystem.CssParse;
        if (category.HasFlag(LogCategory.CSS)) return LogSubsystem.Style;
        if (category.HasFlag(LogCategory.Layout)) return LogSubsystem.Layout;
        if (category.HasFlag(LogCategory.Paint)) return LogSubsystem.Paint;
        if (category.HasFlag(LogCategory.JavaScript) || category.HasFlag(LogCategory.JsExecution)) return LogSubsystem.Js;
        if (category.HasFlag(LogCategory.Network)) return LogSubsystem.Net;
        if (category.HasFlag(LogCategory.Navigation)) return LogSubsystem.Nav;
        if (category.HasFlag(LogCategory.Events)) return LogSubsystem.Event;
        if (category.HasFlag(LogCategory.Storage)) return LogSubsystem.Storage;
        if (category.HasFlag(LogCategory.DevTools)) return LogSubsystem.DevTools;
        if (category.HasFlag(LogCategory.Verification)) return LogSubsystem.Verification;
        if (category.HasFlag(LogCategory.Security)) return LogSubsystem.Security;
        if (category.HasFlag(LogCategory.Accessibility)) return LogSubsystem.Accessibility;
        if (category.HasFlag(LogCategory.ProcessIsolation)) return LogSubsystem.ProcessIsolation;
        if (category.HasFlag(LogCategory.Images)) return LogSubsystem.Img;
        if (category.HasFlag(LogCategory.FeatureGaps)) return LogSubsystem.Verification;
        return LogSubsystem.General;
    }

    public static LogCategory ToLegacyCategory(LogSubsystem subsystem)
    {
        return subsystem switch
        {
            LogSubsystem.Html => LogCategory.HtmlParsing,
            LogSubsystem.Dom => LogCategory.DOM,
            LogSubsystem.CssParse => LogCategory.CssParsing,
            LogSubsystem.Style => LogCategory.CSS,
            LogSubsystem.Layout => LogCategory.Layout,
            LogSubsystem.Paint => LogCategory.Paint,
            LogSubsystem.Js => LogCategory.JsExecution,
            LogSubsystem.Net => LogCategory.Network,
            LogSubsystem.Fetch => LogCategory.Network,
            LogSubsystem.Nav => LogCategory.Navigation,
            LogSubsystem.Event => LogCategory.Events,
            LogSubsystem.Storage => LogCategory.Storage,
            LogSubsystem.DevTools => LogCategory.DevTools,
            LogSubsystem.Verification => LogCategory.Verification,
            LogSubsystem.Security => LogCategory.Security,
            LogSubsystem.Accessibility => LogCategory.Accessibility,
            LogSubsystem.ProcessIsolation => LogCategory.ProcessIsolation,
            LogSubsystem.Img => LogCategory.Images,
            _ => LogCategory.General
        };
    }
}
