using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Interpreter2;

/// <summary>Why a function body cannot run on the register-window loop.</summary>
public enum Interp2Bailout
{
    None = 0,
    NotOrdinaryFunction,
    EvalCode,
    ClassConstructor,
    ArgumentsObject,
    BindsOwnName,
    RestParameter,
    LexicalDeclarations,
    UnmappedSlot,
    UnsupportedOpCode,
    FreeVariableResolve,
    CapturedReceiver,

    /// <summary>A block whose bytecode this layout cannot read as a nesting of scopes.</summary>
    BlockScope,

    /// <summary>A block-scoped slot named from outside every block that declares it.</summary>
    BlockScopeEscapes,

    /// <summary>
    /// A block-scoped binding a nested function captures, which needs a fresh
    /// record on every entry to the block rather than one per call.
    /// </summary>
    BlockScopeCaptured,

    DirectEval,
    FrameTooWide,
}

/// <summary>
/// Everything the register-window loop needs to know about a function body,
/// worked out once and cached on the function itself.
/// </summary>
/// <remarks>
/// The old loop gives every call a heap <c>InterpreterFrame</c>, a pooled
/// register array and an <c>EnvironmentRecord</c> whose bindings are reached
/// through slot arrays hanging off it. Most of that exists to serve the cases
/// that need it - a closure capturing the scope, a generator suspending with it
/// intact, <c>with</c> splicing a record into the chain - and every ordinary
/// call pays for all of them.
///
/// This layout answers the opposite question: is this body one where none of
/// that is observable? If it is, its variables are just more registers, its
/// frame is a window on a shared stack, and entering it is a bump of two
/// pointers.
///
/// Eligibility is decided <b>before</b> the frame is entered and it is total:
/// every opcode in the body must be one the new loop implements. There is
/// deliberately no mid-body bailout. A loop that can abandon a half-executed
/// frame has to be able to rebuild the old loop's state out of its own, and that
/// reconstruction is where an engine of this shape grows its subtlest bugs. A
/// gate that is a single "yes" costs one cached field read per call and cannot
/// be wrong halfway through.
/// </remarks>
/// <summary>Where a body's variable actually lives while it runs.</summary>
public enum SlotHome : byte
{
    /// <summary>A slot in this frame's window - the ordinary case.</summary>
    Register = 0,

    /// <summary>
    /// A binding in a heap record shared with the closures that read it. A
    /// register cannot be shared, so a variable some nested function captures
    /// has to live somewhere both can reach.
    /// </summary>
    Context = 1,

    /// <summary>Declared somewhere else; resolved through the scope chain.</summary>
    Free = 2,
}

public sealed class FrameLayout
{
    private static readonly bool[] Supported = BuildSupportedOpCodeTable();

    private FrameLayout(BytecodeFunction function, Interp2Bailout bailout, OpCode? bailoutOpCode = null)
    {
        Function = function;
        Bailout = bailout;
        BailoutOpCode = bailoutOpCode;
        Code = Array.Empty<Instruction>();
        SlotHomes = Array.Empty<SlotHome>();
        SlotNames = Array.Empty<string?>();
        FreeSites = Array.Empty<FreeSlotSite?>();
        ParameterSlots = Array.Empty<int>();
        ParameterWindowIndex = Array.Empty<int>();
    }

    private FrameLayout(
        BytecodeFunction function,
        int registerCount,
        int slotCount,
        SlotHome[] slotHomes,
        string?[] slotNames,
        int[] parameterSlots,
        int[] parameterWindowIndex)
    {
        Function = function;
        Bailout = Interp2Bailout.None;
        Code = function.InstructionArray;
        RegisterCount = registerCount;
        SlotCount = slotCount;
        WindowSize = registerCount + slotCount;
        SlotHomes = slotHomes;
        SlotNames = slotNames;
        FreeSites = HasAnyFree(slotHomes) ? new FreeSlotSite?[slotCount] : Array.Empty<FreeSlotSite?>();
        ParameterSlots = parameterSlots;
        ParameterWindowIndex = parameterWindowIndex;
    }

    public BytecodeFunction Function { get; }

    /// <summary>Why the new loop declined this body; <c>None</c> when it did not.</summary>
    public Interp2Bailout Bailout { get; }

    /// <summary>
    /// The opcode that decided it, when the reason was an unimplemented one.
    /// Ranked across a real workload this is the work queue: the opcode at the
    /// top of the list is the one holding the most code on the old loop.
    /// </summary>
    public OpCode? BailoutOpCode { get; }

    public bool Eligible => Bailout == Interp2Bailout.None;

    /// <summary>Bytecode registers - the low half of the frame's window.</summary>
    public int RegisterCount { get; }

    /// <summary>Declared variable slots - the high half of the frame's window.</summary>
    public int SlotCount { get; }

    /// <summary>Where each slot lives: a register, the context record, or outside.</summary>
    public SlotHome[] SlotHomes { get; }

    /// <summary>
    /// True when at least one variable is captured, so the frame allocates a
    /// record on entry for those and only those.
    /// </summary>
    public bool HasContext { get; private init; }

    /// <summary>Slot-indexed names, for the slots that resolve through the outer chain.</summary>
    public string?[] SlotNames { get; }

    /// <summary>
    /// Where each free identifier resolved last time, indexed by slot. Empty
    /// when the body has no free identifiers. Entries are filled in on first
    /// use and re-verified on every use.
    /// </summary>
    public FreeSlotSite?[] FreeSites { get; }

    /// <summary>The slot each formal parameter names, in declaration order.</summary>
    public int[] ParameterSlots { get; }

    /// <summary>
    /// The slot holding this body's own <c>arguments</c> object, or -1 when it
    /// has none. ECMA-262 10.2.11 creates it after the formals are bound.
    /// </summary>
    public int ArgumentsSlot { get; private init; } = -1;

    /// <summary>ECMA-262 10.2.11: the strict form throws on `callee`.</summary>
    public bool RestrictedArguments { get; private init; }

    /// <summary>
    /// Slots holding a `const`, so an assignment to one is a TypeError rather
    /// than a write. Empty when the body declares none.
    /// </summary>
    public bool[] SlotIsConst { get; private init; } = Array.Empty<bool>();

    /// <summary>
    /// The slot holding a named function expression's own name, or -1. ECMA-262
    /// 15.2.5 binds it immutably so the body can call itself, which is why it
    /// is not simply another var: assigning to it is a TypeError in strict code
    /// and silently ignored in sloppy code - neither of which is a write.
    /// </summary>
    public int SelfNameSlot { get; private init; } = -1;

    /// <summary>
    /// Window index each formal parameter is bound at, in declaration order.
    /// Meaningful only where the matching <see cref="SlotHomes"/> entry is
    /// <see cref="SlotHome.Register"/>.
    /// </summary>
    public int[] ParameterWindowIndex { get; }

    /// <summary>
    /// Whether two formals share a window slot, which sloppy mode allows:
    /// `function f(x, a, b, x)` binds both x's to one slot and the last one
    /// wins, so `f(1, 2)` leaves x undefined rather than 1. Binding only the
    /// arguments that were supplied would leave the first x's value in place,
    /// so a body shaped like this has every formal written, supplied or not.
    /// </summary>
    public bool HasDuplicateParameterSlots { get; private init; }

    /// <summary>Total <c>JsValue</c> slots one activation of this body occupies.</summary>
    public int WindowSize { get; }

    /// <summary>
    /// The body's instructions. Held here so entering a frame reads one field
    /// rather than going through the function's lazily-materialised array
    /// property, and so a frame record need not carry its own copy.
    /// </summary>
    public Instruction[] Code { get; }

    /// <summary>
    /// Whether any identifier in this body resolves outside it. A body with
    /// none never consults the scope chain, so its frame does not need the
    /// closure environment resolved - which is a shape check and sometimes a
    /// property probe saved on every call to a leaf function.
    /// </summary>
    public bool HasFreeVariables { get; private init; }

    /// <summary>
    /// Sloppy-mode bodies coerce their receiver on entry (ECMA-262 10.2.1.3
    /// OrdinaryCallBindThis steps 6-7); strict ones take it as it comes, and an
    /// arrow never binds one at all.
    /// </summary>
    public bool BindsThisLoosely { get; private init; }

    /// <summary>
    /// True for an arrow: it has no receiver of its own, so `this` is resolved
    /// by walking outwards (ECMA-262 9.1.2.5 GetThisEnvironment) rather than
    /// read off the frame.
    /// </summary>
    public bool ResolvesThisOutwards { get; private init; }

    public bool IsStrict { get; private init; }

    /// <summary>
    /// The layout for a function, computed once. Cached on the
    /// <see cref="BytecodeFunction"/> so a call site pays a field read, not a
    /// table lookup - a per-call weak-table probe is exactly the sort of cost
    /// this whole exercise exists to remove.
    /// </summary>
    public static FrameLayout For(BytecodeFunction function)
    {
        if (function.Interp2Layout is { } cached)
        {
            return cached;
        }

        var layout = Analyze(function);
        function.Interp2Layout = layout;
        if (Interp2Options.Log)
        {
            Interp2Stats.RecordLayout(layout);
        }

        return layout;
    }

    private static FrameLayout Analyze(BytecodeFunction function)
    {
        // Anything whose activation outlives its call, or whose scope is
        // reachable by name from outside it, needs a real environment record.
        // An arrow qualifies alongside an ordinary function: it differs only in
        // where `this` comes from, and that is one branch on entry.
        //
        // So does a method. A method differs in having a [[HomeObject]] and no
        // [[Construct]], and neither shows up in the frame: `this` arrives the
        // same way, `arguments` is the same object, and the activation ends
        // with the call. What a home object is *for* is `super`, and a body
        // that reaches for one emits LoadSuperProperty, LoadSuperElement or
        // LoadSuperConstructor, which the opcode gate below refuses on its own.
        if (function.Kind is not (FunctionKind.Ordinary or FunctionKind.Arrow or FunctionKind.Method))
            return new FrameLayout(function, Interp2Bailout.NotOrdinaryFunction);
        if (function.IsEvalCode)
            return new FrameLayout(function, Interp2Bailout.EvalCode);
        if (function.IsDerivedConstructor || function.IsClassConstructor)
            return new FrameLayout(function, Interp2Bailout.ClassConstructor);
        // An arrow reaching outwards for the enclosing `arguments` is refused;
        // a body's own arguments object is not, because this engine builds it as
        // a snapshot rather than as an alias of the parameter bindings, so a
        // parameter can still be a register.
        if (function.UsesOuterArguments)
            return new FrameLayout(function, Interp2Bailout.ArgumentsObject);
        if (function.RestParameterIndex >= 0)
            return new FrameLayout(function, Interp2Bailout.RestParameter);
        // let/const need a hole distinct from undefined to keep the temporal
        // dead zone observable. A register window has no such value yet, so the
        // bodies that declare them stay on the old loop for now.
        if (function.LexicalDeclarationNames.Count > 0 || function.ConstDeclarationNames.Count > 0)
            return new FrameLayout(function, Interp2Bailout.LexicalDeclarations);

        // Slots are classified before the opcodes are scanned, because two of
        // the opcodes are only implementable for a slot this body owns.
        var slotNames = SlotNamesOf(function);
        var slotCount = slotNames.Length;
        var registerCount = function.RegisterCount;
        if (registerCount + slotCount > Interp2Options.MaxFrameWindow)
            return new FrameLayout(function, Interp2Bailout.FrameTooWide);

        // A slot this body declares lives in its own window; anything else is a
        // free identifier and resolves through the closure's environment chain,
        // exactly as it does on the old loop.
        // A block introduces its binding with EnterScope, whose operand is the
        // slot. Those are declarations of this body as much as a `var` is, and
        // have to be classified before anything else looks at a slot.
        var blockScopes = BlockScopeRegions(function.InstructionArray, out var blockScopeSlots, out var constSlots);
        if (blockScopes is null)
        {
            return new FrameLayout(function, Interp2Bailout.BlockScope);
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < function.ParameterNames.Count; i++)
            declared.Add(function.ParameterNames[i]);
        for (var i = 0; i < function.VarDeclarationNames.Count; i++)
            declared.Add(function.VarDeclarationNames[i]);

        // `arguments` is declared by the body having one, not by a declaration
        // in it, so nothing above names it - but it is a variable of this frame
        // like any other and has to be classified as one.
        var ownsArguments = function.HasOwnArgumentsObject && !function.ArgumentsShadowedByParameter;
        if (ownsArguments)
            declared.Add("arguments");

        // ECMA-262 15.2.5: a named function expression can see its own name,
        // unless something in the body declares that name itself.
        var selfName = function.BindsOwnNameInBody && function.Name is { Length: > 0 } candidate &&
                       !declared.Contains(candidate) &&
                       !function.LexicalDeclarationNames.Contains(candidate) &&
                       !function.ConstDeclarationNames.Contains(candidate)
            ? candidate
            : null;
        if (selfName is not null)
            declared.Add(selfName);

        var declaredNames = declared;
        var slotHomes = new SlotHome[slotCount];
        var hasFreeVariables = false;
        for (var slot = 0; slot < slotCount; slot++)
        {
            var name = slotNames[slot];
            var own = (name is not null && declared.Contains(name)) || blockScopeSlots.Contains(slot);
            slotHomes[slot] = own ? SlotHome.Register : SlotHome.Free;
            hasFreeVariables |= !own;
        }

        // A block-scoped slot may only be touched from inside a block that
        // declares it. Read outside one, the register would still hold that
        // block's value where the spec says the binding is gone and the name
        // resolves outwards.
        if (!BlockScopeReferencesConfined(function.InstructionArray, blockScopes, blockScopeSlots))
        {
            return new FrameLayout(function, Interp2Bailout.BlockScopeEscapes);
        }

        var instructions = function.InstructionArray;
        var makesClosures = false;
        for (var i = 0; i < instructions.Length; i++)
        {
            ref readonly var ins = ref instructions[i];
            if (!Supported[(int)ins.OpCode])
                return new FrameLayout(function, Interp2Bailout.UnsupportedOpCode, ins.OpCode);

            // A slot the layout never classified has no home to write to.
            // A free one does now: the loop carries the resolution between the
            // two instructions on the interpreter, the way the old loop does.
            if (ins.OpCode is OpCode.PreResolveVar or OpCode.StoreResolvedVar &&
                (uint)ins.B >= (uint)slotCount)
            {
                return new FrameLayout(function, Interp2Bailout.FreeVariableResolve);
            }

            // Direct eval is flagged on the call site rather than by a distinct
            // opcode, and it can both read and add bindings in the caller's scope.
            if (ins.E == 1 && ins.OpCode is OpCode.Call0 or OpCode.Call1 or OpCode.CallN)
                return new FrameLayout(function, Interp2Bailout.DirectEval);

            makesClosures |= ins.OpCode == OpCode.CreateFunction;
        }

        // A closure is a function object plus the environment it captured, and
        // that environment exists only to resolve the names the closure does not
        // declare itself. Those names are readable off the nested bytecode, so
        // the ones this body declares - and only those - move out of the window
        // into a record the closures share. A body no closure reads from gets no
        // record at all, and is handed the environment it closed over itself.
        var hasContext = false;
        if (makesClosures)
        {
            var captured = CaptureAnalysis.CapturedNames(function, declaredNames, out var capturesReceiver);
            if (captured is null)
                return new FrameLayout(function, Interp2Bailout.CapturedReceiver);

            // A closure that reads the enclosing `this` needs a record to find
            // it on, so this body keeps one even when none of its variables are
            // captured. An arrow cannot supply it - its own record is one the
            // walk passes straight through - so it is refused instead.
            // ECMA-262 15.2.5 binds a function expression's own name
            // immutably. Inside this body that is enforced at the store, which
            // knows the slot; a closure that reads the name outwards resolves
            // it through a record, and the record this loop would share holds
            // slots that are all mutable - so an arrow assigning the name would
            // succeed where the spec drops the write in sloppy code and throws
            // in strict. Refuse the body rather than give the binding a home
            // that cannot say no.
            if (selfName is not null && captured.Contains(selfName))
            {
                return new FrameLayout(function, Interp2Bailout.BindsOwnName);
            }

            if (capturesReceiver)
            {
                if (function.Kind == FunctionKind.Arrow)
                    return new FrameLayout(function, Interp2Bailout.CapturedReceiver);
                hasContext = true;
            }

            for (var slot = 0; slot < slotCount; slot++)
            {
                if (slotHomes[slot] == SlotHome.Register &&
                    slotNames[slot] is { } name &&
                    captured.Contains(name))
                {
                    // A captured block binding needs a fresh record on every
                    // entry to the block - one closure per turn of a loop must
                    // not share a variable with the next - and this loop has one
                    // record per call, not per block.
                    if (blockScopeSlots.Contains(slot))
                    {
                        return new FrameLayout(function, Interp2Bailout.BlockScopeCaptured);
                    }

                    slotHomes[slot] = SlotHome.Context;
                    hasContext = true;
                }
            }
        }

        // Every parameter must have a slot of its own, or its binding would only
        // exist under a name this loop never creates a record for.
        var functionParameterSlots = function.ParameterSlots;
        var layoutParameterSlots = new int[function.ParameterNames.Count];
        var parameterWindowIndex = new int[function.ParameterNames.Count];
        for (var i = 0; i < parameterWindowIndex.Length; i++)
        {
            var slot = i < functionParameterSlots.Length ? functionParameterSlots[i] : -1;
            if (slot < 0 || slot >= slotCount || slotHomes[slot] == SlotHome.Free)
                return new FrameLayout(function, Interp2Bailout.UnmappedSlot);
            layoutParameterSlots[i] = slot;
            parameterWindowIndex[i] = registerCount + slot;
        }

        // Same for hoisted vars: the old loop's declaration instantiation would
        // have created a binding by name for one the compiler gave no slot.
        if (!function.AllVarSlotsMapped)
            return new FrameLayout(function, Interp2Bailout.UnmappedSlot);

        // A body that has an arguments object but never names it needs no slot;
        // one that names it must have got one, or there is nowhere to put it.
        var argumentsSlot = -1;
        if (ownsArguments && function.VariableSlots.TryGetValue("arguments", out var argumentsSlotIndex))
        {
            if ((uint)argumentsSlotIndex >= (uint)slotCount || slotHomes[argumentsSlotIndex] == SlotHome.Free)
                return new FrameLayout(function, Interp2Bailout.UnmappedSlot);
            argumentsSlot = argumentsSlotIndex;
        }

        var selfNameSlot = -1;
        if (selfName is not null && function.VariableSlots.TryGetValue(selfName, out var selfSlotIndex))
        {
            if ((uint)selfSlotIndex >= (uint)slotCount || slotHomes[selfSlotIndex] == SlotHome.Free)
                return new FrameLayout(function, Interp2Bailout.UnmappedSlot);
            selfNameSlot = selfSlotIndex;
        }

        var duplicateParameters = false;
        for (var i = 1; i < parameterWindowIndex.Length && !duplicateParameters; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (parameterWindowIndex[i] == parameterWindowIndex[j])
                {
                    duplicateParameters = true;
                    break;
                }
            }
        }

        return new FrameLayout(
            function, registerCount, slotCount, slotHomes, slotNames, layoutParameterSlots, parameterWindowIndex)
        {
            IsStrict = function.IsStrictMode,
            ResolvesThisOutwards = function.Kind == FunctionKind.Arrow,
            BindsThisLoosely = !function.IsStrictMode && function.Kind != FunctionKind.Arrow,
            HasFreeVariables = hasFreeVariables,
            HasDuplicateParameterSlots = duplicateParameters,
            HasContext = hasContext,
            ArgumentsSlot = argumentsSlot,
            RestrictedArguments = function.UsesRestrictedArgumentsObject,
            SlotIsConst = BuildConstMap(constSlots, slotCount),
            SelfNameSlot = selfNameSlot,
        };
    }

    /// <summary>
    /// The block scopes in a body, as (slot, first instruction, last
    /// instruction) triples, or null when they are shaped in a way this loop
    /// cannot keep in registers.
    /// </summary>
    /// <remarks>
    /// A block scope on the old loop is a record spliced into the chain, holding
    /// one binding. Here it is just a slot in the window, which works exactly
    /// when the block cannot be told apart from a straight-line assignment:
    ///
    /// - the scopes must nest properly, so a region can be identified at all;
    /// - two nested scopes must not share a slot, or the inner one's value would
    ///   survive into the outer one, which is a distinct binding;
    /// - the binding must be initialised before anything can read it, because a
    ///   register has no value distinct from `undefined` to stand for the
    ///   temporal dead zone.
    ///
    /// The compiler emits the catch binding of a `try`/`catch` in exactly that
    /// shape - EnterScope then InitVar - which is what this exists to admit.
    /// </remarks>
    private static List<(int Slot, int Start, int End)>? BlockScopeRegions(
        Instruction[] code, out HashSet<int> slots, out HashSet<int> constants)
    {
        slots = new HashSet<int>();
        constants = new HashSet<int>();
        var regions = new List<(int, int, int)>();
        var open = new List<(int Slot, int Start)>();

        for (var ip = 0; ip < code.Length; ip++)
        {
            ref readonly var ins = ref code[ip];
            if (ins.OpCode == OpCode.LeaveScope)
            {
                if (open.Count == 0) return null;
                var (slot, start) = open[^1];
                open.RemoveAt(open.Count - 1);
                regions.Add((slot, start, ip));
                continue;
            }

            if (ins.OpCode != OpCode.EnterScope) continue;

            var scopeSlot = ins.A;
            foreach (var (openSlot, _) in open)
            {
                // The inner binding would write the outer one's register, and
                // leaving the inner block would not bring the outer value back.
                if (openSlot == scopeSlot) return null;
            }

            // C = 1 pre-initialises the binding to undefined; otherwise the
            // declaration must be the very next thing, so nothing can observe
            // the slot between the block opening and the value arriving.
            if (ins.C != 1 &&
                (ip + 1 >= code.Length ||
                 code[ip + 1].OpCode != OpCode.InitVar ||
                 code[ip + 1].B != scopeSlot))
            {
                return null;
            }

            if (ins.B == 1) constants.Add(scopeSlot);
            slots.Add(scopeSlot);
            open.Add((scopeSlot, ip));
        }

        return open.Count == 0 ? regions : null;
    }

    /// <summary>
    /// Whether every instruction naming a block-scoped slot falls inside one of
    /// the blocks that declare that slot.
    /// </summary>
    /// <remarks>
    /// One slot can have several blocks. The compiler numbers a body's bindings
    /// in one space and reuses a number across blocks that cannot both be open -
    /// two sibling `{ let x }` blocks share one - and checking each block on its
    /// own therefore failed on the *other* block's instructions every time,
    /// which is not a slot escaping anything. Those blocks are disjoint, because
    /// a slot re-entered while already open is refused before this, and each one
    /// re-initialises the register as it opens. So a reference inside any of a
    /// slot's blocks names the binding that block declared.
    ///
    /// It was 332 of the 437 bodies this loop declined on reCAPTCHA's bundle,
    /// against zero for the captured-binding case the design notes expected.
    /// </remarks>
    private static bool BlockScopeReferencesConfined(
        Instruction[] code, List<(int Slot, int Start, int End)> regions, HashSet<int> blockScopeSlots)
    {
        for (var ip = 0; ip < code.Length; ip++)
        {
            ref readonly var ins = ref code[ip];
            var named = ins.OpCode switch
            {
                OpCode.LoadVar or OpCode.StoreVar or OpCode.InitVar or OpCode.TypeOfName or
                OpCode.PreResolveVar or OpCode.StoreResolvedVar or OpCode.StoreVarTop => ins.B,
                OpCode.EnterScope => ins.A,
                _ => -1,
            };

            if (named < 0 || !blockScopeSlots.Contains(named)) continue;

            var inside = false;
            for (var r = 0; r < regions.Count; r++)
            {
                var region = regions[r];
                if (region.Slot == named && ip >= region.Start && ip <= region.End)
                {
                    inside = true;
                    break;
                }
            }

            if (!inside) return false;
        }

        return true;
    }

    private static bool[] BuildConstMap(HashSet<int> constants, int slotCount)
    {
        if (constants.Count == 0) return Array.Empty<bool>();

        var map = new bool[slotCount];
        foreach (var slot in constants)
        {
            if ((uint)slot < (uint)slotCount) map[slot] = true;
        }

        return map;
    }

    private static bool HasAnyFree(SlotHome[] slotHomes)
    {
        for (var i = 0; i < slotHomes.Length; i++)
        {
            if (slotHomes[i] == SlotHome.Free) return true;
        }

        return false;
    }

    private static string?[] SlotNamesOf(BytecodeFunction function)
    {
        var max = -1;
        foreach (var pair in function.VariableSlots)
        {
            if (pair.Value > max) max = pair.Value;
        }

        if (max < 0) return Array.Empty<string?>();

        var names = new string?[max + 1];
        foreach (var pair in function.VariableSlots)
        {
            names[pair.Value] = pair.Key;
        }

        return names;
    }

    /// <summary>
    /// The opcodes the register-window loop implements. Everything absent from
    /// this table sends its function to the old loop, so growing the new loop is
    /// always additive: an opcode joins the table only once its handler is
    /// written and its test262 slice is green.
    /// </summary>
    private static bool[] BuildSupportedOpCodeTable()
    {
        var table = new bool[256];
        ReadOnlySpan<OpCode> supported =
        [
            // Data movement and control flow.
            OpCode.LoadConst, OpCode.LoadVar, OpCode.LoadThis, OpCode.StoreVar,
            OpCode.InitVar, OpCode.PreResolveVar, OpCode.StoreResolvedVar,
            OpCode.Move, OpCode.Jump, OpCode.JumpIfFalse,
            OpCode.Return, OpCode.Nop, OpCode.PrologueEnd, OpCode.Throw,
            OpCode.PushHandler, OpCode.PopHandler, OpCode.EndFinally,
            OpCode.EnterScope, OpCode.LeaveScope,
            OpCode.CreateFunction,

            // Arithmetic, coercion and comparison.
            OpCode.Add, OpCode.Sub, OpCode.Mul, OpCode.Div, OpCode.Mod, OpCode.Exp,
            OpCode.Neg, OpCode.Pos, OpCode.Not, OpCode.Void, OpCode.BitNot,
            OpCode.ToNumeric, OpCode.ToStringCoerce, OpCode.Increment, OpCode.Decrement,
            OpCode.TypeOf, OpCode.TypeOfName,
            OpCode.Eq, OpCode.Neq, OpCode.StrictEq, OpCode.StrictNeq,
            OpCode.Lt, OpCode.Gt, OpCode.Le, OpCode.Ge, OpCode.And, OpCode.Or,
            OpCode.BitAnd, OpCode.BitOr, OpCode.BitXor,
            OpCode.ShiftLeft, OpCode.ShiftRight, OpCode.UnsignedShiftRight,

            // Property reads, property writes and object literals.
            OpCode.GetPropByName, OpCode.GetElem, OpCode.GetElemConst,
            OpCode.SetPropByName, OpCode.SetElem, OpCode.SetElemByIndex,
            OpCode.DeletePropByName, OpCode.DeleteElem,
            OpCode.InstanceOf, OpCode.In,
            OpCode.GetPrivateField, OpCode.SetPrivateField,

            // for-in and for-of.
            OpCode.EnumerateKeys, OpCode.ForInNext,
            OpCode.EnumerateValues, OpCode.ForOfNext, OpCode.IteratorClose,
            OpCode.NewObject, OpCode.NewArray, OpCode.NewRegExp,

            // Calls and construction.
            OpCode.Construct0, OpCode.Construct1, OpCode.ConstructN,
            OpCode.Call0, OpCode.Call1, OpCode.CallN,
            OpCode.CallMethod0, OpCode.CallMethod1, OpCode.CallMethodN,
        ];

        foreach (var op in supported)
        {
            table[(int)op] = true;
        }

        return table;
    }
}
