using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public sealed class JsFunctionObject : JsObject
{
    public JsFunctionObject(
        BytecodeFunction function,
        IReadOnlyDictionary<string, JsVariableCell>? capturedVariables = null,
        EnvironmentRecord? outerEnvironment = null)
    {
        Function = function;
        CapturedVariables = capturedVariables ?? new Dictionary<string, JsVariableCell>(StringComparer.Ordinal);
        OuterEnvironment = outerEnvironment;
    }

    public BytecodeFunction Function { get; }

    public IReadOnlyDictionary<string, JsVariableCell> CapturedVariables { get; }

    // B.6.4 — the lexical EnvironmentRecord active when this function was created
    // (ECMA-262 10.2 [[Environment]] slot). When non-null, ExecuteInternalCore builds
    // the callee frame's env as a fresh declarative record whose OuterEnv is this one,
    // so free identifier references in the callee walk the lexical scope chain via
    // env records instead of (only) the legacy JsVariableCell capture map.
    public EnvironmentRecord? OuterEnvironment { get; }

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
