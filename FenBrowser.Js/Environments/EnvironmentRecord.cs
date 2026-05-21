using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1 Environment Records.
//
// An Environment Record is a specification type used to define the association of
// Identifiers to specific variables and functions, based upon the lexical nesting
// structure of ECMAScript code. Usually an Environment Record is associated with some
// specific syntactic structure of ECMAScript code such as a FunctionDeclaration, a
// BlockStatement, or a Catch clause of a TryStatement. Each time such code is
// evaluated, a new Environment Record is created to record the identifier bindings
// that are created by that code.
//
// Concrete subclasses (DeclarativeEnvironmentRecord, ObjectEnvironmentRecord,
// FunctionEnvironmentRecord, GlobalEnvironmentRecord, ModuleEnvironmentRecord)
// override the abstract operations.
public abstract class EnvironmentRecord
{
    protected EnvironmentRecord(EnvironmentRecord? outerEnv)
    {
        OuterEnv = outerEnv;
    }

    // The outer (enclosing) Environment Record in the lexical environment chain, or
    // null for the outermost (global) record.
    public EnvironmentRecord? OuterEnv { get; }

    // 9.1.1.1.1 HasBinding ( N ) - true if the record has a binding for N.
    public abstract bool HasBinding(string name);

    // 9.1.1.1.2 CreateMutableBinding ( N, D ) - create a new mutable binding; D
    // indicates whether the binding may later be deleted by a delete operator.
    public abstract BindingOpResult CreateMutableBinding(string name, bool deletable);

    // 9.1.1.1.3 CreateImmutableBinding ( N, S ) - create a new immutable binding; S
    // indicates the binding is a strict binding (assignment after init is a TypeError).
    public abstract BindingOpResult CreateImmutableBinding(string name, bool strict);

    // 9.1.1.1.4 InitializeBinding ( N, V ) - set the bound value for an existing
    // uninitialized binding (lifts the TDZ).
    public abstract BindingOpResult InitializeBinding(string name, JsValue value);

    // 9.1.1.1.5 SetMutableBinding ( N, V, S ) - update an existing binding; S indicates
    // whether the caller is strict (affects the error returned when the binding is
    // immutable or missing).
    public abstract BindingOpResult SetMutableBinding(string name, JsValue value, bool strict);

    // 9.1.1.1.6 GetBindingValue ( N, S ) - retrieve the value bound to N. When the
    // binding is still in its TDZ the result is BindingOpResult.TdzAccess.
    public abstract BindingOpResult GetBindingValue(string name, bool strict, out JsValue value);

    // 9.1.1.1.7 DeleteBinding ( N ) - remove a binding if it is deletable.
    public abstract BindingOpResult DeleteBinding(string name);

    // 9.1.1.1.8 HasThisBinding ( ) - whether the record provides a `this` binding.
    public virtual bool HasThisBinding => false;

    // 9.1.1.1.9 HasSuperBinding ( ) - whether the record provides a `super` binding.
    public virtual bool HasSuperBinding => false;

    // 9.1.1.1.10 WithBaseObject ( ) - the object an ObjectEnvironmentRecord wraps when
    // it was produced by a `with` statement; null otherwise.
    public virtual ObjectHandle? WithBaseObject => null;
}
