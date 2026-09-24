using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

/// <summary>
/// WebIDL 3.7.4 named properties object: the exotic object a host puts in an
/// interface's prototype chain when the interface has a named property getter
/// and is [Global] - Window's is where <c>window.someId</c> and a bare
/// <c>someId</c> find the element (HTML 7.2.2.3 named access on the Window object).
/// </summary>
/// <remarks>
/// The names are the host's to answer on every lookup: they follow the document,
/// which no shape can describe, so every name is reported as able to appear
/// outside the shape and no cache reads through this object.
/// </remarks>
public sealed class NamedPropertiesObject : JsObject
{
    private readonly Func<string, JsValue?> _resolve;

    /// <param name="resolve">
    /// The named property getter: the value for a supported property name, or
    /// null when the name is not supported.
    /// </param>
    public NamedPropertiesObject(Func<string, JsValue?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    public override bool MayGainOwnPropertyOutsideShape(string key) => true;

    // WebIDL 3.7.4.1 [[GetOwnProperty]]: a supported name yields
    // { [[Value]]: value, [[Writable]]: true, [[Enumerable]]: false,
    // [[Configurable]]: true } (Window is [LegacyUnenumerableNamedProperties]);
    // anything else is OrdinaryGetOwnProperty.
    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (!IsShadowedAbove(key) && _resolve(key) is { } value)
        {
            descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: true);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    // Named property visibility (WebIDL 3.7.4.1 step 5): a name an object further up
    // the chain defines as its own (EventTarget.prototype, Object.prototype) is not
    // a named property. The global and Window.prototype sit below this object and
    // shadow it through the ordinary lookup, so they need no check here.
    private bool IsShadowedAbove(string key)
    {
        if (OwnerHeap is not { } heap) return false;
        for (var proto = PrototypeHandle; proto is { } handle;)
        {
            var obj = heap.GetObject(handle);
            if (obj.TryGetOwnProperty(key, out _)) return true;
            proto = obj.PrototypeHandle;
        }

        return false;
    }

    // WebIDL 3.7.4.2 [[DefineOwnProperty]] and 3.7.4.3 [[Delete]] both return false.
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor) => false;

    public override bool DeleteProperty(string key) => false;
}
