using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

[EcmaSpecReference("20.5", AbstractOperation = "ErrorObjects", Url = "https://tc39.es/ecma262/#sec-error-objects")]
public sealed class ErrorBuiltins : IBuiltinModule
{
    public string Name => "ErrorBuiltins";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var heap = context.Heap;
        var bindings = new List<BuiltinBinding>(8);

        // Error first — all subclasses chain to Error.prototype
        var errorProtoHandle = CreateErrorPrototype(context, null, "Error");
        var errorCtor = CreateErrorConstructor(context, errorProtoHandle, "Error");
        bindings.Add(BuiltinBinding.NonEnumerable("Error", JsValue.FromObject(errorCtor)));

        // ECMA-262 (ES2025) Error.isError(arg): true iff arg has an [[ErrorData]]
        // internal slot, which FenJS brands as the Error tag slot. A fake error
        // (Error.prototype + @@toStringTag but no slot) correctly returns false.
        DefineProtoMethod(context, heap, errorCtor, heap.GetObject(errorCtor), "isError", IsError, length: 1);

        // Store Error.prototype in a place subclasses can reach
        var errorProto = errorProtoHandle;

        // Subclasses chain to Error.prototype, AND the constructor's [[Prototype]]
        // is %Error% (ECMA-262 19.5.1).
        bindings.Add(BuiltinBinding.NonEnumerable("TypeError", JsValue.FromObject(CreateNativeError(context, errorProto, "TypeError", errorCtor))));
        bindings.Add(BuiltinBinding.NonEnumerable("RangeError", JsValue.FromObject(CreateNativeError(context, errorProto, "RangeError", errorCtor))));
        bindings.Add(BuiltinBinding.NonEnumerable("SyntaxError", JsValue.FromObject(CreateNativeError(context, errorProto, "SyntaxError", errorCtor))));
        bindings.Add(BuiltinBinding.NonEnumerable("ReferenceError", JsValue.FromObject(CreateNativeError(context, errorProto, "ReferenceError", errorCtor))));
        bindings.Add(BuiltinBinding.NonEnumerable("EvalError", JsValue.FromObject(CreateNativeError(context, errorProto, "EvalError", errorCtor))));
        bindings.Add(BuiltinBinding.NonEnumerable("URIError", JsValue.FromObject(CreateNativeError(context, errorProto, "URIError", errorCtor))));
        // ECMA-262 20.5.12 SuppressedError(error, suppressed, message[, options])
        // Message is the THIRD argument, not the first. Use a dedicated constructor.
        {
            var protoHandle = CreateErrorPrototype(context, errorProto, "SuppressedError");
            var capturedProto = protoHandle;
            var capturedCtx = context;
            var heap2 = context.Heap;
            // ECMA-262 20.5.12 SuppressedError(error, suppressed, message):
            //   message (if not undefined), then error, then suppressed as
            //   non-enumerable own data properties.
            // ECMA-262 20.5.12 SuppressedError(error, suppressed, message):
            //   Properties must be in order: message, error, suppressed.
            //   We build the object manually (not via BuildErrorCore) so stack
            //   comes last, satisfying the test's index-based assertions.
            JsValue BuildSuppressedError(bool asConstruct, IReadOnlyList<JsValue> args)
            {
                var err = new JsObject();
                err.ToStringTagSlot = BuiltinTagSlot.Error;
                err.SetPrototype(capturedProto);
                string msgStr = string.Empty;
                if (args.Count > 2 && args[2].Tag != JsValueTag.Undefined)
                {
                    msgStr = context.ToStringValue(args[2]);
                    err.DefineOwnProperty("message",
                        new Objects.JsPropertyDescriptor(JsValue.FromString(msgStr), Writable: true, Enumerable: false, Configurable: true));
                }
                err.DefineOwnProperty("error",
                    new Objects.JsPropertyDescriptor(args.Count > 0 ? args[0] : JsValue.Undefined, Writable: true, Enumerable: false, Configurable: true));
                err.DefineOwnProperty("suppressed",
                    new Objects.JsPropertyDescriptor(args.Count > 1 ? args[1] : JsValue.Undefined, Writable: true, Enumerable: false, Configurable: true));
                // stack is provided by Error.prototype.stack accessor
                return JsValue.FromObject(heap2.AllocateObject(err, AllocationSite.Current()));
            }
            var suppressedErrorCtor = new NativeFunctionObject(
                "SuppressedError",
                (thisVal, args) => BuildSuppressedError(asConstruct: false, args),
                args => BuildSuppressedError(asConstruct: true, args),
                length: 3);
            suppressedErrorCtor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(protoHandle), Writable: false, Enumerable: false, Configurable: false));
            var ctorHandle = heap2.AllocateObject(suppressedErrorCtor, AllocationSite.Current());
            heap2.PushRoot(ctorHandle);
            heap2.WriteBarrier(ctorHandle, protoHandle);
            var proto = heap2.GetObject(protoHandle);
            _ = proto.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(ctorHandle), Writable: true, Enumerable: false, Configurable: true));
            heap2.WriteBarrier(protoHandle, ctorHandle);
            // ECMA-262 19.5.1: NativeError constructors have [[Prototype]] = %Error%.
            heap2.GetObject(ctorHandle).SetPrototype(errorCtor);
            bindings.Add(BuiltinBinding.NonEnumerable("SuppressedError", JsValue.FromObject(ctorHandle)));
        }

        // Error.prototype.toString
        DefineProtoMethod(context, heap, errorProto, heap.GetObject(errorProto), "toString", ErrorToString);

        return bindings;
    }

    private static ObjectHandle CreateErrorPrototype(IBuiltinContext ctx, ObjectHandle? superProto, string name)
    {
        var prototype = new JsObject();
        if (superProto is { } sp) prototype.SetPrototype(sp);
        else prototype.SetPrototype(ctx.GetObjectPrototype());
        var handle = ctx.Heap.AllocateObject(prototype, AllocationSite.Current());
        ctx.Heap.PushRoot(handle);
        prototype.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString(name), Writable: true, Enumerable: false, Configurable: true));
        prototype.DefineOwnProperty("message",
            new JsPropertyDescriptor(JsValue.FromString(string.Empty), Writable: true, Enumerable: false, Configurable: true));

        // ECMA-262 20.5.3.1 get/set Error.prototype.stack — only on %Error.prototype%,
        // NOT on subclass prototypes (they inherit it via the prototype chain).
        if (superProto is null)
        {
            var capturedCtx = ctx;
            var stackGetter = new NativeFunctionObject("get stack", (thisValue, _) =>
            {
                var msg = string.Empty;
                if (thisValue.Tag == JsValueTag.Object &&
                    ctx.Heap.GetObject(thisValue.AsObjectHandle()).TryGetOwnProperty("message", out var msgDesc))
                    msg = msgDesc.Value.Tag == JsValueTag.String ? msgDesc.Value.AsString() : string.Empty;
                return JsValue.FromString(capturedCtx.CaptureCallStack(name, msg));
            }, length: 0);
            var stackSetter = new NativeFunctionObject("set stack", (thisValue, args) =>
            {
                if (thisValue.Tag != JsValueTag.Object) return JsValue.Undefined;
                var val = args.Count > 0 ? args[0] : JsValue.Undefined;
                ctx.Heap.GetObject(thisValue.AsObjectHandle()).DefineOwnProperty("stack",
                    new JsPropertyDescriptor(val, Writable: true, Enumerable: true, Configurable: true));
                return JsValue.Undefined;
            }, length: 1);
            var getterHandle = ctx.Heap.AllocateObject(stackGetter, AllocationSite.Current());
            var setterHandle = ctx.Heap.AllocateObject(stackSetter, AllocationSite.Current());
            prototype.DefineOwnProperty("stack",
                JsPropertyDescriptor.Accessor(JsValue.FromObject(getterHandle), JsValue.FromObject(setterHandle),
                    Enumerable: false, Configurable: true));
            ctx.Heap.WriteBarrier(handle, getterHandle);
            ctx.Heap.WriteBarrier(handle, setterHandle);
        }

        return handle;
    }

    private static ObjectHandle CreateErrorConstructor(IBuiltinContext ctx, ObjectHandle protoHandle, string name)
    {
        var capturedProto = protoHandle;
        var capturedCtx = ctx;
        var heap = ctx.Heap;
        var constructor = new NativeFunctionObject(
            name,
            (_, args) => BuildError(capturedCtx, capturedProto, name, args),
            args => BuildError(capturedCtx, capturedProto, name, args),
            length: 1);
        constructor.SetPrototype(ctx.GetFunctionPrototype());
        constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(protoHandle), Writable: false, Enumerable: false, Configurable: false));
        var ctorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(ctorHandle);
        heap.WriteBarrier(ctorHandle, protoHandle);

        // Keep Error instances aligned with test262 assert.throws semantics:
        // thrown.constructor must resolve to the specific native error ctor.
        var proto = heap.GetObject(protoHandle);
        _ = proto.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(
                JsValue.FromObject(ctorHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        heap.WriteBarrier(protoHandle, ctorHandle);
        return ctorHandle;
    }

    private static ObjectHandle CreateNativeError(IBuiltinContext ctx, ObjectHandle errorProtoHandle, string name, ObjectHandle errorCtorHandle, int length = 1)
    {
        var protoHandle = CreateErrorPrototype(ctx, errorProtoHandle, name);
        var ctorHandle = CreateErrorConstructor(ctx, protoHandle, name, length);
        // ECMA-262 19.5.1: NativeError constructors have [[Prototype]] = %Error%.
        ctx.Heap.GetObject(ctorHandle).SetPrototype(errorCtorHandle);
        return ctorHandle;
    }

    private static ObjectHandle CreateErrorConstructor(IBuiltinContext ctx, ObjectHandle protoHandle, string name, int length = 1)
    {
        var capturedProto = protoHandle;
        var capturedCtx = ctx;
        var heap = ctx.Heap;
        var constructor = new NativeFunctionObject(
            name,
            (_, args) => BuildError(capturedCtx, capturedProto, name, args),
            args => BuildError(capturedCtx, capturedProto, name, args),
            length: length);
        constructor.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(protoHandle), Writable: false, Enumerable: false, Configurable: false));
        var ctorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(ctorHandle);
        heap.WriteBarrier(ctorHandle, protoHandle);
        var proto = heap.GetObject(protoHandle);
        _ = proto.DefineOwnProperty(
            "constructor",
            new JsPropertyDescriptor(JsValue.FromObject(ctorHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(protoHandle, ctorHandle);
        return ctorHandle;
    }

    private static JsValue BuildError(IBuiltinContext ctx, ObjectHandle protoHandle, string name, IReadOnlyList<JsValue> args)
    {
        var messageArg = args.Count > 0 ? args[0] : JsValue.Undefined;
        var err = BuildErrorCore(ctx, protoHandle, name, messageArg);
        // ECMA-262 20.5.8.1 InstallErrorCause ( O, options )
        if (args.Count > 1 && args[1].Tag == JsValueTag.Object)
        {
            var errObj = ctx.Heap.GetObject(err.AsObjectHandle());
            var optionsObj = ctx.Heap.GetObject(args[1].AsObjectHandle());
            if (ctx.TryGetPropertyValue(optionsObj, args[1], "cause", out var causeValue))
            {
                _ = errObj.DefineOwnProperty("cause",
                    new Objects.JsPropertyDescriptor(causeValue, Writable: true, Enumerable: false, Configurable: true));
            }
        }
        return err;
    }

    // Core error object construction: sets [[ErrorData]] tag, prototype, message
    // (when not undefined), and stack. Does NOT handle options.cause or extra
    // SuppressedError properties — callers add those after.
    internal static JsValue BuildErrorCore(IBuiltinContext ctx, ObjectHandle protoHandle, string name, JsValue messageArg)
    {
        var err = new JsObject();
        err.ToStringTagSlot = BuiltinTagSlot.Error;
        err.SetPrototype(protoHandle);
        string msg = string.Empty;
        bool hasMessage = messageArg.Tag != JsValueTag.Undefined;
        if (hasMessage)
        {
            msg = ctx.ToStringValue(messageArg);
            _ = err.DefineOwnProperty("message",
                new Objects.JsPropertyDescriptor(JsValue.FromString(msg), Writable: true, Enumerable: false, Configurable: true));
        }
        // ECMA-262: stack is provided by Error.prototype.stack accessor (getter
        // captures call stack, setter creates own property). Don't set an own
        // data property here — it would shadow the prototype accessor.
        return JsValue.FromObject(ctx.Heap.AllocateObject(err, AllocationSite.Current()));
    }

    private static JsValue IsError(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = thisValue;
        var arg = args.Count > 0 ? args[0] : JsValue.Undefined;
        if (arg.Tag != JsValueTag.Object) return JsValue.FromBoolean(false);
        var obj = ctx.Heap.GetObject(arg.AsObjectHandle());
        return JsValue.FromBoolean(obj.ToStringTagSlot == BuiltinTagSlot.Error);
    }

    private static JsValue ErrorToString(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args)
    {
        _ = args;
        if (thisValue.Tag != JsValueTag.Object)
            throw new JsThrownException(ctx.CreateTypeError("Error.prototype.toString called on non-object."));
        var obj = ctx.Heap.GetObject(thisValue.AsObjectHandle());
        var name = "Error";
        if (ctx.TryGetPropertyValue(obj, thisValue, "name", out var nameValue) && nameValue.Tag != JsValueTag.Undefined)
            name = ctx.ToStringValue(nameValue);
        var message = string.Empty;
        if (ctx.TryGetPropertyValue(obj, thisValue, "message", out var messageValue) && messageValue.Tag != JsValueTag.Undefined)
            message = ctx.ToStringValue(messageValue);
        if (name.Length == 0) return JsValue.FromString(message);
        if (message.Length == 0) return JsValue.FromString(name);
        return JsValue.FromString(name + ": " + message);
    }

    private delegate JsValue ProtoMethod(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args);

    private static void DefineProtoMethod(IBuiltinContext ctx, JsHeap heap, ObjectHandle protoHandle, JsObject proto, string name, ProtoMethod method, int length = 0)
    {
        var captured = ctx;
        var fn = new NativeFunctionObject(name, (thisValue, args) => method(captured, thisValue, args), length: length);
        var fnHandle = heap.AllocateObject(fn, AllocationSite.Current());
        var callHandle = ctx.GetFunctionCallMethod();
        fn.SetPrototype(ctx.GetObjectPrototype());
        fn.SetProperty("call", JsValue.FromObject(callHandle));
        heap.WriteBarrier(fnHandle, callHandle);
        proto.DefineOwnProperty(name, new JsPropertyDescriptor(JsValue.FromObject(fnHandle), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(protoHandle, fnHandle);
    }
}
