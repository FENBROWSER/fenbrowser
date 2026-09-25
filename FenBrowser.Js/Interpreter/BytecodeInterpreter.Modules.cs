using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-262 13.3.10 ImportCall + 13.3.12 ImportMeta runtime helpers.
//
// ImportCall is asynchronous in the spec and has to be asynchronous here too: the
// thread that evaluates a module body is the same one that drains the host's event
// queue, so a loader that produces the namespace before returning stalls input for
// the length of a network round-trip per import. A host therefore sets
// DynamicImportLoader and settles the capability it is handed once the module has
// loaded. DynamicImportResolver is the older synchronous shape, kept for hosts that
// can answer from memory (test262's module fixtures, an embedder with a preloaded
// map); it must never be given a resolver that performs I/O.
//
// With neither hook set, ImportCall resolves to an empty module namespace exotic
// object, which has the right shape for the test262 syntax cohort - dynamic import
// expressions only need to parse and produce a thenable - though tests that read a
// real export still fail at the assertion phase.
public sealed partial class BytecodeInterpreter
{
    public Func<string, string?, JsValue>? DynamicImportResolver { get; set; }

    /// <summary>
    /// ECMA-262 16.2.1.8 HostLoadImportedModule. Receives the specifier, the
    /// referrer's <c>import.meta.url</c> (null outside a module) and a capability to
    /// settle when the module is ready. <c>import()</c> returns that capability's
    /// promise immediately, so the calling thread goes back to its loop rather than
    /// waiting on the load. Takes precedence over <see cref="DynamicImportResolver"/>.
    /// </summary>
    public Action<string, string?, PromiseCapability>? DynamicImportLoader { get; set; }

    /// <summary>
    /// A fresh {[[Promise]], [[Resolve]], [[Reject]]} triple over %Promise%, for a
    /// host that produces a promise now and settles it later. The host owns keeping
    /// all three values reachable until it settles them - nothing else roots them.
    /// </summary>
    public PromiseCapability CreatePromiseCapability() => NewPromiseCapability();

    internal JsValue HandleDynamicImport(JsValue specifier, JsValue options, EnvironmentRecord environment)
    {
        // Touch the specifier through ToString to surface user-defined toString
        // side effects, per ECMA-262 13.3.10.1 step 5. We catch the throw and
        // let it propagate via the returned Promise rejection.
        string specifierText;
        try
        {
            specifierText = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
            if (DynamicImportLoader is { } loader)
            {
                // The capability is created before the loader runs so a loader that
                // completes inline - a cache hit - settles a promise that already
                // exists, and one that goes to the network leaves it pending.
                var capability = NewPromiseCapability();
                loader(specifierText, FindImportMetaUrl(environment), capability);
                return capability.Promise;
            }

            if (DynamicImportResolver is { } resolver)
            {
                return BuildResolvedPromise(resolver(specifierText, FindImportMetaUrl(environment)));
            }
        }
        catch (JsThrownException ex)
        {
            return BuildRejectedPromise(ex.Value);
        }
        catch (FenBrowser.Js.Parser.JsParserException ex)
        {
            // ParseModule failing inside HostLoadImportedModule is a SyntaxError
            // (ECMA-262 16.2.1.7.1 ParseModule step 2), like any early error.
            return BuildRejectedPromise(CreateSyntaxError(ex.Message));
        }
        catch (Exception ex)
        {
            return BuildRejectedPromise(CreateTypeError(ex.Message));
        }

        // Return a resolved Promise with an empty module namespace exotic object.
        // Full host module resolution is not yet wired, but an empty namespace
        // has the correct shape (non-extensible, read-only bindings,
        // @@toStringTag = "Module") so property access on the imported namespace
        // does not throw — it returns undefined for unknown exports.
        var ns = new ModuleNamespaceObject(new Dictionary<string, JsValue>());
        var nsHandle = _heap.AllocateObject(ns, AllocationSite.Current());
        return BuildResolvedPromise(JsValue.FromObject(nsHandle));
    }

    private static string? FindImportMetaUrl(EnvironmentRecord environment)
    {
        for (var current = environment; current is not null; current = current.OuterEnv)
        {
            if (current is ModuleEnvironmentRecord moduleEnvironment)
            {
                return moduleEnvironment.ImportMetaUrl;
            }
        }

        return null;
    }

    internal JsValue HandleImportMeta(EnvironmentRecord environment)
    {
        string url = string.Empty;
        for (var current = environment; current is not null; current = current.OuterEnv)
        {
            if (current is ModuleEnvironmentRecord moduleEnvironment)
            {
                url = moduleEnvironment.ImportMetaUrl;
                break;
            }
        }

        var obj = new JsObject();
        obj.DefineOwnProperty(
            "url",
            new JsPropertyDescriptor(
                JsValue.FromString(url),
                Writable: true,
                Enumerable: true,
                Configurable: true));
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    internal JsValue HandleImportSource(JsValue specifier, JsValue options)
    {
        // ES2025 Import Source proposal — `import.source(specifier)` syntactic form.
        // Returns a resolved Promise with an empty module namespace, same pattern
        // as HandleDynamicImport (no host module resolver wired yet).
        try
        {
            _ = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
        }
        catch (JsThrownException ex)
        {
            return BuildRejectedPromise(ex.Value);
        }

        var ns = new ModuleNamespaceObject(new Dictionary<string, JsValue>());
        var nsHandle = _heap.AllocateObject(ns, AllocationSite.Current());
        return BuildResolvedPromise(JsValue.FromObject(nsHandle));
    }

    internal JsValue HandleImportDefer(JsValue specifier, JsValue options)
    {
        // ES2025 Import Defer proposal — `import.defer(specifier)` syntactic form.
        // Returns a resolved Promise with an empty module namespace, same pattern
        // as HandleDynamicImport (no host module resolver wired yet).
        try
        {
            _ = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
        }
        catch (JsThrownException ex)
        {
            return BuildRejectedPromise(ex.Value);
        }

        var ns = new ModuleNamespaceObject(new Dictionary<string, JsValue>());
        var nsHandle = _heap.AllocateObject(ns, AllocationSite.Current());
        return BuildResolvedPromise(JsValue.FromObject(nsHandle));
    }

    private void ProcessDynamicImportOptions(JsValue options)
    {
        if (options.Tag == JsValueTag.Undefined)
        {
            return;
        }
        if (options.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Dynamic import options must be an object."));
        }

        var attributes = GetReceiverProperty(options, "with");
        if (attributes.Tag == JsValueTag.Undefined)
        {
            return;
        }
        if (attributes.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Dynamic import attributes must be an object."));
        }

        _ = CollectOwnEnumerable(new[] { attributes }, OwnEnumerableKind.Values);
    }

    internal void PinIfObject(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            _heap.PushRoot(value.AsObjectHandle());
        }
    }

    private JsValue BuildRejectedPromise(JsValue reason)
    {
        var promise = new PromiseObject();
        var instance = new PromiseInstance(promise);
        if (_promisePrototypeHandle is { } proto)
        {
            instance.SetPrototype(proto);
        }
        var handle = _heap.AllocateObject(instance, AllocationSite.Current());
        RejectPromise(handle, reason);
        return JsValue.FromObject(handle);
    }

    private JsValue BuildResolvedPromise(JsValue value)
    {
        var promise = new PromiseObject();
        var instance = new PromiseInstance(promise);
        if (_promisePrototypeHandle is { } proto)
        {
            instance.SetPrototype(proto);
        }
        var handle = _heap.AllocateObject(instance, AllocationSite.Current());
        FulfillPromise(handle, value);
        return JsValue.FromObject(handle);
    }
}
