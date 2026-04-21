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
    public void Write(in EngineLogEvent evt)
    {
        var marker = evt.Header.Marker == LogMarker.None ? string.Empty : $"[{evt.Header.Marker}]";
        var ctx = string.IsNullOrWhiteSpace(evt.Header.Context.Url) ? string.Empty : $" | url={evt.Header.Context.Url}";
        var source = string.IsNullOrWhiteSpace(evt.Payload?.SourceFile) ? string.Empty : $" | source={evt.Payload.SourceFile}:{evt.Payload.SourceLine}";
        var line = $"{evt.Header.TimestampUtc:HH:mm:ss.fff} [{evt.Header.Subsystem}][{evt.Header.Severity}]{marker} {evt.Payload?.MessageTemplate ?? string.Empty}{ctx}{source}";

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // no-op
        }

        try
        {
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch
        {
            // no-op
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

internal sealed class ChromiumTraceEngineLogSink : ILogSink
{
    private readonly string _path;
    private readonly object _lock = new();

    public ChromiumTraceEngineLogSink(string path)
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
        var timestampUs = evt.Header.TimestampUtc.ToUnixTimeMilliseconds() * 1000.0;
        var fields = evt.Payload?.Fields;
        double durationMs = 0;
        var hasDuration = fields != null &&
            fields.TryGetValue("durationMs", out var durationRaw) &&
            double.TryParse(Convert.ToString(durationRaw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out durationMs) &&
            durationMs > 0;

        var traceEvent = new Dictionary<string, object>
        {
            ["name"] = evt.Payload?.MessageTemplate ?? string.Empty,
            ["cat"] = $"renderer.{evt.Header.Subsystem.ToString().ToLowerInvariant()}",
            ["ph"] = hasDuration ? "X" : "i",
            ["ts"] = timestampUs,
            ["pid"] = evt.Header.ProcessId,
            ["tid"] = evt.Header.ThreadId,
            ["args"] = BuildArgs(evt)
        };

        if (hasDuration)
        {
            traceEvent["dur"] = durationMs * 1000.0;
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

    private static Dictionary<string, object> BuildArgs(in EngineLogEvent evt)
    {
        var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["severity"] = evt.Header.Severity.ToString(),
            ["marker"] = evt.Header.Marker.ToString(),
            ["eventId"] = evt.Header.EventId.ToString()
        };

        if (!string.IsNullOrWhiteSpace(evt.Header.Context.Url))
        {
            args["url"] = evt.Header.Context.Url;
        }

        if (!string.IsNullOrWhiteSpace(evt.Header.Context.TestId))
        {
            args["testId"] = evt.Header.Context.TestId;
        }

        if (evt.Payload?.Fields != null)
        {
            foreach (var pair in evt.Payload.Fields.Where(pair => pair.Value != null))
            {
                if (!args.ContainsKey(pair.Key))
                {
                    args[pair.Key] = pair.Value;
                }
            }
        }

        return args;
    }
}
