using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.3 Function Environment Records.
//
// A function Environment Record is a declarative Environment Record that is used to
// represent the top-level scope of a function and, if the function is not an
// ArrowFunction, provides a `this` binding. If a function is not an ArrowFunction
// function and references `super`, its function Environment Record also contains the
// state that is used to perform `super` method invocations from within the function.
public sealed class FunctionEnvironmentRecord : DeclarativeEnvironmentRecord
{
    private JsValue _thisValue;

    public FunctionEnvironmentRecord(
        ThisBindingStatus thisBindingStatus,
        JsValue functionObject,
        JsValue newTarget,
        ObjectHandle? homeObject,
        EnvironmentRecord? outerEnv)
        : base(outerEnv)
    {
        ThisBindingStatus = thisBindingStatus;
        FunctionObject = functionObject;
        NewTarget = newTarget;
        HomeObject = homeObject;
        _thisValue = JsValue.Undefined;
    }

    public ThisBindingStatus ThisBindingStatus { get; private set; }

    // The function that produced this environment. Stored as a JsValue so it can carry
    // either an object handle (regular functions) or a host-function reference.
    public JsValue FunctionObject { get; }

    // The value passed to the function via `new` (or undefined when called normally).
    // Surfaced to the runtime as `new.target`.
    public JsValue NewTarget { get; }

    // The object on which this function was defined as a method; used as the [[Home]]
    // for `super` lookups (9.1.1.3.5 GetSuperBase). Null for arrow functions and
    // top-level functions.
    public ObjectHandle? HomeObject { get; }

    public override bool HasThisBinding => ThisBindingStatus != ThisBindingStatus.Lexical;

    public override bool HasSuperBinding => HasThisBinding && HomeObject is not null;

    // 9.1.1.3.1 BindThisValue ( V ). Returns AlreadyDeclared if `this` is already
    // initialized (the spec throws ReferenceError - the interpreter translates).
    // Returns NotInitializable for arrow (lexical) environments since they should
    // never reach this path.
    public BindingOpResult BindThisValue(JsValue value)
    {
        switch (ThisBindingStatus)
        {
            case ThisBindingStatus.Lexical:
                return BindingOpResult.NotInitializable;
            case ThisBindingStatus.Initialized:
                return BindingOpResult.AlreadyDeclared;
            case ThisBindingStatus.Uninitialized:
                _thisValue = value;
                ThisBindingStatus = ThisBindingStatus.Initialized;
                return BindingOpResult.Ok;
            default:
                return BindingOpResult.NotInitializable;
        }
    }

    // 9.1.1.3.4 GetThisBinding ( ). Returns TdzAccess if `this` has not yet been bound
    // (spec: ReferenceError). Returns NotInitializable when the environment is lexical
    // and therefore has no `this` to fetch - calling sites should consult
    // HasThisBinding first.
    public override BindingOpResult GetThisBinding(out JsValue value)
    {
        switch (ThisBindingStatus)
        {
            case ThisBindingStatus.Initialized:
                value = _thisValue;
                return BindingOpResult.Ok;
            case ThisBindingStatus.Uninitialized:
                value = JsValue.Undefined;
                return BindingOpResult.TdzAccess;
            case ThisBindingStatus.Lexical:
            default:
                value = JsValue.Undefined;
                return BindingOpResult.NotInitializable;
        }
    }

    public JsValue ThisValueForTest => _thisValue;
}
