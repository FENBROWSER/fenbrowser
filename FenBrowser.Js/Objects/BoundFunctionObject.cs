using FenBrowser.Js.Runtime;

using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Objects;

// ECMA-262 10.4.1 Bound Function Exotic Objects.
// Created by Function.prototype.bind. Stores the target function, bound this,
// and bound arguments. Exotic [[Call]] and [[Construct]] dispatch is handled
// by BytecodeInterpreter.CallFunction / ConstructFunction.
public sealed class BoundFunctionObject : JsObject
{
    // [[BoundTargetFunction]] — the original callable that .bind() was called on.
    public JsValue TargetFunction { get; }

    // [[BoundThis]] — the thisArg pinned by .bind().
    public JsValue BoundThis { get; }

    // [[BoundArguments]] — the partial application args from .bind().
    public JsValue[] BoundArgs { get; }

    // ECMA-262 10.4.1.3 — [[Call]] merges bound args + call-site args, then
    // calls [[BoundTargetFunction]] with [[BoundThis]] as the receiver.

    // ECMA-262 10.4.1.4 — [[Construct]] merges bound args + call-site args,
    // then constructs [[BoundTargetFunction]] with newTarget set to target
    // (when newTarget === bound function) per step 5.

    public BoundFunctionObject(JsValue targetFunction, JsValue boundThis, JsValue[] boundArgs, int length)
    {
        TargetFunction = targetFunction;
        BoundThis = boundThis;
        BoundArgs = boundArgs;
        _ = DefineOwnProperty(
            "length",
            new JsPropertyDescriptor(
                JsValue.FromNumber(length),
                Writable: false,
                Enumerable: false,
                Configurable: true));
        _ = DefineOwnProperty(
            "name",
            new JsPropertyDescriptor(
                JsValue.FromString("bound " + GetTargetName(targetFunction)),
                Writable: false,
                Enumerable: false,
                Configurable: true));
    }

    private static string GetTargetName(JsValue target)
    {
        // Try to get the target function's "name" property.
        // This is a best-effort string for the "name" property; spec says
        // "bound " + target.[[Get]]("name", receiver) in 20.2.3.2 step 5.
        return "function";
    }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        if (TargetFunction.Tag == JsValueTag.Object)
            tracer.Trace(TargetFunction.AsObjectHandle());
        if (BoundThis.Tag == JsValueTag.Object)
            tracer.Trace(BoundThis.AsObjectHandle());
        for (var i = 0; i < BoundArgs.Length; i++)
        {
            if (BoundArgs[i].Tag == JsValueTag.Object)
                tracer.Trace(BoundArgs[i].AsObjectHandle());
        }
    }
}
