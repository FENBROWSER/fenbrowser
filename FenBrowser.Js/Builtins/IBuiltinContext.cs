using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// Services the builtin framework needs from the engine host (currently
// BytecodeInterpreter). Every method added to this interface expands the
// surface that every IBuiltinModule implementation can reach, so only add
// a service when at least two builtin modules genuinely need it.
public interface IBuiltinContext
{
    // ECMA-262 7.1.4 ToNumber.
    double ToNumber(JsValue value);

    // ECMA-262 7.1.17 ToString.
    string ToStringValue(JsValue value);

    // The heap this context operates on.
    JsHeap Heap { get; }

    // ECMA-262 7.3.13 Call.
    JsValue CallFunction(JsValue fn, IReadOnlyList<JsValue> args, JsValue thisValue);

    // ECMA-262 7.3.14 Construct.
    JsValue ConstructFunction(JsValue ctor, IReadOnlyList<JsValue> args);

    // Property access via [[Get]].
    bool TryGetPropertyValue(JsObject obj, JsValue receiver, string name, out JsValue value);

    // ECMA-262 10.4.2.2 length of an Array exotic object.
    int GetArrayLength(JsObject obj);

    // Error constructors.
    JsValue CreateTypeError(string message);
    JsValue CreateRangeError(string message);
    JsValue CreateSyntaxError(string message);
    JsValue CreateError(string message);
    JsValue CreateUriError(string message);
    JsValue CreateReferenceError(string message);

    // Define a [Writable, !Enumerable, Configurable] native function property.
    void DefineIntrinsicFunction(
        ObjectHandle ownerHandle, JsObject owner, string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call, int length);

    // Prototype access.
    ObjectHandle GetObjectPrototype();
    ObjectHandle GetArrayPrototype();
    ObjectHandle GetErrorPrototype();
    ObjectHandle GetParseIntFunction();
    ObjectHandle GetParseFloatFunction();

    // Symbol primitives.
    JsValue CreateSymbol(string? description);
    JsValue CreateWellKnownSymbol(string name);
    JsValue SymbolFor(string key);
    JsValue SymbolKeyFor(long id);

    // The %Function.prototype.call% shared function object.
    ObjectHandle GetFunctionCallMethod();

    // eval(x).
    JsValue Eval(IReadOnlyList<JsValue> args);
    string CaptureCallStack(string errorName, string message);

    // queueMicrotask(callback).
    void EnqueueMicrotask(JsValue callback);

    // 27.2.1.5 NewPromiseCapability(%Promise%). Returns a fresh pending promise
    // together with its resolve/reject functions, so a native builtin can feed
    // a promise from the outside (e.g. AsyncDisposableStack.prototype.disposeAsync).
    (JsValue Promise, JsValue Resolve, JsValue Reject) CreatePromiseCapability();

    // Builtin constructor/materialize helpers — each returns the ObjectHandle of the
    // fully-wired constructor (including prototype + methods). Used by thin-wrapper
    // builtin modules for complex types whose prototype methods still live on the
    // interpreter.
    ObjectHandle MaterializeObjectConstructor();
    ObjectHandle MaterializeArrayConstructor();
    ObjectHandle MaterializeFunctionConstructor();
    ObjectHandle MaterializeGeneratorFunctionConstructor();
    ObjectHandle MaterializeSetConstructor();
    ObjectHandle MaterializeMapConstructor();
    ObjectHandle MaterializeWeakMapConstructor();
    ObjectHandle MaterializeWeakSetConstructor();
    ObjectHandle MaterializePromiseConstructor();
    ObjectHandle MaterializeJsonObject();
    ObjectHandle MaterializeReflectObject();
    ObjectHandle MaterializeIteratorConstructor();
    ObjectHandle MaterializeWeakRefConstructor();
    ObjectHandle MaterializeFinalizationRegistryConstructor();
    ObjectHandle MaterializeAggregateErrorConstructor();
    ObjectHandle MaterializeSuppressedErrorConstructor();
    ObjectHandle MaterializeStructuredCloneFunction();

    // Intl (ECMA-402).
    ObjectHandle MaterializeIntlObject();

    // TypedArray / ArrayBuffer / DataView constructors.
    ObjectHandle MaterializeArrayBufferConstructor();
    ObjectHandle MaterializeSharedArrayBufferConstructor();
    ObjectHandle MaterializeDataViewConstructor();
    BuiltinBinding[] MaterializeTypedArrayConstructors();

    // Install prototype methods on already-created prototypes (for builtins that
    // create their own prototypes and need the interpreter to install methods).
    void InstallDatePrototypeMethods(ObjectHandle protoHandle, JsObject proto);
    void InstallRegExpPrototypeMethods(ObjectHandle protoHandle, JsObject proto);
}
