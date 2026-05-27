using FenBrowser.Js.Runtime;
using FenBrowser.Js.Objects;

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

    public int RestParameterIndex { get; init; } = -1;

    public bool HasOwnArgumentsObject { get; init; }

    public FunctionKind Kind { get; init; } = FunctionKind.Ordinary;

    public bool IsStrictMode { get; init; }

    public required IReadOnlyList<BytecodeFunction> NestedFunctions { get; init; }

    public int RegisterCount { get; init; }

    // H.5: true if this function is the constructor of a class with `extends`.
    // Derived constructors must call super() before accessing `this`.
    public bool IsDerivedConstructor { get; init; }

    internal Dictionary<int, PolymorphicInlineCache>? LoadICs { get; set; }
    internal Dictionary<int, PolymorphicInlineCache>? StoreICs { get; set; }
    internal Dictionary<int, CallICEntry>? CallICs { get; set; }

    // Brand tokens for private fields/methods. Each class with private members
    // gets a unique long token. The D field on DefinePrivateField/GetPrivateField/
    // SetPrivateField instructions indexes into this list. The interpreter checks
    // obj.PrivateBrand == BrandTokens[ins.D] for access.
    public IReadOnlyList<long> BrandTokens { get; init; } = Array.Empty<long>();
}
