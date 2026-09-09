using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

/// <summary>
/// The surface the register-window loop is allowed to reach through.
/// </summary>
/// <remarks>
/// <para>
/// The new loop decides where a value lives and how a frame is entered. It does
/// not decide what any operator, coercion or property lookup means: every one of
/// those goes back through the method the old loop already calls, so the two
/// cannot disagree about semantics no matter how far the new one is taken. This
/// file is that boundary, and it is the only place the two loops touch.
/// </para>
/// <para>
/// It is a partial of <see cref="BytecodeInterpreter"/> rather than an interface
/// because most of what it forwards to is private, and widening thirty members
/// to expose them would make the old loop's internals part of the engine's API.
/// Keeping the file beside the new loop instead means all of the new loop's
/// surface is in one folder and can be deleted in one commit if the direction
/// does not pay.
/// </para>
/// </remarks>
public sealed partial class BytecodeInterpreter
{
    private Interp2? _interp2;

    private Interp2 Interp2Loop => _interp2 ??= new Interp2(this);

    /// <summary>
    /// The register-window loop's live state, or null if it has never run. The
    /// collector needs this even when the loop is not currently executing,
    /// because a frame it left behind is unwound lazily.
    /// </summary>
    internal Interp2? Interp2State => _interp2;

    /// <summary>
    /// Entry from <c>CallFunctionCore</c>: run an eligible body on the new loop.
    /// </summary>
    internal JsValue Interp2Execute(JsFunctionObject callee, FrameLayout layout, in CallArgs args, JsValue thisValue)
        => Interp2Loop.Execute(callee, layout, in args, thisValue);

    // ------------------------------------------------------------- guard rails

    /// <summary>
    /// Whether anything is watching this execution: an instruction budget, a
    /// wall-clock deadline or an embedder interrupt. When nothing is, the new
    /// loop skips its guard accounting entirely.
    /// </summary>
    internal bool Interp2HasGuards =>
        InstructionBudget > 0 || _wallClockDeadlineTicks != 0 || InterruptCallback is not null;

    /// <summary>
    /// Charge a block of dispatched instructions against the budget and sample
    /// the deadline and the interrupt.
    /// </summary>
    /// <remarks>
    /// The old loop tests the budget on every instruction and samples the clock
    /// every 8192. The new loop charges in blocks, so a script overruns its
    /// budget by at most one block before it is stopped. That is the same
    /// guarantee in substance - a runaway script halts - with an error raised a
    /// few thousand instructions later than the old loop would raise it. Both
    /// failures are uncatchable by script, so neither can be swallowed by a
    /// handler and turned into a hang.
    /// </remarks>
    internal void Interp2Guard(long dispatchedInstructions)
    {
        if (InstructionBudget > 0)
        {
            // The counter is an int and the block is bounded by the guard
            // interval, so this cannot overflow before the budget test below.
            _instructionCount += (int)dispatchedInstructions;
            if (_instructionCount > InstructionBudget)
            {
                throw new JsThrownException(CreateRangeError("Maximum instruction budget exceeded."))
                {
                    IsUncatchableByScript = true,
                };
            }
        }

        if (InterruptCallback is { } interrupt && !interrupt())
        {
            throw new JsThrownException(CreateRangeError("Execution interrupted."))
            {
                IsUncatchableByScript = true,
            };
        }

        if (_wallClockDeadlineTicks != 0 && System.Environment.TickCount64 >= _wallClockDeadlineTicks)
        {
            throw new JsThrownException(CreateRangeError("Script wall-clock timeout exceeded."))
            {
                IsUncatchableByScript = true,
            };
        }
    }

    internal JsThrownException Interp2CallStackOverflow()
        => new(CreateRangeError("Maximum call stack size exceeded."));

    // -------------------------------------------------------------- call entry

    /// <summary>
    /// Everything the new loop cannot enter itself: natives, bound functions,
    /// proxies, generators, async bodies, and any function its layout declined.
    /// </summary>
    internal JsValue Interp2Call(JsValue callee, in CallArgs args, JsValue thisValue)
        => CallFunction(callee, args, thisValue);

    /// <summary>ECMA-262 10.2.1.3 OrdinaryCallBindThis, steps 6-7 (sloppy mode).</summary>
    internal JsValue Interp2CoerceReceiver(JsValue thisValue)
    {
        if (thisValue.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            return JsValue.FromObject(EnsureGlobalObject());
        }

        return thisValue.Tag == JsValueTag.Object ? thisValue : CreateObjectFromValue(thisValue);
    }

    internal EnvironmentRecord? Interp2OuterEnvironment(JsFunctionObject callee)
        => ResolveFunctionOuterEnvironment(callee);

    // ---------------------------------------------------- captured variables

    /// <summary>
    /// The heap record that holds the variables of one activation that some
    /// closure made inside it can read.
    /// </summary>
    /// <remarks>
    /// It is a record for this function's slot numbering, so a variable that
    /// lives here is reached by the same slot index the bytecode already
    /// carries, and a closure resolving the name by walking the chain finds it
    /// where it would have found it before. Only the captured slots are
    /// declared in it; every other name falls straight through to the
    /// environment this body itself closed over.
    ///
    /// It is a FunctionEnvironmentRecord rather than a plain declarative one
    /// because it is this call's *variable* environment, not a block inside it,
    /// and code that walks the chain asks which it is: a direct eval declaring
    /// `var x` walks outwards for the nearest variable environment and treats
    /// every declarative record before it as a block, so a plain record here
    /// made `var x` collide with the enclosing function's own x.
    ///
    /// The storage is not taken from the slot pool. A pooled file is handed back
    /// when the frame that rented it tears down, and this record is created
    /// precisely because it outlives the frame.
    /// </remarks>
    internal DeclarativeEnvironmentRecord Interp2CreateContext(
        BytecodeFunction function,
        JsFunctionObject callee,
        JsValue thisValue,
        EnvironmentRecord? outerEnvironment)
    {
        // ECMA-262 9.1.1.3: an arrow has no `this` of its own, so its record
        // must be one that `this` resolves straight through. Giving it a
        // function environment would stop the walk at the arrow and hand back
        // the wrong receiver.
        if (function.Kind == FunctionKind.Arrow)
        {
            var arrowContext = StampEnvironment(new DeclarativeEnvironmentRecord(outerEnvironment));
            arrowContext.AttachSlotStorage(function, function.VariableSlots, function.SlotNames.Length);
            return arrowContext;
        }

        var context = StampEnvironment(new FunctionEnvironmentRecord(
            ThisBindingStatus.Uninitialized,
            callee.SelfHandle is { } selfHandle ? JsValue.FromObject(selfHandle) : JsValue.Undefined,
            JsValue.Undefined,
            callee.HomeObject,
            outerEnvironment));
        context.AttachSlotStorage(function, function.VariableSlots, function.SlotNames.Length);
        _ = context.BindThisValue(thisValue);
        return context;
    }

    /// <summary>
    /// ECMA-262 9.1.2.5 GetThisEnvironment: the receiver an arrow sees, which is
    /// the nearest enclosing record that provides one.
    /// </summary>
    internal JsValue Interp2ResolveThis(EnvironmentRecord? environment, JsValue frameReceiver)
    {
        var status = ResolveThisBinding(environment, out var value);

        // ECMA-262 9.2.2 step 9: a derived constructor's `this` stays
        // uninitialized until super() runs, and an arrow inside it that reads
        // `this` first sees the same ReferenceError the constructor would.
        if (status == BindingOpResult.TdzAccess)
        {
            throw new JsThrownException(CreateReferenceError(
                "Must call super constructor in derived class before accessing 'this'."));
        }

        // No record in the chain provides one - fall back to the receiver the
        // call supplied, which is what the dispatch loop does.
        return status == BindingOpResult.Ok ? value : frameReceiver;
    }

    internal static void Interp2DeclareContextSlot(
        DeclarativeEnvironmentRecord context, int slot, JsValue value)
        => context.DeclareAtSlot(slot, value, deletable: false, overwrite: true);

    internal JsValue Interp2LoadContext(
        DeclarativeEnvironmentRecord context, BytecodeFunction function, int slot, string? name, bool strict)
    {
        if (context.TryReadOwnSlot(function, slot, out var value))
        {
            return value;
        }

        if (context.TryGetAtSlot(slot, out var slotValue, out var status))
        {
            if (status == BindingOpResult.Ok)
            {
                return slotValue;
            }

            throw Interp2BindingFailure(status, name ?? "?", assignment: false);
        }

        // Defensive: every context slot is declared when the frame is entered,
        // so this is unreachable unless the layout and the entry path disagree.
        return Interp2LoadFree(context.OuterEnv, name, strict);
    }

    internal void Interp2StoreContext(
        DeclarativeEnvironmentRecord context, BytecodeFunction function, int slot, JsValue value, string? name, bool strict)
    {
        if (context.TryWriteOwnSlot(function, slot, value))
        {
            return;
        }

        if (context.TrySetAtSlot(slot, value, strict, out var status))
        {
            if (status == BindingOpResult.Ok)
            {
                return;
            }

            throw Interp2BindingFailure(status, name ?? "?", assignment: true);
        }

        Interp2StoreFree(context.OuterEnv, name, value, strict);
    }

    // ------------------------------------------------------- free identifiers

    /// <summary>
    /// ECMA-262 9.1.2.1 GetIdentifierReference for a name this body does not
    /// declare, through the site cache that remembers where it resolved last
    /// time.
    /// </summary>
    /// <remarks>
    /// Both cached shapes are re-verified rather than trusted: the slot form
    /// re-checks that the record at the cached depth still numbers slots for the
    /// same function, and the global form checks the lexical version and then
    /// runs a shape guard. A stale entry misses and takes the walk; it never
    /// answers.
    /// </remarks>
    internal JsValue Interp2LoadFreeCached(
        FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment)
    {
        var name = slot < layout.SlotNames.Length ? layout.SlotNames[slot] : null;
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        var sites = layout.FreeSites;
        if (slot < sites.Length && sites[slot] is { } site)
        {
            var env = outerEnvironment;
            for (var hop = site.Hops; hop > 0 && env is not null; hop--)
            {
                env = env.OuterEnv;
            }

            if (site.SlotOwner is { } owner)
            {
                if (env is DeclarativeEnvironmentRecord declarative &&
                    declarative.TryReadOwnSlot(owner, site.TargetSlot, out var slotValue))
                {
                    return slotValue;
                }
            }
            else if (env is GlobalEnvironmentRecord global &&
                     global.LexicalVersion == site.LexicalVersion &&
                     global.GlobalObjectHandle is { } globalHandle &&
                     TryGetLoadIC(layout.Function, icOffset, JsValue.FromObject(globalHandle), name, out var cached))
            {
                return cached;
            }
        }

        return Interp2LoadFreeAndCache(layout, slot, icOffset, outerEnvironment, name);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private JsValue Interp2LoadFreeAndCache(
        FrameLayout layout, int slot, int icOffset, EnvironmentRecord? outerEnvironment, string name)
    {
        var strict = layout.IsStrict;
        var hops = 0;
        for (var env = outerEnvironment; env is not null; env = env.OuterEnv, hops++)
        {
            var status = env.TryLookupBinding(name, strict, out var value);
            if (status == BindingOpResult.NotFound)
            {
                continue;
            }

            if (status != BindingOpResult.Ok)
            {
                throw Interp2BindingFailure(status, name, assignment: false);
            }

            RecordFreeSlotSite(layout, slot, icOffset, hops, env, name);
            return value;
        }

        throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
    }

    /// <summary>
    /// Remember where a name resolved, when the answer is one of the two shapes
    /// worth caching. Anything else - an object environment, a record with no
    /// slot numbering - is left uncached and walks every time.
    /// </summary>
    private void RecordFreeSlotSite(
        FrameLayout layout, int slot, int icOffset, int hops, EnvironmentRecord env, string name)
    {
        var sites = layout.FreeSites;
        if ((uint)slot >= (uint)sites.Length)
        {
            return;
        }

        if (env is GlobalEnvironmentRecord global)
        {
            // Only a property of the global object is cacheable this way; a
            // top-level let/const lives on the record's lexical half, which the
            // load cache cannot see. HasLexicalDeclaration separates them.
            if (global.HasLexicalDeclaration(name) || global.GlobalObjectHandle is not { } handle)
            {
                return;
            }

            var receiver = JsValue.FromObject(handle);
            PopulateLoadIC(layout.Function, icOffset, receiver, name);
            sites[slot] = FreeSlotSite.OnGlobalObject(hops, global.LexicalVersion);
            return;
        }

        if (env is DeclarativeEnvironmentRecord declarative &&
            declarative.SlotOwner is { } owner &&
            declarative.TryGetSlotIndex(name, out var targetSlot))
        {
            sites[slot] = FreeSlotSite.AtSlot(hops, owner, targetSlot);
        }
    }

    /// <summary>
    /// ECMA-262 9.1.2.1 GetIdentifierReference for a name this body does not
    /// declare. The walk starts at the closure's environment because a frame on
    /// the new loop has none of its own - everything it declares is a register,
    /// so there is nothing between it and its closure to shadow the name.
    /// </summary>
    internal JsValue Interp2LoadFree(EnvironmentRecord? outerEnvironment, string? name, bool strict)
    {
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            var status = env.TryLookupBinding(name, strict, out var value);
            if (status == BindingOpResult.NotFound)
            {
                continue;
            }

            if (status == BindingOpResult.Ok)
            {
                return value;
            }

            throw Interp2BindingFailure(status, name, assignment: false);
        }

        throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
    }

    /// <summary>ECMA-262 9.1.1.1.5 SetMutableBinding through the closure chain.</summary>
    internal void Interp2StoreFree(EnvironmentRecord? outerEnvironment, string? name, JsValue value, bool strict)
    {
        if (name is null)
        {
            throw new JsThrownException(CreateReferenceError("Invalid variable slot."));
        }

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasBinding(name))
            {
                continue;
            }

            var status = env.SetMutableBinding(name, value, strict);
            if (status == BindingOpResult.Ok)
            {
                return;
            }

            throw Interp2BindingFailure(status, name, assignment: true);
        }

        // ECMA-262 9.1.1.4.4: an unresolvable reference is a ReferenceError in
        // strict code and creates a global property in sloppy code.
        if (strict)
        {
            throw new JsThrownException(CreateReferenceError($"{name} is not defined."));
        }

        SetImplicitGlobalProperty(name, value);
    }

    /// <summary>
    /// ECMA-262 13.5.3.1: <c>typeof</c> applied to an identifier is the one
    /// read that does not throw for a name that resolves nowhere - it answers
    /// "undefined". A name in the temporal dead zone still throws.
    /// </summary>
    internal string Interp2TypeOfFree(EnvironmentRecord? outerEnvironment, string? name, bool strict)
    {
        if (name is null)
        {
            return "undefined";
        }

        for (var env = outerEnvironment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasBinding(name))
            {
                continue;
            }

            var status = env.GetBindingValue(name, strict, out var value);
            if (status == BindingOpResult.Ok)
            {
                return TypeOfValue(value);
            }

            throw Interp2BindingFailure(status, name, assignment: false);
        }

        return "undefined";
    }

    private JsThrownException Interp2BindingFailure(BindingOpResult status, string name, bool assignment)
        => status switch
        {
            BindingOpResult.TdzAccess =>
                new JsThrownException(CreateReferenceError($"Cannot access '{name}' before initialization.")),
            BindingOpResult.ConstAssignment => new JsThrownException(CreateTypeError(assignment
                ? $"Assignment to constant variable '{name}'."
                : $"Cannot read immutable binding '{name}'.")),
            BindingOpResult.NotInitializable or BindingOpResult.AlreadyDeclared =>
                new JsThrownException(CreateTypeError($"Cannot initialize binding '{name}'.")),
            _ => new JsThrownException(CreateReferenceError($"{name} is not defined.")),
        };

    // ------------------------------------------------------------- operators

    /// <summary>
    /// The two-operand numeric fast path, shared verbatim with the old loop and
    /// the JIT so all three agree on what a Number-Number operation produces.
    /// </summary>
    internal static bool Interp2TryFastBinary(OpCode op, in JsValue left, in JsValue right, out JsValue result)
        => TryFastBinop(op, in left, in right, out result);

    /// <summary>
    /// The generic form of every binary operator the new loop dispatches, each
    /// forwarding to the same helper the old loop's case forwards to.
    /// </summary>
    internal JsValue Interp2SlowBinary(OpCode op, JsValue left, JsValue right) => op switch
    {
        OpCode.Add => Add(left, right),
        OpCode.Sub => BigIntArith(left, right, "subtraction", static (a, b) => a - b, static (a, b) => a - b),
        OpCode.Mul => BigIntArith(left, right, "multiplication", static (a, b) => a * b, static (a, b) => a * b),
        OpCode.Div => BigIntArith(left, right, "division", static (a, b) => a / b, static (a, b) => a / b),
        OpCode.Mod => BigIntArith(left, right, "modulo", static (a, b) => a % b, static (a, b) => a % b),
        OpCode.Exp => ExponentiationOp(left, right),
        OpCode.Lt => JsValue.FromBoolean(IsLessThan(left, right)),
        OpCode.Gt => JsValue.FromBoolean(IsGreaterThan(left, right)),
        OpCode.Le => JsValue.FromBoolean(IsLessThanOrEqual(left, right)),
        OpCode.Ge => JsValue.FromBoolean(IsGreaterThanOrEqual(left, right)),
        OpCode.Eq => JsValue.FromBoolean(AreEqual(left, right)),
        OpCode.Neq => JsValue.FromBoolean(!AreEqual(left, right)),
        OpCode.StrictEq => JsValue.FromBoolean(AreStrictlyEqual(left, right)),
        OpCode.StrictNeq => JsValue.FromBoolean(!AreStrictlyEqual(left, right)),
        OpCode.BitAnd => BitwiseAndOp(left, right),
        OpCode.BitOr => BitwiseOrOp(left, right),
        OpCode.BitXor => BitwiseXorOp(left, right),
        OpCode.ShiftLeft => LeftShiftOp(left, right),
        OpCode.ShiftRight => RightShiftOp(left, right),
        OpCode.UnsignedShiftRight => UnsignedRightShiftOp(left, right),
        _ => throw new JsEngineFatalException($"Interp2SlowBinary: unsupported opcode {op}."),
    };

    internal JsValue Interp2Unary(OpCode op, JsValue operand)
    {
        switch (op)
        {
            case OpCode.Pos:
                return JsValue.FromNumberCompact(ToNumber(operand));
            case OpCode.Neg:
            {
                var numeric = ToNumericValue(operand);
                return numeric.Tag == JsValueTag.BigInt
                    ? JsValue.FromBigInt(-numeric.AsBigInt())
                    : JsValue.FromNumberCompact(-numeric.AsNumber());
            }

            case OpCode.BitNot:
                return BitwiseNotOp(operand);
            case OpCode.TypeOf:
                return JsValue.FromString(TypeOfValue(operand));
            case OpCode.ToNumeric:
                return ToNumericValue(operand);
            case OpCode.ToStringCoerce:
                return JsValue.FromString(ToStringValue(operand));
            case OpCode.Increment:
                return StepNumeric(operand, +1);
            case OpCode.Decrement:
                return StepNumeric(operand, -1);
            default:
                throw new JsEngineFatalException($"Interp2Unary: unsupported opcode {op}.");
        }
    }

    internal bool Interp2IsTruthy(JsValue value) => IsTruthy(value);

    internal string Interp2TypeOfValue(JsValue value) => TypeOfValue(value);

    // -------------------------------------------------------------- properties

    /// <summary>
    /// ECMA-262 13.3.2 property access by literal name, through the same
    /// per-call-site inline cache the old loop populates. The caches are keyed
    /// by (function, instruction offset), which both loops agree on because they
    /// execute the same bytecode - so a site warmed on one loop is warm on the
    /// other.
    /// </summary>
    internal JsValue Interp2GetPropertyByName(BytecodeFunction function, int icOffset, JsValue receiver, string key)
    {
        if (TryGetLoadIC(function, icOffset, receiver, key, out var cached))
        {
            return cached;
        }

        var value = GetReceiverProperty(receiver, key);
        PopulateLoadIC(function, icOffset, receiver, key);
        return value;
    }

    /// <summary>ECMA-262 13.3.3 computed member access.</summary>
    internal JsValue Interp2GetElement(BytecodeFunction function, int icOffset, JsValue receiver, JsValue key)
    {
        // 13.3.2.1 GetValue requires the base to be object-coercible before the
        // key is coerced, so `null[obj]` throws before obj's toString runs.
        if (receiver.Tag is JsValueTag.Null or JsValueTag.Undefined)
        {
            var kind = receiver.Tag == JsValueTag.Null ? "null" : "undefined";
            throw new JsThrownException(CreateTypeError(key.Tag == JsValueTag.String
                ? $"Cannot read properties of {kind} (reading '{key.AsString()}')."
                : $"Cannot read properties of {kind}."));
        }

        if (key.Tag == JsValueTag.Symbol)
        {
            return GetReceiverSymbolProperty(receiver, key.AsSymbolId());
        }

        if (TryGetDenseElement(receiver, key, out var element))
        {
            // A dense array element is an own data property: the slot itself is
            // the answer, with no key to build and no prototype chain to walk.
            return element;
        }

        if (key.Tag == JsValueTag.String &&
            TryGetElemStringIC(function, icOffset, receiver, key.AsString(), out var cached))
        {
            return cached;
        }

        var propertyKey = ToPropertyKey(key);
        var value = GetReceiverProperty(receiver, propertyKey);
        if (key.Tag == JsValueTag.String)
        {
            PopulateGetElemStringIC(function, icOffset, receiver, propertyKey);
        }

        return value;
    }

    /// <summary>ECMA-262 13.15.2 assignment to a literal property name.</summary>
    internal void Interp2SetPropertyByName(
        BytecodeFunction function, int icOffset, JsValue receiver, string key, JsValue value, bool strict)
        => SetPropertyByNameCore(function, icOffset, receiver, key, value, strict);

    /// <summary>ECMA-262 13.15.2 assignment to a computed member.</summary>
    internal void Interp2SetElement(JsValue receiver, JsValue key, JsValue value, bool strict)
        => SetElementCore(receiver, key, value, strict);

    /// <summary>Array-literal element store at a compiler-known index.</summary>
    internal void Interp2SetElementByIndex(JsValue receiver, int index, JsValue value, bool strict)
        => SetElementByIndexCore(receiver, index, value, strict);

    /// <summary>ECMA-262 13.3.5 EvaluateNew: <c>new F(...)</c>.</summary>
    /// <remarks>
    /// newTarget is the constructor itself. A derived class's <c>super()</c> is
    /// a different path and this loop declines the bodies that can reach it.
    /// </remarks>
    internal JsValue Interp2Construct(JsValue constructor, in CallArgs args)
        => ConstructFunction(constructor, args, constructor);

    /// <summary>
    /// ECMA-262 10.2.3 OrdinaryFunctionCreate for a nested function.
    /// </summary>
    /// <remarks>
    /// The captured environment is the enclosing frame's own closure rather than
    /// a record for the frame itself, which the layout has already established
    /// no function created here can tell apart: none of them reaches for a name
    /// this body declares, so the record would have been a link they resolve
    /// straight through.
    /// </remarks>
    internal JsValue Interp2CreateFunction(BytecodeFunction nested, EnvironmentRecord? outerEnvironment)
        => CreateFunctionObject(nested, outerEnvironment);

    internal JsValue Interp2NewObject()
        => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()));

    internal JsValue Interp2NewArray(int length)
        => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(length), AllocationSite.Current()));
}
