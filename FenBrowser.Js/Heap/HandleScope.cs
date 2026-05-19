using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class HandleScope : IDisposable
{
    private readonly JsHeap _heap;
    private readonly int _rootMark;
    private bool _disposed;

    public HandleScope(JsHeap heap)
    {
        _heap = heap;
        _rootMark = heap.RootCount;
    }

    public Handle<T> Create<T>(T handle)
        where T : struct
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HandleScope));
        }

        if (handle is ObjectHandle objectHandle)
        {
            _heap.PushRoot(objectHandle);
        }

        return new Handle<T>(handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _heap.PopRootsTo(_rootMark);
        _disposed = true;
    }
}
