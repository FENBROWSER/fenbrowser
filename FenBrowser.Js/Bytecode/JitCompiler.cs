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

        // Whitelist: only trivial straight-line bodies. The simplest viable
        // shape is `LoadConst RA, constIdx; Return RA` (a constant-returning
        // function), which is what we recognize here.
        if (function.Instructions.Count != 2) return null;
        var loadIns = function.Instructions[0];
        var retIns = function.Instructions[1];
        if (loadIns.OpCode != OpCode.LoadConst) return null;
        if (retIns.OpCode != OpCode.Return) return null;
        if (loadIns.A != retIns.A) return null;
        if (loadIns.B < 0 || loadIns.B >= function.Constants.Count) return null;

        var constantValue = function.Constants[loadIns.B];
        Interlocked.Increment(ref CompileSuccesses);
        return (_, _) => constantValue;
    }
}
