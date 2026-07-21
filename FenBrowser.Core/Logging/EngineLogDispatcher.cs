using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FenBrowser.Core.Logging;

internal sealed class EngineLogDispatcher : IDisposable
{
    private readonly BlockingCollection<EngineLogEvent> _queue;
    private readonly List<ILogSink> _sinks;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _progress = new(false);

    // Per-severity drop tracking.
    private long _droppedTrace;
    private long _droppedDebug;
    private long _droppedInfo;
    private long _droppedWarn;
    private long _droppedError;
    private long _droppedFatal;
    private long _acceptedCount;
    private long _processedCount;
    private int _enqueueInProgress;
    private volatile bool _disposed;

    // Per-sink failure tracking.
    private readonly SinkHealth[] _sinkHealth;
    private long _subscriberFailureCount;

    public EngineLogDispatcher(int capacity, List<ILogSink> sinks)
    {
        _queue = new BlockingCollection<EngineLogEvent>(capacity);
        _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
        _sinkHealth = new SinkHealth[_sinks.Count];
        for (var i = 0; i < _sinks.Count; i++)
        {
            _sinkHealth[i] = new SinkHealth { Name = _sinks[i].GetType().Name };
        }

        _worker = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "EngineLogDispatcher"
        };
        _worker.Start();
    }

    public long DroppedTrace => Interlocked.Read(ref _droppedTrace);
    public long DroppedDebug => Interlocked.Read(ref _droppedDebug);
    public long DroppedInfo => Interlocked.Read(ref _droppedInfo);
    public long DroppedWarn => Interlocked.Read(ref _droppedWarn);
    public long DroppedError => Interlocked.Read(ref _droppedError);
    public long DroppedFatal => Interlocked.Read(ref _droppedFatal);
    public long SubscriberFailureCount => Interlocked.Read(ref _subscriberFailureCount);

    public long TotalDropped => DroppedTrace + DroppedDebug + DroppedInfo + DroppedWarn + DroppedError + DroppedFatal;

    public bool TryEnqueue(in EngineLogEvent evt)
    {
        if (_disposed)
        {
            return false;
        }

        Interlocked.Increment(ref _enqueueInProgress);
        try
        {
            if (_queue.TryAdd(evt))
            {
                Interlocked.Increment(ref _acceptedCount);
                return true;
            }

            // Severity-aware drop tracking.
            IncrementDropCounter(evt.Header.Severity);

            // Emergency path for Error and Fatal: wait briefly and retry once.
            if (evt.Header.Severity >= LogSeverity.Error)
            {
                if (_queue.TryAdd(evt, millisecondsTimeout: 100))
                {
                    Interlocked.Increment(ref _acceptedCount);
                    return true;
                }

                // Last resort: write minimal emergency message to debugger.
                try
                {
                    Debug.WriteLine(
                        $"[EngineLog EMERGENCY] Dropped {evt.Header.Severity} event: " +
                        $"{evt.Header.Subsystem} {evt.Payload?.MessageTemplate ?? "?"}");
                }
                catch
                {
                    // absolute last resort
                }
            }

            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _enqueueInProgress);
            _progress.Set();
        }
    }

    private void IncrementDropCounter(LogSeverity severity)
    {
        switch (severity)
        {
            case LogSeverity.Trace: Interlocked.Increment(ref _droppedTrace); break;
            case LogSeverity.Debug: Interlocked.Increment(ref _droppedDebug); break;
            case LogSeverity.Info: Interlocked.Increment(ref _droppedInfo); break;
            case LogSeverity.Warn: Interlocked.Increment(ref _droppedWarn); break;
            case LogSeverity.Error: Interlocked.Increment(ref _droppedError); break;
            case LogSeverity.Fatal: Interlocked.Increment(ref _droppedFatal); break;
        }
    }

    private void DrainLoop()
    {
        try
        {
            foreach (var evt in _queue.GetConsumingEnumerable())
            {
                for (int i = 0; i < _sinks.Count; i++)
                {
                    var health = _sinkHealth[i];
                    if (health.Disabled)
                    {
                        continue;
                    }

                    try
                    {
                        _sinks[i].Write(evt);
                        health.ResetFailures();
                    }
                    catch (Exception ex)
                    {
                        health.RecordFailure(ex);
                        if (health.ConsecutiveFailures >= 3)
                        {
                            health.Disable();
                            ReportSinkDisabled(health, ex);
                        }
                    }
                }

                Interlocked.Increment(ref _processedCount);
                _progress.Set();
            }
        }
        catch (Exception ex)
        {
            // Drain-loop failure is a last resort: report once via debugger.
            try
            {
                Debug.WriteLine(
                    $"[EngineLog] Dispatcher drain loop terminated: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // absolute last resort
            }
        }
    }

    private static void ReportSinkDisabled(SinkHealth health, Exception ex)
    {
        try
        {
            Debug.WriteLine(
                $"[EngineLog] Sink '{health.Name}' disabled after {health.ConsecutiveFailures} " +
                $"consecutive failures. Last: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // absolute last resort
        }
    }

    public IReadOnlyList<SinkHealthSnapshot> GetSinkHealth()
    {
        var snapshots = new SinkHealthSnapshot[_sinkHealth.Length];
        for (var i = 0; i < _sinkHealth.Length; i++)
        {
            snapshots[i] = _sinkHealth[i].Snapshot();
        }

        return snapshots;
    }

    /// <summary>
    /// Records a subscriber (event handler) failure without breaking the dispatch loop.
    /// Rate-limited: only the first few failures are counted.
    /// </summary>
    public void RecordSubscriberFailure()
    {
        var count = Interlocked.Increment(ref _subscriberFailureCount);
        if (count <= 5)
        {
            try
            {
                Debug.WriteLine($"[EngineLog] Event subscriber failure #{count}");
            }
            catch
            {
                // absolute last resort
            }
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        var deadlineTicks = Stopwatch.GetTimestamp() +
            (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        // Wait for in-progress enqueues.
        while (Volatile.Read(ref _enqueueInProgress) != 0)
        {
            if (Stopwatch.GetTimestamp() >= deadlineTicks)
            {
                return false;
            }

            _progress.Reset();
            if (Volatile.Read(ref _enqueueInProgress) != 0)
            {
                _progress.Wait(TimeSpan.FromMilliseconds(10));
            }
        }

        // Wait for all accepted events to be processed.
        var target = Interlocked.Read(ref _acceptedCount);
        while (Interlocked.Read(ref _processedCount) < target)
        {
            if (Stopwatch.GetTimestamp() >= deadlineTicks)
            {
                return false;
            }

            _progress.Reset();
            if (Interlocked.Read(ref _processedCount) >= target)
            {
                break;
            }

            _progress.Wait(TimeSpan.FromMilliseconds(10));
        }

        // Flush buffered file sinks explicitly.
        foreach (var sink in _sinks)
        {
            if (sink is BufferedFileLogSink buffered)
            {
                buffered.Flush(timeout);
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        try
        {
            _worker.Join(2000);
        }
        catch
        {
            // no-op
        }

        for (int i = 0; i < _sinks.Count; i++)
        {
            try
            {
                _sinks[i].Dispose();
            }
            catch
            {
                // no-op
            }
        }

        _queue.Dispose();
        _progress.Dispose();
    }

    internal sealed class SinkHealth
    {
        public string Name { get; set; }
        public int ConsecutiveFailures { get; private set; }
        public int TotalFailures { get; private set; }
        public string LastFailureType { get; private set; }
        public string LastFailureMessage { get; private set; }
        public DateTimeOffset LastFailureTime { get; private set; }
        public bool Disabled { get; private set; }

        public void RecordFailure(Exception ex)
        {
            ConsecutiveFailures++;
            TotalFailures++;
            LastFailureType = ex.GetType().Name;
            LastFailureMessage = ex.Message;
            LastFailureTime = DateTimeOffset.UtcNow;
        }

        public void ResetFailures()
        {
            ConsecutiveFailures = 0;
        }

        public void Disable()
        {
            Disabled = true;
        }

        public SinkHealthSnapshot Snapshot()
        {
            return new SinkHealthSnapshot(
                Name ?? "?",
                !Disabled,
                ConsecutiveFailures,
                TotalFailures,
                LastFailureTime,
                LastFailureType ?? string.Empty,
                LastFailureMessage ?? string.Empty);
        }
    }
}

public readonly record struct SinkHealthSnapshot(
    string Name,
    bool Healthy,
    int ConsecutiveFailures,
    int TotalFailures,
    DateTimeOffset LastFailureTime,
    string LastFailureType,
    string LastFailureMessage);
