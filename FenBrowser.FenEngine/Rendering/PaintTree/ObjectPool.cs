using System;
using System.Collections.Concurrent;

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

public ObjectPool(Func<T> factory = null, Action<T> reset = null)
{
_factory = factory ?? (() => new T());
_reset = reset;
}

public T Get()
{
return _objects.TryPop(out var item) ? item : _factory();
}

public void Return(T obj)
{
if (obj == null) return;
_reset?.Invoke(obj);
_objects.Push(obj);
}

public void Clear()
{
while (_objects.TryPop(out _)) { }
}
}
}
