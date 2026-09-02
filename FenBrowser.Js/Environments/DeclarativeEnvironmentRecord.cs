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
    private Dictionary<string, Binding>? _bindings;

    public DeclarativeEnvironmentRecord(EnvironmentRecord? outerEnv)
        : base(outerEnv)
    {
    }

    // GetBindingValue already answers NotFound for an absent binding here, so
    // one dictionary probe settles both questions. ModuleEnvironmentRecord and
    // FunctionEnvironmentRecord inherit this; their GetBindingValue overrides
    // fall through to this record's for a name they do not know.
    public override BindingOpResult TryLookupBinding(string name, bool strict, out JsValue value)
        => GetBindingValue(name, strict, out value);

    // One dictionary lookup instead of two. Call cost here scales with the
    // number of locals a function has - about 0.19us per local per call before
    // this - because each one was hashed once to create the binding and again
    // to initialise it.
    public override BindingOpResult CreateAndInitializeBinding(string name, JsValue value, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(bindings, name, out var existed);

        if (existed)
        {
            // Re-declaring keeps the binding's own flags; only the value is set.
            if (!slot.IsMutable)
            {
                return BindingOpResult.AlreadyDeclared;
            }

            slot = slot with { Value = value, IsInitialized = true };
        }
        else
        {
            slot = new Binding(value, IsMutable: true, IsInitialized: true, IsStrict: false, IsDeletable: deletable);
        }

        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore();
        }

        return BindingOpResult.Ok;
    }

    // One lookup: present stays untouched, absent is created holding undefined.
    public override BindingOpResult EnsureVarBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(bindings, name, out var existed);

        if (!existed)
        {
            slot = new Binding(
                JsValue.Undefined, IsMutable: true, IsInitialized: true, IsStrict: false, IsDeletable: deletable);
        }

        return BindingOpResult.Ok;
    }

    public override bool HasBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bindings?.ContainsKey(name) == true;
    }

    // Returns true if the binding exists and is a lexical (non-deletable) binding.
    public bool HasLexicalBinding(string name)
    {
        return _bindings?.TryGetValue(name, out var b) == true && !b.IsDeletable;
    }

    public bool HasVarBinding(string name)
    {
        return _bindings?.TryGetValue(name, out var b) == true && b.IsDeletable;
    }

    public override BindingOpResult CreateMutableBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (bindings.ContainsKey(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        bindings[name] = new Binding(
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

        var bindings = _bindings ??= new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (bindings.ContainsKey(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        bindings[name] = new Binding(
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

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
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
        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore();
        }
        return BindingOpResult.Ok;
    }

    public override BindingOpResult SetMutableBinding(string name, JsValue value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
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
            if (strict || binding.IsStrict) return BindingOpResult.ConstAssignment;
            return BindingOpResult.Ok;
        }

        _bindings[name] = binding with { Value = value };
        if (value.Tag == JsValueTag.Object)
        {
            RememberBindingStore();
        }
        return BindingOpResult.Ok;
    }

    public override BindingOpResult GetBindingValue(string name, bool strict, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = strict;

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
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

        if (_bindings is null || !_bindings.TryGetValue(name, out var binding))
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

    public int BindingCountForTest => _bindings?.Count ?? 0;

    public bool IsInitializedForTest(string name)
        => _bindings?.TryGetValue(name, out var b) == true && b.IsInitialized;

    public bool IsMutableForTest(string name)
        => _bindings?.TryGetValue(name, out var b) == true && b.IsMutable;

    protected internal override void TraceOwnEdges(IHeapTracer tracer)
    {
        TraceDeclarativeBindings(tracer);
    }

    /// <summary>
    /// Traces object edges held by THIS record's bindings only. Shared with
    /// derived module records, which add their own import-target edges.
    /// </summary>
    protected void TraceDeclarativeBindings(IHeapTracer tracer)
    {
        if (_bindings is null)
        {
            return;
        }

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
