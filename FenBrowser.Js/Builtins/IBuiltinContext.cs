using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// Minimal services the builtin framework needs from the engine host
// (currently BytecodeInterpreter). Kept deliberately narrow: every method
// added to this interface expands the surface that every IBuiltinModule
// implementation can reach, so only add a service when at least two
// builtin modules genuinely need it.
//
// ToNumber is the first service: virtually every builtin that accepts user
// arguments must coerce them through the ECMA-262 ToNumber abstract
// operation, which can execute user code (valueOf / toString /
// Symbol.toPrimitive) and therefore must route through the interpreter's
// reentrancy-safe path.
public interface IBuiltinContext
{
    // ECMA-262 7.1.4 ToNumber. May execute user code when value is an Object.
    double ToNumber(JsValue value);

    // The heap this context operates on. Exposed so modules can allocate
    // objects, push roots, and call WriteBarrier without needing a separate
    // parameter.
    JsHeap Heap { get; }
}
