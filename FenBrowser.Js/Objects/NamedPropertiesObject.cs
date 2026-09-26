using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

/// <summary>
/// WebIDL §3.7.4 named properties object: the object an interface with a named property
/// getter on its global puts in the prototype chain (HTML's WindowProperties, between
/// <c>Window.prototype</c> and <c>EventTarget.prototype</c>). Its own properties are
/// whatever the host's resolver finds under a name, so a name reads as a property only
/// while something in the document carries it; nothing on the object itself can be
/// defined or deleted.
/// </summary>
public sealed class NamedPropertiesObject : JsObject
{
    private readonly Func<string, JsValue?> _resolve;
    private readonly Func<ObjectHandle, JsObject> _resolvePrototype;

    /// <param name="resolve">
    /// The value named <c>name</c> now, or null when there is none. Called on every
    /// lookup that reaches this object, so it has to be cheap for names that miss.
    /// </param>
    /// <param name="resolvePrototype">Resolves a prototype handle, for the visibility check.</param>
    public NamedPropertiesObject(Func<string, JsValue?> resolve, Func<ObjectHandle, JsObject> resolvePrototype)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(resolvePrototype);
        _resolve = resolve;
        _resolvePrototype = resolvePrototype;
    }

    /// <summary>A name can start or stop resolving without this object's shape changing.</summary>
    public override bool MayGainOwnPropertyOutsideShape(string key) => true;

    // WebIDL 3.7.4.1 [[GetOwnProperty]]: a supported property name is an own data
    // property, writable and configurable but not enumerable; anything else is ordinary.
    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (IsVisibleName(key) && _resolve(key) is { } value)
        {
            descriptor = new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: true);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    // WebIDL 3.7.3 named property visibility: a name something further up the chain
    // defines as its own (constructor, toString, addEventListener) is not a named property.
    private bool IsVisibleName(string key)
    {
        for (var handle = PrototypeHandle; handle is { } current;)
        {
            var prototype = _resolvePrototype(current);
            if (prototype is not NamedPropertiesObject && prototype.TryGetOwnProperty(key, out _))
                return false;
            handle = prototype.PrototypeHandle;
        }

        return true;
    }

    // 3.7.4.2 [[DefineOwnProperty]] and 3.7.4.3 [[Delete]]: always false.
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor) => false;

    public override bool DeleteProperty(string key) => false;

    // An assignment that reaches this object creates the property on the receiver
    // (OrdinarySet), never here.
    public override bool SetProperty(string key, JsValue value) => false;
}
