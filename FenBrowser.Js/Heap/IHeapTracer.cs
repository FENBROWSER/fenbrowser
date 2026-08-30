using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public interface IHeapTracer
{
    void Trace(ObjectHandle handle);
    void Trace(StringHandle handle);
    void Trace(SymbolHandle handle);

    // Whether tracing an object payload should walk its captured
    // environment-record chain (bindings + outer chain). Remembered-set scans
    // (CardTracer) leave this false: binding edges are covered precisely by the
    // heap's remembered-environment set, so re-walking the chain once per
    // referencing cell would repeat identical work for every closure sharing
    // the record. Full markings (MarkingTracer) keep the default true.
    bool TraceEnvironmentChains => true;
}
