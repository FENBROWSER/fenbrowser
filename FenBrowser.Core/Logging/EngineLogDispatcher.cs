using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Core.Logging;

internal sealed class EngineLogDispatcher : IDisposable
{
    private readonly BlockingCollection<EngineLogEvent> _queue;
    private readonly List<ILogSink> _sinks;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _progress = new(false);
    private long _droppedCount;
    private long _acceptedCount;
    private long _processedCount;
    private int _enqueueInProgress;
    private volatile bool _disposed;

    public EngineLogDispatcher(int capacity, List<ILogSink> sinks)
    {
        _queue = new BlockingCollection<EngineLogEvent>(capacity);
        _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
        _worker = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "EngineLogDispatcher"
        };
        _worker.Start();
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

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

            Interlocked.Increment(ref _droppedCount);
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _enqueueInProgress);
            _progress.Set();
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
                    try
                    {
                        _sinks[i].Write(evt);
                    }
                    catch
                    {
                        // sink failures must not break logging
                    }
                }

                Interlocked.Increment(ref _processedCount);
                _progress.Set();
            }
        }
        catch
        {
            // no-op
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _enqueueInProgress) != 0)
        {
            var enqueueRemaining = deadline - DateTime.UtcNow;
            if (enqueueRemaining <= TimeSpan.Zero)
            {
                return false;
            }

            _progress.Reset();
            if (Volatile.Read(ref _enqueueInProgress) != 0)
            {
                _progress.Wait(enqueueRemaining);
            }
        }

        var target = Interlocked.Read(ref _acceptedCount);
        if (Interlocked.Read(ref _processedCount) >= target)
        {
            return true;
        }

        while (Interlocked.Read(ref _processedCount) < target)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            _progress.Reset();
            if (Interlocked.Read(ref _processedCount) >= target)
            {
                return true;
            }

            _progress.Wait(remaining);
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
}
