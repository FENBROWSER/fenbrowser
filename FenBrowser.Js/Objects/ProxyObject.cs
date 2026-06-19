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
    {
        TargetHandle = targetHandle;
        HandlerHandle = handlerHandle;
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
        return base.SetProperty(key, value);
    }

    // ECMA-262 10.5.10 [[Delete]] — if the handler has a "deleteProperty" trap,
    // call it; otherwise forward to the target.
    public override bool DeleteProperty(string key)
    {
        if (IsRevoked) ThrowProxyError("Cannot perform 'deleteProperty' on a revoked Proxy.");
        if (ProxyDeleteTrap is { } deleter)
            return deleter(this, key);
        return base.DeleteProperty(key);
    }

    // ECMA-262 10.5.11 [[OwnPropertyKeys]] — routes through the handler's "ownKeys"
    // trap. Returns an enumerable of own string-keyed property descriptors for the
    // target, filtered through the trap.
    public override System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        if (IsRevoked) ThrowProxyError("Cannot enumerate own properties on a revoked Proxy.");
        if (ProxyEnumerateTrap is { } en)
            return en(this);
        return base.EnumerateOwnProperties();
    }

    // Set by the interpreter during initialization. Provides access to CreateTypeError
    // so Proxy methods can throw proper JS TypeError objects.
    internal static Func<string, JsValue>? CreateTypeErrorFn;

    private static void ThrowProxyError(string msg)
    {
        if (CreateTypeErrorFn is { } fn)
            throw new JsThrownException(fn(msg));
        throw new NotImplementedException("Proxy trap dispatch not initialized.");
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        tracer.Trace(TargetHandle);
        if (HandlerHandle is { } handler)
            tracer.Trace(handler);
    }
}
