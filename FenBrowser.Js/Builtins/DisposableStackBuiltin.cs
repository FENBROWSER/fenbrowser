using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("28.2", AbstractOperation = "DisposableStack", Url = "https://tc39.es/ecma262/#sec-disposablestack-constructor")]
public sealed class DisposableStackBuiltin : IBuiltinModule
{
    public string Name => "DisposableStack";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var heap = context.Heap;
        var prototype = new JsObject();
        prototype.SetPrototype(context.GetObjectPrototype());
        var prototypeHandle = heap.AllocateObject(prototype, AllocationSite.Current());
        heap.PushRoot(prototypeHandle);

        var constructor = new NativeFunctionObject(
            "DisposableStack",
            (_, _) => throw new JsThrownException(context.CreateTypeError("DisposableStack constructor requires 'new'.")),
            length: 0,
            constructWithNewTarget: (_, newTarget) =>
            {
                if (newTarget.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(context.CreateTypeError("DisposableStack constructor requires 'new'."));
                }

                var stack = new DisposableStackObject();
                stack.SetPrototype(ResolveConstructorPrototype(context, newTarget, prototypeHandle));
                return JsValue.FromObject(heap.AllocateObject(stack, AllocationSite.Current()));
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

        var disposeHandle = DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "dispose", Dispose, length: 0);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "use", Use, length: 1);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "adopt", Adopt, length: 2);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "defer", Defer, length: 1);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "move", (ctx, thisValue, _) => Move(ctx, thisValue, prototypeHandle), length: 0);

        var disposeSymbol = context.CreateWellKnownSymbol("dispose");
        _ = prototype.DefineOwnSymbolProperty(
            disposeSymbol.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromObject(disposeHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, disposeHandle);

        var disposedGetter = new NativeFunctionObject("get disposed", (thisValue, _) =>
        {
            var stack = RequireDisposableStack(context, thisValue);
            return JsValue.FromBoolean(stack.State == DisposableState.Disposed);
        }, length: 0);
        var disposedGetterHandle = heap.AllocateObject(disposedGetter, AllocationSite.Current());
        disposedGetter.SetPrototype(context.GetObjectPrototype());
        var callHandle = context.GetFunctionCallMethod();
        disposedGetter.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(disposedGetterHandle, callHandle);
        _ = prototype.DefineOwnProperty(
            "disposed",
            JsPropertyDescriptor.Accessor(
                JsValue.FromObject(disposedGetterHandle),
                JsValue.Undefined,
                Enumerable: false,
                Configurable: true));
        heap.WriteBarrier(prototypeHandle, disposedGetterHandle);

        var toStringTag = context.CreateWellKnownSymbol("toStringTag");
        _ = prototype.DefineOwnSymbolProperty(
            toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("DisposableStack"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("DisposableStack", JsValue.FromObject(constructorHandle)) };
    }

    private enum DisposableState
    {
        Pending,
        Disposed,
    }

    private sealed class DisposableResource
    {
        public DisposableResource(JsValue value, JsValue method)
        {
            Value = value;
            Method = method;
        }

        public JsValue Value { get; }
        public JsValue Method { get; }
    }

    private sealed class DisposableStackObject : JsObject
    {
        public DisposableState State { get; set; } = DisposableState.Pending;
        public List<DisposableResource> Resources { get; set; } = new();

        public override void Trace(IHeapTracer tracer)
        {
            base.Trace(tracer);
            foreach (var resource in Resources)
            {
                if (resource.Value.Tag == JsValueTag.Object)
                {
                    tracer.Trace(resource.Value.AsObjectHandle());
                }

                if (resource.Method.Tag == JsValueTag.Object)
                {
                    tracer.Trace(resource.Method.AsObjectHandle());
                }
            }
        }
    }

    private delegate JsValue PrototypeMethod(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args);

    private static ObjectHandle DefinePrototypeMethod(
        IBuiltinContext ctx,
        JsHeap heap,
        ObjectHandle prototypeHandle,
        JsObject prototype,
        string name,
        PrototypeMethod method,
        int length)
    {
        var fn = new NativeFunctionObject(name, (thisValue, args) => method(ctx, thisValue, args), length: length);
        var fnHandle = heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        fn.SetPrototype(ctx.GetObjectPrototype());
        fn.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(fnHandle, callHandle);
        _ = prototype.DefineOwnProperty(
            name,
            new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, fnHandle);
        return fnHandle;
    }

    private static ObjectHandle ResolveConstructorPrototype(IBuiltinContext ctx, JsValue newTarget, ObjectHandle defaultPrototypeHandle)
    {
        var newTargetObject = ctx.Heap.GetObject(newTarget.AsObjectHandle());
        if (ctx.TryGetPropertyValue(newTargetObject, newTarget, "prototype", out var prototypeValue) &&
            prototypeValue.Tag == JsValueTag.Object)
        {
            return prototypeValue.AsObjectHandle();
        }

        if (TryGetRealmDisposableStackPrototype(ctx, newTargetObject, newTarget, out var realmPrototypeHandle))
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

    private static bool TryGetRealmDisposableStackPrototype(
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
        if (!ctx.TryGetPropertyValue(realmGlobalObject, realmGlobal, "DisposableStack", out var realmDisposableStack) ||
            realmDisposableStack.Tag != JsValueTag.Object)
        {
            return false;
        }

        var realmDisposableStackObject = ctx.Heap.GetObject(realmDisposableStack.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(realmDisposableStackObject, realmDisposableStack, "prototype", out var prototypeValue) ||
            prototypeValue.Tag != JsValueTag.Object)
        {
            return false;
        }

        prototypeHandle = prototypeValue.AsObjectHandle();
        return true;
    }

    private static DisposableStackObject RequireDisposableStack(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            ctx.Heap.GetObject(thisValue.AsObjectHandle()) is not DisposableStackObject stack)
        {
            throw new JsThrownException(ctx.CreateTypeError("DisposableStack method called on incompatible receiver."));
        }

        return stack;
    }

    private static void ThrowIfDisposed(IBuiltinContext ctx, DisposableStackObject stack)
    {
        if (stack.State == DisposableState.Disposed)
        {
            throw new JsThrownException(ctx.CreateReferenceError("DisposableStack is already disposed."));
        }
    }

    private static bool IsCallable(IBuiltinContext ctx, JsValue value)
    {
        return value.Tag == JsValueTag.Object &&
               ctx.Heap.GetObject(value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject or BoundFunctionObject;
    }

    private static JsValue GetMethodBySymbol(IBuiltinContext ctx, JsValue receiver, string symbolName)
    {
        if (receiver.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(ctx.CreateTypeError("DisposableStack resource must be an object."));
        }

        var symbol = ctx.CreateWellKnownSymbol(symbolName);
        var obj = ctx.Heap.GetObject(receiver.AsObjectHandle());
        if (!obj.TryGetSymbolProperty(symbol.AsSymbolId(), h => ctx.Heap.GetObject(h), out var desc))
        {
            return JsValue.Undefined;
        }

        JsValue method;
        if (desc.IsAccessor)
        {
            if (desc.Get.Tag == JsValueTag.Undefined)
            {
                return JsValue.Undefined;
            }

            if (!IsCallable(ctx, desc.Get))
            {
                throw new JsThrownException(ctx.CreateTypeError("@@dispose getter is not callable."));
            }

            method = ctx.CallFunction(desc.Get, Array.Empty<JsValue>(), receiver);
        }
        else
        {
            method = desc.Value;
        }

        if (method.Tag is JsValueTag.Undefined or JsValueTag.Null)
        {
            return JsValue.Undefined;
        }

        if (!IsCallable(ctx, method))
        {
            throw new JsThrownException(ctx.CreateTypeError("@@dispose method is not callable."));
        }

        return method;
    }

    private static JsValue Use(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var stack = RequireDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (value.Tag is JsValueTag.Null or JsValueTag.Undefined)
        {
            return value;
        }

        if (value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(ctx.CreateTypeError("DisposableStack.prototype.use requires an object, null, or undefined."));
        }

        var method = GetMethodBySymbol(ctx, value, "dispose");
        if (method.Tag == JsValueTag.Undefined)
        {
            throw new JsThrownException(ctx.CreateTypeError("DisposableStack.prototype.use requires a Symbol.dispose method."));
        }

        stack.Resources.Add(new DisposableResource(value, method));
        return value;
    }

    private static JsValue Adopt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var stack = RequireDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        var onDispose = args.Count > 1 ? args[1] : JsValue.Undefined;
        if (!IsCallable(ctx, onDispose))
        {
            throw new JsThrownException(ctx.CreateTypeError("DisposableStack.prototype.adopt requires a callable disposer."));
        }

        var closure = new NativeFunctionObject(string.Empty, (_, _) =>
        {
            _ = ctx.CallFunction(onDispose, new[] { value }, JsValue.Undefined);
            return JsValue.Undefined;
        }, length: 0);
        closure.SetPrototype(ctx.GetObjectPrototype());
        var closureHandle = ctx.Heap.AllocateObject(closure, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        closure.SetProperty("call", JsValue.FromObject(callHandle));
        ctx.Heap.WriteBarrier(closureHandle, callHandle);

        stack.Resources.Add(new DisposableResource(JsValue.Undefined, JsValue.FromObject(closureHandle)));
        return value;
    }

    private static JsValue Defer(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var stack = RequireDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var onDispose = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (!IsCallable(ctx, onDispose))
        {
            throw new JsThrownException(ctx.CreateTypeError("DisposableStack.prototype.defer requires a callable disposer."));
        }

        stack.Resources.Add(new DisposableResource(JsValue.Undefined, onDispose));
        return JsValue.Undefined;
    }

    private static JsValue Move(IBuiltinContext ctx, JsValue thisValue, ObjectHandle prototypeHandle)
    {
        var stack = RequireDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var newStack = new DisposableStackObject
        {
            State = DisposableState.Pending,
            Resources = stack.Resources,
        };
        newStack.SetPrototype(prototypeHandle);
        var newHandle = ctx.Heap.AllocateObject(newStack, AllocationSite.Current());

        stack.Resources = new List<DisposableResource>();
        stack.State = DisposableState.Disposed;

        return JsValue.FromObject(newHandle);
    }

    private static JsValue Dispose(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var stack = RequireDisposableStack(ctx, thisValue);
        if (stack.State == DisposableState.Disposed)
        {
            return JsValue.Undefined;
        }

        stack.State = DisposableState.Disposed;
        JsValue pendingError = JsValue.Undefined;
        for (var i = stack.Resources.Count - 1; i >= 0; i--)
        {
            var resource = stack.Resources[i];
            try
            {
                if (resource.Method.Tag != JsValueTag.Undefined)
                {
                    _ = ctx.CallFunction(resource.Method, Array.Empty<JsValue>(), resource.Value);
                }
            }
            catch (JsThrownException ex)
            {
                pendingError = pendingError.Tag == JsValueTag.Undefined
                    ? ex.Value
                    : CreateSuppressedError(ctx, ex.Value, pendingError);
            }
        }

        stack.Resources.Clear();
        if (pendingError.Tag != JsValueTag.Undefined)
        {
            throw new JsThrownException(pendingError);
        }

        return JsValue.Undefined;
    }

    private static JsValue CreateSuppressedError(IBuiltinContext ctx, JsValue error, JsValue suppressed)
    {
        var ctorHandle = ctx.MaterializeSuppressedErrorConstructor();
        var ctor = ctx.Heap.GetObject(ctorHandle);
        if (!ctx.TryGetPropertyValue(ctor, JsValue.FromObject(ctorHandle), "prototype", out var prototype) ||
            prototype.Tag != JsValueTag.Object)
        {
            throw new InvalidOperationException("SuppressedError.prototype is unavailable.");
        }

        var suppressedError = new JsObject();
        suppressedError.ToStringTagSlot = BuiltinTagSlot.Error;
        suppressedError.SetPrototype(prototype.AsObjectHandle());
        _ = suppressedError.DefineOwnProperty(
            "error",
            new JsPropertyDescriptor(error, Writable: true, Enumerable: false, Configurable: true));
        _ = suppressedError.DefineOwnProperty(
            "suppressed",
            new JsPropertyDescriptor(suppressed, Writable: true, Enumerable: false, Configurable: true));
        _ = suppressedError.DefineOwnProperty(
            "stack",
            new JsPropertyDescriptor(
                JsValue.FromString(ctx.CaptureCallStack("SuppressedError", string.Empty)),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        return JsValue.FromObject(ctx.Heap.AllocateObject(suppressedError, AllocationSite.Current()));
    }
}
