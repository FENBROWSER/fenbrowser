using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

/// <summary>
/// Where a <see cref="ForwardingObject"/> sends the names it does not own.
/// </summary>
public interface IForwardedPropertySource
{
    /// <summary>The value named <paramref name="key"/> now, if the source has one.</summary>
    bool TryRead(string key, out JsValue value);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>; false when the source will not take it.</summary>
    bool TryWrite(string key, JsValue value);
}

/// <summary>
/// An ordinary object whose missing names are answered by a host source: a read of a
/// name it does not own asks the source, and a new property is defined on the source
/// when the source accepts it. An embedder uses it for an object that stands in for one
/// on another heap - a same-origin frame's WindowProxy, whose expandos and globals live
/// in the frame's realm (HTML 7.2.3) - without changing the object's identity.
/// </summary>
public sealed class ForwardingObject : JsObject
{
    /// <summary>Null until the host has finished defining the object's own properties.</summary>
    public IForwardedPropertySource? Source { get; set; }

    /// <summary>Names the source answers can change without this object's shape changing.</summary>
    public override bool MayGainOwnPropertyOutsideShape(string key) => Source is not null;

    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (base.TryGetOwnProperty(key, out descriptor))
            return true;

        if (Source is { } source && source.TryRead(key, out var value))
        {
            descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true);
            return true;
        }

        return false;
    }

    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (Source is { } source &&
            !base.TryGetOwnProperty(key, out _) &&
            descriptor.HasValue &&
            !descriptor.IsAccessor &&
            source.TryWrite(key, descriptor.Value))
        {
            return true;
        }

        return base.DefineOwnProperty(key, descriptor);
    }

    public override bool SetProperty(string key, JsValue value)
    {
        if (Source is { } source && !base.TryGetOwnProperty(key, out _) && source.TryWrite(key, value))
            return true;

        return base.SetProperty(key, value);
    }
}
