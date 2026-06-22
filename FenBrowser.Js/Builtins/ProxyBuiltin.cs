using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 28.2 The Proxy Constructor.
[EcmaSpecReference("28.2")]
public sealed class ProxyBuiltin : IBuiltinModule
{
    public string Name => "Proxy";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var heap = context.Heap;

        JsValue CreateProxy(IReadOnlyList<JsValue> args)
        {
            if (args.Count < 2 || args[0].Tag != JsValueTag.Object)
                throw new JsThrownException(context.CreateTypeError("Proxy: target must be an object."));
            if (args[1].Tag != JsValueTag.Object)
                throw new JsThrownException(context.CreateTypeError("Proxy: handler must be an object."));
            var targetHandle = args[0].AsObjectHandle();
            var handlerHandle = args[1].AsObjectHandle();
            var target = heap.GetObject(targetHandle);
            var proxy = new ProxyObject(targetHandle, handlerHandle);
            proxy.SetPrototype(target.PrototypeHandle);
            var proxyHandle = heap.AllocateObject(proxy, AllocationSite.Current());
            heap.WriteBarrier(proxyHandle, targetHandle);
            heap.WriteBarrier(proxyHandle, handlerHandle);
            if (target.PrototypeHandle is { } targetProto)
            {
                heap.WriteBarrier(proxyHandle, targetProto);
            }
            return JsValue.FromObject(proxyHandle);
        }

        var constructor = new NativeFunctionObject(
            "Proxy",
            call: (_, _) => throw new JsThrownException(context.CreateTypeError("Proxy must be called with 'new'.")),
            construct: args => CreateProxy(args),
            length: 2);
        var functionCtorHandle = context.MaterializeFunctionConstructor();
        var functionCtor = heap.GetObject(functionCtorHandle);
        if (context.TryGetPropertyValue(functionCtor, JsValue.FromObject(functionCtorHandle), "prototype", out var functionPrototypeValue) &&
            functionPrototypeValue.Tag == JsValueTag.Object)
        {
            constructor.SetPrototype(functionPrototypeValue.AsObjectHandle());
        }
        var constructorHandle = heap.AllocateObject(constructor, AllocationSite.Current());
        heap.PushRoot(constructorHandle);

        // 28.2.2.1 Proxy.revocable(target, handler)
        var revocableFn = new NativeFunctionObject("revocable", (_, args) =>
        {
            if (args.Count < 2 || args[0].Tag != JsValueTag.Object)
                throw new JsThrownException(context.CreateTypeError("Proxy.revocable: target must be an object."));
            if (args[1].Tag != JsValueTag.Object)
                throw new JsThrownException(context.CreateTypeError("Proxy.revocable: handler must be an object."));
            var targetHandle = args[0].AsObjectHandle();
            var handlerHandle = args[1].AsObjectHandle();
            var proxy = new ProxyObject(targetHandle, handlerHandle);
            var proxyHandle = heap.AllocateObject(proxy, AllocationSite.Current());
            heap.WriteBarrier(proxyHandle, targetHandle);
            heap.WriteBarrier(proxyHandle, handlerHandle);

            var revokeFn = new NativeFunctionObject("revoke", (_, _) =>
            {
                proxy.Revoke();
                return JsValue.Undefined;
            }, length: 0);
            var revokeHandle = heap.AllocateObject(revokeFn, AllocationSite.Current());

            var result = new JsObject();
            var resultHandle = heap.AllocateObject(result, AllocationSite.Current());
            result.DefineOwnProperty(
                "proxy",
                new JsPropertyDescriptor(JsValue.FromObject(proxyHandle),
                    Writable: true, Enumerable: true, Configurable: true));
            result.DefineOwnProperty(
                "revoke",
                new JsPropertyDescriptor(JsValue.FromObject(revokeHandle),
                    Writable: true, Enumerable: true, Configurable: true));
            heap.WriteBarrier(resultHandle, proxyHandle);
            heap.WriteBarrier(resultHandle, revokeHandle);
            return JsValue.FromObject(resultHandle);
        }, length: 2);

        var revocableHandle = heap.AllocateObject(revocableFn, AllocationSite.Current());
        constructor.DefineOwnProperty(
            "revocable",
            new JsPropertyDescriptor(
                JsValue.FromObject(revocableHandle),
                Writable: true,
                Enumerable: false,
                Configurable: true));
        heap.WriteBarrier(constructorHandle, revocableHandle);

        return new[]
        {
            BuiltinBinding.NonEnumerable("Proxy", JsValue.FromObject(constructorHandle))
        };
    }
}
