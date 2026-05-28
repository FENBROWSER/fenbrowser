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

    // Instruction index of the first opcode AFTER parameter-binding statements.
    // Used by generator / async-generator call paths to execute parameter
    // destructuring synchronously before suspending the new generator.
    // 0 means "no separate prologue" (run the whole body lazily as before).
    public int PrologueEndIp { get; init; }

    // H.5: true if this function is the constructor of a class with `extends`.
    // Derived constructors must call super() before accessing `this`.
    public bool IsDerivedConstructor { get; init; }

    internal Dictionary<int, PolymorphicInlineCache>? LoadICs { get; set; }
    internal Dictionary<int, PolymorphicInlineCache>? StoreICs { get; set; }
    internal Dictionary<int, CallICEntry>? CallICs { get; set; }

    // Tier 4 #24 JIT bookkeeping. Invocations is incremented on every call
    // through CallFunction; once it crosses JitCompiler.TierUpThreshold the
    // interpreter calls TryCompile once. JitDelegate is the produced
    // compiled body, or null when the JIT bailed out (which is the
    // default path for almost every function today).
    internal int Invocations;
    // Back-edge counter for JIT tier-up. Incremented in the dispatch loop
    // whenever a Jump/JumpIfFalse targets an earlier instruction (i.e. a
    // loop iteration). Combined with Invocations in the tier-up trigger:
    // a function with one invocation but a million loop iterations still
    // gets JIT-compiled on the next call. See audit doc §3.2.
    internal int BackEdges;
    internal JitCompiler.JitDelegate? JitDelegate;
    internal bool JitCompileAttempted;

    // Read-only accessors for test/diagnostic use. The setters are
    // internal so only the interpreter mutates them; the getters expose
    // counters for tier-up assertion in tests and for runtime
    // introspection by tooling.
    public int InvocationsObserved => Invocations;
    public int BackEdgesObserved => BackEdges;
    public bool JitCompiled => JitDelegate is not null;
    public bool JitCompileWasAttempted => JitCompileAttempted;

    // Brand tokens for private fields/methods. Each class with private members
    // gets a unique long token. The D field on DefinePrivateField/GetPrivateField/
    // SetPrivateField instructions indexes into this list. The interpreter checks
    // obj.PrivateBrand == BrandTokens[ins.D] for access.
    public IReadOnlyList<long> BrandTokens { get; init; } = Array.Empty<long>();
}
