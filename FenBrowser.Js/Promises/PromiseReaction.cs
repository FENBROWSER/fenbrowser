using FenBrowser.Js.Heap;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.1.2 PromiseReaction Records.
//
// Slots:
//   [[Capability]] - the PromiseCapability whose resolve/reject will receive the
//                    handler's outcome. Empty when the reaction was queued by an
//                    internal job (await desugaring) that does not surface its own
//                    promise.
//   [[Type]]       - Fulfill or Reject; selects which path of the parent promise this
//                    reaction belongs to.
//   [[Handler]]    - the user-provided handler function, or empty (undefined) to mean
//                    "pass the parent's settled value through unchanged".
//
// Marked as ITraceable so each queued reaction can be allocated as its own heap cell
// and reached during GC through PromiseObject.Trace.
public sealed class PromiseReaction : ITraceable
{
    public PromiseReaction(
        PromiseCapability capability,
        PromiseReactionType type,
        JsValue handler)
    {
        Capability = capability;
        Type = type;
        Handler = handler;
    }

    public PromiseCapability Capability { get; }
    public PromiseReactionType Type { get; }
    public JsValue Handler { get; }

    public bool HasHandler => Handler.Tag != JsValueTag.Undefined;

    public void Trace(IHeapTracer tracer)
    {
        TraceJsValue(tracer, Capability.Promise);
        TraceJsValue(tracer, Capability.Resolve);
        TraceJsValue(tracer, Capability.Reject);
        TraceJsValue(tracer, Handler);
    }

    private static void TraceJsValue(IHeapTracer tracer, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            tracer.Trace(value.AsObjectHandle());
        }
    }
}
