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
    private long _droppedCount;
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

        if (_queue.TryAdd(evt))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
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
            }
        }
        catch
        {
            // no-op
        }
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
    }
}
