using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.4 Global Environment Records.
//
// A global Environment Record is used to represent the outer-most scope that is shared
// by all of the ECMAScript Script elements that are processed in a common Realm. A
// global Environment Record provides the bindings for built-in globals (clause 19),
// properties of the global object, and for all top-level declarations (8.1.9, 8.1.11)
// that occur within a Script.
//
// Logically, the global Environment Record is a composite of an object Environment
// Record (over the global object) and a declarative Environment Record (carrying the
// let/const/class top-level declarations). Identifier resolution favors the
// declarative side first so that a `let x = 1` shadows a same-named property of the
// global object, matching the spec.
//
// All ECMA-262 9.1.1.4 abstract operations are implemented: binding-routing
// (HasBinding, CreateMutableBinding, SetMutableBinding, GetBindingValue,
// DeleteBinding), global-this surface, plus the declaration-instantiation operations
// (HasRestrictedGlobalProperty, CanDeclareGlobalVar / CanDeclareGlobalFunction,
// CreateGlobalVarBinding / CreateGlobalFunctionBinding, HasVarDeclaration,
// HasLexicalDeclaration). The script-init path that drives these from the
// interpreter still lands in a later commit.
public sealed class GlobalEnvironmentRecord : EnvironmentRecord
{
    private readonly IGlobalObject _globalObject;
    private readonly DeclarativeEnvironmentRecord _declarativeRecord;
    private readonly ObjectEnvironmentRecord _objectRecord;
    private readonly HashSet<string> _varNames = new(StringComparer.Ordinal);

    public GlobalEnvironmentRecord(
        IGlobalObject globalObject,
        JsValue globalThisValue)
        : base(outerEnv: null)
    {
        ArgumentNullException.ThrowIfNull(globalObject);
        _globalObject = globalObject;
        _objectRecord = new ObjectEnvironmentRecord(globalObject, isWithEnvironment: false, outerEnv: null);
        _declarativeRecord = new DeclarativeEnvironmentRecord(outerEnv: null);
        GlobalThisValue = globalThisValue;
    }

    // 9.1.1.4.11 GetThisBinding ( ): return [[GlobalThisValue]]. Stored explicitly so
    // that hosts (Window vs. a sandbox global) can decide what `this` should resolve
    // to at the top level.
    public JsValue GlobalThisValue { get; }

    public override bool HasThisBinding => true;

    public override bool HasSuperBinding => false;

    // 9.1.1.4.11 GetThisBinding: surface the configured [[GlobalThisValue]] without
    // any TDZ semantics - the global `this` is always present.
    public override BindingOpResult GetThisBinding(out JsValue value)
    {
        value = GlobalThisValue;
        return BindingOpResult.Ok;
    }

    // 9.1.1.4.1 HasBinding ( N ).
    public override bool HasBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _declarativeRecord.HasBinding(name) || _objectRecord.HasBinding(name);
    }

    // 9.1.1.4.2 CreateMutableBinding ( N, D ). Always routes to the declarative side;
    // duplicate global declarations through this path are TypeErrors (the interpreter
    // maps AlreadyDeclared accordingly).
    public override BindingOpResult CreateMutableBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        return _declarativeRecord.CreateMutableBinding(name, deletable);
    }

    // 9.1.1.4.3 CreateImmutableBinding ( N, S ).
    public override BindingOpResult CreateImmutableBinding(string name, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        return _declarativeRecord.CreateImmutableBinding(name, strict);
    }

    // 9.1.1.4.4 InitializeBinding ( N, V ). Declarative side first; otherwise the spec
    // asserts the object record has the binding and routes there.
    public override BindingOpResult InitializeBinding(string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return _declarativeRecord.InitializeBinding(name, value);
        }

        return _objectRecord.InitializeBinding(name, value);
    }

    // 9.1.1.4.5 SetMutableBinding ( N, V, S ).
    public override BindingOpResult SetMutableBinding(string name, JsValue value, bool strict)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return _declarativeRecord.SetMutableBinding(name, value, strict);
        }

        return _objectRecord.SetMutableBinding(name, value, strict);
    }

    // 9.1.1.4.6 GetBindingValue ( N, S ).
    public override BindingOpResult GetBindingValue(string name, bool strict, out JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return _declarativeRecord.GetBindingValue(name, strict, out value);
        }

        return _objectRecord.GetBindingValue(name, strict, out value);
    }

    // 9.1.1.4.7 DeleteBinding ( N ). Routes to whichever side owns the binding; the
    // global-object side additionally drops the var-name from [[VarNames]] when the
    // delete succeeds, so that a later `var x` does not see a stale entry.
    public override BindingOpResult DeleteBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return _declarativeRecord.DeleteBinding(name);
        }

        if (!_objectRecord.HasBinding(name))
        {
            return BindingOpResult.NotFound;
        }

        var result = _objectRecord.DeleteBinding(name);
        if (result == BindingOpResult.Ok)
        {
            _varNames.Remove(name);
        }

        return result;
    }

    // 9.1.1.4.12 HasVarDeclaration ( N ).
    public bool HasVarDeclaration(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _varNames.Contains(name);
    }

    // 9.1.1.4.13 HasLexicalDeclaration ( N ).
    public bool HasLexicalDeclaration(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _declarativeRecord.HasBinding(name);
    }

    // Interpreter seam for the VarNames list. The full CreateGlobalVarBinding
    // operation lives in a follow-up commit; this lets script-init code add a name to
    // [[VarNames]] as part of `var` hoisting without forcing the full
    // CanDeclareGlobalVar / DefinePropertyOrThrow plumbing up front.
    public bool TryRecordVarName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _varNames.Add(name);
    }

    // 9.1.1.4.14 HasRestrictedGlobalProperty ( N ). True iff the global object has an
    // own non-configurable property of that name - declarations cannot shadow such
    // entries (e.g. `undefined`, `NaN`, `Infinity` on the real Web platform).
    public bool HasRestrictedGlobalProperty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_globalObject.HasOwnProperty(name))
        {
            return false;
        }

        return !_globalObject.IsOwnPropertyConfigurable(name);
    }

    // 9.1.1.4.15 CanDeclareGlobalVar ( N ). A `var` declaration succeeds when the name
    // already exists on the global object (it will be reused) or when the object is
    // still extensible (a new property can be installed).
    public bool CanDeclareGlobalVar(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_globalObject.HasOwnProperty(name))
        {
            return true;
        }

        return _globalObject.IsExtensible;
    }

    // 9.1.1.4.17 CreateGlobalVarBinding ( N, D ). Adds to [[VarNames]] and, when the
    // name is not already present on the global object, installs a configurable data
    // property. Returns Ok on success, ConstAssignment when the declaration is
    // forbidden (object frozen, restricted property, etc.), AlreadyDeclared when the
    // name conflicts with a lexical binding already in the declarative half.
    public BindingOpResult CreateGlobalVarBinding(string name, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        var alreadyExists = _globalObject.HasOwnProperty(name);
        if (!alreadyExists)
        {
            if (!_globalObject.IsExtensible)
            {
                return BindingOpResult.ConstAssignment;
            }

            if (!_globalObject.DefineMutableData(name, JsValue.Undefined, deletable))
            {
                return BindingOpResult.ConstAssignment;
            }
        }

        _varNames.Add(name);
        return BindingOpResult.Ok;
    }

    // 9.1.1.4.16 CanDeclareGlobalFunction ( N ). A `function` declaration may overwrite
    // an existing own property when (a) no own property exists and the object is
    // extensible, (b) the existing property is configurable, or (c) the existing
    // property is a data descriptor that is already writable+enumerable (so the
    // function-declaration redefinition is observationally a no-op apart from the
    // value). Otherwise the declaration is forbidden and the caller raises TypeError.
    public bool CanDeclareGlobalFunction(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_globalObject.HasOwnProperty(name))
        {
            return _globalObject.IsExtensible;
        }

        if (_globalObject.IsOwnPropertyConfigurable(name))
        {
            return true;
        }

        return _globalObject.IsOwnDataPropertyWritableEnumerable(name);
    }

    // 9.1.1.4.18 CreateGlobalFunctionBinding ( N, V, D ). Installs a function value as
    // a global property: a missing or configurable own property is fully redefined as
    // a writable+enumerable+configurable=D data property; a non-configurable
    // writable+enumerable own property keeps its attributes and only its value
    // changes. Either way the name is recorded in [[VarNames]] for later `var`
    // hoisting checks. The interpreter calls CanDeclareGlobalFunction first so this
    // method's failure modes (object frozen, define rejected) collapse to
    // ConstAssignment.
    public BindingOpResult CreateGlobalFunctionBinding(string name, JsValue value, bool deletable)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_declarativeRecord.HasBinding(name))
        {
            return BindingOpResult.AlreadyDeclared;
        }

        var hasExisting = _globalObject.HasOwnProperty(name);
        var existingIsConfigurable = hasExisting && _globalObject.IsOwnPropertyConfigurable(name);

        if (!hasExisting || existingIsConfigurable)
        {
            if (!hasExisting && !_globalObject.IsExtensible)
            {
                return BindingOpResult.ConstAssignment;
            }

            if (!_globalObject.DefineMutableData(name, value, deletable))
            {
                return BindingOpResult.ConstAssignment;
            }
        }
        else
        {
            // Existing non-configurable but writable+enumerable data property: per spec
            // we leave the attributes alone and only update the value via [[Set]].
            if (!_globalObject.TrySet(name, value))
            {
                return BindingOpResult.ConstAssignment;
            }
        }

        _varNames.Add(name);
        return BindingOpResult.Ok;
    }

    public DeclarativeEnvironmentRecord DeclarativeRecordForTest => _declarativeRecord;
    public ObjectEnvironmentRecord ObjectRecordForTest => _objectRecord;
    public IReadOnlyCollection<string> VarNamesSnapshotForTest => _varNames;

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        TraceValue(tracer, GlobalThisValue);
        _declarativeRecord.Trace(tracer);
    }
}
