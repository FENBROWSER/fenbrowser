using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// A JsObject backed by a C# delegate. Used for every builtin function
// (Math.abs, parseInt, etc.) and for host-provided callables. Not
// constructible unless a separate construct delegate is supplied.
//
// Extracted from BytecodeInterpreter.cs where it was a private nested
// class; now public so Builtins/ modules can allocate native functions
// without going through the interpreter monolith.
public sealed class NativeFunctionObject : JsObject
{
    private readonly Func<JsValue, IReadOnlyList<JsValue>, JsValue> _call;
    private readonly Func<IReadOnlyList<JsValue>, JsValue>? _construct;
    // Optional construct delegate that also receives the [[Construct]] newTarget, for
    // builtins whose behavior depends on it (e.g. an abstract base like Iterator that
    // throws for `new Iterator()` but allows `class X extends Iterator`).
    private readonly Func<IReadOnlyList<JsValue>, JsValue, JsValue>? _constructWithNewTarget;

    public NativeFunctionObject(
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        Func<IReadOnlyList<JsValue>, JsValue>? construct = null,
        int length = 0,
        Func<IReadOnlyList<JsValue>, JsValue, JsValue>? constructWithNewTarget = null)
    {
        Name = name;
        _call = call;
        _construct = construct;
        _constructWithNewTarget = constructWithNewTarget;
        _ = DefineOwnProperty(
            "name",
            new JsPropertyDescriptor(
                JsValue.FromString(name),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        _ = DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(length),
                Writable: false,
                Enumerable: false,
                Configurable: true));
    }

    public string Name { get; }
    public bool IsConstructor => _construct is not null || _constructWithNewTarget is not null;

    public JsValue Call(JsValue thisValue, IReadOnlyList<JsValue> args) => _call(thisValue, args);

    public JsValue Construct(IReadOnlyList<JsValue> args) => ConstructWithNewTarget(args, JsValue.Undefined);

    public JsValue ConstructWithNewTarget(IReadOnlyList<JsValue> args, JsValue newTarget)
    {
        if (_constructWithNewTarget is not null)
        {
            return _constructWithNewTarget(args, newTarget);
        }

        if (_construct is null)
        {
            // ECMA-262 9.1.10 — throw TypeError for non-constructor callables.
            // Callers (ConstructFunction) catch JsThrownException and convert
            // to a proper TypeError via ThrowOrHandle.
            throw new JsThrownException(JsValue.Undefined);
        }

        return _construct(args);
    }
}
