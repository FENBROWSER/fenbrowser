using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 20.3 — The Boolean Constructor.
[EcmaSpecReference(
    "20.3",
    AbstractOperation = "Boolean",
    Url = "https://tc39.es/ecma262/#sec-boolean-objects")]
public sealed class BooleanBuiltin : IBuiltinModule
{
    public string Name => "Boolean";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;

        // 20.3.3 Properties of the Boolean Prototype Object
        var prototype = new BooleanObject(false);
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var capturedProto = prototypeHandle;
        var capturedCtx = context;

        var constructor = new NativeFunctionObject(
            "Boolean",
            (_, args) => JsValue.FromBoolean(args.Count > 0 && IsTruthy(args[0])),
            length: 1,
            constructWithNewTarget: (args, newTarget) =>
            {
                var obj = new BooleanObject(args.Count > 0 && IsTruthy(args[0]));
                obj.SetPrototype(ResolveConstructorPrototype(context, newTarget, capturedProto));
                return JsValue.FromObject(heap.AllocateObject(obj, AllocationSite.Current()));
            });
        constructor.SetPrototype(GetFunctionPrototypeHandle(context));
        _ = constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        var protoObj = heap.GetObject(prototypeHandle);
        _ = protoObj.SetProperty("constructor", JsValue.FromObject(constructorHandle));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        // 20.3.3.2 Boolean.prototype.toString()
        DefinePrototypeMethod(capturedCtx, prototypeHandle, protoObj, "toString", (ctx, thisValue, _) =>
            JsValue.FromString(BooleanThisValue(ctx, thisValue) ? "true" : "false"));

        // 20.3.3.3 Boolean.prototype.valueOf()
        DefinePrototypeMethod(capturedCtx, prototypeHandle, protoObj, "valueOf", (ctx, thisValue, _) =>
            JsValue.FromBoolean(BooleanThisValue(ctx, thisValue)));

        return new[] { BuiltinBinding.NonEnumerable("Boolean", JsValue.FromObject(constructorHandle)) };
    }

    private static bool BooleanThisValue(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag == JsValueTag.Boolean)
            return thisValue.AsBoolean();
        if (thisValue.Tag == JsValueTag.Object && ctx.Heap.GetObject(thisValue.AsObjectHandle()) is BooleanObject bo)
            return bo.Value;
        throw new JsThrownException(ctx.CreateTypeError("Boolean.prototype method called on incompatible receiver."));
    }

    private static bool IsTruthy(JsValue value)
    {
        return value.Tag switch
        {
            JsValueTag.Undefined => false,
            JsValueTag.Null => false,
            JsValueTag.Boolean => value.AsBoolean(),
            JsValueTag.Int32 => value.AsInt32() != 0,
            JsValueTag.Number => !double.IsNaN(value.AsNumber()) && value.AsNumber() != 0d,
            JsValueTag.BigInt => value.AsBigInt() != System.Numerics.BigInteger.Zero,
            JsValueTag.String => value.AsString().Length > 0,
            _ => true // Objects (incl. Symbols) are always truthy.
        };
    }

    private static ObjectHandle ResolveConstructorPrototype(IBuiltinContext ctx, JsValue newTarget, ObjectHandle defaultPrototypeHandle)
    {
        if (newTarget.Tag != JsValueTag.Object)
        {
            return defaultPrototypeHandle;
        }

        var newTargetObject = ctx.Heap.GetObject(newTarget.AsObjectHandle());
        if (ctx.TryGetPropertyValue(newTargetObject, newTarget, "prototype", out var prototypeValue) &&
            prototypeValue.Tag == JsValueTag.Object)
        {
            return prototypeValue.AsObjectHandle();
        }

        if (TryGetRealmBooleanPrototype(ctx, newTargetObject, newTarget, out var realmPrototypeHandle))
        {
            return realmPrototypeHandle;
        }

        return defaultPrototypeHandle;
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

    private static bool TryGetRealmBooleanPrototype(
        IBuiltinContext ctx,
        JsObject newTargetObject,
        JsValue newTarget,
        out ObjectHandle prototypeHandle)
    {
        prototypeHandle = default;
        if (!ctx.TryGetPropertyValue(newTargetObject, newTarget, "__realmGlobal__", out var realmGlobal) ||
            realmGlobal.Tag != JsValueTag.Object)
        {
            return false;
        }

        var realmGlobalObject = ctx.Heap.GetObject(realmGlobal.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(realmGlobalObject, realmGlobal, "Boolean", out var realmBoolean) ||
            realmBoolean.Tag != JsValueTag.Object)
        {
            return false;
        }

        var realmBooleanObject = ctx.Heap.GetObject(realmBoolean.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(realmBooleanObject, realmBoolean, "prototype", out var prototypeValue) ||
            prototypeValue.Tag != JsValueTag.Object)
        {
            return false;
        }

        prototypeHandle = prototypeValue.AsObjectHandle();
        return true;
    }

    private static void DefinePrototypeMethod(
        IBuiltinContext ctx,
        ObjectHandle prototypeHandle,
        JsObject prototype,
        string name,
        Func<IBuiltinContext, JsValue, IReadOnlyList<JsValue>, JsValue> call)
    {
        var captured = ctx;
        var fn = new NativeFunctionObject(name, (thisValue, args) => call(captured, thisValue, args), length: 0);
        var fnHandle = ctx.Heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        fn.SetProperty("call", JsValue.FromObject(callHandle));
        ctx.Heap.WriteBarrier(fnHandle, callHandle);
        _ = prototype.DefineOwnProperty(name,
            new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        ctx.Heap.WriteBarrier(prototypeHandle, fnHandle);
    }
}
