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
// Tier 5 #28 partial: known cross-realm leakage surfaces today are
//   * the process-global JsValue string/symbol/bigint pools (immutable
//     primitives — leakage is observational, not exploitable);
//   * the global BytecodeCache (set Isolated=true to skip it for this
//     realm — gives a fresh compilation but does not invalidate any
//     entries already cached from other realms);
//   * Shape.Root (global singleton — by design, makes ICs realm-agnostic).
// True multi-realm isolation (per-realm intrinsics, per-realm symbol
// registry, ShadowRealm) is a larger refactor and intentionally not
// attempted here.
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

    public JsValue Execute(BytecodeFunction fn) => Interpreter.Execute(fn);
}
