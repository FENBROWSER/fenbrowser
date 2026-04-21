using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FenBrowser.Core.Logging;

internal sealed class EngineLogger : IEngineLogger, IDisposable
{
    private readonly EngineLoggingOptions _options;
    private readonly EngineLogDispatcher _dispatcher;
    private readonly RingBufferEngineLogSink _ringBuffer;
    private long _sequence;

    public event Action<EngineLogEvent> EventWritten;

    public EngineLogger(EngineLoggingOptions options, EngineLogDispatcher dispatcher, RingBufferEngineLogSink ringBuffer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _ringBuffer = ringBuffer;
    }

    public bool IsEnabled(LogSubsystem subsystem, LogSeverity severity)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        if (_options.SubsystemOverrides.TryGetValue(subsystem, out var overrideLevel))
        {
            return severity >= overrideLevel;
        }

        return severity >= _options.GlobalMinimumSeverity;
    }

    public void Write(
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
        Guid? correlationId = null)
    {
        if (!IsEnabled(subsystem, severity))
        {
            return;
        }

        var header = new EngineLogHeader(
            DateTimeOffset.UtcNow,
            Interlocked.Increment(ref _sequence),
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            subsystem,
            severity,
            marker,
            Guid.NewGuid(),
            parentEventId,
            correlationId ?? ParseGuid(context.NavigationId),
            context);

        var payload = new EngineLogPayload
        {
            MessageTemplate = messageTemplate ?? string.Empty,
            Fields = fields,
            SourceFile = sourceFile,
            SourceLine = sourceLine,
            SourceMember = sourceMember
        };

        var evt = new EngineLogEvent(header, payload);
        if (!_dispatcher.TryEnqueue(evt))
        {
            _ringBuffer?.Write(evt);
        }

        try
        {
            EventWritten?.Invoke(evt);
        }
        catch
        {
            // no-op
        }
    }

    public void WriteRaw(in EngineLogEvent evt)
    {
        if (!IsEnabled(evt.Header.Subsystem, evt.Header.Severity))
        {
            return;
        }

        if (!_dispatcher.TryEnqueue(evt))
        {
            _ringBuffer?.Write(evt);
        }

        try
        {
            EventWritten?.Invoke(evt);
        }
        catch
        {
            // no-op
        }
    }

    public ITraceScope BeginScope(
        LogSubsystem subsystem,
        string name,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null,
        Guid? parentEventId = null,
        Guid? correlationId = null)
    {
        return new TraceScope(this, subsystem, name, context, fields);
    }

    public List<EngineLogEvent> GetRingBufferSnapshot(int count = 1000)
    {
        return _ringBuffer?.Snapshot(count) ?? new List<EngineLogEvent>();
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
    }

    private static Guid? ParseGuid(string raw)
    {
        if (Guid.TryParse(raw, out var id))
        {
            return id;
        }

        return null;
    }

    private sealed class TraceScope : ITraceScope
    {
        private readonly EngineLogger _logger;
        private readonly Stopwatch _sw;
        private readonly LogSubsystem _subsystem;
        private readonly string _name;
        private readonly EngineLogContext _context;
        private readonly Dictionary<string, object> _fields;
        private bool _failed;
        private string _failureReason;
        private LogMarker _failureMarker;

        public Guid EventId { get; } = Guid.NewGuid();

        public TraceScope(
            EngineLogger logger,
            LogSubsystem subsystem,
            string name,
            in EngineLogContext context,
            IReadOnlyDictionary<string, object> fields)
        {
            _logger = logger;
            _subsystem = subsystem;
            _name = name ?? "Scope";
            _context = context;
            _fields = fields != null
                ? new Dictionary<string, object>(fields)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _sw = Stopwatch.StartNew();
        }

        public void AddField(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _fields[key] = value;
        }

        public void MarkSuccess()
        {
            _failed = false;
            _failureReason = null;
            _failureMarker = LogMarker.None;
        }

        public void MarkFailure(string reason, LogMarker marker = LogMarker.None)
        {
            _failed = true;
            _failureReason = reason;
            _failureMarker = marker;
        }

        public void Dispose()
        {
            _sw.Stop();
            _fields["ScopeName"] = _name;
            _fields["durationMs"] = _sw.Elapsed.TotalMilliseconds;

            if (_failed)
            {
                _fields["failureReason"] = _failureReason ?? "unknown";
                _logger.Write(
                    _subsystem,
                    LogSeverity.Error,
                    "{ScopeName} failed",
                    _failureMarker == LogMarker.None ? LogMarker.Unexpected : _failureMarker,
                    _context,
                    _fields,
                    sourceMember: nameof(TraceScope));
                return;
            }

            _logger.Write(
                _subsystem,
                LogSeverity.Debug,
                "{ScopeName} completed",
                LogMarker.None,
                _context,
                _fields,
                sourceMember: nameof(TraceScope));
        }
    }
}
