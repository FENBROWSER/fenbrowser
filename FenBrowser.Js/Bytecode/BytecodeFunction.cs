using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeFunction
{
    public string? Name { get; init; }

    public required IReadOnlyList<Instruction> Instructions { get; init; }

    public required IReadOnlyList<JsValue> Constants { get; init; }

    public required IReadOnlyDictionary<string, int> VariableSlots { get; init; }

    public IReadOnlyList<string> VarDeclarationNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LexicalDeclarationNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstDeclarationNames { get; init; } = Array.Empty<string>();

    public required IReadOnlyList<string> PropertyNames { get; init; }

    public required IReadOnlyList<string> ParameterNames { get; init; }

    public bool HasOwnArgumentsObject { get; init; }

    public required IReadOnlyList<BytecodeFunction> NestedFunctions { get; init; }

    public int RegisterCount { get; init; }

    internal Dictionary<int, PolymorphicInlineCache>? LoadICs { get; set; }
    internal Dictionary<int, PolymorphicInlineCache>? StoreICs { get; set; }
}
