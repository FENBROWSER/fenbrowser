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
public class JsObject : ITraceable
{
    // Current shape describing the property layout. Starts at the root shape
    // (empty) and transitions each time DefineOwnProperty adds a new property.
    private Shape _shape = Shape.Root;

    // Flat property storage indexed by Shape slot. Grows on property addition.
    // Deleted properties are set to null so slot indices stay valid.
    private JsPropertyDescriptor?[] _properties = Array.Empty<JsPropertyDescriptor?>();

    // Symbol-keyed own properties (unchanged — symbols are not shape-tracked).
    private Dictionary<long, JsPropertyDescriptor>? _symbolProperties;

    // Plan H.5: private field brand. Each class with private members gets a unique
    // brand Symbol stored here. The constructor stamps it; private field access
    // checks it. Null means "no brand on this object."
    internal long PrivateBrand { get; set; }

    internal bool HasPrivateBrand(long brand) => PrivateBrand == brand;

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
            _properties[existingSlot] = descriptor;
            BarrierIfObject(descriptor.Value);
            if (descriptor.IsAccessor)
            {
                BarrierIfObject(descriptor.Get);
                BarrierIfObject(descriptor.Set);
            }
            return true;
        }

        // New property: transition shape and grow array.
        _shape = _shape.TransitionTo(key);
        var slot = _shape.PropertyCount - 1;
        if (slot >= _properties.Length)
        {
            var bigger = new JsPropertyDescriptor?[Math.Max(_properties.Length * 2, slot + 1)];
            Array.Copy(_properties, bigger, _properties.Length);
            _properties = bigger;
        }
        _properties[slot] = descriptor;
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

    // Enumerate own string-keyed properties in insertion order. Virtual so
    // exotic objects (String) can yield their synthesised indexed properties.
    public virtual IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        var chain = new List<(string key, int slot)>();
        for (Shape? s = _shape; s != null && s != Shape.Root; s = s.Parent)
            chain.Add((s.AddedProperty!, s.AddedSlot));
        chain.Reverse();

        foreach (var (key, slot) in chain)
        {
            if (_properties[slot] is { } desc)
                yield return new KeyValuePair<string, JsPropertyDescriptor>(key, desc);
        }
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
        DefineOwnProperty(key, new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        return true;
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
            if (descriptor is not { } d) continue;
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
}
