using FenBrowser.Js.Heap;
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

    public ProxyObject(ObjectHandle targetHandle, ObjectHandle handlerHandle)
    {
        TargetHandle = targetHandle;
        HandlerHandle = handlerHandle;
    }

    public void Revoke()
    {
        HandlerHandle = null;
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        tracer.Trace(TargetHandle);
        if (HandlerHandle is { } handler)
            tracer.Trace(handler);
    }
}
