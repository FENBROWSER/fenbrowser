using FenBrowser.Js.Builtins;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Tests;

// Minimal IBuiltinContext for unit-testing builtin modules without a full
// BytecodeInterpreter. ToNumber handles primitives only — it does not
// support object-to-primitive coercion, which is fine for tests of
// GlobalConstantsBuiltin, BuiltinRegistry, and other modules that don't
// call ToNumber on user-provided arguments.
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
}
