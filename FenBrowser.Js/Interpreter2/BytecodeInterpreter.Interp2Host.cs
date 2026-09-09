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

    // ------------------------------------------------------- free identifiers

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

    internal JsValue Interp2NewObject()
        => JsValue.FromObject(_heap.AllocateObject(CreateOrdinaryObject(), AllocationSite.Current()));

    internal JsValue Interp2NewArray(int length)
        => JsValue.FromObject(_heap.AllocateObject(CreateArrayObject(length), AllocationSite.Current()));
}
