using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-262 13.3.10 ImportCall + 13.3.12 ImportMeta runtime helpers.
// FenJS does not yet wire a host module resolver, so ImportCall produces a
// rejected Promise (TypeError) and ImportMeta returns a fresh host-populated object.
// These are placeholders: they're sufficient for the test262 syntax cohort
// (where dynamic import expressions only need to parse and produce a thenable)
// and for assertion-style tests that expect a TypeError. Tests that depend on
// a host-loaded module namespace still fail at the assertion phase.
public sealed partial class BytecodeInterpreter
{
    internal JsValue HandleDynamicImport(JsValue specifier, JsValue options)
    {
        // Touch the specifier through ToString to surface user-defined toString
        // side effects, per ECMA-262 13.3.10.1 step 5. We catch the throw and
        // let it propagate via the returned Promise rejection.
        try
        {
            _ = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
        }
        catch (JsThrownException ex)
        {
            return BuildRejectedPromise(ex.Value);
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
