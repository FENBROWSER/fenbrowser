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
        Variables = new VariableStore(function.VariableSlots.Count);
        ExceptionHandlers = new Stack<int>();
        Registers[0] = JsValue.Undefined;

        // B.6.2 seam: every frame now carries an EnvironmentRecord alongside the
        // VariableStore. Callers that have already migrated supply one; everyone else
        // gets a fresh declarative record so downstream env-aware code can rely on
        // Environment never being null. Reads/writes still go through VariableStore
        // until B.6.3 migrates the opcode handlers.
        Environment = environment ?? new DeclarativeEnvironmentRecord(outerEnv: null);
    }

    public BytecodeFunction Function { get; }
    public JsValue ThisValue { get; }

    public JsValue[] Registers { get; }

    public VariableStore Variables { get; }

    public EnvironmentRecord Environment { get; }

    public Stack<int> ExceptionHandlers { get; }

    public int InstructionPointer { get; set; }
}
