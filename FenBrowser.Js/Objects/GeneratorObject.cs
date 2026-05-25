using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 27.5 — Generator Objects.
// Stores the suspended execution state of a generator function so it can be
// resumed via .next(), .return(), or .throw().
public sealed class GeneratorObject : JsObject
{
    // The bytecode function being executed.
    public BytecodeFunction Function { get; }

    // Saved instruction pointer for resumption.
    public int InstructionPointer { get; set; }

    // Saved register file.
    public JsValue[] Registers { get; }

    // Saved environment record chain.
    public EnvironmentRecord? Environment { get; set; }

    // Saved outer environment for closure resolution.
    public EnvironmentRecord? OuterEnvironment { get; set; }

    // Saved this value.
    public JsValue ThisValue { get; set; }

    // Execution state: Suspended, Executing, Completed.
    public GeneratorState State { get; set; } = GeneratorState.Suspended;

    // The last value sent into the generator (via .next(val)).
    public JsValue SentValue { get; set; } = JsValue.Undefined;

    // The A-operand (destination register) of the last Yield instruction.
    // When the generator is resumed, SentValue is injected into this register
    // so `var x = yield expr;` evaluates x to the value passed to .next().
    // Set to -1 on creation; overwritten by each Yield opcode handler.
    public int YieldDestReg { get; set; } = -1;

    public GeneratorObject(BytecodeFunction function, JsValue[] registers, EnvironmentRecord? environment)
    {
        Function = function;
        Registers = registers;
        Environment = environment;
    }

    // Extract the initial parameter values that were bound when the generator
    // function was called. These live in registers[1..paramCount+1] (register 0
    // is the return slot). On the first .next() call, ExecuteGenerator passes
    // these as the args to ExecuteInternal so the frame environment binds the
    // correct parameter values.
    public JsValue[] GetInitialParameters()
    {
        var paramCount = Function.ParameterNames.Count;
        var result = new JsValue[paramCount];
        for (var i = 0; i < paramCount; i++)
            result[i] = Registers[i + 1]; // register 0 = return slot
        return result;
    }
}

public enum GeneratorState
{
    Suspended,
    Executing,
    Completed
}
