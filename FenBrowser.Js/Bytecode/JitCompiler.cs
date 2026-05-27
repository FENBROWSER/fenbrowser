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
        if (function.Instructions.Count < 2) return null;
        var last = function.Instructions[^1];
        if (last.OpCode != OpCode.Return) return null;

        // Track each register's static value as we walk forward. Slot is
        // null if the register has not been touched by an opcode in the
        // whitelist (e.g., a parameter binding). A Return that names such
        // a register cannot be folded to a constant here, so we bail.
        var registerValues = new JsValue?[function.RegisterCount];

        for (var i = 0; i < function.Instructions.Count - 1; i++)
        {
            var ins = function.Instructions[i];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Constants.Count) return null;
                    registerValues[ins.A] = function.Constants[ins.B];
                    break;
                case OpCode.Move:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.RegisterCount) return null;
                    if (registerValues[ins.B] is not { } srcValue) return null;
                    registerValues[ins.A] = srcValue;
                    break;
                default:
                    return null; // unsupported opcode — bail.
            }
        }

        if (last.A < 0 || last.A >= function.RegisterCount) return null;
        if (registerValues[last.A] is not { } returnValue) return null;

        Interlocked.Increment(ref CompileSuccesses);
        return (_, _) => returnValue;
    }
}
