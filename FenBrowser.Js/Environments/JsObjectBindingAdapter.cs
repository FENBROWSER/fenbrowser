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
//   * TryGet invokes accessor getters only when the interpreter supplies an
//     `accessorGet` evaluator (used for the real global and `with` bindings); with
//     no evaluator it falls back to the data-only path.
//   * TrySet does not walk the prototype chain looking for a setter. ECMA-262 [[Set]]
//     does walk, but for top-level globals the receiver is the global object itself
//     and the data path covers it. Setter routing lands when accessor calls are
//     wired through the interpreter.
public sealed class JsObjectBindingAdapter : IGlobalObject
{
    private readonly JsHeap _heap;
    private readonly ObjectHandle _handle;
    // Optional interpreter hook to evaluate an accessor get (the getter function
    // invoked with this binding object as the receiver). When null the adapter
    // falls back to the data-only path (getters surface as "unreadable").
    private readonly Func<ObjectHandle, string, JsValue>? _accessorGet;

    public JsObjectBindingAdapter(
        JsHeap heap, ObjectHandle handle, Func<ObjectHandle, string, JsValue>? accessorGet = null)
    {
        ArgumentNullException.ThrowIfNull(heap);
        _heap = heap;
        _handle = handle;
        _accessorGet = accessorGet;
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
                // Accessor get needs to call the getter with this object as receiver.
                // When the interpreter supplied an evaluator, use it (ECMA-262
                // 9.1.1.2.6 / Get(O, N, O)); otherwise fall back to the lossy path.
                if (_accessorGet is not null)
                {
                    value = _accessorGet(_handle, name);
                    return true;
                }

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

    public bool IsExtensible => _heap.GetObject(_handle).Extensible;

    public bool HasOwnProperty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _heap.GetObject(_handle).TryGetOwnProperty(name, out _);
    }

    public bool IsOwnPropertyConfigurable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        if (!obj.TryGetOwnProperty(name, out var descriptor))
        {
            // No own property -> vacuously not restricted (a fresh declaration can
            // proceed). 9.1.1.4.14 returns false in this case.
            return true;
        }

        return descriptor.Configurable;
    }

    public bool IsOwnDataPropertyWritableEnumerable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var obj = _heap.GetObject(_handle);
        if (!obj.TryGetOwnProperty(name, out var descriptor))
        {
            return false;
        }

        if (descriptor.IsAccessor)
        {
            return false;
        }

        return descriptor.Writable && descriptor.Enumerable;
    }

    private JsObject ResolvePrototype(ObjectHandle prototypeHandle)
        => _heap.GetObject(prototypeHandle);
}
