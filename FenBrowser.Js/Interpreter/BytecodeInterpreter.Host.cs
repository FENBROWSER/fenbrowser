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
    private readonly Dictionary<HostObjectHandle, Dictionary<string, JsPropertyDescriptor>> _hostDefinedProperties = new();
    private readonly Dictionary<HostObjectHandle, JsValue> _hostObjectPrototypes = new();
    private readonly Dictionary<HostObjectHandle, long> _hostPrivateBrands = new();

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
        _ = RequireHostObject(receiver, "get host property");
        var handle = receiver.AsHostObjectHandle();
        if (key == "__proto__")
        {
            return GetHostObjectPrototype(handle);
        }

        if (TryGetHostObjectDefinedProperty(handle, key, out var descriptor))
        {
            return GetDescriptorValue(descriptor, receiver);
        }

        if (TryGetHostObjectPrototypeProperty(handle, receiver, key, out var value))
        {
            return value;
        }

        // Audit JSRT rooting family: host hooks build facades lazily (node
        // lists, record objects, bound methods) where intermediates have no
        // incoming JS edge until the top-level value materializes. The
        // construction window keeps every cell allocated inside the hook
        // alive until the hook returns its final value.
        using var _constructionWindow = _heap.BeginConstructionWindow();
        if (_hostHooks.TryGetHostProperty(handle, key, out value))
        {
            return value;
        }

        // WebIDL 3.7.3: a platform object's own interface members (the host
        // hook) shadow %Object.prototype%; the implicit prototype is only
        // consulted for what the interface does not define (hasOwnProperty,
        // the generic toString, ...).
        if (!HasHostObjectPrototype(handle) &&
            TryGetPropertyValue(_heap.GetObject(EnsureObjectPrototype()), receiver, key, out value))
        {
            return value;
        }

        return JsValue.Undefined;
    }

    // Write a property on a host object. Resolves the handle first, then
    // defers to IHostHooks.TrySetHostProperty. A false return becomes a
    // TypeError so scripts can see refused writes; this matches the spec's
    // "throw a TypeError" branch of OrdinarySet when the host vetoes.
    private void SetHostObjectProperty(JsValue receiver, string key, JsValue value)
    {
        _ = RequireHostObject(receiver, "set host property");
        var handle = receiver.AsHostObjectHandle();
        if (key == "__proto__")
        {
            if (value.Tag == JsValueTag.Object || value.Tag == JsValueTag.Null)
            {
                SetHostObjectPrototype(handle, value);
            }

            return;
        }

        if (TryGetHostObjectDefinedProperty(handle, key, out var descriptor))
        {
            if (descriptor.IsAccessor)
            {
                if (!CallSetter(descriptor, value, receiver))
                {
                    throw new JsThrownException(CreateTypeError(
                        "Cannot set property '" + key + "' on host object accessor without a setter."));
                }

                return;
            }

            if (!descriptor.Writable)
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot assign to read-only property '" + key + "' on host object."));
            }

            DefineHostObjectProperty(handle, key, descriptor with { Value = value });
            return;
        }

        if (_hostHooks.TrySetHostProperty(handle, key, value))
        {
            return;
        }

        if (!_hostHooks.TryGetHostProperty(handle, key, out _))
        {
            DefineHostObjectProperty(
                handle,
                key,
                new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
            return;
        }

        throw new JsThrownException(CreateTypeError(
            "Cannot set property '" + key + "' on host object (refused by embedder)."));
    }

    private bool TryGetHostObjectDefinedProperty(
        HostObjectHandle handle,
        string key,
        out JsPropertyDescriptor descriptor)
    {
        if (_hostDefinedProperties.TryGetValue(handle, out var properties) &&
            properties.TryGetValue(key, out descriptor))
        {
            return true;
        }

        descriptor = default;
        return false;
    }

    private IEnumerable<string> EnumerateHostObjectDefinedPropertyNames(HostObjectHandle handle)
        => _hostDefinedProperties.TryGetValue(handle, out var properties)
            ? properties.Keys
            : Array.Empty<string>();

    // WebIDL 3.9 [[OwnPropertyKeys]] for a platform object: the host's own keys
    // (indices, then supported names) first, then properties script defined
    // on the object, each once.
    private IReadOnlyList<string> EnumerateHostObjectOwnKeys(HostObjectHandle handle)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (_hostHooks.TryGetHostOwnKeys(handle, out var hostKeys))
        {
            foreach (var key in hostKeys)
            {
                if (seen.Add(key)) keys.Add(key);
            }
        }

        foreach (var key in EnumerateHostObjectDefinedPropertyNames(handle))
        {
            if (seen.Add(key)) keys.Add(key);
        }

        return keys;
    }

    private void DefineHostObjectProperty(HostObjectHandle handle, string key, JsPropertyDescriptor descriptor)
    {
        if (!_hostDefinedProperties.TryGetValue(handle, out var properties))
        {
            properties = new Dictionary<string, JsPropertyDescriptor>(StringComparer.Ordinal);
            _hostDefinedProperties[handle] = properties;
        }

        properties[key] = descriptor;
    }

    // Host embedder seam: define a data property on a host-object facade whose
    // value the GC must trace — _hostDefinedProperties is walked by
    // IHeapRootSource.TraceRoots. Use this to root JS values a CLR wrapper holds
    // invisibly to the tracer (e.g. MutationObserver callbacks).
    public void DefineHostObjectRootProperty(JsValue facade, string key, JsValue value)
    {
        if (facade.Tag != JsValueTag.HostObject)
        {
            throw new ArgumentException("Facade must be a host object.", nameof(facade));
        }

        ArgumentException.ThrowIfNullOrEmpty(key);
        DefineHostObjectProperty(
            facade.AsHostObjectHandle(),
            key,
            new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false));
    }

    private void DefineHostPrivateField(
        JsValue receiver,
        string name,
        JsValue value,
        long brand)
    {
        _ = RequireHostObject(receiver, "define private field");
        var handle = receiver.AsHostObjectHandle();
        if (_hostPrivateBrands.TryGetValue(handle, out var existingBrand) &&
            existingBrand != 0 &&
            existingBrand != brand)
        {
            throw new JsThrownException(CreateTypeError(
                "Cannot define private field on an object whose class did not declare it."));
        }

        _hostPrivateBrands[handle] = brand;
        DefineHostObjectProperty(
            handle,
            name,
            new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false));
    }

    private bool TryGetHostPrivateField(
        JsValue receiver,
        string name,
        long brand,
        out JsValue value)
    {
        _ = RequireHostObject(receiver, "read private field");
        var handle = receiver.AsHostObjectHandle();
        if (!_hostPrivateBrands.TryGetValue(handle, out var actualBrand) ||
            actualBrand != brand ||
            !TryGetHostObjectDefinedProperty(handle, name, out var descriptor))
        {
            value = JsValue.Undefined;
            return false;
        }

        value = GetDescriptorValue(descriptor, receiver);
        return true;
    }

    private bool TrySetHostPrivateField(
        JsValue receiver,
        string name,
        JsValue value,
        long brand)
    {
        _ = RequireHostObject(receiver, "write private field");
        var handle = receiver.AsHostObjectHandle();
        if (!_hostPrivateBrands.TryGetValue(handle, out var actualBrand) ||
            actualBrand != brand ||
            !TryGetHostObjectDefinedProperty(handle, name, out var descriptor) ||
            descriptor.IsAccessor)
        {
            return false;
        }

        DefineHostObjectProperty(handle, name, descriptor with { Value = value });
        return true;
    }

    public void CopyHostObjectOwnProperties(JsValue source, JsValue target)
    {
        if (source.Tag != JsValueTag.HostObject || target.Tag != JsValueTag.HostObject)
        {
            return;
        }

        var sourceHandle = source.AsHostObjectHandle();
        var targetHandle = target.AsHostObjectHandle();
        _ = RequireHostObject(source, "copy host object source properties");
        _ = RequireHostObject(target, "copy host object target properties");
        if (_hostDefinedProperties.TryGetValue(sourceHandle, out var sourceProperties))
        {
            foreach (var property in sourceProperties)
            {
                DefineHostObjectProperty(targetHandle, property.Key, property.Value);
            }
        }

        CopyHostObjectAssignedPrototypeProperties(sourceHandle, targetHandle);
    }

    private void CopyHostObjectAssignedPrototypeProperties(HostObjectHandle sourceHandle, HostObjectHandle targetHandle)
    {
        var prototype = GetExplicitHostObjectPrototype(sourceHandle);
        if (prototype.Tag != JsValueTag.Object)
        {
            return;
        }

        var prototypeObject = _heap.GetObject(prototype.AsObjectHandle());
        foreach (var property in prototypeObject.EnumerateOwnProperties())
        {
            if (TryGetHostObjectDefinedProperty(targetHandle, property.Key, out _))
            {
                continue;
            }

            DefineHostObjectProperty(targetHandle, property.Key, property.Value);
        }
    }

    // WebIDL 3.7.3: every platform object's prototype chain ends at
    // %Object.prototype%, so a host object the embedder gave no interface
    // prototype still has toString, hasOwnProperty and the rest. An explicit
    // null (Object.setPrototypeOf(host, null)) is honoured as null.
    private JsValue GetHostObjectPrototype(HostObjectHandle handle)
        => _hostObjectPrototypes.TryGetValue(handle, out var prototype)
            ? prototype
            : JsValue.FromObject(EnsureObjectPrototype());

    // The prototype an object was explicitly given, for the property walk:
    // the implicit %Object.prototype% is consulted after the host hook, not
    // before it (see GetHostObjectProperty).
    private JsValue GetExplicitHostObjectPrototype(HostObjectHandle handle)
        => _hostObjectPrototypes.TryGetValue(handle, out var prototype) ? prototype : JsValue.Null;

    private bool HasHostObjectPrototype(HostObjectHandle handle)
        => _hostObjectPrototypes.ContainsKey(handle);

    private void SetHostObjectPrototype(HostObjectHandle handle, JsValue prototype)
    {
        if (prototype.Tag != JsValueTag.Object && prototype.Tag != JsValueTag.Null)
        {
            throw new ArgumentException("Host object prototype must be an object or null.", nameof(prototype));
        }

        _hostObjectPrototypes[handle] = prototype;
    }

    private bool TryGetHostObjectPrototypeProperty(
        HostObjectHandle handle,
        JsValue receiver,
        string key,
        out JsValue value)
    {
        var prototype = GetExplicitHostObjectPrototype(handle);
        if (prototype.Tag != JsValueTag.Object)
        {
            value = JsValue.Undefined;
            return false;
        }

        var prototypeObject = _heap.GetObject(prototype.AsObjectHandle());
        return TryGetPropertyValue(prototypeObject, receiver, key, out value);
    }

    private bool TryGetHostObjectPrototypeSymbolProperty(
        HostObjectHandle handle,
        JsValue receiver,
        long symbolId,
        out JsValue value)
    {
        var prototype = GetExplicitHostObjectPrototype(handle);
        if (prototype.Tag != JsValueTag.Object)
        {
            value = JsValue.Undefined;
            return false;
        }

        var prototypeObject = _heap.GetObject(prototype.AsObjectHandle());
        if (prototypeObject.TryGetSymbolProperty(symbolId, ResolvePrototypeDelegate, out var descriptor))
        {
            value = GetDescriptorValue(descriptor, receiver);
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    private bool HasHostObjectProperty(JsValue receiver, string key)
    {
        _ = RequireHostObject(receiver, "check host property");
        var handle = receiver.AsHostObjectHandle();
        if (TryGetHostObjectDefinedProperty(handle, key, out _))
        {
            return true;
        }

        if (TryGetHostObjectPrototypeProperty(handle, receiver, key, out _))
        {
            return true;
        }

        if (_hostHooks.TryGetHostProperty(handle, key, HostPropertyAccessKind.InCheck, out _))
        {
            return true;
        }

        return !HasHostObjectPrototype(handle) &&
               TryGetPropertyValue(_heap.GetObject(EnsureObjectPrototype()), receiver, key, out _);
    }

    private bool HasHostObjectDefinedOrEmbedderProperty(HostObjectHandle handle, string key)
        => TryGetHostObjectDefinedProperty(handle, key, out _) ||
           _hostHooks.TryGetHostProperty(handle, key, HostPropertyAccessKind.DescriptorOperation, out _);

    private void ApplyDefaultHostObjectPrototypeIfUnset(JsValue constructed, JsValue newTarget)
    {
        if (constructed.Tag != JsValueTag.HostObject || newTarget.Tag != JsValueTag.Object)
        {
            return;
        }

        var handle = constructed.AsHostObjectHandle();
        _ = RequireHostObject(constructed, "set host object construction prototype");

        var prototype = GetReceiverProperty(newTarget, "prototype");
        if ((prototype.Tag == JsValueTag.Object || prototype.Tag == JsValueTag.Null) &&
            ShouldApplyDefaultHostObjectPrototype(handle, prototype))
        {
            SetHostObjectPrototype(handle, prototype);
        }
    }

    private bool ShouldApplyDefaultHostObjectPrototype(HostObjectHandle handle, JsValue prototype)
    {
        var current = GetExplicitHostObjectPrototype(handle);
        if (current.Tag == JsValueTag.Null)
        {
            return true;
        }

        if (current.Tag == JsValueTag.Object &&
            prototype.Tag == JsValueTag.Object &&
            current.AsObjectHandle().Equals(prototype.AsObjectHandle()))
        {
            return true;
        }

        return IsFenDomDefaultPrototype(current);
    }

    private bool IsFenDomDefaultPrototype(JsValue prototype)
    {
        if (prototype.Tag != JsValueTag.Object)
        {
            return false;
        }

        var handle = prototype.AsObjectHandle();
        if (_objectPrototypeHandle is { } objectPrototype &&
            handle.Equals(objectPrototype))
        {
            return true;
        }

        var obj = _heap.GetObject(handle);
        return obj.TryGetOwnProperty("__fenDomBrands", out var brandDescriptor) &&
            brandDescriptor.HasValue;
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

    public void EnqueueHostedPromiseJob(PromiseJob job)
    {
        _jobQueue.Enqueue(job);
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

    public bool TrySetHostObjectPrototypeFromGlobalConstructor(JsValue target, string constructorName)
    {
        if (target.Tag != JsValueTag.HostObject ||
            string.IsNullOrWhiteSpace(constructorName) ||
            !TryReadGlobalValue(constructorName, out var constructor) ||
            constructor.Tag != JsValueTag.Object)
        {
            return false;
        }

        var constructorObject = _heap.GetObject(constructor.AsObjectHandle());
        if (!TryGetPropertyValue(constructorObject, constructor, "prototype", out var prototype) ||
            (prototype.Tag != JsValueTag.Object && prototype.Tag != JsValueTag.Null))
        {
            return false;
        }

        _ = RequireHostObject(target, "set host object prototype");
        SetHostObjectPrototype(target.AsHostObjectHandle(), prototype);
        return true;
    }

    // Host embedder seam: allocate a callable native function on the current
    // heap so hosted browser surfaces can expose DOM/Web API methods directly
    // without reaching into interpreter internals.
    public JsValue AllocateNativeFunction(
        string name,
        Func<JsValue, IReadOnlyList<JsValue>, JsValue> call,
        int length = 0,
        IReadOnlyList<JsValue>? capturedRoots = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(call);

        var function = new NativeFunctionObject(
            name,
            call,
            length: length,
            capturedRoots: capturedRoots);
        function.SetPrototype(EnsureFunctionPrototype());
        var site = AllocationSite.Current();
        var handle = _heap.AllocateObject(
            function,
            site with { MemberName = $"{site.MemberName}:{name}" });
        return JsValue.FromObject(handle);
    }

    // Host seam for the Annex B document.all-compatible [[IsHTMLDDA]] exotic.
    public void MarkAsHtmlDda(JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            throw new ArgumentException("[[IsHTMLDDA]] can only mark an object.", nameof(value));
        }

        _heap.GetObject(value.AsObjectHandle()).IsHtmlDda = true;
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
        var site = AllocationSite.Current();
        var handle = _heap.AllocateObject(
            function,
            site with { MemberName = $"{site.MemberName}:{name}" });
        return JsValue.FromObject(handle);
    }

    public bool CanCallValue(JsValue value)
    {
        return IsCallable(value);
    }

    /// <summary>
    /// ECMA-262 7.3.15 Construct(F, argumentsList): a host-initiated <c>new</c>
    /// of a script constructor, for host objects the embedder models in
    /// script (DOMException) rather than as native cells.
    /// </summary>
    public JsValue ConstructValue(JsValue constructor, IReadOnlyList<JsValue> args)
    {
        try
        {
            return ConstructFunction(constructor, args);
        }
        catch (JsThrownException thrown)
        {
            StampDescription(thrown);
            throw;
        }
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

    public JsValue AllocateModuleNamespaceObject(
        Environments.ModuleEnvironmentRecord environment,
        IReadOnlyDictionary<string, string> localNames)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(localNames);
        var obj = new ModuleNamespaceObject(
            environment,
            localNames,
            GetWellKnownSymbolId("toStringTag"));
        return JsValue.FromObject(_heap.AllocateObject(obj, AllocationSite.Current()));
    }
}
