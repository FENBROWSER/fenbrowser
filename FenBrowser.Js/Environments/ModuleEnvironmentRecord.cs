using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.5 Module Environment Records.
//
// A module Environment Record is a declarative Environment Record that is used to
// represent the outer scope of an ECMAScript Module. In additional to normal mutable
// and immutable bindings, module Environment Records also provide immutable import
// bindings which are bindings that provide indirect access to a target binding that
// exists in another Environment Record.
public sealed class ModuleEnvironmentRecord : DeclarativeEnvironmentRecord
{
    private readonly Dictionary<string, ImportBinding> _importBindings = new(StringComparer.Ordinal);

    public ModuleEnvironmentRecord(EnvironmentRecord? outerEnv)
        : base(outerEnv)
    {
    }

    // 9.1.1.5.5 The module-scope `this` is always present but its value is undefined.
    public override bool HasThisBinding => true;

    public override BindingOpResult GetThisBinding(out JsValue value)
    {
        value = JsValue.Undefined;
        return BindingOpResult.Ok;
    }

    public override bool HasBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _importBindings.ContainsKey(name) || base.HasBinding(name);
    }

    // 9.1.1.5.6 CreateImportBinding ( N, M, N2 ). Establishes an indirect binding from
    // a local name N to the binding for N2 inside the target module's environment.
    // Import bindings are immutable from the importer's perspective.
    public BindingOpResult CreateImportBinding(string name, EnvironmentRecord targetEnv, string targetName)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(targetEnv);
        ArgumentNullException.ThrowIfNull(targetName);

        if (_importBindings.ContainsKey(name) || base.HasBinding(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        _importBindings[name] = new ImportBinding(targetEnv, targetName);
        return BindingOpResult.Ok;
    }

    public override BindingOpResult GetBindingValue(string name, bool strict, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_importBindings.TryGetValue(name, out var import))
        {
            // 9.1.1.5.1: dereference the import. The TDZ of the target module's
            // exported binding bubbles back to the importer unchanged.
            return import.TargetEnv.GetBindingValue(import.TargetName, strict, out value);
        }

        return base.GetBindingValue(name, strict, out value);
    }

    public override BindingOpResult SetMutableBinding(string name, JsValue value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_importBindings.ContainsKey(name))
        {
            // Import bindings are immutable. Strict assignment becomes a TypeError;
            // the interpreter translates ConstAssignment accordingly.
            return BindingOpResult.ConstAssignment;
        }

        return base.SetMutableBinding(name, value, strict);
    }

    public override BindingOpResult InitializeBinding(string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_importBindings.ContainsKey(name))
        {
            // Imports are initialized as part of CreateImportBinding; calling
            // InitializeBinding on one is an engine bug.
            return BindingOpResult.NotInitializable;
        }

        return base.InitializeBinding(name, value);
    }

    public override BindingOpResult DeleteBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // 9.1.1.5.2: "The DeleteBinding concrete method ... is never used within this
        // specification." Module bindings are not deletable; surface NotInitializable
        // as a defensive signal rather than silently succeeding.
        if (_importBindings.ContainsKey(name) || base.HasBinding(name))
        {
            return BindingOpResult.NotInitializable;
        }

        return BindingOpResult.NotFound;
    }

    public bool IsImportBindingForTest(string name) => _importBindings.ContainsKey(name);

    private readonly record struct ImportBinding(EnvironmentRecord TargetEnv, string TargetName);
}
