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
// With neither hook set there is nothing that can load a module, so ImportCall
// rejects with a TypeError, the way HostLoadImportedModule completing with a
// throw would. Resolving to an empty namespace instead made every import appear
// to succeed and every export read as undefined.
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

    internal JsValue HandleDynamicImport(JsValue specifier, JsValue options, EnvironmentRecord? environment)
        => ImportCall(specifier, options, environment, sourcePhase: false);

    // ECMA-262 13.3.10.1 EvaluateImportCall: ToString(specifier) and the options
    // run first, and any abrupt completion there, like a failed load, rejects the
    // returned promise rather than throwing.
    private JsValue ImportCall(JsValue specifier, JsValue options, EnvironmentRecord? environment, bool sourcePhase)
    {
        JsValue loaded;
        try
        {
            var specifierText = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
            loaded = LoadImportedModule(specifierText, environment);
        }
        catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
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

        return sourcePhase ? RejectSourcePhaseOfSourceTextModule(loaded) : loaded;
    }

    // HostLoadImportedModule through whichever hook the host installed, as a
    // promise for the module namespace.
    private JsValue LoadImportedModule(string specifierText, EnvironmentRecord? environment)
    {
        var referrer = environment is null ? null : FindImportMetaUrl(environment);
        if (DynamicImportLoader is { } loader)
        {
            // The capability is created before the loader runs so a loader that
            // completes inline - a cache hit - settles a promise that already
            // exists, and one that goes to the network leaves it pending.
            var capability = NewPromiseCapability();
            loader(specifierText, referrer, capability);
            return capability.Promise;
        }

        if (DynamicImportResolver is { } resolver)
        {
            return BuildResolvedPromise(resolver(specifierText, referrer));
        }

        throw new JsThrownException(CreateTypeError(
            "Cannot import '" + specifierText + "': no module loader is available."));
    }

    // Source phase imports (ECMA-262 16.2.1.7.2 GetModuleSource): every module
    // this engine loads is a Source Text Module Record, whose source phase is
    // a SyntaxError - but only once the load itself has succeeded, so a failed
    // load still rejects with its own reason.
    private JsValue RejectSourcePhaseOfSourceTextModule(JsValue loaded)
    {
        if (loaded.Tag != JsValueTag.Object || _heap.GetObject(loaded.AsObjectHandle()) is not PromiseInstance instance)
        {
            return loaded;
        }

        var onFulfilled = AllocateNativeCallback((_, _) =>
            throw new JsThrownException(CreateSyntaxError(
                "Source phase imports are not available for JavaScript modules.")));
        return PerformPromiseThen(loaded.AsObjectHandle(), instance.Promise, onFulfilled, JsValue.Undefined, NewPromiseCapability());
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

    internal JsValue HandleImportMeta(EnvironmentRecord? environment)
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

    // `import.source(specifier)`: load the module, then ask for its source.
    internal JsValue HandleImportSource(JsValue specifier, JsValue options, EnvironmentRecord? environment)
        => ImportCall(specifier, options, environment, sourcePhase: true);

    // `import.defer(specifier)` loads exactly what import() loads. The namespace
    // it resolves to is an ordinary one, so the module has already been
    // evaluated rather than deferred until its first property access.
    internal JsValue HandleImportDefer(JsValue specifier, JsValue options, EnvironmentRecord? environment)
        => ImportCall(specifier, options, environment, sourcePhase: false);

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
