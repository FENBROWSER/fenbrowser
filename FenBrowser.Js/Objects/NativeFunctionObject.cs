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

    public NativeFunctionObject(
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        Func<IReadOnlyList<JsValue>, JsValue>? construct = null,
        int length = 0)
    {
        Name = name;
        _call = call;
        _construct = construct;
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

    public JsValue Call(JsValue thisValue, IReadOnlyList<JsValue> args) => _call(thisValue, args);

    public JsValue Construct(IReadOnlyList<JsValue> args)
    {
        if (_construct is null)
        {
            throw new InvalidOperationException("Value is not constructible.");
        }

        return _construct(args);
    }
}
