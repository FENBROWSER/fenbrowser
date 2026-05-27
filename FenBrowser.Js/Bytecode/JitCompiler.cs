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
                    if (!TryFoldNumericBinop(function, ins, registerValues, out var foldedValue))
                        return null;
                    registerValues[ins.A] = foldedValue;
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

    // Tier 4 #24 (arithmetic extension): constant-fold a numeric binary
    // opcode at compile time. Both operands must already be known
    // numeric constants — anything that depends on runtime parameter or
    // identifier values bails out. Semantics mirror the interpreter's
    // double-precision arithmetic; string concatenation via Add is not
    // attempted because that would also need to model ToPrimitive.
    private static bool TryFoldNumericBinop(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } lhs || regs[ins.C] is not { } rhs) return false;
        if (!TryGetNumber(lhs, out var ln) || !TryGetNumber(rhs, out var rn)) return false;

        double folded = ins.OpCode switch
        {
            OpCode.Add => ln + rn,
            OpCode.Sub => ln - rn,
            OpCode.Mul => ln * rn,
            OpCode.Div => ln / rn,
            _ => double.NaN,
        };
        result = JsValue.FromNumber(folded);
        return true;
    }

    private static bool TryGetNumber(JsValue v, out double n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Number: n = v.AsNumber(); return true;
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            default: n = 0; return false;
        }
    }
}
