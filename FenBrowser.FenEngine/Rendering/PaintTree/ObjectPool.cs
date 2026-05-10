using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FenBrowser.FenEngine.Rendering
{
/// <summary>
/// Simple object pool for reducing GC pressure on frequently allocated objects
/// </summary>
internal class ObjectPool<T> where T : new()
{
private readonly ConcurrentStack<T> _objects = new ConcurrentStack<T>();
private readonly Func<T> _factory;
private readonly Action<T> _reset;
private readonly int _maxRetained;
private int _retainedCount;
private long _droppedReturns;

public ObjectPool(Func<T> factory = null, Action<T> reset = null, int maxRetained = 1024)
{
_factory = factory ?? (() => new T());
_reset = reset;
_maxRetained = Math.Max(1, maxRetained);
}

internal int MaxRetained => _maxRetained;
internal int RetainedCount => Math.Max(0, Volatile.Read(ref _retainedCount));
internal long DroppedReturns => Interlocked.Read(ref _droppedReturns);

public T Get()
{
if (_objects.TryPop(out var item))
{
    Interlocked.Decrement(ref _retainedCount);
    return item;
}

return _factory();
}

public void Return(T obj)
{
if (obj == null) return;
_reset?.Invoke(obj);
var retainedAfterIncrement = Interlocked.Increment(ref _retainedCount);
if (retainedAfterIncrement > _maxRetained)
{
    Interlocked.Decrement(ref _retainedCount);
    Interlocked.Increment(ref _droppedReturns);
    return;
}

_objects.Push(obj);
}

public void Clear()
{
while (_objects.TryPop(out _)) { }
Interlocked.Exchange(ref _retainedCount, 0);
}
}
}
