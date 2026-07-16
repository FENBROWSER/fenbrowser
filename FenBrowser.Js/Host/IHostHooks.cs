using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Host;

public enum HostPropertyAccessKind
{
    Read,
    InCheck,
    DescriptorOperation,
    PrototypeAccess
}

// Plan §23.1. The single seam between FenJS and its embedder.
//
// At this milestone the surface is intentionally lean - it covers the operations the
// engine already knows how to invoke from existing code paths (promise jobs, host
// property access via HostObjectHandle, unhandled-rejection reporting). PropertyKey,
// JsResult<T>, and the full CallHostFunction signature land alongside the property-
// key infrastructure in a follow-up; the present interface stays string-keyed and
// throw-on-error so the standalone shell can implement it without dragging in the
// not-yet-built types.
public interface IHostHooks
{
    // Schedule a promise reaction or thenable job for the next microtask checkpoint.
    // Conceptually the engine's JobQueue.Enqueue, surfaced through the host so the
    // embedder can intercept (e.g. cancel pending jobs at navigation time).
    void EnqueuePromiseJob(PromiseJob job);

    // Mirror of ECMA-262 27.2.1.9 HostPromiseRejectionTracker. The default tracker
    // implementation lives in the Promises module; hosts that want richer behavior
    // (DOM event firing, devtools surface) override here.
    void ReportPromiseRejection(JsValue promise, PromiseRejectionOperation operation);

    // Read a property off a host-owned object. Returns false when the property is
    // absent, when the handle no longer resolves (navigated frame), or when the host
    // refuses the access (cross-origin policy). The interpreter translates a false
    // result with `wasMissing=true` into undefined, otherwise into a TypeError.
    bool TryGetHostProperty(HostObjectHandle handle, string property, out JsValue value);

    bool TryGetHostProperty(
        HostObjectHandle handle,
        string property,
        HostPropertyAccessKind accessKind,
        out JsValue value)
        => TryGetHostProperty(handle, property, out value);

    // Records a missing operation when FenJS can determine absence without asking
    // the embedder to execute a getter (for example, an own-descriptor query).
    void ObserveMissingHostPropertyOperation(
        HostObjectHandle handle,
        string property,
        HostPropertyAccessKind accessKind)
    {
        _ = handle;
        _ = property;
        _ = accessKind;
    }

    // Companion to TryGetHostProperty. False return = host rejected the write.
    bool TrySetHostProperty(HostObjectHandle handle, string property, JsValue value);

    // Diagnostic-only observation emitted after a script successfully creates an
    // own string property on an ordinary function's instance prototype. Hosts may
    // use the key to distinguish framework protocol markers from missing Web APIs.
    // Implementations must keep this bounded and must not retain the prototype.
    void ObserveFunctionPrototypePropertyDefinition(string property, JsValue value)
    {
        _ = property;
        _ = value;
    }

    // Invoke a host-registered native function by integer id. Hosts that don't have
    // native functions can throw NotSupportedException; the interpreter only calls
    // this when the bytecode references a HostFunctionId, which only the host can
    // produce.
    JsValue CallHostFunction(int functionId, JsValue thisValue, ReadOnlySpan<JsValue> args);
}

// Default implementation used by the standalone shell and tests: it owns a JobQueue,
// uses InMemoryPromiseRejectionTracker for rejection reporting, and refuses host
// property access (there are no host objects in a pure JS shell).
public sealed class StandaloneHostHooks : IHostHooks
{
    public StandaloneHostHooks()
    {
        JobQueue = new JobQueue();
        RejectionTracker = new InMemoryPromiseRejectionTracker();
    }

    public JobQueue JobQueue { get; }
    public InMemoryPromiseRejectionTracker RejectionTracker { get; }

    public void EnqueuePromiseJob(PromiseJob job) => JobQueue.Enqueue(job);

    public void ReportPromiseRejection(JsValue promise, PromiseRejectionOperation operation)
        => RejectionTracker.Track(promise, operation);

    public bool TryGetHostProperty(HostObjectHandle handle, string property, out JsValue value)
    {
        _ = handle;
        _ = property;
        value = JsValue.Undefined;
        return false;
    }

    public bool TrySetHostProperty(HostObjectHandle handle, string property, JsValue value)
    {
        _ = handle;
        _ = property;
        _ = value;
        return false;
    }

    public void ObserveFunctionPrototypePropertyDefinition(string property, JsValue value)
    {
        _ = property;
        _ = value;
    }

    public JsValue CallHostFunction(int functionId, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        _ = thisValue;
        _ = args;
        throw new NotSupportedException(
            $"StandaloneHostHooks does not register host functions (requested id {functionId}).");
    }
}
