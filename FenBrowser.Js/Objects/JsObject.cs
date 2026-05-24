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

    // Symbol-keyed own properties (unchanged — symbols are not shape-tracked
    // and won't benefit from ICs in the initial implementation).
    private Dictionary<long, JsPropertyDescriptor>? _symbolProperties;

    public ObjectHandle? PrototypeHandle { get; private set; }

    public bool Extensible { get; private set; } = true;

    // Internal accessors for inline caches.
    internal Shape CurrentShape => _shape;
    internal JsPropertyDescriptor?[] PropertyArray => _properties;

    public void PreventExtensions()
    {
        Extensible = false;
    }

    // ECMA-262 9.1.6 [[DefineOwnProperty]].
    public bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (_shape.TryGetSlot(key, out var existingSlot))
        {
            _properties[existingSlot] = descriptor;
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
        return true;
    }

    // Own property lookup via Shape → slot → array. Null slot = deleted.
    public bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (_shape.TryGetSlot(key, out var slot) && _properties[slot] is { } desc)
        {
            descriptor = desc;
            return true;
        }
        descriptor = default;
        return false;
    }

    // Enumerate own string-keyed properties in insertion order.
    public IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
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

    // Symbol-keyed property access (unchanged).
    public bool DefineOwnSymbolProperty(long symbolId, JsPropertyDescriptor descriptor)
    {
        _symbolProperties ??= new Dictionary<long, JsPropertyDescriptor>();
        _symbolProperties[symbolId] = descriptor;
        return true;
    }

    public bool TryGetOwnSymbolProperty(long symbolId, out JsPropertyDescriptor descriptor)
    {
        if (_symbolProperties is not null && _symbolProperties.TryGetValue(symbolId, out descriptor))
            return true;
        descriptor = default;
        return false;
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

    public bool SetProperty(string key, JsValue value)
    {
        if (_shape.TryGetSlot(key, out var slot) && _properties[slot] is { } existing)
        {
            if (!existing.Writable) return false;
            _properties[slot] = existing with { Value = value };
            return true;
        }
        DefineOwnProperty(key, new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        return true;
    }

    public bool DeleteProperty(string key)
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
