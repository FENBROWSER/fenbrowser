using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Logging;

public sealed class CompatibilityEventEnvelope
{
    public const string CurrentSchema = "fenbrowser.compatibility-event.v1";

    public string Schema { get; init; } = CurrentSchema;
    public int SchemaVersion { get; init; } = 1;
    public string EventId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public long Sequence { get; init; }
    public int ProcessId { get; init; }
    public int ThreadId { get; init; }
    public string Domain { get; init; }
    public string Kind { get; init; }
    public string Outcome { get; init; }
    public string Subsystem { get; init; }
    public string Severity { get; init; }
    public string Marker { get; init; }
    public string CorrelationId { get; init; }
    public string BrowserSessionId { get; init; }
    public string NavigationId { get; init; }
    public string TabId { get; init; }
    public string FrameId { get; init; }
    public string DocumentId { get; init; }
    public string RealmId { get; init; }
    public string RequestId { get; init; }
    public string ScriptId { get; init; }
    public string TaskId { get; init; }
    public string Url { get; init; }
    public string ResourceUrl { get; init; }
    public string Source { get; init; }
    public string Message { get; init; }
    public IReadOnlyDictionary<string, object> Fields { get; init; }
}

public enum WebIdlBehaviorKind
{
    ReturnType,
    Descriptor,
    Semantics
}

public static class CompatibilityEventRecorder
{
    private const int DefaultCapacity = 5000;
    private static readonly object Sync = new();
    private static readonly Dictionary<Guid, LinkedListNode<CompatibilityEventEnvelope>> ByEventId = new();
    private static readonly LinkedList<CompatibilityEventEnvelope> Events = new();
    private static int _capacity = DefaultCapacity;

    public static void ConfigureCapacity(int capacity)
    {
        lock (Sync)
        {
            _capacity = Math.Max(1000, capacity);
            TrimToCapacity();
        }
    }

    public static void Record(in EngineLogEvent evt)
    {
        if (evt.Header.EventId == Guid.Empty)
        {
            return;
        }

        var envelope = Normalize(evt);
        lock (Sync)
        {
            if (ByEventId.ContainsKey(evt.Header.EventId))
            {
                return;
            }

            var node = Events.AddLast(envelope);
            ByEventId.Add(evt.Header.EventId, node);
            TrimToCapacity();
        }
    }

    public static IReadOnlyList<CompatibilityEventEnvelope> Snapshot(int maxCount = DefaultCapacity)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<CompatibilityEventEnvelope>();
        }

        lock (Sync)
        {
            return Events
                .Reverse()
                .Take(maxCount)
                .Reverse()
                .ToArray();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Events.Clear();
            ByEventId.Clear();
        }
    }

    public static void RecordWebIdlBehaviorMismatch(
        string interfaceName,
        string memberName,
        WebIdlBehaviorKind behavior,
        object expected,
        object actual,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> details = null)
    {
        var fields = details != null
            ? new Dictionary<string, object>(details, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        fields["eventName"] = "WebIdlBehaviorMismatch";
        fields["eventKind"] = "webidl.behavior-mismatch";
        fields["interfaceName"] = interfaceName ?? string.Empty;
        fields["memberName"] = memberName ?? string.Empty;
        fields["behavior"] = behavior switch
        {
            WebIdlBehaviorKind.ReturnType => "return-type",
            WebIdlBehaviorKind.Descriptor => "descriptor",
            WebIdlBehaviorKind.Semantics => "semantics",
            _ => throw new ArgumentOutOfRangeException(nameof(behavior))
        };
        fields["expected"] = expected;
        fields["actual"] = actual;

        var webIdlContext = context with { SpecArea = "WebIDL" };
        EngineLog.Write(
            LogSubsystem.Js,
            LogSeverity.Warn,
            $"WebIDL behavior mismatch: {interfaceName}.{memberName} ({fields["behavior"]})",
            LogMarker.SpecGap,
            webIdlContext,
            fields);
    }

    internal static CompatibilityEventEnvelope Normalize(in EngineLogEvent evt)
    {
        var fields = evt.Payload?.Fields != null
            ? new Dictionary<string, object>(evt.Payload.Fields, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var kind = FirstNonEmpty(fields, "eventKind", "eventName", "event")
            ?? evt.Header.Subsystem.ToString().ToLowerInvariant() + ".message";
        var correlationId = evt.Header.CorrelationId?.ToString("N")
            ?? FirstNonEmpty(fields, "ipcCorrelationId", "correlationId");

        return new CompatibilityEventEnvelope
        {
            EventId = evt.Header.EventId.ToString("N"),
            TimestampUtc = evt.Header.TimestampUtc,
            Sequence = evt.Header.Sequence,
            ProcessId = evt.Header.ProcessId,
            ThreadId = evt.Header.ThreadId,
            Domain = ClassifyDomain(evt.Header.Subsystem, kind, fields),
            Kind = kind,
            Outcome = ClassifyOutcome(evt.Header.Severity, evt.Header.Marker, kind),
            Subsystem = evt.Header.Subsystem.ToString(),
            Severity = evt.Header.Severity.ToString(),
            Marker = evt.Header.Marker.ToString(),
            CorrelationId = correlationId,
            BrowserSessionId = evt.Header.Context.BrowserSessionId ?? FirstNonEmpty(fields, "browserSessionId"),
            NavigationId = evt.Header.Context.NavigationId ?? FirstNonEmpty(fields, "navigationId"),
            TabId = evt.Header.Context.TabId ?? FirstNonEmpty(fields, "tabId"),
            FrameId = evt.Header.Context.FrameId ?? FirstNonEmpty(fields, "frameId"),
            DocumentId = evt.Header.Context.DocumentId ?? FirstNonEmpty(fields, "documentId"),
            RealmId = evt.Header.Context.RealmId ?? FirstNonEmpty(fields, "realmId"),
            RequestId = evt.Header.Context.RequestId ?? FirstNonEmpty(fields, "requestId"),
            ScriptId = evt.Header.Context.ScriptId ?? FirstNonEmpty(fields, "scriptId"),
            TaskId = evt.Header.Context.TaskId ?? FirstNonEmpty(fields, "taskId"),
            Url = evt.Header.Context.Url ?? FirstNonEmpty(fields, "url", "siteUrl"),
            ResourceUrl = evt.Header.Context.ResourceUrl ?? FirstNonEmpty(fields, "resourceUrl", "scriptUrl"),
            Source = evt.Header.Context.Source ?? FirstNonEmpty(fields, "source"),
            Message = evt.Payload?.MessageTemplate ?? string.Empty,
            Fields = fields
        };
    }

    private static string ClassifyDomain(
        LogSubsystem subsystem,
        string kind,
        IReadOnlyDictionary<string, object> fields)
    {
        var traceCategory = FirstNonEmpty(fields, "traceCategory");
        if (string.Equals(kind, "MissingApiObserved", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(traceCategory, "MissingAPI", StringComparison.OrdinalIgnoreCase))
        {
            return "missing-api";
        }

        if (kind.StartsWith("webidl.", StringComparison.OrdinalIgnoreCase))
        {
            return "webidl";
        }

        return subsystem switch
        {
            LogSubsystem.Js => "javascript",
            LogSubsystem.Event => "event",
            LogSubsystem.Dom => "dom",
            LogSubsystem.Net or LogSubsystem.Fetch => "network",
            LogSubsystem.Ipc or LogSubsystem.ProcessIsolation => "ipc",
            _ => "engine"
        };
    }

    private static string ClassifyOutcome(LogSeverity severity, LogMarker marker, string kind)
    {
        if (severity >= LogSeverity.Error ||
            kind.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("crashed", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            return "failure";
        }

        if (marker is LogMarker.Unimplemented or LogMarker.Partial or LogMarker.SpecGap ||
            kind.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "compatibility-gap";
        }

        return severity >= LogSeverity.Warn ? "warning" : "observed";
    }

    private static string FirstNonEmpty(IReadOnlyDictionary<string, object> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static void TrimToCapacity()
    {
        while (Events.Count > _capacity)
        {
            var oldest = Events.First;
            if (oldest == null)
            {
                break;
            }

            Events.RemoveFirst();
            if (Guid.TryParse(oldest.Value.EventId, out var eventId))
            {
                ByEventId.Remove(eventId);
            }
        }
    }
}
