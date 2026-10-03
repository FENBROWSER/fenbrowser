using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public interface IHeapTracer
{
    void Trace(ObjectHandle handle);

    /// <summary>
    /// Traces a handle that is a GC *root* — an entry of the root set itself,
    /// rather than an edge discovered inside an object payload — naming the
    /// slot it came from.
    ///
    /// A dangling entry in the root set fails validation from inside the mark
    /// phase, where the only thing the error could say was which cell was
    /// missing; the root that pointed at it, which is the thing actually
    /// broken, went unnamed. Root walks route through here so the collector
    /// knows what it is holding when a handle does not resolve.
    /// </summary>
    void TraceRoot(string context, ObjectHandle handle) => Trace(handle);
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
    /// True while a minor collection marks: only young cells can be freed, and
    /// marking an Old one does nothing, so a root source may trace just the
    /// values that could still be young (see <see cref="RecentRootLog"/>).
    /// Every other tracer - major marking, audits, the heap verifier - sees the
    /// whole root set.
    /// </summary>
    bool MinorOnly => false;

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
