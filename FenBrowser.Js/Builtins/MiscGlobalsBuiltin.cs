using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

public sealed class MiscGlobalsBuiltin : IBuiltinModule
{
    public string Name => "MiscGlobals";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var heap = context.Heap;

        var bindings = new List<BuiltinBinding>(8);

        // eval
        var evalFn = new NativeFunctionObject("eval", (_, args) => context.Eval(args), length: 1);
        var evalHandle = heap.AllocateObject(evalFn, AllocationSite.Current());
        heap.PushRoot(evalHandle);
        bindings.Add(BuiltinBinding.NonEnumerable("eval", JsValue.FromObject(evalHandle)));

        // queueMicrotask(callback) takes a Web IDL VoidFunction, so the argument is
        // accepted according to ECMAScript IsCallable rather than by checking only
        // the two concrete function classes. Bound functions and callable proxies are
        // function objects too and must not be rejected just because their call path
        // is exotic.
        var qmFn = new NativeFunctionObject("queueMicrotask", (_, args) =>
        {
            if (args.Count == 0 || !IsCallable(context, args[0]))
                throw new JsThrownException(context.CreateTypeError("queueMicrotask: argument must be callable."));
            context.EnqueueMicrotask(args[0]);
            return JsValue.Undefined;
        }, length: 1);
        var qmHandle = heap.AllocateObject(qmFn, AllocationSite.Current());
        heap.PushRoot(qmHandle);
        bindings.Add(BuiltinBinding.NonEnumerable("queueMicrotask", JsValue.FromObject(qmHandle)));

        var printFn = new NativeFunctionObject("print", (_, _) => JsValue.Undefined, length: 0);
        var printHandle = heap.AllocateObject(printFn, AllocationSite.Current());
        heap.PushRoot(printHandle);
        bindings.Add(BuiltinBinding.NonEnumerable("print", JsValue.FromObject(printHandle)));

        // WeakRef, FinalizationRegistry, structuredClone — materialize from interpreter
        bindings.Add(BuiltinBinding.NonEnumerable("WeakRef", JsValue.FromObject(context.MaterializeWeakRefConstructor())));
        bindings.Add(BuiltinBinding.NonEnumerable("FinalizationRegistry", JsValue.FromObject(context.MaterializeFinalizationRegistryConstructor())));
        bindings.Add(BuiltinBinding.NonEnumerable("structuredClone", JsValue.FromObject(context.MaterializeStructuredCloneFunction())));

        // AsyncFunction, AsyncGeneratorFunction constructors
        bindings.Add(BuiltinBinding.NonEnumerable("AsyncFunction", JsValue.FromObject(context.MaterializeAsyncFunctionConstructor())));
        bindings.Add(BuiltinBinding.NonEnumerable("AsyncGeneratorFunction", JsValue.FromObject(context.MaterializeAsyncGeneratorFunctionConstructor())));

        return bindings;
    }

    private static bool IsCallable(IBuiltinContext context, JsValue value)
    {
        // Bound/proxy call targets are immutable and point to already-created
        // objects, so the chain is normally acyclic. Keep a visited set anyway: it
        // makes this boundary robust against a malformed host-created object graph
        // without turning argument validation into unbounded recursion.
        HashSet<long>? visited = null;

        while (value.Tag == JsValueTag.Object)
        {
            var handle = value.AsObjectHandle();
            var packedHandle = handle.ToInt64();
            visited ??= new HashSet<long>();
            if (!visited.Add(packedHandle))
                return false;

            var obj = context.Heap.GetObject(handle);
            switch (obj)
            {
                case NativeFunctionObject:
                case JsFunctionObject:
                    return true;
                case BoundFunctionObject bound:
                    value = bound.TargetFunction;
                    continue;
                case ProxyObject proxy:
                    value = JsValue.FromObject(proxy.TargetHandle);
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }
}