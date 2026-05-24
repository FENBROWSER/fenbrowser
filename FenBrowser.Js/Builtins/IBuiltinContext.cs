using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// Services the builtin framework needs from the engine host (currently
// BytecodeInterpreter). Every method added to this interface expands the
// surface that every IBuiltinModule implementation can reach, so only add
// a service when at least two builtin modules genuinely need it.
//
// ToNumber was the first: virtually every builtin that accepts user arguments
// must coerce through ECMA-262 ToNumber, which can execute user code (valueOf /
// toString / Symbol.toPrimitive) and must route through the interpreter's
// reentrancy-safe path. ToStringValue, CallFunction, and TryGetPropertyValue
// are similarly on the hot path for builtins that handle callbacks (JSON,
// Array.prototype.map/filter/forEach, Promise, etc.).
public interface IBuiltinContext
{
    // ECMA-262 7.1.4 ToNumber. May execute user code when value is an Object.
    double ToNumber(JsValue value);

    // ECMA-262 7.1.17 ToString. May execute user code when value is an Object.
    string ToStringValue(JsValue value);

    // The heap this context operates on.
    JsHeap Heap { get; }

    // ECMA-262 7.3.13 Call. Invokes [[Call]] on a function value.
    JsValue CallFunction(JsValue fn, IReadOnlyList<JsValue> args, JsValue thisValue);

    // ECMA-262 7.3.14 Construct. Invokes [[Construct]] on a constructor value.
    JsValue ConstructFunction(JsValue ctor, IReadOnlyList<JsValue> args);

    // Property access on a heap object via [[Get]].
    bool TryGetPropertyValue(JsObject obj, JsValue receiver, string name, out JsValue value);

    // ECMA-262 10.4.2.2 length of an Array exotic object.
    int GetArrayLength(JsObject obj);

    // Error constructors — each returns a fresh error JsValue.
    JsValue CreateTypeError(string message);
    JsValue CreateRangeError(string message);
    JsValue CreateSyntaxError(string message);
    JsValue CreateError(string message);
    JsValue CreateUriError(string message);
    JsValue CreateReferenceError(string message);

    // Convenience: define a [Writable, !Enumerable, Configurable] native function
    // property on an owner object, with a WriteBarrier from owner to the function.
    void DefineIntrinsicFunction(
        ObjectHandle ownerHandle,
        JsObject owner,
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length);

    // The %Object.prototype% object — the root of the prototype chain.
    ObjectHandle GetObjectPrototype();

    // The %Error.prototype% object. All native error constructors (TypeError,
    // RangeError, URIError, etc.) set their prototype's [[Prototype]] to this.
    ObjectHandle GetErrorPrototype();

    // %parseInt% and %parseFloat% — shared function objects used by both the
    // global scope and Number.parseInt / Number.parseFloat (ECMA-262 21.1.2.13).
    ObjectHandle GetParseIntFunction();
    ObjectHandle GetParseFloatFunction();
}
