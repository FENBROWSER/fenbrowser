using FenBrowser.Js.Heap;
using FenBrowser.Js.Host;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// F.5 - Host object dereferencing. Every interpreter site that consumes a
// JsValue with tag JsValueTag.HostObject routes the handle through
// HostObjectTable.Resolve first, validating realm + generation + document
// epoch + navigation epoch in one pass. Reads and writes against valid host
// objects then go through IHostHooks so the embedder controls the actual
// property surface (cross-origin policy, security checks, DOM proxies, etc.).
//
// The standalone interpreter ships with an empty HostObjectTable and the
// StandaloneHostHooks default, both of which refuse host property access -
// so any test that builds host handles must register them on the table
// before they will resolve.
public sealed partial class BytecodeInterpreter
{
    private HostObjectTable _hostObjectTable = new();
    private IHostHooks _hostHooks = new StandaloneHostHooks();
    private HostObjectResolveContext _hostResolveContext =
        new(CurrentRealmId: 0, CurrentDocumentEpoch: default, CurrentNavigationEpoch: default);

    public HostObjectTable HostObjectTable
    {
        get => _hostObjectTable;
        set => _hostObjectTable = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IHostHooks HostHooks
    {
        get => _hostHooks;
        set => _hostHooks = value ?? throw new ArgumentNullException(nameof(value));
    }

    public HostObjectResolveContext HostResolveContext
    {
        get => _hostResolveContext;
        set => _hostResolveContext = value;
    }

    // Resolve a HostObject-tagged JsValue or throw a JS TypeError describing the
    // failure. Used everywhere the interpreter is about to act on the embedder-
    // side identity of a host object - if the handle is stale, the embedder
    // never sees the call and the script gets a catchable error.
    private HostObjectResolution RequireHostObject(JsValue value, string operation)
    {
        if (value.Tag != JsValueTag.HostObject)
        {
            throw new JsThrownException(CreateTypeError(
                $"{operation}: expected a host object, found {value.Tag}."));
        }

        var resolution = _hostObjectTable.Resolve(value.AsHostObjectHandle(), _hostResolveContext);
        if (!resolution.IsOk)
        {
            throw new JsThrownException(CreateTypeError(
                $"{operation}: host object handle is invalid ({resolution.Reason})."));
        }

        return resolution;
    }

    // Read a property from a host object. Resolves the handle first, then
    // defers to IHostHooks.TryGetHostProperty. A false return becomes
    // `undefined` per the host-property convention; the interpreter cannot
    // distinguish "missing" from "host refused" here, by design (the host can
    // route either through TypeError by throwing inside its hook).
    private JsValue GetHostObjectProperty(JsValue receiver, string key)
    {
        var resolution = RequireHostObject(receiver, "get host property");
        _ = resolution;
        return _hostHooks.TryGetHostProperty(receiver.AsHostObjectHandle(), key, out var value)
            ? value
            : JsValue.Undefined;
    }

    // Write a property on a host object. Resolves the handle first, then
    // defers to IHostHooks.TrySetHostProperty. A false return becomes a
    // TypeError so scripts can see refused writes; this matches the spec's
    // "throw a TypeError" branch of OrdinarySet when the host vetoes.
    private void SetHostObjectProperty(JsValue receiver, string key, JsValue value)
    {
        _ = RequireHostObject(receiver, "set host property");
        if (!_hostHooks.TrySetHostProperty(receiver.AsHostObjectHandle(), key, value))
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot set property '" + key + "' on host object (refused by embedder)."));
        }
    }

    // Enqueue a Promise job, routing through the host hooks when an embedder
    // has been installed. The default StandaloneHostHooks calls back into our
    // own JobQueue via its own JobQueue field; the interpreter still drains
    // the local _jobQueue, so the standalone path uses _jobQueue directly.
    //
    // Hosted embeds replace IHostHooks with a custom impl that may intercept
    // for navigation-time cancellation - in that case the host owns the queue
    // and the interpreter's JobQueue stays empty.
    private void EnqueuePromiseJobThroughHost(PromiseJob job)
    {
        if (_hostHooks is StandaloneHostHooks)
        {
            _jobQueue.Enqueue(job);
            return;
        }

        _hostHooks.EnqueuePromiseJob(job);
    }

    // Test/host seam: install a host object as a named property on the global so
    // a script can address it directly. The interpreter does not (and should
    // not) materialise host objects spontaneously - real embedders register
    // their root host objects (window, document, ...) through this hook at
    // realm-init time before running any user script.
    public void RegisterGlobalHostObject(string name, HostObjectHandle handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var globalHandle = EnsureGlobalObject();
        var global = _heap.GetObject(globalHandle);
        _ = global.DefineOwnProperty(
            name,
            new Objects.JsPropertyDescriptor(
                JsValue.FromHostObject(handle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
    }

    // Host embedder seam: allocate a callable native function on the current
    // heap so hosted browser surfaces can expose DOM/Web API methods directly
    // without reaching into interpreter internals.
    public JsValue AllocateNativeFunction(
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(call);

        var function = new NativeFunctionObject(name, call, length: length);
        function.SetPrototype(EnsureFunctionPrototype());
        var handle = _heap.AllocateObject(function, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    public JsValue AllocateNativeConstructor(
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        Func<IReadOnlyList<JsValue>, JsValue> construct,
        int length = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(construct);

        var function = new NativeFunctionObject(name, call, construct, length: length);
        function.SetPrototype(EnsureFunctionPrototype());
        var handle = _heap.AllocateObject(function, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    public bool CanCallValue(JsValue value)
    {
        return IsCallable(value);
    }

    public JsValue InvokeFunction(JsValue function, IReadOnlyList<JsValue> args, JsValue thisValue)
    {
        // Give each host-initiated invocation (timer/event callback) a fresh wall-clock
        // budget. The deadline field is otherwise only armed by Execute, so without this
        // a callback would inherit the previous script's already-expired deadline and
        // abort immediately with a spurious timeout RangeError.
        if (WallClockTimeoutMs > 0)
        {
            _wallClockDeadlineTicks = System.Environment.TickCount64 + WallClockTimeoutMs;
            _wallClockCheckCountdown = WallClockCheckInterval;
        }

        try
        {
            return CallFunction(function, args, thisValue);
        }
        catch (JsThrownException thrown)
        {
            StampDescription(thrown);
            throw;
        }
    }

    public JsValue AllocateObject(IReadOnlyDictionary<string, JsValue> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var obj = CreateOrdinaryObject();
        foreach (var property in properties)
        {
            _ = obj.DefineOwnProperty(
                property.Key,
                new JsPropertyDescriptor(
                    property.Value,
                    Writable: true,
                    Enumerable: true,
                    Configurable: true));
        }

        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }

    public JsValue AllocateArray(IReadOnlyList<JsValue> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return JsValue.FromObject(_heap.AllocateObject(CreateArrayFromElements(elements), AllocationSite.Current()));
    }

    public void SetObjectProperty(
        JsValue target,
        string name,
        JsValue value,
        bool writable = true,
        bool enumerable = true,
        bool configurable = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (target.Tag != JsValueTag.Object)
        {
            throw new ArgumentException("Target must be an object.", nameof(target));
        }

        var handle = target.AsObjectHandle();
        var obj = _heap.GetObject(handle);
        _ = obj.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(
                value,
                Writable: writable,
                Enumerable: enumerable,
                Configurable: configurable));

        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(handle, value.AsObjectHandle());
        }
    }

    // E.6 - install a JS value as a global binding under the given name. Used
    // by ModuleEvaluator to wire imported bindings into the importer's
    // execution context before the module body runs.
    public void RegisterGlobalValue(string name, JsValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var globalHandle = EnsureGlobalObject();
        var global = _heap.GetObject(globalHandle);
        _ = global.DefineOwnProperty(
            name,
            new Objects.JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: true));
        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(globalHandle, value.AsObjectHandle());
        }
    }

    // E.6 - read a global by name. Used by ModuleEvaluator to harvest the
    // values of locally-declared module exports after the body has executed.
    public bool TryReadGlobalValue(string name, out JsValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var globalHandle = EnsureGlobalObject();
        var global = _heap.GetObject(globalHandle);
        if (global.TryGetOwnProperty(name, out var descriptor) && !descriptor.IsAccessor)
        {
            value = descriptor.Value;
            return true;
        }
        value = JsValue.Undefined;
        return false;
    }

    // E.6 - allocate a plain JS object exposing the given (name, value) pairs
    // as enumerable own properties. Used as the lightweight namespace object
    // for `import * as ns from "mod"`. A full ECMA-262 namespace exotic object
    // with frozen [[Set]] / Symbol.toStringTag is deferred.
    public JsValue AllocateNamespaceObject(IReadOnlyDictionary<string, JsValue> exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var obj = CreateOrdinaryObject();
        // ECMA-262 28.3 Module Namespace Exotic Objects: [[Prototype]] is null,
        // properties are { value, writable: true, enumerable: true,
        // configurable: false }, and Symbol.toStringTag is "Module" with a
        // non-writable non-enumerable non-configurable descriptor.
        obj.SetPrototype(null);
        foreach (var kv in exports)
        {
            _ = obj.DefineOwnProperty(kv.Key,
                new Objects.JsPropertyDescriptor(kv.Value, Writable: true, Enumerable: true, Configurable: false));
        }

        var toStringTagId = GetWellKnownSymbolId("toStringTag");
        if (toStringTagId != 0)
        {
            _ = obj.DefineOwnSymbolProperty(toStringTagId,
                new Objects.JsPropertyDescriptor(JsValue.FromString("Module"),
                    Writable: false, Enumerable: false, Configurable: false));
        }

        obj.PreventExtensions();
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }
}
