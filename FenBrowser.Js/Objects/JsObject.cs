using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 ordinary object with shape-based property storage (plan §31).
//
// Properties are stored in a flat JsPropertyDescriptor[] array indexed by the
// slot number obtained from Shape.TryGetSlot(). Shapes form a parent-linked
// tree and only grow — deletion marks the slot as absent rather than shrinking
// the array, so existing inline caches stay valid.
//
// The older Dictionary<string, JsPropertyDescriptor> storage is replaced by
// Shape + array. For enumeration and legacy paths, EnumerateOwnProperties
// walks the array and Shape lineage simultaneously.
// Marks an object as carrying a spec internal slot that Object.prototype.toString
// (20.1.3.6) maps to a builtin tag: [[ParameterMap]] → "Arguments", [[ErrorData]] → "Error".
internal enum BuiltinTagSlot
{
    None,
    Arguments,
    Error,
}

public class JsObject : ITraceable
{
    // Current shape describing the property layout. Starts at the root shape
    // (empty) and transitions each time DefineOwnProperty adds a new property.
    private Shape _shape = Shape.Root;

    // Flat property storage indexed by Shape slot. Grows on property addition.
    // Deleted properties are set to null so slot indices stay valid.
    private JsPropertyDescriptor?[] _properties = Array.Empty<JsPropertyDescriptor?>();

    // ECMA-262 10.1.11.1: own string keys enumerate in property-creation order. A
    // deleted-then-readded property counts as a *new* creation and must move to the
    // end. Shapes reuse the original slot (to keep inline caches valid), so the shape
    // chain alone no longer reflects creation order after a delete+re-add. This
    // parallel per-slot sequence number records the true creation order; a re-add
    // gets a fresh number so it sorts last. Indexed by Shape slot, grows with
    // _properties. 0 = unassigned (slot never held a live property).
    private int[] _insertionSeq = Array.Empty<int>();
    private int _nextSeq = 1;

    // Symbol-keyed own properties (unchanged — symbols are not shape-tracked).
    private Dictionary<long, JsPropertyDescriptor>? _symbolProperties;

    // Plan H.5: private field brand. Each class with private members gets a unique
    // brand Symbol stored here. The constructor stamps it; private field access
    // checks it. Null means "no brand on this object."
    internal long PrivateBrand { get; set; }

    internal bool HasPrivateBrand(long brand) => PrivateBrand == brand;

    // ECMA-262 20.1.3.6 Object.prototype.toString uses internal-slot presence
    // ([[ParameterMap]], [[ErrorData]]) to pick the builtin tag. We model those
    // slots as a marker here; ordinary objects leave it None.
    internal BuiltinTagSlot ToStringTagSlot { get; set; } = BuiltinTagSlot.None;

    // ECMA-262 immutable prototype exotic object (e.g. %Object.prototype%): its
    // [[SetPrototypeOf]] succeeds only when the new value equals the current one.
    internal bool ImmutablePrototype { get; set; }

    public ObjectHandle? PrototypeHandle { get; private set; }

    public bool Extensible { get; private set; } = true;

    // Tier 4 #22: GC bookkeeping. Set by JsHeap.AllocateObject when this
    // object is registered with the heap. WriteBarrier on every
    // object-valued property write keeps the generational remembered set
    // accurate without forcing every callsite to remember to barrier.
    internal ObjectHandle? OwnerHandle;
    internal JsHeap? OwnerHeap;

    private void BarrierIfObject(JsValue value)
    {
        if (value.Tag == JsValueTag.Object &&
            OwnerHandle is { } owner &&
            OwnerHeap is { } heap)
        {
            heap.WriteBarrier(owner, value.AsObjectHandle());
        }
    }

    // Internal accessors for inline caches.
    internal Shape CurrentShape => _shape;
    internal JsPropertyDescriptor?[] PropertyArray => _properties;

    public void PreventExtensions()
    {
        Extensible = false;
    }

    // ECMA-262 9.1.6 [[DefineOwnProperty]].
    public virtual bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (_shape.TryGetSlot(key, out var existingSlot))
        {
            // Re-adding a previously deleted property is a new creation: give it a
            // fresh sequence so it enumerates last (10.1.11.1). Redefining a live
            // property preserves its existing creation order.
            if (_properties[existingSlot] is null)
                _insertionSeq[existingSlot] = _nextSeq++;
            _properties[existingSlot] = descriptor;
            BarrierIfObject(descriptor.Value);
            if (descriptor.IsAccessor)
            {
                BarrierIfObject(descriptor.Get);
                BarrierIfObject(descriptor.Set);
            }
            return true;
        }

        // New property: object must be extensible (ECMA-262 9.1.6.3 step 3.b).
        if (!Extensible)
            return false;

        // New property: transition shape and grow array.
        _shape = _shape.TransitionTo(key);
        var slot = _shape.PropertyCount - 1;
        if (slot >= _properties.Length)
        {
            var newLen = Math.Max(_properties.Length * 2, slot + 1);
            var bigger = new JsPropertyDescriptor?[newLen];
            Array.Copy(_properties, bigger, _properties.Length);
            _properties = bigger;
            var biggerSeq = new int[newLen];
            Array.Copy(_insertionSeq, biggerSeq, _insertionSeq.Length);
            _insertionSeq = biggerSeq;
        }
        _properties[slot] = descriptor;
        _insertionSeq[slot] = _nextSeq++;
        BarrierIfObject(descriptor.Value);
        if (descriptor.IsAccessor)
        {
            BarrierIfObject(descriptor.Get);
            BarrierIfObject(descriptor.Set);
        }
        return true;
    }

    // Own property lookup via Shape → slot → array. Null slot = deleted.
    // Virtual so exotic objects (String) can synthesise computed properties
    // (indexed character access) on demand.
    public virtual bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (_shape.TryGetSlot(key, out var slot) && _properties[slot] is { } desc)
        {
            descriptor = desc;
            return true;
        }
        descriptor = default;
        return false;
    }

    // Enumerate own string-keyed properties. ECMA-262 10.1.11.1 OrdinaryOwnPropertyKeys:
    // array-index keys first in ascending numeric order, then the remaining string keys
    // in insertion (property-creation) order. Virtual so exotic objects (String) can
    // yield their synthesised indexed properties.
    public virtual IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        var chain = new List<(string key, int slot)>();
        for (Shape? s = _shape; s != null && s != Shape.Root; s = s.Parent)
            chain.Add((s.AddedProperty!, s.AddedSlot));
        chain.Reverse();

        List<(uint idx, string key, int slot)>? integerKeys = null;
        List<(int seq, string key, int slot)>? stringKeys = null;
        var deleteReordered = false;
        foreach (var (key, slot) in chain)
        {
            if (IsArrayIndexKey(key, out var idx))
            {
                (integerKeys ??= new List<(uint, string, int)>()).Add((idx, key, slot));
            }
            else
            {
                // Chain order is shape-transition order; _insertionSeq is the true
                // creation order. They diverge only after a delete+re-add, in which
                // case the seq for this slot won't match its chain position.
                if (stringKeys is { Count: > 0 } && _insertionSeq[slot] < stringKeys[^1].seq)
                    deleteReordered = true;
                (stringKeys ??= new List<(int, string, int)>()).Add((_insertionSeq[slot], key, slot));
            }
        }

        // Fast path: no array-index keys and no delete-induced reordering, so the
        // shape-chain order already matches the spec enumeration order.
        if (integerKeys is null && !deleteReordered)
        {
            foreach (var (key, slot) in chain)
            {
                if (_properties[slot] is { } desc)
                    yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
            }

            yield break;
        }

        if (integerKeys is not null)
        {
            integerKeys.Sort((a, b) => a.idx.CompareTo(b.idx));
            foreach (var (_, key, slot) in integerKeys)
            {
                if (_properties[slot] is { } desc)
                    yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
            }
        }

        if (stringKeys is not null)
        {
            if (deleteReordered)
                stringKeys.Sort((a, b) => a.seq.CompareTo(b.seq));
            foreach (var (_, key, slot) in stringKeys)
            {
                if (_properties[slot] is { } desc)
                    yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
            }
        }
    }

    // ECMA-262 6.1.7: an array index is a canonical numeric string whose value is an
    // integer in [0, 2^32 - 1). Used to order own keys (integer indices come first).
    internal static bool IsArrayIndexKey(string key, out uint index)
    {
        index = 0;
        if (string.IsNullOrEmpty(key) || key.Length > 10)
            return false;
        if (key.Length > 1 && key[0] == '0')
            return false;   // no leading zeros — not a canonical numeric string

        ulong result = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
                return false;
            result = (result * 10) + (ulong)(c - '0');
        }

        if (result >= 4294967295UL)   // 2^32 - 1 is not itself an array index
            return false;

        index = (uint)result;
        return true;
    }

    // Symbol-keyed property access.
    public bool DefineOwnSymbolProperty(long symbolId, JsPropertyDescriptor descriptor)
    {
        _symbolProperties ??= new Dictionary<long, JsPropertyDescriptor>();
        _symbolProperties[symbolId] = descriptor;
        BarrierIfObject(descriptor.Value);
        if (descriptor.IsAccessor)
        {
            BarrierIfObject(descriptor.Get);
            BarrierIfObject(descriptor.Set);
        }
        return true;
    }

    public bool TryGetOwnSymbolProperty(long symbolId, out JsPropertyDescriptor descriptor)
    {
        if (_symbolProperties is not null && _symbolProperties.TryGetValue(symbolId, out descriptor))
            return true;
        descriptor = default;
        return false;
    }

    public IEnumerable<KeyValuePair<long, JsPropertyDescriptor>> EnumerateOwnSymbolProperties()
    {
        if (_symbolProperties is null)
            yield break;
        foreach (var pair in _symbolProperties)
            yield return pair;
    }

    public bool TryGetSymbolProperty(long symbolId, Func<ObjectHandle, JsObject> prototypeResolver, out JsPropertyDescriptor descriptor)
    {
        if (TryGetOwnSymbolProperty(symbolId, out descriptor))
            return true;
        if (PrototypeHandle is { } proto)
            return prototypeResolver(proto).TryGetSymbolProperty(symbolId, prototypeResolver, out descriptor);
        descriptor = default;
        return false;
    }

    public bool DeleteSymbolProperty(long symbolId)
    {
        if (_symbolProperties is null) return false;
        if (_symbolProperties.TryGetValue(symbolId, out var existing) && !existing.Configurable)
            return false;
        return _symbolProperties.Remove(symbolId);
    }

    // Full property lookup: own + prototype chain walk.
    public bool TryGetProperty(string key, Func<ObjectHandle, JsObject> prototypeResolver, out JsPropertyDescriptor descriptor)
    {
        if (TryGetOwnProperty(key, out descriptor))
            return true;
        if (PrototypeHandle is { } proto)
            return prototypeResolver(proto).TryGetProperty(key, prototypeResolver, out descriptor);
        descriptor = default;
        return false;
    }

    public virtual bool SetProperty(string key, JsValue value)
    {
        if (_shape.TryGetSlot(key, out var slot) && _properties[slot] is { } existing)
        {
            if (!existing.Writable) return false;
            _properties[slot] = existing with { Value = value };
            BarrierIfObject(value);
            return true;
        }
        return DefineOwnProperty(key, new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
    }

    public virtual bool DeleteProperty(string key)
    {
        if (_shape.TryGetSlot(key, out var slot) && _properties[slot] is { } existing)
        {
            if (!existing.Configurable) return false;
            _properties[slot] = null;
            return true;
        }
        return false;
    }

    public void SetPrototype(ObjectHandle? prototypeHandle)
    {
        PrototypeHandle = prototypeHandle;
        if (prototypeHandle is { } proto && OwnerHandle is { } owner && OwnerHeap is { } heap)
        {
            heap.WriteBarrier(owner, proto);
        }
    }

    public virtual void Trace(IHeapTracer tracer)
    {
        if (PrototypeHandle is { } proto)
            tracer.Trace(proto);

        foreach (var descriptor in _properties)
        {
            if (descriptor is { } d)
                TraceDescriptor(tracer, d);
        }

        // Symbol-keyed properties hold live references too — e.g. an object's
        // [Symbol.iterator] method. Omitting them let the GC reclaim the target
        // (a generator's @@iterator function, say) and surfaced as a "Stale heap
        // handle." on the next for-of over that object.
        if (_symbolProperties is not null)
        {
            foreach (var pair in _symbolProperties)
                TraceDescriptor(tracer, pair.Value);
        }
    }

    private static void TraceDescriptor(IHeapTracer tracer, JsPropertyDescriptor d)
    {
        if (d.IsAccessor)
        {
            if (d.Get.Tag == JsValueTag.Object)
                tracer.Trace(d.Get.AsObjectHandle());
            if (d.Set.Tag == JsValueTag.Object)
                tracer.Trace(d.Set.AsObjectHandle());
        }
        else if (d.Value.Tag == JsValueTag.Object)
        {
            tracer.Trace(d.Value.AsObjectHandle());
        }
    }
}
