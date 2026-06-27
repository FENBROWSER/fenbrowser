using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FenBrowser.Core.Logging;

internal sealed class ConsoleEngineLogSink : ILogSink
{
    private readonly bool _writeConsole;
    private readonly bool _writeDebug;

    public ConsoleEngineLogSink(bool writeConsole = true, bool writeDebug = true)
    {
        _writeConsole = writeConsole;
        _writeDebug = writeDebug;
    }

    public void Write(in EngineLogEvent evt)
    {
        var marker = evt.Header.Marker == LogMarker.None ? string.Empty : $"[{evt.Header.Marker}]";
        var ctx = string.IsNullOrWhiteSpace(evt.Header.Context.Url) ? string.Empty : $" | url={evt.Header.Context.Url}";
        var source = string.IsNullOrWhiteSpace(evt.Payload?.SourceFile) ? string.Empty : $" | source={evt.Payload.SourceFile}:{evt.Payload.SourceLine}";
        var line = $"{evt.Header.TimestampUtc:HH:mm:ss.fff} [{evt.Header.Subsystem}][{evt.Header.Severity}]{marker} {evt.Payload?.MessageTemplate ?? string.Empty}{ctx}{source}";

        if (_writeConsole)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // no-op
            }
        }

        if (_writeDebug)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch
            {
                // no-op
            }
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class NdjsonEngineLogSink : ILogSink
{
    private readonly string _path;
    private readonly object _lock = new();

    public NdjsonEngineLogSink(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Write(in EngineLogEvent evt)
    {
        var fields = evt.Payload?.Fields;
        var obj = new Dictionary<string, object>
        {
            ["timestampUtc"] = evt.Header.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            ["sequence"] = evt.Header.Sequence,
            ["processId"] = evt.Header.ProcessId,
            ["threadId"] = evt.Header.ThreadId,
            ["subsystem"] = evt.Header.Subsystem.ToString(),
            ["severity"] = evt.Header.Severity.ToString(),
            ["marker"] = evt.Header.Marker.ToString(),
            ["eventId"] = evt.Header.EventId,
            ["parentEventId"] = evt.Header.ParentEventId,
            ["correlationId"] = evt.Header.CorrelationId,
            ["context"] = evt.Header.Context,
            ["message"] = evt.Payload?.MessageTemplate ?? string.Empty,
            ["sourceFile"] = evt.Payload?.SourceFile,
            ["sourceLine"] = evt.Payload?.SourceLine ?? 0,
            ["sourceMember"] = evt.Payload?.SourceMember,
            ["fields"] = fields
        };

        var json = JsonSerializer.Serialize(obj);
        lock (_lock)
        {
            ResilientFileWriter.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class RingBufferEngineLogSink : ILogSink
{
    private readonly ConcurrentQueue<EngineLogEvent> _events = new();
    private readonly int _capacity;

    public RingBufferEngineLogSink(int capacity)
    {
        _capacity = Math.Max(100, capacity);
    }

    public void Write(in EngineLogEvent evt)
    {
        _events.Enqueue(evt);
        while (_events.Count > _capacity)
        {
            _events.TryDequeue(out _);
        }
    }

    public List<EngineLogEvent> Snapshot(int count)
    {
        var all = _events.ToArray();
        if (count <= 0 || all.Length <= count)
        {
            return new List<EngineLogEvent>(all);
        }

        var start = all.Length - count;
        var list = new List<EngineLogEvent>(count);
        for (var i = start; i < all.Length; i++)
        {
            list.Add(all[i]);
        }

        return list;
    }

    public void Dispose()
    {
    }
}

internal sealed class DiagnosticTraceEngineLogSink : ILogSink
{
    private readonly string _path;
    private readonly object _lock = new();

    public DiagnosticTraceEngineLogSink(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Write(in EngineLogEvent evt)
    {
        var data = BuildData(evt);
        var traceEvent = new Dictionary<string, object>
        {
            ["ts"] = evt.Header.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            ["level"] = evt.Header.Severity.ToString().ToUpperInvariant(),
            ["category"] = ToDiagnosticCategory(evt.Header.Subsystem, data),
            ["event"] = ResolveEventName(evt),
            ["session_id"] = evt.Header.Context.BrowserSessionId,
            ["process_id"] = evt.Header.ProcessId,
            ["thread_id"] = evt.Header.ThreadId,
            ["nav_id"] = evt.Header.Context.NavigationId,
            ["frame_id"] = evt.Header.Context.FrameId,
            ["doc_id"] = evt.Header.Context.DocumentId,
            ["realm_id"] = evt.Header.Context.RealmId,
            ["script_id"] = evt.Header.Context.ScriptId,
            ["request_id"] = evt.Header.Context.RequestId,
            ["task_id"] = evt.Header.Context.TaskId,
            ["message"] = evt.Payload?.MessageTemplate ?? string.Empty,
            ["data"] = data
        };

        if (evt.Header.EventId != Guid.Empty)
        {
            traceEvent["event_id"] = evt.Header.EventId.ToString();
        }

        var json = JsonSerializer.Serialize(traceEvent);
        lock (_lock)
        {
            ResilientFileWriter.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public void Dispose()
    {
    }

    private static Dictionary<string, object> BuildData(in EngineLogEvent evt)
    {
        var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["marker"] = evt.Header.Marker.ToString(),
            ["sequence"] = evt.Header.Sequence,
            ["subsystem"] = evt.Header.Subsystem.ToString()
        };

        if (!string.IsNullOrWhiteSpace(evt.Header.Context.Url))
        {
            data["url"] = evt.Header.Context.Url;
        }

        if (!string.IsNullOrWhiteSpace(evt.Header.Context.ResourceUrl))
        {
            data["resource_url"] = evt.Header.Context.ResourceUrl;
        }

        if (!string.IsNullOrWhiteSpace(evt.Header.Context.TestId))
        {
            data["test_id"] = evt.Header.Context.TestId;
        }

        if (!string.IsNullOrWhiteSpace(evt.Payload?.SourceFile))
        {
            data["source_file"] = evt.Payload.SourceFile;
            data["source_line"] = evt.Payload.SourceLine;
            data["source_member"] = evt.Payload.SourceMember;
        }

        if (evt.Payload?.Fields != null)
        {
            foreach (var pair in evt.Payload.Fields.Where(pair => pair.Value != null))
            {
                if (!data.ContainsKey(pair.Key))
                {
                    data[pair.Key] = pair.Value;
                }
            }
        }

        return data;
    }

    private static string ResolveEventName(in EngineLogEvent evt)
    {
        if (evt.Payload?.Fields != null &&
            evt.Payload.Fields.TryGetValue("event", out var eventName) &&
            !string.IsNullOrWhiteSpace(Convert.ToString(eventName, CultureInfo.InvariantCulture)))
        {
            return Convert.ToString(eventName, CultureInfo.InvariantCulture);
        }

        return evt.Payload?.MessageTemplate ?? evt.Header.Subsystem.ToString();
    }

    private static string ToDiagnosticCategory(LogSubsystem subsystem, IReadOnlyDictionary<string, object> data)
    {
        if (data != null &&
            data.TryGetValue("traceCategory", out var categoryOverride) &&
            !string.IsNullOrWhiteSpace(Convert.ToString(categoryOverride, CultureInfo.InvariantCulture)))
        {
            return Convert.ToString(categoryOverride, CultureInfo.InvariantCulture);
        }

        return subsystem switch
        {
            LogSubsystem.Html => "HTMLParser",
            LogSubsystem.Dom => "DOM",
            LogSubsystem.CssParse => "CSSParser",
            LogSubsystem.Style => "Style",
            LogSubsystem.Layout => "Layout",
            LogSubsystem.Paint => "Paint",
            LogSubsystem.Compositor => "Compositor",
            LogSubsystem.Js => "JS",
            LogSubsystem.Net => "Network",
            LogSubsystem.Fetch => "Network",
            LogSubsystem.Url => "Navigation",
            LogSubsystem.Nav => "Navigation",
            LogSubsystem.Event => "EventLoop",
            LogSubsystem.Storage => "Storage",
            LogSubsystem.Ipc => "IPC",
            LogSubsystem.Security => "Security",
            LogSubsystem.ProcessIsolation => "Process",
            LogSubsystem.Img => "ResourceLoader",
            LogSubsystem.Verification => "Performance",
            _ => "Performance"
        };
    }
}
