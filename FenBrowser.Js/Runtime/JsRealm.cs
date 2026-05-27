using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;

namespace FenBrowser.Js.Runtime;

// ECMA-262 9.3 Realm Records.
//
// A JsRealm bundles a heap, a set of intrinsics, and an interpreter. Two
// JsRealm instances created with default constructors do not share heap
// state or global objects, so values cannot leak across realm boundaries
// by direct reference.
//
// Tier 5 #28: realm isolation.
//   * Symbol.for() registry is per-BytecodeInterpreter (instance-scoped
//     _symbolRegistryByKey), so Isolated realms get a fresh registry.
//   * Global object, intrinsics (Math/Object/Array/etc.), and Promise
//     job queue are per-interpreter.
//   * The BytecodeCache and Compile path are bypassed for Isolated
//     realms (via BytecodeCache.BypassScope) so the realm's compiled
//     bytecode is never read from or written to the shared cache.
//   * Remaining cross-realm leakage surfaces:
//       - process-global JsValue string/symbol/bigint pools — immutable
//         primitives, observational only;
//       - Shape.Root — global singleton by design, makes ICs
//         realm-agnostic and is safe for isolation since shapes do not
//         carry per-realm capability state.
// Anything that needs to call back into the interpreter from outside the
// realm (e.g. CompileScript) must run inside Run(...) so the bypass
// applies for the full compile + execute.
public sealed class JsRealm
{
    public BytecodeInterpreter Interpreter { get; }

    // When true, CompileScript paths bypass the shared BytecodeCache for
    // this realm — useful when callers want guaranteed-fresh compilation
    // for sandboxing or fuzzing.
    public bool Isolated { get; set; }

    public JsRealm(JsHeap? heap = null)
    {
        Interpreter = heap is not null ? new BytecodeInterpreter(heap) : new BytecodeInterpreter();
    }

    public JsRealm(BytecodeInterpreter interpreter)
    {
        Interpreter = interpreter;
    }

    public JsValue Execute(BytecodeFunction fn)
    {
        if (!Isolated) return Interpreter.Execute(fn);
        using var _ = BytecodeCache.BypassScope();
        return Interpreter.Execute(fn);
    }

    // Run an arbitrary callback under this realm's isolation scope.
    // Use for compile+execute pipelines where the BytecodeCache must be
    // bypassed for the entire operation.
    public T Run<T>(Func<BytecodeInterpreter, T> callback)
    {
        if (!Isolated) return callback(Interpreter);
        using var _ = BytecodeCache.BypassScope();
        return callback(Interpreter);
    }
}
