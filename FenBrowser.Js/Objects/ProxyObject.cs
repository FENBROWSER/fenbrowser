using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 28.2 Proxy Exotic Objects.
//
// A ProxyObject wraps a target object and an optional handler object.
// Internal methods are intercepted by the engine (BytecodeInterpreter)
// which checks `IsRevoked` and then looks up the corresponding trap on
// the handler. When no trap exists, the operation is forwarded directly
// to the target.
//
// After Revoke() is called, every internal operation on the proxy throws
// a TypeError per ECMA-262 28.2.2.1.
public sealed class ProxyObject : JsObject
{
    private readonly Func<string, JsValue>? _createTypeError;

    public ObjectHandle TargetHandle { get; }
    public ObjectHandle? HandlerHandle { get; private set; }

    public bool IsRevoked => HandlerHandle == null;

    // Trap delegates set by the interpreter during engine init so that
    // virtual methods (SetProperty/DeleteProperty) can dispatch through
    // the Proxy [[Set]] / [[Delete]] internal methods.
    internal static Func<ProxyObject, JsValue, string, JsValue, bool>? ProxySetTrap;
    internal static Func<ProxyObject, string, bool>? ProxyDeleteTrap;
    internal static Func<ProxyObject, List<System.Collections.Generic.KeyValuePair<string, JsPropertyDescriptor>>>? ProxyEnumerateTrap;

    public ProxyObject(ObjectHandle targetHandle, ObjectHandle handlerHandle)
        : this(targetHandle, handlerHandle, createTypeError: null)
    {
    }

    public ProxyObject(
        ObjectHandle targetHandle,
        ObjectHandle handlerHandle,
        Func<string, JsValue>? createTypeError)
    {
        TargetHandle = targetHandle;
        HandlerHandle = handlerHandle;
        _createTypeError = createTypeError;
    }

    public void Revoke()
    {
        HandlerHandle = null;
    }

    // ECMA-262 10.5.12 [[Set]] — if the handler has a "set" trap, call it;
    // otherwise forward to the target.
    public override bool SetProperty(string key, JsValue value)
    {
        if (IsRevoked) ThrowProxyError("Cannot perform 'set' on a revoked Proxy.");
        if (ProxySetTrap is { } setter)
            return setter(this, JsValue.FromObject(TargetHandle), key, value);
        return GetTarget().SetProperty(key, value);
    }

    // ECMA-262 10.5.10 [[Delete]] — if the handler has a "deleteProperty" trap,
    // call it; otherwise forward to the target.
    public override bool DeleteProperty(string key)
    {
        if (IsRevoked) ThrowProxyError("Cannot perform 'deleteProperty' on a revoked Proxy.");
        if (ProxyDeleteTrap is { } deleter)
            return deleter(this, key);
        return GetTarget().DeleteProperty(key);
    }

    // ECMA-262 10.5.11 [[OwnPropertyKeys]] — routes through the handler's "ownKeys"
    // trap. Returns an enumerable of own string-keyed property descriptors for the
    // target, filtered through the trap.
    public override System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        if (IsRevoked) ThrowProxyError("Cannot enumerate own properties on a revoked Proxy.");
        if (ProxyEnumerateTrap is { } en)
            return en(this);
        return GetTarget().EnumerateOwnProperties();
    }

    private JsObject GetTarget()
    {
        if (OwnerHeap is { } heap)
            return heap.GetObject(TargetHandle);

        throw new InvalidOperationException("Proxy is not attached to a JavaScript heap.");
    }

    private void ThrowProxyError(string message)
    {
        if (_createTypeError is { } createTypeError)
            throw new JsThrownException(createTypeError(message));

        // A Proxy created outside the builtin/realm bootstrap is an engine-host bug.
        // Do not masquerade that wiring problem as an unimplemented JavaScript feature.
        throw new InvalidOperationException(
            "Proxy is missing the realm error factory required for revoked operations.");
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        tracer.Trace(TargetHandle);
        if (HandlerHandle is { } handler)
            tracer.Trace(handler);
    }
}
