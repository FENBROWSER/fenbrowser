using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// ECMA-262 27.5 — Generator.prototype methods.
public sealed class GeneratorBuiltin : IBuiltinModule
{
    public string Name => "GeneratorPrototype";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var heap = context.Heap;
        var proto = new JsObject();
        proto.SetPrototype(context.GetObjectPrototype());
        var ph = heap.AllocateObject(proto, AllocationSite.Current());
        heap.PushRoot(ph);

        var captured = context;
        DefineMethod(heap, captured, ph, proto, "next", (ctx, tv, args) =>
        {
            if (tv.Tag != JsValueTag.Object || ctx.Heap.GetObject(tv.AsObjectHandle()) is not GeneratorObject g)
                throw new JsThrownException(ctx.CreateTypeError("Generator.prototype.next: receiver is not a generator"));
            if (g.State == GeneratorState.Completed)
                return CreateResult(ctx, heap, JsValue.Undefined, done: true);
            if (g.State == GeneratorState.Executing)
                throw new JsThrownException(ctx.CreateTypeError("Generator.prototype.next: generator is already executing"));
            var sentValue = args.Count > 0 ? args[0] : JsValue.Undefined;
            g.State = GeneratorState.Executing;
            try
            {
                var interpreter = ctx as BytecodeInterpreter;
                if (interpreter is null) return CreateResult(ctx, heap, JsValue.Undefined, done: true);
                var result = interpreter.ExecuteGenerator(g, sentValue);
                // ExecuteGenerator already updated g.State (Suspended if yielded,
                // Completed if returned). Don't override here.
                return result;
            }
            catch (JsThrownException)
            {
                g.State = GeneratorState.Completed;
                throw;
            }
        }, length: 1);

        DefineMethod(heap, captured, ph, proto, "return", (ctx, tv, args) =>
        {
            if (tv.Tag != JsValueTag.Object || ctx.Heap.GetObject(tv.AsObjectHandle()) is not GeneratorObject g)
                throw new JsThrownException(ctx.CreateTypeError("Generator.prototype.return: receiver is not a generator"));
            var returnValue = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (g.State == GeneratorState.Completed)
                return CreateResult(ctx, heap, returnValue, done: true);
            if (g.State == GeneratorState.Executing)
                throw new JsThrownException(ctx.CreateTypeError("Generator.prototype.return: generator is already executing"));
            // ECMA-262 27.5.1.3 step 4: if state is suspendedStart, transition
            // to completed without executing the body.
            if (g.InstructionPointer == 0)
            {
                g.State = GeneratorState.Completed;
                return CreateResult(ctx, heap, returnValue, done: true);
            }
            var interpreter = ctx as BytecodeInterpreter;
            if (interpreter is null) { g.State = GeneratorState.Completed; return CreateResult(ctx, heap, returnValue, done: true); }
            g.CompletionMode = GeneratorCompletionMode.Return;
            g.State = GeneratorState.Executing;
            try
            {
                // The interpreter injects the return completion at the suspended
                // yield (ECMA-262 27.5.3.3): finally blocks covering the yield run,
                // and a yield inside such a finally (or a yield* forward) legitimately
                // re-suspends the generator with that yield's result. Otherwise the
                // generator completed and the result is already {value, done: true}.
                return interpreter.ExecuteGenerator(g, returnValue);
            }
            catch (JsThrownException)
            {
                g.State = GeneratorState.Completed;
                throw;
            }
        }, length: 1);

        DefineMethod(heap, captured, ph, proto, "throw", (ctx, tv, args) =>
        {
            if (tv.Tag != JsValueTag.Object || ctx.Heap.GetObject(tv.AsObjectHandle()) is not GeneratorObject g)
                throw new JsThrownException(ctx.CreateTypeError("Generator.prototype.throw: receiver is not a generator"));
            var excValue = args.Count > 0 ? args[0] : JsValue.Undefined;
            if (g.State == GeneratorState.Completed)
                throw new JsThrownException(excValue);
            if (g.State == GeneratorState.Executing)
                throw new JsThrownException(ctx.CreateTypeError("Generator.prototype.throw: generator is already executing"));
            var interpreter = ctx as BytecodeInterpreter;
            if (interpreter is null) { g.State = GeneratorState.Completed; throw new JsThrownException(excValue); }
            g.CompletionMode = GeneratorCompletionMode.Throw;
            g.State = GeneratorState.Executing;
            try
            {
                var result = interpreter.ExecuteGenerator(g, excValue);
                // Generator caught the exception and yielded — return the yield.
                return result;
            }
            catch (JsThrownException)
            {
                g.State = GeneratorState.Completed;
                throw;
            }
        }, length: 1);

        proto.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(ph), Writable: true, Enumerable: false, Configurable: true));

        // ECMA-262 27.5.1.5 %GeneratorPrototype% [ @@toStringTag ] = "Generator"
        // { [[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: true }.
        var toStringTag = context.CreateWellKnownSymbol("toStringTag");
        proto.DefineOwnSymbolProperty(
            toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("Generator"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("GeneratorPrototype", JsValue.FromObject(ph)) };
    }

    private static JsValue CreateResult(IBuiltinContext ctx, JsHeap heap, JsValue value, bool done)
    {
        var obj = new JsObject();
        obj.DefineOwnProperty("value", new JsPropertyDescriptor(value, Writable: true, Enumerable: true, Configurable: true));
        obj.DefineOwnProperty("done", new JsPropertyDescriptor(JsValue.FromBoolean(done), Writable: true, Enumerable: true, Configurable: true));
        return JsValue.FromObject(heap.AllocateObject(obj, AllocationSite.Current()));
    }

    private delegate JsValue GenMethod(IBuiltinContext ctx, JsValue thisValue, IReadOnlyList<JsValue> args);

    private static void DefineMethod(JsHeap heap, IBuiltinContext ctx, ObjectHandle ph, JsObject proto, string name, GenMethod method, int length)
    {
        var captured = ctx;
        var fn = new NativeFunctionObject(name, (thisValue, args) => method(captured, thisValue, args), length: length);
        var fh = heap.AllocateObject(fn, AllocationSite.Current());
        proto.DefineOwnProperty(name, new JsPropertyDescriptor(JsValue.FromObject(fh), Writable: true, Enumerable: false, Configurable: true));
        heap.WriteBarrier(ph, fh);
    }
}
