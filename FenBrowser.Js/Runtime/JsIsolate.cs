using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Runtime;

public sealed class JsIsolate
{
    public JsIsolate(JsHeap? heap = null)
    {
        Heap = heap ?? new JsHeap();
    }

    public JsHeap Heap { get; }

    public HandleScope EnterHandleScope() => new(Heap);

    public Handle<ObjectHandle> AllocateObjectInScope(HandleScope scope, JsObject obj, AllocationSite site)
    {
        var handle = Heap.AllocateObject(obj, site);
        return scope.Create(handle);
    }
}
