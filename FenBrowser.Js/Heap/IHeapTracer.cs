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

    /// <summary>
    /// Announces an environment record that is about to be traced. Returns
    /// false when this collection has already traced it -- and therefore
    /// everything outside it too -- so the walk can stop there.
    ///
    /// Every closure captures a scope chain that ends at the global record, and
    /// without this each one re-walks that chain and re-traces every global
    /// binding. The cost is quadratic in the number of closures, which a large
    /// bundle has thousands of.
    /// </summary>
    bool BeginEnvironment(FenBrowser.Js.Environments.EnvironmentRecord record) => true;
}
