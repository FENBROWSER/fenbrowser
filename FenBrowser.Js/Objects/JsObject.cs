using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public class JsObject : ITraceable
{
    private readonly Dictionary<string, JsPropertyDescriptor> _properties = new(StringComparer.Ordinal);

    public ObjectHandle? PrototypeHandle { get; private set; }

    public bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        _properties[key] = descriptor;
        return true;
    }

    public bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor) => _properties.TryGetValue(key, out descriptor);

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

    public void Trace(IHeapTracer tracer)
    {
        if (PrototypeHandle is { } proto)
        {
            tracer.Trace(proto);
        }

        foreach (var descriptor in _properties.Values)
        {
            if (descriptor.Value.Tag == JsValueTag.Object)
            {
                tracer.Trace(descriptor.Value.AsObjectHandle());
            }
        }
    }
}
