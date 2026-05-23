using FenBrowser.Js.Runtime;
using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.1 Declarative Environment Records.
//
// Each declarative Environment Record is associated with an ECMAScript program scope
// containing variable, constant, let, class, module, import, and/or function
// declarations. A declarative Environment Record binds the set of identifiers defined
// by the declarations contained within its scope.
public class DeclarativeEnvironmentRecord : EnvironmentRecord
{
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);

    public DeclarativeEnvironmentRecord(EnvironmentRecord? outerEnv)
        : base(outerEnv)
    {
    }

    public override bool HasBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bindings.ContainsKey(name);
    }

    public override BindingOpResult CreateMutableBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_bindings.ContainsKey(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        _bindings[name] = new Binding(
            Value: JsValue.Undefined,
            IsMutable: true,
            IsInitialized: false,
            IsStrict: false,
            IsDeletable: deletable);
        return BindingOpResult.Ok;
    }

    public override BindingOpResult CreateImmutableBinding(string name, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_bindings.ContainsKey(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        _bindings[name] = new Binding(
            Value: JsValue.Undefined,
            IsMutable: false,
            IsInitialized: false,
            IsStrict: strict,
            IsDeletable: false);
        return BindingOpResult.Ok;
    }

    public override BindingOpResult InitializeBinding(string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindings.TryGetValue(name, out var binding))
        {
            return BindingOpResult.NotFound;
        }

        if (binding.IsInitialized)
        {
            // ECMA-262 step "Assert: envRec does not already have an initialized
            // binding for N." - re-initialization is a host bug, surface it instead of
            // silently overwriting.
            return BindingOpResult.NotInitializable;
        }

        _bindings[name] = binding with { Value = value, IsInitialized = true };
        return BindingOpResult.Ok;
    }

    public override BindingOpResult SetMutableBinding(string name, JsValue value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindings.TryGetValue(name, out var binding))
        {
            // 9.1.1.1.5 step 1: "If envRec does not have a binding for N..." Both
            // strict and non-strict callers receive NotFound; the caller (interpreter)
            // turns strict NotFound into a ReferenceError, and non-strict NotFound into
            // an auto-created global binding.
            _ = strict;
            return BindingOpResult.NotFound;
        }

        if (!binding.IsInitialized)
        {
            return BindingOpResult.TdzAccess;
        }

        if (!binding.IsMutable)
        {
            return BindingOpResult.ConstAssignment;
        }

        _bindings[name] = binding with { Value = value };
        return BindingOpResult.Ok;
    }

    public override BindingOpResult GetBindingValue(string name, bool strict, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = strict;

        if (!_bindings.TryGetValue(name, out var binding))
        {
            value = JsValue.Undefined;
            return BindingOpResult.NotFound;
        }

        if (!binding.IsInitialized)
        {
            // TDZ - ECMA-262 9.1.1.1.6 throws ReferenceError when the binding exists
            // but has not yet been initialized.
            value = JsValue.Undefined;
            return BindingOpResult.TdzAccess;
        }

        value = binding.Value;
        return BindingOpResult.Ok;
    }

    public override BindingOpResult DeleteBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_bindings.TryGetValue(name, out var binding))
        {
            return BindingOpResult.NotFound;
        }

        if (!binding.IsDeletable)
        {
            return BindingOpResult.ConstAssignment;
        }

        _bindings.Remove(name);
        return BindingOpResult.Ok;
    }

    public int BindingCountForTest => _bindings.Count;

    public bool IsInitializedForTest(string name)
        => _bindings.TryGetValue(name, out var b) && b.IsInitialized;

    public bool IsMutableForTest(string name)
        => _bindings.TryGetValue(name, out var b) && b.IsMutable;

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        foreach (var binding in _bindings.Values)
        {
            if (binding.IsInitialized)
            {
                TraceValue(tracer, binding.Value);
            }
        }
    }

    private readonly record struct Binding(
        JsValue Value,
        bool IsMutable,
        bool IsInitialized,
        bool IsStrict,
        bool IsDeletable);
}
