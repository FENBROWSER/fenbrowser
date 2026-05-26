using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class InterpreterFrame
{
    public InterpreterFrame(
        BytecodeFunction function,
        JsValue thisValue,
        EnvironmentRecord? environment = null)
    {
        Function = function;
        ThisValue = thisValue;
        Registers = new JsValue[function.RegisterCount];
        ExceptionHandlers = new Stack<int>();
        Registers[0] = JsValue.Undefined;

        // Every frame carries an EnvironmentRecord. Callers that have a real outer
        // lexical environment supply one; everyone else gets a fresh declarative
        // record so env-aware code can rely on Environment never being null.
        Environment = environment ?? new DeclarativeEnvironmentRecord(outerEnv: null);
    }

    public BytecodeFunction Function { get; }
    public JsValue ThisValue { get; }

    public JsValue[] Registers { get; }

    public EnvironmentRecord Environment { get; set; }

    public Stack<int> ExceptionHandlers { get; }

    public int InstructionPointer { get; set; }

    // H.3 - the JsFunctionObject whose body this frame is executing. Set by
    // the call site when known; null for the top-level Execute frame and for
    // native-only call paths. `super.x` reads CalleeFunctionObject.HomeObject
    // to walk the prototype chain.
    public JsFunctionObject? CalleeFunctionObject { get; set; }

    // H.5 - new.target. Bound to the constructor invoked by `new` when this
    // frame is a construct call, or JsValue.Undefined for ordinary calls.
    // ECMA-262 9.1.1.3 NewTarget.
    public JsValue NewTarget { get; set; } = JsValue.Undefined;

    // Generator that owns this frame. When set, the Yield/YieldStar opcodes
    // will save frame state (IP, registers, environment) back to the generator
    // before returning, so the next .next()/resume can continue from this point.
    // ECMA-262 27.5.1.3 GeneratorYield / 27.5.1.2 Resume.
    public GeneratorObject? OwnerGenerator { get; set; }

    // Async context for async function suspend/resume. When set, the Await
    // opcode will save frame state to the context before returning, so the
    // promise reaction callback can resume execution from this point.
    // ECMA-262 27.7.5 Await.
    public AsyncContext? AsyncContext { get; set; }
}
