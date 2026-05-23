using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
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

    public EnvironmentRecord Environment { get; }

    public Stack<int> ExceptionHandlers { get; }

    public int InstructionPointer { get; set; }
}
