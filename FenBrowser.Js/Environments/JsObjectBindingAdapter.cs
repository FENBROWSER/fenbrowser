using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// Adapter that exposes a heap-resident JsObject through the IBindingObject surface
// expected by ObjectEnvironmentRecord (and, transitively, GlobalEnvironmentRecord).
//
// The adapter owns no state beyond a (heap, handle) pair - every operation re-resolves
// the handle so that GC compaction or interpreter reentrancy that mutates the heap
// cannot leave the adapter pointing at a stale C# reference.
//
// Known limitations - intentional, deferred to a follow-up:
//   * TryGet does not invoke accessor getters. Callers that need accessor semantics
//     (with statements, certain host globals) will need an interpreter-aware variant
//     that can run the getter. For the bulk of global-binding use the data path is
//     sufficient.
//   * TrySet does not walk the prototype chain looking for a setter. ECMA-262 [[Set]]
//     does walk, but for top-level globals the receiver is the global object itself
//     and the data path covers it. Setter routing lands when accessor calls are
//     wired through the interpreter.
public sealed class JsObjectBindingAdapter : IBindingObject
{
    private readonly JsHeap _heap;
    private readonly ObjectHandle _handle;

    public JsObjectBindingAdapter(JsHeap heap, ObjectHandle handle)
    {
        ArgumentNullException.ThrowIfNull(heap);
        _heap = heap;
        _handle = handle;
    }

    public ObjectHandle? AsObjectHandle => _handle;

    public bool HasProperty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        return obj.TryGetProperty(name, ResolvePrototype, out _);
    }

    public bool TryGet(string name, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        if (obj.TryGetProperty(name, ResolvePrototype, out var descriptor))
        {
            if (descriptor.IsAccessor)
            {
                // Accessor get needs to call the getter function; this adapter has no
                // interpreter handle, so surface as "not readable through here". The
                // env-record translates that into undefined (non-strict) or NotFound
                // (strict), which is the safe-but-lossy behavior documented above.
                value = JsValue.Undefined;
                return false;
            }

            value = descriptor.Value;
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    public bool TrySet(string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        return obj.SetProperty(name, value);
    }

    public bool DefineMutableData(string name, JsValue value, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        return obj.DefineOwnProperty(name, new JsPropertyDescriptor(
            Value: value,
            Writable: true,
            Enumerable: true,
            Configurable: deletable));
    }

    public bool DeleteProperty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        return obj.DeleteProperty(name);
    }

    private JsObject ResolvePrototype(ObjectHandle prototypeHandle)
        => _heap.GetObject(prototypeHandle);
}
