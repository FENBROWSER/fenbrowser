using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// ECMA-262 13.3.10 ImportCall + 13.3.12 ImportMeta runtime helpers.
// FenJS does not yet wire a host module resolver, so ImportCall produces a
// rejected Promise (TypeError) and ImportMeta returns a fresh empty object.
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
        JsValue reason;
        try
        {
            _ = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
            reason = CreateTypeError("Dynamic import is not supported in this host.");
        }
        catch (JsThrownException ex)
        {
            reason = ex.Value;
        }

        return BuildRejectedPromise(reason);
    }

    internal JsValue HandleImportMeta()
    {
        // Fresh empty object. ECMA-262 makes import.meta host-specific; with no
        // host hook FenJS returns a plain object so property access doesn't throw.
        var obj = new JsObject();
        var handle = _heap.AllocateObject(obj, AllocationSite.Current());
        return JsValue.FromObject(handle);
    }

    internal JsValue HandleImportSource(JsValue specifier, JsValue options)
    {
        // ES2025 Import Source proposal — `import.source(specifier)` syntactic form.
        // Returns a rejected Promise per DynamicImport pattern (no host module
        // resolver wired yet). Per ECMA-262 ContinueDynamicImport semantics,
        // the specifier is ToString'd before rejection so user-defined toString
        // side effects are surfaced.
        JsValue reason;
        try
        {
            _ = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
            reason = CreateTypeError("Dynamic import (source phase) is not supported in this host.");
        }
        catch (JsThrownException ex)
        {
            reason = ex.Value;
        }
        return BuildRejectedPromise(reason);
    }

    internal JsValue HandleImportDefer(JsValue specifier, JsValue options)
    {
        // ES2025 Import Defer proposal — `import.defer(specifier)` syntactic form.
        // Returns a rejected Promise per DynamicImport pattern (no host module
        // resolver wired yet). Per ECMA-262 ContinueDynamicImport semantics,
        // the specifier is ToString'd before rejection so user-defined toString
        // side effects are surfaced.
        JsValue reason;
        try
        {
            _ = ToStringValue(specifier);
            ProcessDynamicImportOptions(options);
            reason = CreateTypeError("Dynamic import (defer phase) is not supported in this host.");
        }
        catch (JsThrownException ex)
        {
            reason = ex.Value;
        }
        return BuildRejectedPromise(reason);
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
}
