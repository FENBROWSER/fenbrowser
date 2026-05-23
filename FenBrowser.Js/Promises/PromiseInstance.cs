using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Promises;

// ECMA-262 27.2.6 Properties of Promise Instances. JsObject wrapper that carries the
// PromiseObject internal slots while remaining indistinguishable from any other
// ordinary object to property access. The interpreter pulls the inner PromiseObject
// out via the public Promise getter when it needs to act on the promise state.
public sealed class PromiseInstance : JsObject
{
    public PromiseInstance(PromiseObject promise)
    {
        ArgumentNullException.ThrowIfNull(promise);
        Promise = promise;
    }

    public PromiseObject Promise { get; }

    public override void Trace(IHeapTracer tracer)
    {
        base.Trace(tracer);
        Promise.Trace(tracer);
    }
}
