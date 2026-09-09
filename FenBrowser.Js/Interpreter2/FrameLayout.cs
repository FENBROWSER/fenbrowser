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

    /// <summary>The slot each formal parameter names, in declaration order.</summary>
    public int[] ParameterSlots { get; }

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
    /// OrdinaryCallBindThis steps 6-7); strict ones take it as it comes.
    /// </summary>
    public bool BindsThisLoosely { get; private init; }

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
        if (function.Kind != FunctionKind.Ordinary)
            return new FrameLayout(function, Interp2Bailout.NotOrdinaryFunction);
        if (function.IsEvalCode)
            return new FrameLayout(function, Interp2Bailout.EvalCode);
        if (function.IsDerivedConstructor || function.IsClassConstructor)
            return new FrameLayout(function, Interp2Bailout.ClassConstructor);
        // A mapped arguments object aliases the parameter bindings, and even an
        // unmapped one has to be materialised out of them.
        if (function.HasOwnArgumentsObject || function.UsesOuterArguments)
            return new FrameLayout(function, Interp2Bailout.ArgumentsObject);
        // ECMA-262 15.2.5: a named function expression binds its own name in a
        // record of its own, between its parameters and its closure.
        if (function.BindsOwnNameInBody)
            return new FrameLayout(function, Interp2Bailout.BindsOwnName);
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
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < function.ParameterNames.Count; i++)
            declared.Add(function.ParameterNames[i]);
        for (var i = 0; i < function.VarDeclarationNames.Count; i++)
            declared.Add(function.VarDeclarationNames[i]);

        var declaredNames = declared;
        var slotHomes = new SlotHome[slotCount];
        var hasFreeVariables = false;
        for (var slot = 0; slot < slotCount; slot++)
        {
            var name = slotNames[slot];
            var own = name is not null && declared.Contains(name);
            slotHomes[slot] = own ? SlotHome.Register : SlotHome.Free;
            hasFreeVariables |= !own;
        }

        var instructions = function.InstructionArray;
        var makesClosures = false;
        for (var i = 0; i < instructions.Length; i++)
        {
            ref readonly var ins = ref instructions[i];
            if (!Supported[(int)ins.OpCode])
                return new FrameLayout(function, Interp2Bailout.UnsupportedOpCode, ins.OpCode);

            // ECMA-262 13.3.2.4 resolves a var's binding before its initializer
            // runs, so an initializer that changes what the name resolves to
            // still writes where the declaration meant. For a register there is
            // nothing to resolve and nothing that could change it; for a free
            // name the resolution has to be carried between two instructions,
            // which this loop has nowhere to put yet.
            if (ins.OpCode is OpCode.PreResolveVar or OpCode.StoreResolvedVar &&
                ((uint)ins.B >= (uint)slotCount || slotHomes[ins.B] == SlotHome.Free))
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
            var captured = CaptureAnalysis.CapturedNames(function, declaredNames);
            if (captured is null)
                return new FrameLayout(function, Interp2Bailout.CapturedReceiver);

            for (var slot = 0; slot < slotCount; slot++)
            {
                if (slotHomes[slot] == SlotHome.Register &&
                    slotNames[slot] is { } name &&
                    captured.Contains(name))
                {
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
            BindsThisLoosely = !function.IsStrictMode,
            HasFreeVariables = hasFreeVariables,
            HasDuplicateParameterSlots = duplicateParameters,
            HasContext = hasContext,
        };
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
            OpCode.NewObject, OpCode.NewArray,

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
