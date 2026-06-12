using FenBrowser.Js.Runtime;
using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeFunction
{
    public string? Name { get; init; }

    // The exact source text of this function (from `function`/parameter list
    // through the closing brace, or the full arrow). Populated by the compiler
    // when the original source is available so Function.prototype.toString can
    // return the real source per ECMA-262 20.2.3.5. Null for functions with no
    // recoverable source (Function constructor, synthesised constructors).
    public string? SourceText { get; internal set; }

    public required IReadOnlyList<Instruction> Instructions { get; init; }

    public required IReadOnlyList<JsValue> Constants { get; init; }

    public required IReadOnlyDictionary<string, int> VariableSlots { get; init; }

    public IReadOnlyList<string> VarDeclarationNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LexicalDeclarationNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstDeclarationNames { get; init; } = Array.Empty<string>();

    public required IReadOnlyList<string> PropertyNames { get; init; }

    public required IReadOnlyList<string> ParameterNames { get; init; }

    public int RestParameterIndex { get; init; } = -1;

    // ECMA-262 ExpectedArgumentCount: the function's `length` — the count of formal
    // parameters before the first one that has a default initializer or is the rest
    // parameter. -1 means "not computed" (falls back to ParameterNames.Count).
    public int ExpectedArgumentCount { get; init; } = -1;

    // True for a named function expression: its own name is bound (immutably) inside
    // the function body so it can refer to itself (e.g. for recursion), but the name
    // is not visible outside the expression.
    public bool BindsOwnNameInBody { get; init; }

    public bool HasOwnArgumentsObject { get; init; }
    public bool UsesRestrictedArgumentsObject { get; init; }

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

    // True when this function is a class constructor, including base classes.
    // `super.prop` inside constructors walks the prototype of `class.prototype`,
    // while `super()` walks the constructor's own [[Prototype]] chain.
    public bool IsClassConstructor { get; init; }

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
