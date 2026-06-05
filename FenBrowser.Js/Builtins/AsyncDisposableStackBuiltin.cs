using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// Minimal constructor/prototype surface so sync DisposableStack tests can
// distinguish AsyncDisposableStack instances from sync stacks. Full async
// disposal semantics land in the dedicated category slice.
public sealed class AsyncDisposableStackBuiltin : IBuiltinModule
{
    public string Name => "AsyncDisposableStack";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var heap = context.Heap;
        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "AsyncDisposableStack",
            (_, _) => throw new JsThrownException(context.CreateTypeError("AsyncDisposableStack constructor requires 'new'.")),
            length: 0,
            constructWithNewTarget: (_, newTarget) =>
            {
                if (newTarget.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(context.CreateTypeError("AsyncDisposableStack constructor requires 'new'."));
                }

                var instance = new AsyncDisposableStackObject();
                var newTargetObject = context.Heap.GetObject(newTarget.AsObjectHandle());
                if (context.TryGetPropertyValue(newTargetObject, newTarget, "prototype", out var prototypeValue) &&
                    prototypeValue.Tag == JsValueTag.Object)
                {
                    instance.SetPrototype(prototypeValue.AsObjectHandle());
                }
                else
                {
                    instance.SetPrototype(prototypeHandle);
                }

                return JsValue.FromObject(heap.AllocateObject(instance, AllocationSite.Current()));
            });
        constructor.SetPrototype(GetFunctionPrototypeHandle(context));

        _ = constructor.DefineOwnProperty(
            "prototype",
            new JsPropertyDescriptor(JsValue.FromObject(prototypeHandle), Writable: false, Enumerable: false, Configurable: false));
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);
        heap.WriteBarrier(constructorHandle, prototypeHandle);

        _ = prototype.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(constructorHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, constructorHandle);

        return new[] { BuiltinBinding.NonEnumerable("AsyncDisposableStack", JsValue.FromObject(constructorHandle)) };
    }

    private sealed class AsyncDisposableStackObject : JsObject
    {
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
