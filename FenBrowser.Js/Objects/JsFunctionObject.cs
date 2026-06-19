using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public sealed class JsFunctionObject : JsObject
{
    public JsFunctionObject(
        BytecodeFunction function,
        EnvironmentRecord? outerEnvironment = null,
        FunctionKind kind = FunctionKind.Ordinary)
    {
        Function = function;
        OuterEnvironment = outerEnvironment;
        Kind = kind;
    }

    public BytecodeFunction Function { get; }

    // Plan §18: how this function was created (ordinary, arrow, method, constructor, etc.)
    public FunctionKind Kind { get; }

    // ECMA-262 10.2 [[Environment]]: the lexical EnvironmentRecord active when this
    // function was created. Callee frames chain a fresh declarative record to this
    // one so free identifier references walk the lexical scope chain.
    public EnvironmentRecord? OuterEnvironment { get; }

    // ECMA-262 10.2 [[HomeObject]] (H.3): the object on which this method was
    // installed - the class prototype for instance methods, the class itself
    // for static methods, undefined for ordinary functions. `super` property
    // lookups walk this object's [[Prototype]] chain so super.foo() resolves to
    // the inherited definition.
    public Runtime.ObjectHandle? HomeObject { get; set; }

    // The heap handle of this function object itself, stamped at allocation. Lets a
    // named function expression bind its own name (visible inside its body) to itself.
    public Runtime.ObjectHandle? SelfHandle { get; set; }

    // ECMA-262 15.7.10: computed property name keys for class fields, evaluated
    // at class-definition time and stored here by StoreFieldKey. The constructor
    // loads them via LoadFieldKey instead of recomputing the expression.
    public List<JsValue> ComputedFieldKeys { get; } = new();

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        OuterEnvironment?.Trace(tracer);
        if (HomeObject is { } home)
        {
            tracer.Trace(home);
        }
    }
}
