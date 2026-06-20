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

        var bindings = new List<BuiltinBinding>(5);

        // eval
        var evalFn = new NativeFunctionObject("eval", (_, args) => context.Eval(args), length: 1);
        var evalHandle = heap.AllocateObject(evalFn, AllocationSite.Current());
        heap.PushRoot(evalHandle);
        bindings.Add(BuiltinBinding.NonEnumerable("eval", JsValue.FromObject(evalHandle)));

        // queueMicrotask
        var qmFn = new NativeFunctionObject("queueMicrotask", (_, args) =>
        {
            if (args.Count == 0 || args[0].Tag != JsValueTag.Object ||
                context.Heap.GetObject(args[0].AsObjectHandle()) is not (JsFunctionObject or NativeFunctionObject))
                throw new JsThrownException(context.CreateTypeError("queueMicrotask: argument must be callable."));
            context.EnqueueMicrotask(args[0]);
            return JsValue.Undefined;
        }, length: 1);
        var qmHandle = heap.AllocateObject(qmFn, AllocationSite.Current());
        heap.PushRoot(qmHandle);
        bindings.Add(BuiltinBinding.NonEnumerable("queueMicrotask", JsValue.FromObject(qmHandle)));

        // WeakRef, FinalizationRegistry, structuredClone — materialize from interpreter
        bindings.Add(BuiltinBinding.NonEnumerable("WeakRef", JsValue.FromObject(context.MaterializeWeakRefConstructor())));
        bindings.Add(BuiltinBinding.NonEnumerable("FinalizationRegistry", JsValue.FromObject(context.MaterializeFinalizationRegistryConstructor())));
        bindings.Add(BuiltinBinding.NonEnumerable("structuredClone", JsValue.FromObject(context.MaterializeStructuredCloneFunction())));

        // AsyncFunction, AsyncGeneratorFunction constructors
        bindings.Add(BuiltinBinding.NonEnumerable("AsyncFunction", JsValue.FromObject(context.MaterializeAsyncFunctionConstructor())));
        bindings.Add(BuiltinBinding.NonEnumerable("AsyncGeneratorFunction", JsValue.FromObject(context.MaterializeAsyncGeneratorFunctionConstructor())));

        return bindings;
    }
}
