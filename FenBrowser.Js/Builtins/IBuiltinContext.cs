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

    // ECMA-262 7.3.12 HasProperty(O, P): returns true if O has a property P (own or
    // inherited). Proxy `has` trap is invoked when O is a Proxy.
    bool HasProperty(JsObject obj, string name);

    // Performs a Proxy-aware property set on the receiver. If the receiver already
    // has an own property with the given key, [[Set]] semantics apply (including
    // invoking Proxy `set` trap). Otherwise CreateDataPropertyOrThrow semantics
    // apply (including invoking Proxy `defineProperty` trap). Returns false when
    // the throw flag is false and the operation was rejected (e.g. non-writable
    // target, defineProperty trap returned false).
    bool SetPropertyOnReceiver(JsValue receiver, string key, JsValue value, bool throwOnFailure);

    // ECMA-262 [[GetOwnProperty]] on the receiver, Proxy-aware. Returns true if an
    // own property descriptor was found (even if it came from a Proxy getOwnPropertyDescriptor trap).
    // Returns false when no own property exists for the key. When false, desc is set to default.
    bool GetOwnPropertyOnReceiver(JsValue receiver, string key, out JsPropertyDescriptor desc);

    // Creates a data property (writable, enumerable, configurable) on the receiver via
    // [[DefineOwnProperty]], Proxy-aware (invokes defineProperty trap). Returns false
    // and throws TypeError when throwOnFailure is true and the operation is rejected.
    bool CreateDataPropertyOrThrowOnReceiver(JsValue receiver, string key, JsValue value, bool throwOnFailure);

    // ECMA-262 7.4.1 GetIterator ( obj ): returns an iterator for the given iterable.
    // Throws TypeError if obj is not iterable.
    JsValue GetIterator(JsValue iterable);

    // ECMA-262 7.4.5 IteratorStepValue ( iteratorRecord ): advances the iterator
    // and returns the next value. Returns false in `done` when exhausted.
    bool IteratorStepValue(JsValue iterator, out JsValue value);

    // ECMA-262 7.4.11 IteratorClose ( iteratorRecord, completion ): calls the
    // iterator's "return" method (if present). Used for early termination.
    void IteratorClose(JsValue iterator);

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
    ObjectHandle MaterializeAsyncFunctionConstructor();
    ObjectHandle MaterializeAsyncGeneratorFunctionConstructor();
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

    // Returns the %Function.prototype% object handle.
    ObjectHandle GetFunctionPrototype();

    // Intl (ECMA-402).
    ObjectHandle MaterializeIntlObject();

    // ECMA-402 Number.prototype.toLocaleString / BigInt.prototype.toLocaleString.
    JsValue FormatNumberToLocaleString(double value, JsValue locales, JsValue options);
    JsValue FormatBigIntToLocaleString(System.Numerics.BigInteger value, JsValue locales, JsValue options);

    // TypedArray / ArrayBuffer / DataView constructors.
    ObjectHandle MaterializeArrayBufferConstructor();
    ObjectHandle MaterializeSharedArrayBufferConstructor();
    ObjectHandle MaterializeDataViewConstructor();
    BuiltinBinding[] MaterializeTypedArrayConstructors();

    // Installs the Uint8Array base64/hex methods (fromBase64/fromHex statics and
    // toBase64/toHex/setFromBase64/setFromHex on the prototype). Must run after
    // MaterializeTypedArrayConstructors so the Uint8Array constructor exists.
    void InstallUint8ArrayBase64Hex();

    // Installs ArrayBuffer.prototype.transfer / transferToFixedLength / the
    // detached getter (ES2024). Run after MaterializeArrayBufferConstructor.
    void InstallArrayBufferTransfer();

    // Install prototype methods on already-created prototypes (for builtins that
    // create their own prototypes and need the interpreter to install methods).
    void InstallDatePrototypeMethods(ObjectHandle protoHandle, JsObject proto);

    // ECMA-262 21.4.2.1 Date(...) [[Construct]]: builds a DateObject from the full
    // range of argument shapes (now / time value / string / component form).
    JsValue ConstructDate(IReadOnlyList<JsValue> args);
    void InstallRegExpPrototypeMethods(ObjectHandle protoHandle, JsObject proto);
}
