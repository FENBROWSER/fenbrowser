using FenBrowser.Js.Runtime;
using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeFunction
{
    // Set the first time this function is entered, so CompiledCodeCoverage counts
    // it once. Only written while that diagnostic is on.
    internal bool CoverageEntryRecorded;

    public string? Name { get; init; }

    // The exact source text of this function (from `function`/parameter list
    // through the closing brace, or the full arrow). Populated by the compiler
    // when the original source is available so Function.prototype.toString can
    // return the real source per ECMA-262 20.2.3.5. Null for functions with no
    // recoverable source (Function constructor, synthesised constructors).
    public string? SourceText { get; internal set; }

    // Where the source came from - the script URL, module URL or a host label -
    // so a stack frame from a minified bundle can be traced back to its file.
    // Null when the host gave the compiler no path.
    public string? SourcePath { get; internal set; }

    public required IReadOnlyList<Instruction> Instructions { get; init; }

    // The dispatch loop reads an instruction for every step it takes, and
    // through the interface that is two virtual calls plus a copy of a
    // 24-byte struct each time, with no bounds-check elimination and no
    // inlining. Materialise the array once per function and let the loop
    // index it directly.
    private Instruction[]? _instructionArray;

    // Whether a formal parameter named "arguments" shadows the arguments
    // object. Fixed by the parameter list, so it is settled once instead of
    // scanning the list on every call.
    private bool? _argumentsShadowedByParameter;

    internal bool ArgumentsShadowedByParameter =>
        _argumentsShadowedByParameter ??= ParameterNames.Contains("arguments", StringComparer.Ordinal);

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

    // A var whose name has no slot has to be created through the environment by
    // name. That is rare, but the entry path cannot find out per call without
    // re-walking the array it is trying to avoid walking, so settle it once.
    internal bool AllVarSlotsMapped => _allVarSlotsMapped ??= ComputeAllVarSlotsMapped();

    private bool? _allVarSlotsMapped;

    private bool ComputeAllVarSlotsMapped()
    {
        var slots = VarSlots;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] < 0) return false;
        }

        return true;
    }

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

    // The register-window interpreter's verdict on this body: whether its frame
    // can be a slice of a shared stack, and where each parameter and variable
    // sits in it. Cached on the function rather than in a side table because a
    // call site reads it before every call, and a per-call weak-table probe is
    // the sort of cost that loop exists to remove. Null until first asked for;
    // computed by FrameLayout.For.
    internal FenBrowser.Js.Interpreter2.FrameLayout? Interp2Layout;


    public IReadOnlyList<string> VarDeclarationNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LexicalDeclarationNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstDeclarationNames { get; init; } = Array.Empty<string>();

    // ECMA-262 10.2.1.3 FunctionDeclarationInstantiation steps 28 and 30. When a
    // parameter expression could observe the difference - it creates a closure or
    // calls eval - the body's declarations live in an environment of their own,
    // entered by EnterFunctionBodyScope once the parameters are bound. These are
    // the names that environment holds, and the three lists above then hold only
    // what the parameters declare themselves.
    public IReadOnlyList<string> BodyVarNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BodyLexicalNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BodyConstNames { get; init; } = Array.Empty<string>();

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

    /// <summary>
    /// Compiled from arrow syntax. Kind says how the body runs (an async arrow
    /// is FunctionKind.Async so it gets the async machinery); this says what the
    /// function *is* for ECMA-262 10.2.1: no own `this`, `arguments` or
    /// `new.target`, and no [[Construct]].
    /// </summary>
    public bool IsArrow { get; init; }

    // When compilation was asked for, so the compiler can report how long the
    // request waited behind others before it was served.
    internal long CompileRequestedTicks;

    // Whether this function's environment record provably dies with its call,
    // so the interpreter may reuse its slot storage instead of allocating a
    // fresh Binding[] per call.
    //
    // This is the static half of the test, and it only rules out shapes whose
    // capture cannot be observed at the moment it happens: `with` splices a
    // record into the chain, direct eval can introduce bindings and reach the
    // scope, a generator or async body suspends with its environment intact,
    // eval code has no frame bounding its lifetime, and a mapped arguments
    // object aliases the parameter bindings.
    //
    // Creating a closure deliberately does NOT disqualify a function here.
    // Almost every real function contains a CreateFunction somewhere, so
    // rejecting on that left the reuse covering 2.5% of calls on a real page.
    // A closure is caught instead at the instant it captures a scope, by the
    // runtime Escaped flag - which reflects the calls that really built one
    // rather than the ones that merely could.
    private bool? _environmentDiesWithCall;

    internal bool EnvironmentDiesWithCall
    {
        get
        {
            if (_environmentDiesWithCall is { } cached) return cached;

            var eligible = Kind == FunctionKind.Ordinary &&
                           !IsEvalCode &&
                           !HasOwnArgumentsObject;
            if (eligible)
            {
                foreach (var instruction in InstructionArray)
                {
                    if (instruction.OpCode is OpCode.PushWithEnvironment)
                    {
                        eligible = false;
                        break;
                    }

                    // Direct eval is flagged on the call, not by a distinct opcode.
                    if (instruction.E == 1 && instruction.OpCode is
                        OpCode.Call0 or OpCode.Call1 or OpCode.CallN or OpCode.CallSpread)
                    {
                        eligible = false;
                        break;
                    }
                }
            }

            _environmentDiesWithCall = eligible;
            return eligible;
        }
    }

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

    // A class's instance field initializers are compiled inline into its
    // constructor, but ECMA-262 15.7.10 specifies each as a method of its own:
    // new.target is undefined in them and in eval code they call. These bound
    // the inlined instructions [start, end); an arrow compiled inside an
    // initializer covers its whole body. -1 when there are none.
    public int FieldInitializerStart { get; init; } = -1;
    public int FieldInitializerEnd { get; init; } = -1;

    internal bool IsInFieldInitializer(int ip) => ip >= FieldInitializerStart && ip < FieldInitializerEnd;

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

    // ECMA-262 13.2.8.4 GetTemplateObject: one entry per tagged-template site in
    // this function. Cooked entries are null where the literal has an illegal
    // escape sequence, which the spec renders as undefined. Immutable, so a
    // cached template shares the list with its copies.
    public IReadOnlyList<TemplateSite> TemplateSites { get; init; } = Array.Empty<TemplateSite>();

    // Runtime feedback. These fields are intentionally NOT part of the immutable
    // compiled template stored by BytecodeCache. Property ICs hold Shapes and call
    // ICs hold heap-local ObjectHandle values, so sharing them across interpreters
    // can make one heap consume another heap's feedback.
    // Indexed by instruction offset, not keyed by it. Every property read,
    // property write and call consulted its cache through a dictionary lookup,
    // and on a bundle that runs tens of millions of them the hash of the key
    // cost about as much as the cache saved. The offset is already a dense
    // index into this function's instructions, so an array is the natural
    // store: one bounds check and one load.
    //
    // The background JIT compiler creates sites for the code it is compiling
    // while the interpreter goes on filling them in on the main thread, so both
    // the arrays and their slots are published with a compare-and-swap. With a
    // plain `??=` each thread could install its own array or site, and the
    // compiled code would then read one the interpreter never updates.
    private FenBrowser.Js.Jit.CacheIR.CacheIRSite?[]? _loadCacheSites;
    private FenBrowser.Js.Jit.CacheIR.CacheIRSite?[]? _storeCacheSites;
    private CallICEntry?[]? _callICs;

    internal FenBrowser.Js.Jit.CacheIR.CacheIRSite?[]? LoadCacheSites { get => _loadCacheSites; init => _loadCacheSites = value; }
    internal FenBrowser.Js.Jit.CacheIR.CacheIRSite?[]? StoreCacheSites { get => _storeCacheSites; init => _storeCacheSites = value; }
    internal CallICEntry?[]? CallICs { get => _callICs; init => _callICs = value; }

    internal FenBrowser.Js.Jit.CacheIR.CacheIRSite?[] EnsureLoadCacheSites() =>
        _loadCacheSites ?? PublishOnce(ref _loadCacheSites, new FenBrowser.Js.Jit.CacheIR.CacheIRSite?[InstructionArray.Length]);

    internal FenBrowser.Js.Jit.CacheIR.CacheIRSite?[] EnsureStoreCacheSites() =>
        _storeCacheSites ?? PublishOnce(ref _storeCacheSites, new FenBrowser.Js.Jit.CacheIR.CacheIRSite?[InstructionArray.Length]);

    internal CallICEntry?[] EnsureCallICs() =>
        _callICs ?? PublishOnce(ref _callICs, new CallICEntry?[InstructionArray.Length]);

    /// <summary>The load site at <paramref name="ip"/>, created by whichever thread asks first.</summary>
    internal FenBrowser.Js.Jit.CacheIR.CacheIRSite EnsureLoadCacheSite(int ip) => EnsureSite(EnsureLoadCacheSites(), ip);

    /// <summary>The store site at <paramref name="ip"/>, created by whichever thread asks first.</summary>
    internal FenBrowser.Js.Jit.CacheIR.CacheIRSite EnsureStoreCacheSite(int ip) => EnsureSite(EnsureStoreCacheSites(), ip);

    private static FenBrowser.Js.Jit.CacheIR.CacheIRSite EnsureSite(FenBrowser.Js.Jit.CacheIR.CacheIRSite?[] sites, int ip)
    {
        if ((uint)ip >= (uint)sites.Length)
        {
            // Not an instruction of this function: a detached site keeps
            // callers simple and is never shared.
            return new FenBrowser.Js.Jit.CacheIR.CacheIRSite();
        }

        return System.Threading.Volatile.Read(ref sites[ip])
            ?? PublishOnce(ref sites[ip], new FenBrowser.Js.Jit.CacheIR.CacheIRSite());
    }

    private static T PublishOnce<T>(ref T? slot, T created) where T : class
        => System.Threading.Interlocked.CompareExchange(ref slot, created, null) ?? created;

    // Tier 4 #24 JIT bookkeeping. This is also execution-local feedback and must be
    // reset when a cached template is materialized for another compilation request.
    internal int Invocations;
    internal int BackEdges;
#if !PUBLISH_AOT
    internal bool JitCompileAttempted;

    /// <summary>
    /// Loop headers the JIT body can be entered at while a frame is already
    /// running it. Null when the compiled form only makes sense from the top.
    /// </summary>
    internal HashSet<int>? OsrEntryPoints;
    internal JitCompiler.JitDelegate? JitDelegate;

    /// <summary>Object operands the compiled body indexes. Set before the delegate is published.</summary>
    internal FenBrowser.Js.Jit.Baseline.BaselinePool? BaselinePool;
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
            SourcePath = SourcePath,
            Instructions = Instructions,
            Constants = Constants,
            VariableSlots = VariableSlots,
            VarDeclarationNames = VarDeclarationNames,
            LexicalDeclarationNames = LexicalDeclarationNames,
            ConstDeclarationNames = ConstDeclarationNames,
            BodyVarNames = BodyVarNames,
            BodyLexicalNames = BodyLexicalNames,
            BodyConstNames = BodyConstNames,
            PropertyNames = PropertyNames,
            ParameterNames = ParameterNames,
            RestParameterIndex = RestParameterIndex,
            ExpectedArgumentCount = ExpectedArgumentCount,
            BindsOwnNameInBody = BindsOwnNameInBody,
            HasOwnArgumentsObject = HasOwnArgumentsObject,
            UsesRestrictedArgumentsObject = UsesRestrictedArgumentsObject,
            UsesOuterArguments = UsesOuterArguments,
            IsArrow = IsArrow,
            Kind = Kind,
            IsEvalCode = IsEvalCode,
            IsStrictMode = IsStrictMode,
            NestedFunctions = nested,
            RegisterCount = RegisterCount,
            PrologueEndIp = PrologueEndIp,
            FieldInitializerStart = FieldInitializerStart,
            FieldInitializerEnd = FieldInitializerEnd,
            IsDerivedConstructor = IsDerivedConstructor,
            IsClassConstructor = IsClassConstructor,
            ComputedFieldKeys = new List<JsValue>(ComputedFieldKeys),
            TemplateSites = TemplateSites,
            BrandTokens = BrandTokens,

            // Explicitly document the execution-local reset rather than relying on
            // default field initialization as the cache contract evolves.
            LoadCacheSites = null,
            StoreCacheSites = null,
            CallICs = null,
            Invocations = 0,
            BackEdges = 0,
#if !PUBLISH_AOT
            JitCompileAttempted = false,
            JitDelegate = null,
            BaselinePool = null,
#endif
        };
    }

    private static bool IsHeapLocalValue(JsValue value) =>
        value.Tag is JsValueTag.Object or JsValueTag.HostObject;
}

/// <summary>
/// The strings of one tagged-template site: the cooked values (null where an
/// illegal escape sequence makes the cooked value undefined) and the raw ones.
/// </summary>
public sealed record TemplateSite(IReadOnlyList<string?> Cooked, IReadOnlyList<string> Raw);

