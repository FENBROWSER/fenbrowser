using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// Surface that ObjectEnvironmentRecord needs from its [[BindingObject]] (ECMA-262
// 9.1.1.2). Defined as an interface so the environment record stays testable without
// instantiating a full JsObject, and so future host-backed objects (proxies, with-target
// shims) can plug in without changes here.
public interface IBindingObject
{
    // Equivalent to the abstract [[HasProperty]](O, N) operation - returns true if N
    // is reachable through this object, including via prototype chain when applicable.
    bool HasProperty(string name);

    // Equivalent to ? Get(O, N, O). False = property not found through any chain.
    bool TryGet(string name, out JsValue value);

    // Equivalent to ? Set(O, N, V, throw=false). False = the underlying object refused
    // the write (non-writable property, frozen target, accessor without setter, etc.).
    bool TrySet(string name, JsValue value);

    // Equivalent to ? DefinePropertyOrThrow(O, N, PropertyDescriptor{Value: undefined,
    // Writable: true, Enumerable: true, Configurable: deletable}). Returns false if
    // the descriptor could not be installed (existing non-configurable etc.).
    bool DefineMutableData(string name, JsValue value, bool deletable);

    // Equivalent to ? O.[[Delete]](N). False = property was non-configurable.
    bool DeleteProperty(string name);

    // The heap handle for this object, used by `with` environments to expose the
    // binding object as the `this` value for function calls (9.1.1.2.10
    // WithBaseObject). Null when the implementation has no handle to surface yet,
    // e.g. a test fake.
    ObjectHandle? AsObjectHandle { get; }
}
