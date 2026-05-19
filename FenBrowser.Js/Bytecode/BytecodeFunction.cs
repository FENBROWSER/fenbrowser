using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeFunction
{
    public required IReadOnlyList<Instruction> Instructions { get; init; }

    public required IReadOnlyList<JsValue> Constants { get; init; }

    public required IReadOnlyDictionary<string, int> VariableSlots { get; init; }

    public int RegisterCount { get; init; }
}
