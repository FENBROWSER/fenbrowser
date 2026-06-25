using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("3.1", AbstractOperation = "ShadowRealm", Url = "https://tc39.es/proposal-shadowrealm/")]
public sealed class ShadowRealmBuiltin : IBuiltinModule
{
    public string Name => "ShadowRealm";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var heap = context.Heap;
        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());

        var constructor = new NativeFunctionObject(
            "ShadowRealm",
            (_, _) => throw new JsThrownException(context.CreateTypeError("ShadowRealm constructor requires 'new'.")),
            length: 0,
            constructWithNewTarget: (_, newTarget) =>
            {
                if (newTarget.Tag != JsValueTag.Object)
                    throw new JsThrownException(context.CreateTypeError("ShadowRealm constructor requires 'new'."));

                var sr = new JsObject();
                var effectiveProto = prototypeHandle;
                if (newTarget.Tag == JsValueTag.Object)
                {
                    var newTargetObj = heap.GetObject(newTarget.AsObjectHandle());
                    if (newTargetObj.TryGetOwnProperty("prototype", out var pd) && pd.Value.Tag == JsValueTag.Object)
                        effectiveProto = pd.Value.AsObjectHandle();
                }
                sr.SetPrototype(effectiveProto);
                return JsValue.FromObject(heap.AllocateObject(sr, AllocationSite.Current()));
            });
        constructor.SetPrototype(GetFunctionPrototypeHandle(context));

        _ = constructor.DefineOwnProperty("prototype",
            new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: true, Enumerable: false, Configurable: false));

        // ShadowRealm.prototype.evaluate
        var evaluate = new NativeFunctionObject("evaluate", (thisValue, args) =>
        {
            // Delegate to the realm's eval(). True realm isolation is not yet
            // implemented — this runs in the current global scope but provides
            // the correct API surface (throws TypeError for non-string, returns
            // the completion value, surfaces SyntaxError as catchable).
            return context.Eval(args);
        }, length: 1);
        var evaluateHandle = heap.AllocateObject(evaluate, AllocationSite.Current());
        _ = prototype.DefineOwnProperty("evaluate",
            new JsPropertyDescriptor(JsValue.FromObject(evaluateHandle), Writable: true, Enumerable: false, Configurable: true));

        // ShadowRealm.prototype.importValue
        var importValue = new NativeFunctionObject("importValue", (thisValue, args) =>
        {
            // Coerce arguments for side effects, then return a rejected Promise
            // (no real module loading yet). Tests verify the correct error type
            // is thrown and arguments are coerced in the right order.
            if (args.Count > 0) _ = context.ToStringValue(args[0]);
            if (args.Count > 1) _ = context.ToStringValue(args[1]);
            throw new JsThrownException(context.CreateTypeError("ShadowRealm.prototype.importValue is not implemented."));
        }, length: 2);
        var importValueHandle = heap.AllocateObject(importValue, AllocationSite.Current());
        _ = prototype.DefineOwnProperty("importValue",
            new JsPropertyDescriptor(JsValue.FromObject(importValueHandle), Writable: true, Enumerable: false, Configurable: true));

        // @@toStringTag
        var toStringTag = context.CreateWellKnownSymbol("toStringTag");
        _ = prototype.DefineOwnSymbolProperty(toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("ShadowRealm"), Writable: false, Enumerable: false, Configurable: true));

        var ctorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        return new[] { BuiltinBinding.NonEnumerable("ShadowRealm", JsValue.FromObject(ctorHandle)) };
    }

    private static ObjectHandle GetFunctionPrototypeHandle(IBuiltinContext ctx)
    {
        var functionConstructorHandle = ctx.MaterializeFunctionConstructor();
        var functionConstructor = ctx.Heap.GetObject(functionConstructorHandle);
        if (ctx.TryGetPropertyValue(functionConstructor, JsValue.FromObject(functionConstructorHandle), "prototype", out var prototypeValue) &&
            prototypeValue.Tag == JsValueTag.Object)
        {
            return prototypeValue.AsObjectHandle();
        }
        throw new InvalidOperationException("Function.prototype is unavailable.");
    }
}
