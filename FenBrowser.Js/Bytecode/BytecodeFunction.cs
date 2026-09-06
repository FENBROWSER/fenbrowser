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

    // The dispatch loop reads an instruction for every step it takes, and
    // through the interface that is two virtual calls plus a copy of a
    // 24-byte struct each time, with no bounds-check elimination and no
    // inlining. Materialise the array once per function and let the loop
    // index it directly.
    private Instruction[]? _instructionArray;

    internal Instruction[] InstructionArray =>
        _instructionArray ??= Instructions as Instruction[] ?? System.Linq.Enumerable.ToArray(Instructions);

    public required IReadOnlyList<JsValue> Constants { get; init; }

    public required IReadOnlyDictionary<string, int> VariableSlots { get; init; }
    // Every variable read and write translates a slot back to its name, and
    // that lookup used to go through a weak table keyed by this function on
    // each access. The array derives only from VariableSlots and lives and
    // dies with this function, so holding it here is the same lifetime with
    // none of the lookup.
    private string?[]? _slotNames;

    // The slots for this function's parameters and hoisted vars, worked out
    // once rather than by hashing each name on every call.
    private int[]? _parameterSlots;
    private int[]? _varSlots;

    internal int[] ParameterSlots => _parameterSlots ??= MapSlots(ParameterNames);

    internal int[] VarSlots => _varSlots ??= MapSlots(VarDeclarationNames);

    private int[] MapSlots(IReadOnlyList<string> names)
    {
        var slots = new int[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            slots[i] = names[i] is { } name && VariableSlots.TryGetValue(name, out var slot) ? slot : -1;
        }

        return slots;
    }

    internal string?[] SlotNames =>
        _slotNames ??= FenBrowser.Js.Interpreter.SlotNameTable.BuildNames(this);


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

    // True only for an arrow-like function that resolves `arguments` through
    // its enclosing environment. The compiler propagates this through nested
    // arrows so the nearest ordinary function retains its arguments object.
    internal bool UsesOuterArguments { get; init; }

    public FunctionKind Kind { get; init; } = FunctionKind.Ordinary;

    // True when this function was compiled from eval() source; var/function
    // declarations use deletable bindings per Annex B B.3.3.3.
    public bool IsEvalCode { get; set; }

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

    // ECMA-262 15.7.10: computed property names for class fields are evaluated
    // at class-definition time. The resulting property keys are stored here and
    // loaded in the constructor via LoadFieldKey instead of recomputing.
    public List<JsValue> ComputedFieldKeys { get; init; } = new();

    // Runtime feedback. These fields are intentionally NOT part of the immutable
    // compiled template stored by BytecodeCache. Property ICs hold Shapes and call
    // ICs hold heap-local ObjectHandle values, so sharing them across interpreters
    // can make one heap consume another heap's feedback.
    internal Dictionary<int, PolymorphicInlineCache>? LoadICs { get; set; }
    internal Dictionary<int, PolymorphicInlineCache>? StoreICs { get; set; }
    internal Dictionary<int, CallICEntry>? CallICs { get; set; }

    // Tier 4 #24 JIT bookkeeping. This is also execution-local feedback and must be
    // reset when a cached template is materialized for another compilation request.
    internal int Invocations;
    internal int BackEdges;
#if !PUBLISH_AOT
    internal bool JitCompileAttempted;

    // How many frames for this function are on the interpreter's active stack.
    // The JIT entry test asks "is this a recursive activation?" on every call it
    // is eligible for, and answering that by scanning the whole frame stack made
    // the question cost O(call depth) per call - on a deeply nested bundle, the
    // dominant part of the test. The frame scope maintains this instead.
    internal int ActiveActivations;

    /// <summary>
    /// Loop headers the JIT body can be entered at while a frame is already
    /// running it. Null when the compiled form only makes sense from the top.
    /// </summary>
    internal HashSet<int>? OsrEntryPoints;
    internal JitCompiler.JitDelegate? JitDelegate;
#endif

    public int InvocationsObserved => Invocations;
    public int BackEdgesObserved => BackEdges;
#if !PUBLISH_AOT
    public bool JitCompiled => JitDelegate is not null;
    public bool JitCompileWasAttempted => JitCompileAttempted;
#endif

    // Brand tokens for private fields/methods. Each class with private members
    // gets a unique long token. The D field on DefinePrivateField/GetPrivateField/
    // SetPrivateField instructions indexes into this list. The interpreter checks
    // obj.PrivateBrand == BrandTokens[ins.D] for access.
    public IReadOnlyList<long> BrandTokens { get; init; } = Array.Empty<long>();

    /// <summary>
    /// True when this function tree can safely be retained as a process-global
    /// compiled template. Heap object constants and private-brand tokens are excluded:
    /// the former are heap-local, while the latter must be freshly allocated for each
    /// class evaluation rather than reused from a previous cached compilation.
    /// </summary>
    internal bool CanUseProcessGlobalTemplate()
    {
        if (BrandTokens.Count != 0)
        {
            return false;
        }

        for (var i = 0; i < Constants.Count; i++)
        {
            if (IsHeapLocalValue(Constants[i]))
            {
                return false;
            }
        }

        for (var i = 0; i < ComputedFieldKeys.Count; i++)
        {
            if (IsHeapLocalValue(ComputedFieldKeys[i]))
            {
                return false;
            }
        }

        for (var i = 0; i < NestedFunctions.Count; i++)
        {
            if (!NestedFunctions[i].CanUseProcessGlobalTemplate())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Materializes a fresh execution object from immutable compiler output. The
    /// instruction/constant/name tables are immutable after compilation and can be
    /// shared; nested functions and every mutable runtime-feedback field are fresh.
    /// </summary>
    internal BytecodeFunction CreateExecutionCopy()
    {
        var nested = new BytecodeFunction[NestedFunctions.Count];
        for (var i = 0; i < NestedFunctions.Count; i++)
        {
            nested[i] = NestedFunctions[i].CreateExecutionCopy();
        }

        return new BytecodeFunction
        {
            Name = Name,
            SourceText = SourceText,
            Instructions = Instructions,
            Constants = Constants,
            VariableSlots = VariableSlots,
            VarDeclarationNames = VarDeclarationNames,
            LexicalDeclarationNames = LexicalDeclarationNames,
            ConstDeclarationNames = ConstDeclarationNames,
            PropertyNames = PropertyNames,
            ParameterNames = ParameterNames,
            RestParameterIndex = RestParameterIndex,
            ExpectedArgumentCount = ExpectedArgumentCount,
            BindsOwnNameInBody = BindsOwnNameInBody,
            HasOwnArgumentsObject = HasOwnArgumentsObject,
            UsesRestrictedArgumentsObject = UsesRestrictedArgumentsObject,
            UsesOuterArguments = UsesOuterArguments,
            Kind = Kind,
            IsEvalCode = IsEvalCode,
            IsStrictMode = IsStrictMode,
            NestedFunctions = nested,
            RegisterCount = RegisterCount,
            PrologueEndIp = PrologueEndIp,
            IsDerivedConstructor = IsDerivedConstructor,
            IsClassConstructor = IsClassConstructor,
            ComputedFieldKeys = new List<JsValue>(ComputedFieldKeys),
            BrandTokens = BrandTokens,

            // Explicitly document the execution-local reset rather than relying on
            // default field initialization as the cache contract evolves.
            LoadICs = null,
            StoreICs = null,
            CallICs = null,
            Invocations = 0,
            BackEdges = 0,
#if !PUBLISH_AOT
            JitCompileAttempted = false,
            JitDelegate = null,
#endif
        };
    }

    private static bool IsHeapLocalValue(JsValue value) =>
        value.Tag is JsValueTag.Object or JsValueTag.HostObject;
}
