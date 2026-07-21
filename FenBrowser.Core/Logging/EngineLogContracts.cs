using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace FenBrowser.Core.Logging;

/// <summary>
/// Log severity levels for engine diagnostics.
///
/// Policy:
///   Fatal  — Process or critical subsystem cannot safely continue.
///   Error  — Requested operation failed and was not recovered.
///   Warn   — Behaviour degraded, fallback used, or significant
///            implementation gap encountered. Emit once per cause.
///   Info   — Low-frequency lifecycle milestone (nav start/finish).
///   Debug  — Developer investigation details and non-fatal gaps.
///   Trace  — High-frequency per-frame/per-node/per-property data.
/// </summary>
public enum LogSeverity
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5
}

/// <summary>
/// Diagnostic markers for classifying log events.
///
/// Usage:
///   Unimplemented — Known standards feature not implemented.
///   Partial       — Feature implemented incompletely.
///   Fallback      — A fallback path was selected.
///   Recovered     — A real failure occurred but the engine recovered.
///   SpecGap       — Behaviour unclear, incomplete, or awaiting standards work.
///   EngineBug     — Internal engine defect.
///   Invariant     — Internal invariant violation.
///   Unexpected    — Unexpected condition that does not fit a more precise
///                   marker. Do NOT use as a generic catch-all.
/// </summary>
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
    string RealmId = null,
    string ScriptId = null,
    string TaskId = null,
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
    public LogCategory EnabledCategories { get; set; } = LogCategory.All;
    public Dictionary<LogSubsystem, LogSeverity> SubsystemOverrides { get; } = new();
    public int DispatcherQueueCapacity { get; set; } = 32768;
    public bool EnableConsoleSink { get; set; } = true;
    public bool EnableDebugSink { get; set; } = true;
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
    // Bounded limits to prevent unbounded growth.
    private const int MaxPerDocumentKeys = 20000;
    private const int MaxPerSessionKeys = 10000;
    private const int MaxRateLimitKeys = 5000;
    private const int MaxSuppressedKeys = 5000;
    private const int MaxKeyLength = 256;
    private static readonly TimeSpan SessionKeyTtl = TimeSpan.FromHours(1);

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

        var combined = TruncateKey(string.Concat(documentId.AsSpan(), "::", key.AsSpan()));

        lock (_sync)
        {
            if (_perDocument.ContainsKey(combined))
            {
                IncrementSuppressedLocked(combined);
                return false;
            }

            if (_perDocument.Count >= MaxPerDocumentKeys)
            {
                EvictOldest(_perDocument);
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

        var k = TruncateKey(key);

        lock (_sync)
        {
            if (_perSession.ContainsKey(k))
            {
                IncrementSuppressedLocked(k);
                return false;
            }

            if (_perSession.Count >= MaxPerSessionKeys)
            {
                EvictOldest(_perSession);
            }

            _perSession[k] = 1;
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

        var k = TruncateKey(key);
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            if (_rateLimit.TryGetValue(k, out var last) && (now - last) < window)
            {
                IncrementSuppressedLocked(k);
                return false;
            }

            if (_rateLimit.Count >= MaxRateLimitKeys)
            {
                PruneExpiredRateLimitKeysLocked(now);
            }

            _rateLimit[k] = now;
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

    /// <summary>
    /// Clears all per-document dedup keys for the given document.
    /// Call when a document is destroyed, navigation is replaced, or a tab is closed.
    /// </summary>
    public void ClearDocument(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return;
        }

        var prefix = documentId + "::";

        lock (_sync)
        {
            var toRemove = _perDocument.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            foreach (var key in toRemove)
            {
                _perDocument.Remove(key);
            }
        }
    }

    /// <summary>
    /// Clears all per-session dedup keys. Call on session reset.
    /// </summary>
    public void ClearSession()
    {
        lock (_sync)
        {
            _perSession.Clear();
            _suppressedCounts.Clear();
        }
    }

    /// <summary>
    /// Prunes rate-limit entries older than their window.
    /// Safe to call periodically; cheap when there's nothing to do.
    /// </summary>
    public void PruneExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            PruneExpiredRateLimitKeysLocked(now.UtcDateTime);
        }
    }

    private void PruneExpiredRateLimitKeysLocked(DateTime now)
    {
        // Remove rate-limit entries older than SessionKeyTtl.
        var cutoff = now - SessionKeyTtl;
        var toRemove = _rateLimit
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _rateLimit.Remove(key);
        }
    }

    private void IncrementSuppressedLocked(string key)
    {
        if (_suppressedCounts.TryGetValue(key, out var count))
        {
            _suppressedCounts[key] = count + 1;
            return;
        }

        if (_suppressedCounts.Count >= MaxSuppressedKeys)
        {
            // Drop the smallest-count entry to make room.
            var min = _suppressedCounts.MinBy(kvp => kvp.Value);
            _suppressedCounts.Remove(min.Key);
        }

        _suppressedCounts[key] = 1;
    }

    private static string TruncateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return key.Length <= MaxKeyLength ? key : key.Substring(0, MaxKeyLength);
    }

    private static void TruncateKey(ReadOnlySpan<char> key, Span<char> destination)
    {
        var length = Math.Min(key.Length, MaxKeyLength);
        key.Slice(0, length).CopyTo(destination);
    }

    private static string TruncateKey(ReadOnlySpan<char> docSpan, ReadOnlySpan<char> sep, ReadOnlySpan<char> keySpan)
    {
        var totalLen = docSpan.Length + sep.Length + keySpan.Length;
        if (totalLen <= MaxKeyLength)
        {
            return string.Concat(docSpan, sep, keySpan);
        }

        Span<char> buf = stackalloc char[MaxKeyLength];
        var pos = 0;
        var docLen = Math.Min(docSpan.Length, MaxKeyLength / 2);
        docSpan.Slice(0, docLen).CopyTo(buf.Slice(pos));
        pos += docLen;
        sep.CopyTo(buf.Slice(pos));
        pos += sep.Length;
        var remaining = MaxKeyLength - pos;
        if (remaining > 0)
        {
            var keyLen = Math.Min(keySpan.Length, remaining);
            keySpan.Slice(0, keyLen).CopyTo(buf.Slice(pos));
            pos += keyLen;
        }

        return new string(buf.Slice(0, pos));
    }

    private static void EvictOldest(Dictionary<string, byte> dict)
    {
        // Remove ~10% of entries to make room. Simple FIFO approximation:
        // remove first enumerated entries since Dictionary doesn't track order.
        var toRemove = Math.Max(1, dict.Count / 10);
        var count = 0;
        foreach (var key in dict.Keys.ToList())
        {
            if (count >= toRemove) break;
            dict.Remove(key);
            count++;
        }
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
        if ((category & LogCategory.HtmlParsing) != 0) return LogSubsystem.Html;
        if ((category & LogCategory.DOM) != 0) return LogSubsystem.Dom;
        if ((category & LogCategory.CssParsing) != 0) return LogSubsystem.CssParse;
        if ((category & LogCategory.CSS) != 0) return LogSubsystem.Style;
        if ((category & LogCategory.Layout) != 0) return LogSubsystem.Layout;
        if ((category & LogCategory.Paint) != 0) return LogSubsystem.Paint;
        if ((category & (LogCategory.JavaScript | LogCategory.JsExecution)) != 0) return LogSubsystem.Js;
        if ((category & LogCategory.Network) != 0) return LogSubsystem.Net;
        if ((category & LogCategory.Navigation) != 0) return LogSubsystem.Nav;
        if ((category & LogCategory.Events) != 0) return LogSubsystem.Event;
        if ((category & LogCategory.Storage) != 0) return LogSubsystem.Storage;
        if ((category & LogCategory.DevTools) != 0) return LogSubsystem.DevTools;
        if ((category & LogCategory.Verification) != 0) return LogSubsystem.Verification;
        if ((category & LogCategory.Security) != 0) return LogSubsystem.Security;
        if ((category & LogCategory.Accessibility) != 0) return LogSubsystem.Accessibility;
        if ((category & LogCategory.ProcessIsolation) != 0) return LogSubsystem.ProcessIsolation;
        if ((category & LogCategory.Images) != 0) return LogSubsystem.Img;
        if ((category & LogCategory.FeatureGaps) != 0) return LogSubsystem.Verification;
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
