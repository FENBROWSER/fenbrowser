using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class InterpreterFrame
{
    public InterpreterFrame(BytecodeFunction function, JsValue thisValue)
    {
        Function = function;
        ThisValue = thisValue;
        Registers = new JsValue[function.RegisterCount];
        Variables = new JsValue[Math.Max(1, function.VariableSlots.Count)];
        ExceptionHandlers = new Stack<int>();
        Registers[0] = JsValue.Undefined;
    }

    public BytecodeFunction Function { get; }
    public JsValue ThisValue { get; }

    public JsValue[] Registers { get; }

    public JsValue[] Variables { get; }

    public Stack<int> ExceptionHandlers { get; }

    public int InstructionPointer { get; set; }
}
