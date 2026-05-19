using FenBrowser.Js.Heap;

namespace FenBrowser.Js.Objects;

public sealed class JsObject : ITraceable
{
    public void Trace(IHeapTracer tracer)
    {
        // Object graph tracing is added as object model grows.
    }
}
