using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Objects;

public sealed class JsFunctionObject : JsObject
{
    public JsFunctionObject(
        BytecodeFunction function,
        EnvironmentRecord? outerEnvironment = null)
    {
        Function = function;
        OuterEnvironment = outerEnvironment;
    }

    public BytecodeFunction Function { get; }

    // ECMA-262 10.2 [[Environment]]: the lexical EnvironmentRecord active when this
    // function was created. Callee frames chain a fresh declarative record to this
    // one so free identifier references walk the lexical scope chain.
    public EnvironmentRecord? OuterEnvironment { get; }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        OuterEnvironment?.Trace(tracer);
    }
}
