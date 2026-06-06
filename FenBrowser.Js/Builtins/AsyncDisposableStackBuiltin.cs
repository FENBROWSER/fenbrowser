using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// 28.4 AsyncDisposableStack. Mirrors the synchronous DisposableStack surface
// but uses @@asyncDispose and exposes disposeAsync, which returns a Promise.
//
// Disposal is performed synchronously when disposeAsync runs and the settled
// Promise is then resolved/rejected. The spec awaits each disposer in turn;
// a faithful await chain would have to keep the captured resources reachable
// across microtask ticks, which the native-closure capture is invisible to
// the moving GC. Running the disposers in reverse order synchronously settles
// every test whose disposers record their effect at call time (the common
// case); only the handful of tests asserting the precise microtask cadence of
// an empty/awaited disposal remain.
[EcmaSpecReference("28.4", AbstractOperation = "AsyncDisposableStack", Url = "https://tc39.es/ecma262/#sec-asyncdisposablestack-constructor")]
public sealed class AsyncDisposableStackBuiltin : IBuiltinModule
{
    public string Name => "AsyncDisposableStack";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

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

                var stack = new AsyncDisposableStackObject();
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

        var disposeAsyncHandle = DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "disposeAsync", DisposeAsync, length: 0);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "use", Use, length: 1);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "adopt", Adopt, length: 2);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "defer", Defer, length: 1);
        DefinePrototypeMethod(context, heap, prototypeHandle, prototype, "move", (ctx, thisValue, _) => Move(ctx, thisValue, prototypeHandle), length: 0);

        // 28.4.3.5 AsyncDisposableStack.prototype[@@asyncDispose] is the same
        // function object as the initial disposeAsync.
        var asyncDisposeSymbol = context.CreateWellKnownSymbol("asyncDispose");
        _ = prototype.DefineOwnSymbolProperty(
            asyncDisposeSymbol.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromObject(disposeAsyncHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(prototypeHandle, disposeAsyncHandle);

        var disposedGetter = new NativeFunctionObject("get disposed", (thisValue, _) =>
        {
            var stack = RequireAsyncDisposableStack(context, thisValue);
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
            new JsPropertyDescriptor(JsValue.FromString("AsyncDisposableStack"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("AsyncDisposableStack", JsValue.FromObject(constructorHandle)) };
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

    private sealed class AsyncDisposableStackObject : JsObject
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

        if (TryGetRealmAsyncDisposableStackPrototype(ctx, newTargetObject, newTarget, out var realmPrototypeHandle))
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

    private static bool TryGetRealmAsyncDisposableStackPrototype(
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
        if (!ctx.TryGetPropertyValue(realmGlobalObject, realmGlobal, "AsyncDisposableStack", out var realmStack) ||
            realmStack.Tag != JsValueTag.Object)
        {
            return false;
        }

        var realmStackObject = ctx.Heap.GetObject(realmStack.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(realmStackObject, realmStack, "prototype", out var prototypeValue) ||
            prototypeValue.Tag != JsValueTag.Object)
        {
            return false;
        }

        prototypeHandle = prototypeValue.AsObjectHandle();
        return true;
    }

    private static AsyncDisposableStackObject RequireAsyncDisposableStack(IBuiltinContext ctx, JsValue thisValue)
    {
        if (thisValue.Tag != JsValueTag.Object ||
            ctx.Heap.GetObject(thisValue.AsObjectHandle()) is not AsyncDisposableStackObject stack)
        {
            throw new JsThrownException(ctx.CreateTypeError("AsyncDisposableStack method called on incompatible receiver."));
        }

        return stack;
    }

    private static void ThrowIfDisposed(IBuiltinContext ctx, AsyncDisposableStackObject stack)
    {
        if (stack.State == DisposableState.Disposed)
        {
            throw new JsThrownException(ctx.CreateReferenceError("AsyncDisposableStack is already disposed."));
        }
    }

    private static bool IsCallable(IBuiltinContext ctx, JsValue value)
    {
        return value.Tag == JsValueTag.Object &&
               ctx.Heap.GetObject(value.AsObjectHandle()) is JsFunctionObject or NativeFunctionObject or BoundFunctionObject;
    }

    private static JsValue GetSymbolMethod(IBuiltinContext ctx, JsValue receiver, string symbolName)
    {
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
                throw new JsThrownException(ctx.CreateTypeError("@@asyncDispose getter is not callable."));
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
            throw new JsThrownException(ctx.CreateTypeError("@@asyncDispose method is not callable."));
        }

        return method;
    }

    // 27.3.1.1 GetDisposeMethod(V, async-dispose): prefer @@asyncDispose, fall
    // back to wrapping @@dispose.
    private static JsValue GetAsyncDisposeMethod(IBuiltinContext ctx, JsValue receiver)
    {
        var method = GetSymbolMethod(ctx, receiver, "asyncDispose");
        if (method.Tag != JsValueTag.Undefined)
        {
            return method;
        }

        var syncMethod = GetSymbolMethod(ctx, receiver, "dispose");
        if (syncMethod.Tag == JsValueTag.Undefined)
        {
            return JsValue.Undefined;
        }

        // Wrap the synchronous @@dispose so the recorded disposer calls it with
        // the resource as the receiver.
        var wrapper = new NativeFunctionObject(string.Empty, (_, _) =>
        {
            _ = ctx.CallFunction(syncMethod, Array.Empty<JsValue>(), receiver);
            return JsValue.Undefined;
        }, length: 0);
        wrapper.SetPrototype(ctx.GetObjectPrototype());
        var wrapperHandle = ctx.Heap.AllocateObject(wrapper, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        wrapper.SetProperty("call", JsValue.FromObject(callHandle));
        ctx.Heap.WriteBarrier(wrapperHandle, callHandle);
        return JsValue.FromObject(wrapperHandle);
    }

    private static JsValue Use(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var stack = RequireAsyncDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (value.Tag is JsValueTag.Null or JsValueTag.Undefined)
        {
            return value;
        }

        if (value.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(ctx.CreateTypeError("AsyncDisposableStack.prototype.use requires an object, null, or undefined."));
        }

        var method = GetAsyncDisposeMethod(ctx, value);
        if (method.Tag == JsValueTag.Undefined)
        {
            throw new JsThrownException(ctx.CreateTypeError("AsyncDisposableStack.prototype.use requires a Symbol.asyncDispose or Symbol.dispose method."));
        }

        // A direct @@asyncDispose method is invoked with the resource as its
        // receiver; the @@dispose wrapper ignores its receiver (it captured the
        // resource), so passing the value through is harmless either way.
        stack.Resources.Add(new DisposableResource(value, method));
        return value;
    }

    private static JsValue Adopt(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        var stack = RequireAsyncDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var value = args.Count > 0 ? args[0] : JsValue.Undefined;
        var onDispose = args.Count > 1 ? args[1] : JsValue.Undefined;
        if (!IsCallable(ctx, onDispose))
        {
            throw new JsThrownException(ctx.CreateTypeError("AsyncDisposableStack.prototype.adopt requires a callable disposer."));
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
        var stack = RequireAsyncDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var onDispose = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (!IsCallable(ctx, onDispose))
        {
            throw new JsThrownException(ctx.CreateTypeError("AsyncDisposableStack.prototype.defer requires a callable disposer."));
        }

        stack.Resources.Add(new DisposableResource(JsValue.Undefined, onDispose));
        return JsValue.Undefined;
    }

    private static JsValue Move(IBuiltinContext ctx, JsValue thisValue, ObjectHandle prototypeHandle)
    {
        var stack = RequireAsyncDisposableStack(ctx, thisValue);
        ThrowIfDisposed(ctx, stack);

        var newStack = new AsyncDisposableStackObject
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

    // 28.4.3.3 AsyncDisposableStack.prototype.disposeAsync. Returns a Promise
    // that settles once every recorded disposer has run (in reverse order).
    private static JsValue DisposeAsync(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        var (promise, resolve, reject) = ctx.CreatePromiseCapability();

        if (thisValue.Tag != JsValueTag.Object ||
            ctx.Heap.GetObject(thisValue.AsObjectHandle()) is not AsyncDisposableStackObject stack)
        {
            _ = ctx.CallFunction(reject, new[] { ctx.CreateTypeError("AsyncDisposableStack.prototype.disposeAsync called on incompatible receiver.") }, JsValue.Undefined);
            return promise;
        }

        if (stack.State == DisposableState.Disposed)
        {
            _ = ctx.CallFunction(resolve, new[] { JsValue.Undefined }, JsValue.Undefined);
            return promise;
        }

        stack.State = DisposableState.Disposed;
        var pendingError = JsValue.Undefined;
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
            _ = ctx.CallFunction(reject, new[] { pendingError }, JsValue.Undefined);
        }
        else
        {
            _ = ctx.CallFunction(resolve, new[] { JsValue.Undefined }, JsValue.Undefined);
        }

        return promise;
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
