using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FenBrowser.Core.Logging;

internal sealed class ConsoleEngineLogSink : ILogSink
{
    // Throttle: skip console output if we're emitting faster than ~100 events/sec.
    // Console.WriteLine on Windows is synchronous and slow — flooding it during
    // animations/scroll can compete with the render thread and cause frame drops.
    private long _lastWriteTicks;
    private int _burstCount;
    private const int MaxBurst = 20;
    private const long ThrottleIntervalTicks = 10_000_000 / 100; // 100 events/sec = 10ms between

    public void Write(in EngineLogEvent evt)
    {
        var nowTicks = Stopwatch.GetTimestamp();

        // Burst: allow up to MaxBurst events at any rate.
        if (_burstCount >= MaxBurst)
        {
            var elapsed = nowTicks - Interlocked.Read(ref _lastWriteTicks);
            if (elapsed < ThrottleIntervalTicks)
            {
                return; // throttle — don't write to console
            }
        }

        var marker = evt.Header.Marker == LogMarker.None ? string.Empty : $"[{evt.Header.Marker}]";
        var ctx = string.IsNullOrWhiteSpace(evt.Header.Context.Url) ? string.Empty : $" | url={evt.Header.Context.Url}";
        var source = string.IsNullOrWhiteSpace(evt.Payload?.SourceFile) ? string.Empty : $" | source={evt.Payload.SourceFile}:{evt.Payload.SourceLine}";
        var line = $"{evt.Header.TimestampUtc:HH:mm:ss.fff} [{evt.Header.Subsystem}][{evt.Header.Severity}]{marker} {evt.Payload?.MessageTemplate ?? string.Empty}{ctx}{source}";

        try
        {
            Console.WriteLine(line);
            Interlocked.Exchange(ref _lastWriteTicks, nowTicks);
            var bc = Interlocked.Increment(ref _burstCount);
            // Reset burst counter every ~1 second of quiet.
            if (bc > MaxBurst * 10)
            {
                Interlocked.Exchange(ref _burstCount, 0);
            }
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

internal sealed class DebugEngineLogSink : ILogSink
{
    public void Write(in EngineLogEvent evt)
    {
        var marker = evt.Header.Marker == LogMarker.None ? string.Empty : $"[{evt.Header.Marker}]";
        var ctx = string.IsNullOrWhiteSpace(evt.Header.Context.Url) ? string.Empty : $" | url={evt.Header.Context.Url}";
        var source = string.IsNullOrWhiteSpace(evt.Payload?.SourceFile) ? string.Empty : $" | source={evt.Payload.SourceFile}:{evt.Payload.SourceLine}";
        var line = $"{evt.Header.TimestampUtc:HH:mm:ss.fff} [{evt.Header.Subsystem}][{evt.Header.Severity}]{marker} {evt.Payload?.MessageTemplate ?? string.Empty}{ctx}{source}";

        try
        {
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch
        {
            // no-op — debugger output must not crash the engine
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Base class for rotating file sinks. Opens the file only for the
/// duration of each write — no persistent file handle is held, so
/// external readers (tests, log viewers, failure-bundle exporters)
/// can always access the file between events.
/// </summary>
internal abstract class BufferedFileLogSink : ILogSink, IDisposable
{
    private readonly object _lock = new();
    private readonly string _basePath;
    private readonly int _maxFileSizeBytes;
    private readonly int _maxArchivedFiles;
    private readonly int _flushEveryN;
    private readonly int _flushIntervalMs;

    private string _currentPath;
    private long _currentFileSize;
    private int _rotationIndex;

    private FileStream _stream;
    private StreamWriter _writer;
    private int _eventsSinceFlush;
    private long _lastFlushTicks;

    private int _failureCount;
    private volatile string _lastFailureType;
    private volatile string _lastFailureMessage;

    protected BufferedFileLogSink(
        string baseFilePath,
        int maxFileSizeBytes = 10 * 1024 * 1024,
        int maxArchivedFiles = 10,
        int flushEveryN = 200,
        int flushIntervalMs = 500)
    {
        _basePath = baseFilePath ?? throw new ArgumentNullException(nameof(baseFilePath));
        _maxFileSizeBytes = Math.Max(1024 * 1024, maxFileSizeBytes);
        _maxArchivedFiles = Math.Max(1, maxArchivedFiles);
        _flushEveryN = Math.Max(10, flushEveryN);
        _flushIntervalMs = Math.Max(50, flushIntervalMs);

        var dir = Path.GetDirectoryName(_basePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        ResolveCurrentPath();
        EnsureStreamLocked();
    }

    public bool IsHealthy => _failureCount < 3;
    public int FailureCount => _failureCount;
    public string LastFailureType => _lastFailureType;
    public string LastFailureMessage => _lastFailureMessage;
    public string CurrentPath => _currentPath;

    public void Write(in EngineLogEvent evt)
    {
        if (!IsHealthy)
        {
            return;
        }

        var json = FormatEvent(evt);
        if (json == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                _writer.Write(json);
                _writer.Write('\n');
                _currentFileSize += System.Text.Encoding.UTF8.GetByteCount(json) + 1;
                _eventsSinceFlush++;

                var nowTicks = Stopwatch.GetTimestamp();
                var elapsedMs = (nowTicks - _lastFlushTicks) * 1000L / Stopwatch.Frequency;
                var isHighSeverity = evt.Header.Severity >= LogSeverity.Error;

                if (isHighSeverity || _eventsSinceFlush >= _flushEveryN || elapsedMs >= _flushIntervalMs)
                {
                    FlushLocked();
                }

                if (_currentFileSize >= _maxFileSizeBytes)
                {
                    FlushLocked();
                    RotateLocked();
                }

                _failureCount = 0;
            }
            catch (Exception ex)
            {
                MarkFailure(ex);
                CloseStreamLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { FlushLocked(); } catch { }
            CloseStreamLocked();
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        lock (_lock)
        {
            if (_writer == null) return true;
            try { FlushLocked(); return true; }
            catch { return false; }
        }
    }

    protected abstract string FormatEvent(in EngineLogEvent evt);

    private void EnsureStreamLocked()
    {
        if (_writer != null) return;
        try
        {
            _stream = new FileStream(
                _currentPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536);

            _writer = new StreamWriter(_stream, System.Text.Encoding.UTF8, 65536);
            _currentFileSize = _stream.Length;
            _eventsSinceFlush = 0;
            _lastFlushTicks = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            MarkFailure(ex);
        }
    }

    private void FlushLocked()
    {
        _writer?.Flush();
        _stream?.Flush();
        _eventsSinceFlush = 0;
        _lastFlushTicks = Stopwatch.GetTimestamp();
    }

    private void CloseStreamLocked()
    {
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;
    }

    private void ResolveCurrentPath()
    {
        var dir = Path.GetDirectoryName(_basePath) ?? ".";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(_basePath);
        var ext = Path.GetExtension(_basePath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".jsonl";
        }

        _currentPath = _rotationIndex == 0
            ? _basePath
            : Path.Combine(dir, $"{nameWithoutExt}.{_rotationIndex:D3}{ext}");
    }

    private void RotateLocked()
    {
        CloseStreamLocked();
        _rotationIndex++;
        ResolveCurrentPath();
        _currentFileSize = 0;
        DeleteExcessArchives(_maxArchivedFiles - 1);
        EnsureStreamLocked();
    }

    private void DeleteExcessArchives(int maxKeep)
    {
        try
        {
            var dir = Path.GetDirectoryName(_basePath) ?? ".";
            var nameWithoutExt = Path.GetFileNameWithoutExtension(_basePath);
            var ext = Path.GetExtension(_basePath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".jsonl";
            }

            var pattern = $"{nameWithoutExt}.*{ext}";
            var files = Directory.GetFiles(dir, pattern)
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();

            for (var i = maxKeep; i < files.Count; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }

    private void MarkFailure(Exception ex)
    {
        Interlocked.Increment(ref _failureCount);
        _lastFailureType = ex.GetType().Name;
        _lastFailureMessage = ex.Message;

        var fc = _failureCount;
        if (fc == 1 || fc == 3)
        {
            try
            {
                Debug.WriteLine(
                    $"[EngineLog] File sink failure #{fc} (path={_currentPath ?? "?"}): " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
    }
}

internal sealed class NdjsonEngineLogSink : BufferedFileLogSink
{
    public NdjsonEngineLogSink(string path)
        : base(
            baseFilePath: path,
            maxFileSizeBytes: (BrowserSettings.Instance?.Logging?.MaxLogFileSizeMB ?? 10) * 1024 * 1024,
            maxArchivedFiles: BrowserSettings.Instance?.Logging?.MaxArchivedFiles ?? 10)
    {
    }

    protected override string FormatEvent(in EngineLogEvent evt)
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

        return JsonSerializer.Serialize(obj);
    }
}

internal sealed class DiagnosticTraceEngineLogSink : BufferedFileLogSink
{
    public DiagnosticTraceEngineLogSink(string path)
        : base(
            baseFilePath: path,
            maxFileSizeBytes: (BrowserSettings.Instance?.Logging?.MaxLogFileSizeMB ?? 10) * 1024 * 1024,
            maxArchivedFiles: BrowserSettings.Instance?.Logging?.MaxArchivedFiles ?? 10)
    {
    }

    protected override string FormatEvent(in EngineLogEvent evt)
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

        return JsonSerializer.Serialize(traceEvent);
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

internal sealed class RingBufferEngineLogSink : ILogSink
{
    private readonly ConcurrentQueue<EngineLogEvent> _events = new();
    private readonly int _capacity;
    private int _count;

    public RingBufferEngineLogSink(int capacity)
    {
        _capacity = Math.Max(100, capacity);
    }

    public void Write(in EngineLogEvent evt)
    {
        _events.Enqueue(evt);
        var c = Interlocked.Increment(ref _count);

        if (c > _capacity + 256)
        {
            TrimExcess();
        }
        else
        {
            while (c > _capacity && _events.TryDequeue(out _))
            {
                c = Interlocked.Decrement(ref _count);
            }
        }
    }

    private void TrimExcess()
    {
        var excess = Math.Max(0, _count - _capacity + 128);
        for (var i = 0; i < excess; i++)
        {
            if (_events.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
            }
            else
            {
                break;
            }
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
