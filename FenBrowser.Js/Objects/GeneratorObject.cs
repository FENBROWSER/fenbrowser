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

    public GeneratorObject(BytecodeFunction function, JsValue[] registers, EnvironmentRecord? environment)
    {
        Function = function;
        Registers = registers;
        Environment = environment;
    }
}

public enum GeneratorState
{
    Suspended,
    Executing,
    Completed
}
