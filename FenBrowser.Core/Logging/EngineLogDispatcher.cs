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

    private readonly SinkHealth[] _sinkHealth;
    private long _subscriberFailureCount;

    public EngineLogDispatcher(int capacity, List<ILogSink> sinks)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

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
            // Dispose can begin between the first check and this increment. Re-check
            // while we are represented in _enqueueInProgress so CompleteAdding cannot
            // turn a normal shutdown race into an exception at an arbitrary log caller.
            if (_disposed || _queue.IsAddingCompleted)
            {
                return false;
            }

            try
            {
                if (_queue.TryAdd(evt))
                {
                    Interlocked.Increment(ref _acceptedCount);
                    return true;
                }

                // Error/Fatal gets one short backpressure retry. It is not a dropped
                // event unless that retry also fails; the old code incremented the
                // drop counter before retry and therefore over-reported loss.
                if (evt.Header.Severity >= LogSeverity.Error)
                {
                    if (!_disposed && !_queue.IsAddingCompleted &&
                        _queue.TryAdd(evt, millisecondsTimeout: 100))
                    {
                        Interlocked.Increment(ref _acceptedCount);
                        return true;
                    }

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

                IncrementDropCounter(evt.Header.Severity);
                return false;
            }
            catch (InvalidOperationException) when (_disposed || _queue.IsAddingCompleted)
            {
                // BlockingCollection throws when CompleteAdding races TryAdd. Shutdown
                // is an expected state transition, not a logging failure in the caller.
                return false;
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                return false;
            }
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
                    if (health.IsDisabled)
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
                        var consecutiveFailures = health.RecordFailure(ex);
                        if (consecutiveFailures >= 3 && health.TryDisable())
                        {
                            ReportSinkDisabled(health, ex);
                        }
                    }
                }

                Interlocked.Increment(ref _processedCount);
                _progress.Set();
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Normal only if shutdown had to abandon a stuck worker.
        }
        catch (Exception ex)
        {
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
            var snapshot = health.Snapshot();
            Debug.WriteLine(
                $"[EngineLog] Sink '{snapshot.Name}' disabled after {snapshot.ConsecutiveFailures} " +
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
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var started = Stopwatch.GetTimestamp();

        while (Volatile.Read(ref _enqueueInProgress) != 0)
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            _progress.Reset();
            if (Volatile.Read(ref _enqueueInProgress) != 0)
            {
                _progress.Wait(RemainingWait(started, timeout));
            }
        }

        var target = Interlocked.Read(ref _acceptedCount);
        while (Interlocked.Read(ref _processedCount) < target)
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            _progress.Reset();
            if (Interlocked.Read(ref _processedCount) >= target)
            {
                break;
            }

            _progress.Wait(RemainingWait(started, timeout));
        }

        foreach (var sink in _sinks)
        {
            if (sink is BufferedFileLogSink buffered)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }
                buffered.Flush(remaining);
            }
        }

        return true;
    }

    private static TimeSpan RemainingWait(long started, TimeSpan timeout)
    {
        var remaining = timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < TimeSpan.FromMilliseconds(10)
            ? remaining
            : TimeSpan.FromMilliseconds(10);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Let in-flight producers leave TryEnqueue before closing the collection.
        // This dramatically narrows the CompleteAdding/TryAdd race; TryEnqueue also
        // catches the expected shutdown exception as a final guard.
        var waitStarted = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _enqueueInProgress) != 0 &&
               Stopwatch.GetElapsedTime(waitStarted) < TimeSpan.FromMilliseconds(250))
        {
            _progress.Reset();
            if (Volatile.Read(ref _enqueueInProgress) != 0)
            {
                _progress.Wait(TimeSpan.FromMilliseconds(5));
            }
        }

        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        bool workerStopped = false;
        try
        {
            workerStopped = _worker.Join(2000);
        }
        catch
        {
            // best effort below
        }

        // Never dispose sinks/queue out from underneath a worker that is still
        // executing a sink callback. A misbehaving sink can leak shutdown resources,
        // but racing native/file sink disposal is more dangerous and can crash the
        // process. The background worker can still finish after Dispose returns.
        if (!workerStopped)
        {
            return;
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
        private readonly object _sync = new();
        public string Name { get; set; }
        private int _consecutiveFailures;
        private int _totalFailures;
        private string _lastFailureType;
        private string _lastFailureMessage;
        private DateTimeOffset _lastFailureTime;
        private bool _disabled;

        public bool IsDisabled
        {
            get
            {
                lock (_sync)
                {
                    return _disabled;
                }
            }
        }

        public int RecordFailure(Exception ex)
        {
            lock (_sync)
            {
                _consecutiveFailures++;
                _totalFailures++;
                _lastFailureType = ex.GetType().Name;
                _lastFailureMessage = ex.Message;
                _lastFailureTime = DateTimeOffset.UtcNow;
                return _consecutiveFailures;
            }
        }

        public void ResetFailures()
        {
            lock (_sync)
            {
                _consecutiveFailures = 0;
            }
        }

        public bool TryDisable()
        {
            lock (_sync)
            {
                if (_disabled)
                {
                    return false;
                }

                _disabled = true;
                return true;
            }
        }

        public SinkHealthSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new SinkHealthSnapshot(
                    Name ?? "?",
                    !_disabled,
                    _consecutiveFailures,
                    _totalFailures,
                    _lastFailureTime,
                    _lastFailureType ?? string.Empty,
                    _lastFailureMessage ?? string.Empty);
            }
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
