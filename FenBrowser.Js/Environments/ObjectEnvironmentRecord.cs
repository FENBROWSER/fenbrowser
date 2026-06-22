using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.2 Object Environment Records.
//
// An object Environment Record is associated with an object called its binding object.
// An object Environment Record binds the set of string identifier names that directly
// correspond to the property names of its binding object. Property keys that are not
// strings in the form of an IdentifierName are not included in the set of bound
// identifiers. An object Environment Record can be configured to provide its binding
// object as an implicit `this` value for use in a `with` statement function calls; in
// that case it is called a "with environment record".
public sealed class ObjectEnvironmentRecord : EnvironmentRecord
{
    private readonly IBindingObject _bindingObject;
    private readonly bool _isWithEnvironment;

    public ObjectEnvironmentRecord(
        IBindingObject bindingObject,
        bool isWithEnvironment,
        EnvironmentRecord? outerEnv)
        : base(outerEnv)
    {
        ArgumentNullException.ThrowIfNull(bindingObject);
        _bindingObject = bindingObject;
        _isWithEnvironment = isWithEnvironment;
    }

    public override bool HasBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindingObject.HasProperty(name))
            return false;

        // 9.1.1.2.1 step 4-6: when this is a with-environment and the binding object
        // carries @@unscopables that lists the property name with a truthy value,
        // the binding is considered absent for identifier resolution.
        if (_isWithEnvironment && IsUnscopable is { } check && check(name))
            return false;

        return true;
    }

    // Set by the interpreter when creating a with-environment so HasBinding can
    // consult the binding object's @@unscopables property.
    internal Func<string, bool>? IsUnscopable { get; set; }

    public override BindingOpResult CreateMutableBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindingObject.DefineMutableData(name, JsValue.Undefined, deletable))
        {
            // DefinePropertyOrThrow would raise a TypeError; the interpreter mints it.
            return BindingOpResult.AlreadyDeclared;
        }

        return BindingOpResult.Ok;
    }

    public override BindingOpResult CreateImmutableBinding(string name, bool strict)
    {
        // 9.1.1.2.3: "The CreateImmutableBinding concrete method ... will never be used
        // within this specification in association with object Environment Records."
        _ = name;
        _ = strict;
        return BindingOpResult.NotInitializable;
    }

    public override BindingOpResult InitializeBinding(string name, JsValue value)
    {
        // 9.1.1.2.4 routes InitializeBinding through SetMutableBinding with S=false.
        return SetMutableBinding(name, value, strict: false);
    }

    public override BindingOpResult SetMutableBinding(string name, JsValue value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        var stillExists = _bindingObject.HasProperty(name);
        if (!stillExists && strict)
        {
            return BindingOpResult.NotFound;
        }

        if (!_bindingObject.TrySet(name, value))
        {
            // Underlying object rejected the write (non-writable property, accessor
            // without setter, etc.). In strict mode this becomes a TypeError; the
            // interpreter is the layer that knows that.
            return strict ? BindingOpResult.ConstAssignment : BindingOpResult.Ok;
        }

        return BindingOpResult.Ok;
    }

    public override BindingOpResult GetBindingValue(string name, bool strict, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindingObject.HasProperty(name))
        {
            // 9.1.1.2.6 step 3-4: non-strict missing access yields undefined; strict
            // missing access throws ReferenceError. The interpreter translates
            // NotFound + strict into the throw.
            value = JsValue.Undefined;
            return strict ? BindingOpResult.NotFound : BindingOpResult.Ok;
        }

        if (!_bindingObject.TryGet(name, out value))
        {
            // HasProperty said yes but TryGet said no - the binding object's accessor
            // (getter) was unavailable. Treat as a missing read.
            value = JsValue.Undefined;
            return strict ? BindingOpResult.NotFound : BindingOpResult.Ok;
        }

        return BindingOpResult.Ok;
    }

    public override BindingOpResult DeleteBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindingObject.HasProperty(name))
        {
            return BindingOpResult.NotFound;
        }

        return _bindingObject.DeleteProperty(name)
            ? BindingOpResult.Ok
            : BindingOpResult.ConstAssignment;
    }

    public bool IsWithEnvironment => _isWithEnvironment;

    // 9.1.1.2.10 WithBaseObject: when this is a with-environment, surface the binding
    // object so that function calls inside the `with` block can use it as the implicit
    // `this`. Otherwise the spec returns undefined (modeled here as null).
    public override ObjectHandle? WithBaseObject
        => _isWithEnvironment ? _bindingObject.AsObjectHandle : null;

    public IBindingObject BindingObjectForTest => _bindingObject;

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        if (_bindingObject.AsObjectHandle is { } handle)
        {
            tracer.Trace(handle);
        }
    }
}
