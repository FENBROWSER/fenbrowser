using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public class JsObject : ITraceable
{
    private readonly Dictionary<string, JsPropertyDescriptor> _properties = new(StringComparer.Ordinal);

    // Symbol-keyed own properties live in a separate dictionary so string-key
    // enumeration paths (Object.keys, for-in, JSON.stringify) skip them
    // automatically per ECMA-262 7.3.23 OrdinaryOwnPropertyKeys ordering: integer
    // index keys, then string keys, then symbol keys. The runtime only needs to
    // distinguish whether a given Symbol id has a binding.
    private Dictionary<long, JsPropertyDescriptor>? _symbolProperties;

    public ObjectHandle? PrototypeHandle { get; private set; }

    // ECMA-262 [[Extensible]] - new own properties may be added while true. Flips to
    // false on Object.preventExtensions / freeze / seal. DefineOwnProperty does not
    // yet honor this; env records that need to respect extensibility consult it
    // directly through IGlobalObject.IsExtensible.
    public bool Extensible { get; private set; } = true;

    public void PreventExtensions()
    {
        Extensible = false;
    }

    public bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        _properties[key] = descriptor;
        return true;
    }

    public bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor) => _properties.TryGetValue(key, out descriptor);

    public IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties() => _properties;

    // ECMA-262 9.1.1 [[Get/Set/Delete]] for Symbol keys. The Symbol's identity is
    // the 64-bit id from JsValue.AsSymbolId(); descriptors are kept in a parallel
    // dictionary so string-keyed enumeration is unaffected.
    public bool DefineOwnSymbolProperty(long symbolId, JsPropertyDescriptor descriptor)
    {
        _symbolProperties ??= new Dictionary<long, JsPropertyDescriptor>();
        _symbolProperties[symbolId] = descriptor;
        return true;
    }

    public bool TryGetOwnSymbolProperty(long symbolId, out JsPropertyDescriptor descriptor)
    {
        if (_symbolProperties is not null && _symbolProperties.TryGetValue(symbolId, out descriptor))
        {
            return true;
        }

        descriptor = default;
        return false;
    }

    public bool TryGetSymbolProperty(long symbolId, Func<ObjectHandle, JsObject> prototypeResolver, out JsPropertyDescriptor descriptor)
    {
        if (TryGetOwnSymbolProperty(symbolId, out descriptor))
        {
            return true;
        }

        if (PrototypeHandle is { } proto)
        {
            return prototypeResolver(proto).TryGetSymbolProperty(symbolId, prototypeResolver, out descriptor);
        }

        descriptor = default;
        return false;
    }

    public bool DeleteSymbolProperty(long symbolId)
    {
        if (_symbolProperties is null) return false;
        if (_symbolProperties.TryGetValue(symbolId, out var existing) && !existing.Configurable)
        {
            return false;
        }

        return _symbolProperties.Remove(symbolId);
    }

    public bool TryGetProperty(string key, Func<ObjectHandle, JsObject> prototypeResolver, out JsPropertyDescriptor descriptor)
    {
        if (_properties.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (PrototypeHandle is { } proto)
        {
            return prototypeResolver(proto).TryGetProperty(key, prototypeResolver, out descriptor);
        }

        descriptor = default;
        return false;
    }

    public bool SetProperty(string key, JsValue value)
    {
        if (_properties.TryGetValue(key, out var existing))
        {
            if (!existing.Writable)
            {
                return false;
            }

            _properties[key] = existing with { Value = value };
            return true;
        }

        _properties[key] = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
        return true;
    }

    public bool DeleteProperty(string key)
    {
        if (_properties.TryGetValue(key, out var existing) && !existing.Configurable)
        {
            return false;
        }

        _ = _properties.Remove(key);
        return true;
    }

    public void SetPrototype(ObjectHandle? prototypeHandle)
    {
        PrototypeHandle = prototypeHandle;
    }

    public virtual void Trace(IHeapTracer tracer)
    {
        if (PrototypeHandle is { } proto)
        {
            tracer.Trace(proto);
        }

        foreach (var descriptor in _properties.Values)
        {
            if (descriptor.IsAccessor)
            {
                if (descriptor.Get.Tag == JsValueTag.Object)
                {
                    tracer.Trace(descriptor.Get.AsObjectHandle());
                }

                if (descriptor.Set.Tag == JsValueTag.Object)
                {
                    tracer.Trace(descriptor.Set.AsObjectHandle());
                }
            }
            else if (descriptor.Value.Tag == JsValueTag.Object)
            {
                tracer.Trace(descriptor.Value.AsObjectHandle());
            }
        }
    }
}
