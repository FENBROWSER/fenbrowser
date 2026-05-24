using FenBrowser.Js.Builtins;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Tests;

// Minimal IBuiltinContext for unit-testing builtin modules without a full
// BytecodeInterpreter. Methods that a test doesn't need throw NotSupportedException
// so missing implementations surface loudly rather than silently doing the wrong thing.
internal sealed class TestBuiltinContext : IBuiltinContext
{
    private readonly JsHeap _heap;

    public TestBuiltinContext(JsHeap heap)
    {
        _heap = heap;
    }

    public JsHeap Heap => _heap;

    public double ToNumber(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Int32 => value.AsInt32(),
            JsValueTag.Number => value.AsNumber(),
            JsValueTag.Boolean => value.AsBoolean() ? 1d : 0d,
            JsValueTag.Null => 0d,
            JsValueTag.Undefined => double.NaN,
            JsValueTag.String => double.TryParse(value.AsString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d) ? d : double.NaN,
            _ => double.NaN
        };
    }

    public string ToStringValue(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.String => value.AsString(),
            JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Number => value.AsNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
            JsValueTag.Null => "null",
            JsValueTag.Undefined => "undefined",
            _ => throw new NotSupportedException("ToStringValue for objects is not supported in TestBuiltinContext.")
        };
    }

    public JsValue CallFunction(JsValue fn, IReadOnlyList<JsValue> args, JsValue thisValue)
        => throw new NotSupportedException("CallFunction is not supported in TestBuiltinContext.");

    public JsValue ConstructFunction(JsValue ctor, IReadOnlyList<JsValue> args)
        => throw new NotSupportedException("ConstructFunction is not supported in TestBuiltinContext.");

    public bool TryGetPropertyValue(JsObject obj, JsValue receiver, string name, out JsValue value)
        => throw new NotSupportedException("TryGetPropertyValue is not supported in TestBuiltinContext.");

    public int GetArrayLength(JsObject obj)
        => throw new NotSupportedException("GetArrayLength is not supported in TestBuiltinContext.");

    public JsValue CreateTypeError(string message)
        => throw new NotSupportedException("CreateTypeError is not supported in TestBuiltinContext.");

    public JsValue CreateRangeError(string message)
        => throw new NotSupportedException("CreateRangeError is not supported in TestBuiltinContext.");

    public JsValue CreateSyntaxError(string message)
        => throw new NotSupportedException("CreateSyntaxError is not supported in TestBuiltinContext.");

    public JsValue CreateError(string message)
        => throw new NotSupportedException("CreateError is not supported in TestBuiltinContext.");

    public JsValue CreateUriError(string message)
        => throw new NotSupportedException("CreateUriError is not supported in TestBuiltinContext.");

    public JsValue CreateReferenceError(string message)
        => throw new NotSupportedException("CreateReferenceError is not supported in TestBuiltinContext.");

    public void DefineIntrinsicFunction(
        ObjectHandle ownerHandle, JsObject owner, string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call, int length)
        => throw new NotSupportedException("DefineIntrinsicFunction is not supported in TestBuiltinContext.");

    public ObjectHandle GetObjectPrototype()
        => throw new NotSupportedException("GetObjectPrototype is not supported in TestBuiltinContext.");

    public ObjectHandle GetArrayPrototype()
        => throw new NotSupportedException("GetArrayPrototype is not supported in TestBuiltinContext.");

    public ObjectHandle GetErrorPrototype()
        => throw new NotSupportedException("GetErrorPrototype is not supported in TestBuiltinContext.");

    public ObjectHandle GetParseIntFunction()
        => throw new NotSupportedException("GetParseIntFunction is not supported in TestBuiltinContext.");

    public ObjectHandle GetParseFloatFunction()
        => throw new NotSupportedException("GetParseFloatFunction is not supported in TestBuiltinContext.");

    public JsValue CreateSymbol(string? description)
        => JsValue.FromSymbol(description);

    public JsValue CreateWellKnownSymbol(string name)
        => JsValue.FromSymbol("Symbol." + name);

    public JsValue SymbolFor(string key)
        => JsValue.FromSymbol(key);

    public JsValue SymbolKeyFor(long id)
        => JsValue.Undefined;
}
