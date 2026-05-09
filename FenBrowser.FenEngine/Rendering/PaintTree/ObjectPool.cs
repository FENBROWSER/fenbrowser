using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
/// <summary>
/// Simple object pool for reducing GC pressure on frequently allocated objects
/// </summary>
internal class ObjectPool<T> where T : new()
{
private readonly Stack<T> _objects = new Stack<T>();
private readonly Func<T> _factory;
private readonly Action<T> _reset;

public ObjectPool(Func<T> factory = null, Action<T> reset = null)
{
_factory = factory ?? (() => new T());
_reset = reset;
}

public T Get()
{
lock (_objects)
{
return _objects.Count > 0 ? _objects.Pop() : _factory();
}
}

public void Return(T obj)
{
if (obj == null) return;
_reset?.Invoke(obj);
lock (_objects)
{
_objects.Push(obj);
}
}

public void Clear()
{
lock (_objects)
{
_objects.Clear();
}
}
}
}