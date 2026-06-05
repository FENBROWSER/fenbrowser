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

        // Store Error.prototype in a place subclasses can reach
        var errorProto = errorProtoHandle;

        // Subclasses chain to Error.prototype
        bindings.Add(BuiltinBinding.NonEnumerable("TypeError", JsValue.FromObject(CreateNativeError(context, errorProto, "TypeError"))));
        bindings.Add(BuiltinBinding.NonEnumerable("RangeError", JsValue.FromObject(CreateNativeError(context, errorProto, "RangeError"))));
        bindings.Add(BuiltinBinding.NonEnumerable("SyntaxError", JsValue.FromObject(CreateNativeError(context, errorProto, "SyntaxError"))));
        bindings.Add(BuiltinBinding.NonEnumerable("ReferenceError", JsValue.FromObject(CreateNativeError(context, errorProto, "ReferenceError"))));
        bindings.Add(BuiltinBinding.NonEnumerable("EvalError", JsValue.FromObject(CreateNativeError(context, errorProto, "EvalError"))));
        bindings.Add(BuiltinBinding.NonEnumerable("URIError", JsValue.FromObject(CreateNativeError(context, errorProto, "URIError"))));
        bindings.Add(BuiltinBinding.NonEnumerable("SuppressedError", JsValue.FromObject(CreateNativeError(context, errorProto, "SuppressedError", length: 3))));

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

    private static ObjectHandle CreateNativeError(IBuiltinContext ctx, ObjectHandle errorProtoHandle, string name, int length = 1)
    {
        var protoHandle = CreateErrorPrototype(ctx, errorProtoHandle, name);
        return CreateErrorConstructor(ctx, protoHandle, name, length);
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
        var err = new JsObject();
        err.ToStringTagSlot = BuiltinTagSlot.Error;
        err.SetPrototype(protoHandle);
        // ECMA-262 20.5.1.1 Error ( message ): only define `message` when the
        // argument is not undefined, with attributes { w:t, e:f, c:t }. `name`
        // is inherited from the prototype, not installed on each instance.
        // `stack` is a host extension; keep it non-enumerable to match Chromium/SM.
        string msg = string.Empty;
        bool hasMessage = args.Count > 0 && args[0].Tag != JsValueTag.Undefined;
        if (hasMessage)
        {
            msg = ctx.ToStringValue(args[0]);
            _ = err.DefineOwnProperty("message",
                new Objects.JsPropertyDescriptor(JsValue.FromString(msg), Writable: true, Enumerable: false, Configurable: true));
        }
        _ = err.DefineOwnProperty("stack",
            new Objects.JsPropertyDescriptor(JsValue.FromString(ctx.CaptureCallStack(name, msg)), Writable: true, Enumerable: false, Configurable: true));
        return JsValue.FromObject(ctx.Heap.AllocateObject(err, AllocationSite.Current()));
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
