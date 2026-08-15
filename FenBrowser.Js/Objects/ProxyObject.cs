using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 28.2 Proxy Exotic Objects.
//
// Proxy internal methods are interpreter-owned because trap lookup, invocation,
// receiver handling, and invariant enforcement all require the owning realm/heap.
// A ProxyObject deliberately does not keep process-global trap delegates: those
// delegates allowed one interpreter to redefine another interpreter's semantics
// and, when left unwired, silently bypassed traps by forwarding to the target.
//
// After Revoke() is called, every internal operation on the proxy throws a
// TypeError per ECMA-262 28.2.2.1.
public sealed class ProxyObject : JsObject
{
    private readonly Func<string, JsValue>? _createTypeError;

    public ObjectHandle TargetHandle { get; }
    public ObjectHandle? HandlerHandle { get; private set; }

    public bool IsRevoked => HandlerHandle == null;

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

    // Proxy [[Set]] must be dispatched through BytecodeInterpreter.ProxySet so
    // the handler trap and invariants are evaluated in the owning interpreter.
    // Silently forwarding here is observably wrong whenever a set trap exists.
    public override bool SetProperty(string key, JsValue value)
    {
        if (IsRevoked)
            ThrowProxyError("Cannot perform 'set' on a revoked Proxy.");

        throw ProxyDispatchRequired("set", key);
    }

    // Proxy [[Delete]] likewise needs interpreter-owned trap dispatch.
    public override bool DeleteProperty(string key)
    {
        if (IsRevoked)
            ThrowProxyError("Cannot perform 'deleteProperty' on a revoked Proxy.");

        throw ProxyDispatchRequired("deleteProperty", key);
    }

    // Proxy [[OwnPropertyKeys]] cannot be approximated by enumerating the target:
    // the ownKeys trap has duplicate/non-configurable/non-extensible invariants.
    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        if (IsRevoked)
            ThrowProxyError("Cannot enumerate own properties on a revoked Proxy.");

        throw ProxyDispatchRequired("ownKeys", null);
    }

    private static InvalidOperationException ProxyDispatchRequired(string operation, string? key)
    {
        var suffix = key == null ? string.Empty : $" for property '{key}'";
        return new InvalidOperationException(
            $"Proxy [[{operation}]]{suffix} must be dispatched through the owning JavaScript interpreter.");
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
