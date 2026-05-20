using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public sealed class JsFunctionObject : JsObject
{
    public JsFunctionObject(BytecodeFunction function, IReadOnlyDictionary<string, JsVariableCell>? capturedVariables = null)
    {
        Function = function;
        CapturedVariables = capturedVariables ?? new Dictionary<string, JsVariableCell>(StringComparer.Ordinal);
    }

    public BytecodeFunction Function { get; }

    public IReadOnlyDictionary<string, JsVariableCell> CapturedVariables { get; }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        foreach (var cell in CapturedVariables.Values)
        {
            if (cell.Value.Tag == JsValueTag.Object)
            {
                tracer.Trace(cell.Value.AsObjectHandle());
            }
        }
    }
}
