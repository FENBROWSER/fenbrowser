using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Bytecode;

// Tier 4 #24 baseline JIT — first cut.
//
// This is intentionally a narrow template JIT: it compiles a small whitelist
// of "leaf" functions (currently those whose body is a single LoadConst +
// Return or a Return of a constant register established by trivial Moves)
// into a C# delegate. Anything outside the whitelist falls back to the
// interpreter — which is the entire point of having a tiered design: the
// JIT exists to skip dispatch overhead for the hottest, simplest call
// sites without risking divergence from the interpreter on the long tail
// of opcodes (Yield, Await, Throw, Generators, Proxy traps, etc.).
//
// A more capable JIT (full opcode coverage, type feedback, deopt) is a
// dedicated multi-week effort flagged in the production-gap roadmap.
// This file is the scaffold: a real compile path that handles a real
// subset, plus the integration hooks (tier-up counter, delegate cache,
// fallback) that a fuller JIT would expand into.
public static class JitCompiler
{
    // Function-level invocation count at which TryCompile is invoked.
    // Low for unit tests; production tuning would push this higher.
    public const int TierUpThreshold = 100;

    public delegate JsValue JitDelegate(JsValue thisValue, IReadOnlyList<JsValue> args);

    public static long CompileAttempts;
    public static long CompileSuccesses;

    // Attempt to compile `function` into a delegate equivalent to one full
    // ExecuteInternal pass over its instruction stream. Returns null when
    // the function uses any opcode outside the whitelist or has any
    // structural feature (handlers, generators, async, multiple constants)
    // that the JIT does not yet model.
    public static JitDelegate? TryCompile(BytecodeFunction function)
    {
        Interlocked.Increment(ref CompileAttempts);
        if (function is null || function.Instructions.Count == 0) return null;

        // Whitelist: straight-line bodies composed of LoadConst and Move
        // followed by a single Return. The JIT abstract-interprets the
        // sequence to compute the final value of the returned register
        // and bakes it into the returned delegate. Anything outside this
        // shape (calls, jumps, arithmetic, side effects, handlers) bails
        // to the interpreter.
        //
        // Why this is sound: every selected opcode is purely intra-frame
        // register manipulation with deterministic semantics — LoadConst
        // copies a constant table entry, Move copies a register, Return
        // yields the register. No environment lookups, no allocation, no
        // observable side effect. The compiled delegate produces the
        // same result as the interpreter for every call.
        if (function.Instructions.Count < 1) return null;

        // Abstract-interpret the function following control flow. Each
        // register holds a statically-known JsValue or null (unknown).
        // Unconditional Jump follows the target; JumpIfFalse follows the
        // statically-resolved branch if the condition is known. An unknown
        // condition causes the whole compile to bail. Bounded iteration
        // cap protects against statically-true loops that would otherwise
        // spin forever in the abstract interpreter.
        var registerValues = new JsValue?[function.RegisterCount];
        var ip = 0;
        var stepsRemaining = function.Instructions.Count * 4;

        while (stepsRemaining-- > 0)
        {
            if ((uint)ip >= (uint)function.Instructions.Count) return null;
            var ins = function.Instructions[ip];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Constants.Count) return null;
                    registerValues[ins.A] = function.Constants[ins.B];
                    ip++;
                    break;
                case OpCode.Move:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.RegisterCount) return null;
                    if (registerValues[ins.B] is not { } srcValue) return null;
                    registerValues[ins.A] = srcValue;
                    ip++;
                    break;
                case OpCode.Add:
                case OpCode.Sub:
                case OpCode.Mul:
                case OpCode.Div:
                case OpCode.Mod:
                case OpCode.Exp:
                case OpCode.BitAnd:
                case OpCode.BitOr:
                case OpCode.BitXor:
                case OpCode.ShiftLeft:
                case OpCode.ShiftRight:
                case OpCode.UnsignedShiftRight:
                case OpCode.Eq:
                case OpCode.Neq:
                case OpCode.StrictEq:
                case OpCode.StrictNeq:
                case OpCode.Lt:
                case OpCode.Gt:
                case OpCode.Le:
                case OpCode.Ge:
                case OpCode.And:
                case OpCode.Or:
                    if (!TryFoldBinop(function, ins, registerValues, out var foldedValue))
                        return null;
                    registerValues[ins.A] = foldedValue;
                    ip++;
                    break;
                case OpCode.Not:
                case OpCode.Pos:
                case OpCode.Neg:
                case OpCode.BitNot:
                case OpCode.Void:
                case OpCode.TypeOf:
                    if (!TryFoldUnaryOp(function, ins, registerValues, out var foldedUnary))
                        return null;
                    registerValues[ins.A] = foldedUnary;
                    ip++;
                    break;
                case OpCode.Jump:
                    if (ins.A < 0 || ins.A >= function.Instructions.Count) return null;
                    ip = ins.A;
                    break;
                case OpCode.JumpIfFalse:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Instructions.Count) return null;
                    if (registerValues[ins.A] is not { } condValue) return null;
                    ip = IsTruthy(condValue) ? ip + 1 : ins.B;
                    break;
                case OpCode.Return:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (registerValues[ins.A] is not { } returnValue) return null;
                    Interlocked.Increment(ref CompileSuccesses);
                    return (_, _) => returnValue;
                default:
                    return null;
            }
        }

        // Iteration cap exhausted — likely a runtime-dependent loop.
        return null;
    }

    // ECMA-262 7.1.2 ToBoolean used only for JumpIfFalse condition
    // resolution against compile-time-known values.
    private static bool IsTruthy(JsValue v) => v.Tag switch
    {
        JsValueTag.Undefined => false,
        JsValueTag.Null => false,
        JsValueTag.Boolean => v.AsBoolean(),
        JsValueTag.Int32 => v.AsInt32() != 0,
        JsValueTag.Number => v.AsNumber() != 0 && !double.IsNaN(v.AsNumber()),
        JsValueTag.String => v.AsString().Length > 0,
        _ => true,
    };

    // Tier 4 #24 (binary opcode coverage): constant-fold a binary opcode
    // when both operands have known compile-time values. Numeric ops use
    // double-precision IEEE-754 semantics (matching the interpreter);
    // Add additionally folds string-string concatenation; the equality
    // family handles primitive operand pairs only. Anything that depends
    // on runtime values bails to the interpreter.
    private static bool TryFoldBinop(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } lhs || regs[ins.C] is not { } rhs) return false;

        switch (ins.OpCode)
        {
            case OpCode.Add:
                if (lhs.Tag == JsValueTag.String && rhs.Tag == JsValueTag.String)
                { result = JsValue.FromString(lhs.AsString() + rhs.AsString()); return true; }
                if (!TryGetNumber(lhs, out var addL) || !TryGetNumber(rhs, out var addR)) return false;
                result = JsValue.FromNumber(addL + addR); return true;
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Div:
            case OpCode.Mod:
            case OpCode.Exp:
                if (!TryGetNumber(lhs, out var nL) || !TryGetNumber(rhs, out var nR)) return false;
                result = JsValue.FromNumber(ins.OpCode switch
                {
                    OpCode.Sub => nL - nR,
                    OpCode.Mul => nL * nR,
                    OpCode.Div => nL / nR,
                    OpCode.Mod => nL % nR,
                    OpCode.Exp => Math.Pow(nL, nR),
                    _ => double.NaN,
                });
                return true;
            case OpCode.BitAnd:
            case OpCode.BitOr:
            case OpCode.BitXor:
                if (!TryGetInt32(lhs, out var iL) || !TryGetInt32(rhs, out var iR)) return false;
                result = JsValue.FromInt32(ins.OpCode switch
                {
                    OpCode.BitAnd => iL & iR,
                    OpCode.BitOr => iL | iR,
                    OpCode.BitXor => iL ^ iR,
                    _ => 0,
                });
                return true;
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
                if (!TryGetInt32(lhs, out var sL) || !TryGetInt32(rhs, out var sR)) return false;
                var shift = sR & 0x1F;
                result = JsValue.FromInt32(ins.OpCode == OpCode.ShiftLeft ? sL << shift : sL >> shift);
                return true;
            case OpCode.UnsignedShiftRight:
                if (!TryGetInt32(lhs, out var uL) || !TryGetInt32(rhs, out var uR)) return false;
                var ushift = uR & 0x1F;
                result = JsValue.FromNumber((double)((uint)uL >> ushift));
                return true;
            case OpCode.StrictEq:
                result = JsValue.FromBoolean(StrictEquals(lhs, rhs)); return true;
            case OpCode.StrictNeq:
                result = JsValue.FromBoolean(!StrictEquals(lhs, rhs)); return true;
            case OpCode.Eq:
            case OpCode.Neq:
                // Loose equality across distinct types needs ToPrimitive
                // and string→number coercion paths that we don't model
                // here. Same-type loose equality reduces to strict.
                if (lhs.Tag != rhs.Tag) return false;
                var eqResult = StrictEquals(lhs, rhs);
                result = JsValue.FromBoolean(ins.OpCode == OpCode.Eq ? eqResult : !eqResult);
                return true;
            case OpCode.Lt:
            case OpCode.Gt:
            case OpCode.Le:
            case OpCode.Ge:
                if (!TryGetNumber(lhs, out var cL) || !TryGetNumber(rhs, out var cR)) return false;
                if (double.IsNaN(cL) || double.IsNaN(cR))
                { result = JsValue.FromBoolean(false); return true; }
                result = JsValue.FromBoolean(ins.OpCode switch
                {
                    OpCode.Lt => cL < cR,
                    OpCode.Gt => cL > cR,
                    OpCode.Le => cL <= cR,
                    OpCode.Ge => cL >= cR,
                    _ => false,
                });
                return true;
            case OpCode.And:
                // ECMA-262 short-circuit: returns the left value if falsy,
                // otherwise the right value. Both must be known.
                result = IsTruthy(lhs) ? rhs : lhs; return true;
            case OpCode.Or:
                result = IsTruthy(lhs) ? lhs : rhs; return true;
        }
        return false;
    }

    private static bool TryFoldUnaryOp(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } v) return false;

        switch (ins.OpCode)
        {
            case OpCode.Not:
                result = JsValue.FromBoolean(!IsTruthy(v)); return true;
            case OpCode.Void:
                result = JsValue.Undefined; return true;
            case OpCode.Pos:
                if (!TryGetNumber(v, out var pn)) return false;
                result = JsValue.FromNumber(pn); return true;
            case OpCode.Neg:
                if (!TryGetNumber(v, out var nn)) return false;
                result = JsValue.FromNumber(-nn); return true;
            case OpCode.BitNot:
                if (!TryGetInt32(v, out var bi)) return false;
                result = JsValue.FromInt32(~bi); return true;
            case OpCode.TypeOf:
                result = JsValue.FromString(v.Tag switch
                {
                    JsValueTag.Undefined => "undefined",
                    JsValueTag.Null => "object",
                    JsValueTag.Boolean => "boolean",
                    JsValueTag.Int32 or JsValueTag.Number => "number",
                    JsValueTag.String => "string",
                    JsValueTag.Symbol => "symbol",
                    JsValueTag.BigInt => "bigint",
                    _ => "object",
                });
                return true;
        }
        return false;
    }

    private static bool StrictEquals(JsValue a, JsValue b)
    {
        if (a.Tag != b.Tag)
        {
            // Numeric tags compare across Int32/Number per ECMA-262 7.2.15.
            if ((a.Tag == JsValueTag.Int32 || a.Tag == JsValueTag.Number) &&
                (b.Tag == JsValueTag.Int32 || b.Tag == JsValueTag.Number))
            {
                TryGetNumber(a, out var na);
                TryGetNumber(b, out var nb);
                return na == nb;
            }
            return false;
        }
        return a.Tag switch
        {
            JsValueTag.Undefined or JsValueTag.Null => true,
            JsValueTag.Boolean => a.AsBoolean() == b.AsBoolean(),
            JsValueTag.Int32 => a.AsInt32() == b.AsInt32(),
            JsValueTag.Number => a.AsNumber() == b.AsNumber(),
            JsValueTag.String => string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal),
            _ => false, // objects/symbols/bigints require identity that we don't track here
        };
    }

    private static bool TryGetNumber(JsValue v, out double n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Number: n = v.AsNumber(); return true;
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            case JsValueTag.Boolean: n = v.AsBoolean() ? 1 : 0; return true;
            case JsValueTag.Null: n = 0; return true;
            default: n = 0; return false;
        }
    }

    private static bool TryGetInt32(JsValue v, out int n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            case JsValueTag.Number:
                var d = v.AsNumber();
                if (double.IsNaN(d) || double.IsInfinity(d)) { n = 0; return true; }
                n = unchecked((int)(uint)d);
                return true;
            case JsValueTag.Boolean: n = v.AsBoolean() ? 1 : 0; return true;
            case JsValueTag.Null: n = 0; return true;
            default: n = 0; return false;
        }
    }
}
