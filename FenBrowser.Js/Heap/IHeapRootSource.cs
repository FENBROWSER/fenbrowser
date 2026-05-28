namespace FenBrowser.Js.Heap;

// Audit gap §1 (closing iterator-close stale-handle crashes #1 #2):
// the BytecodeInterpreter holds live JsValue Objects inside its active
// InterpreterFrame Registers. These references are invisible to the
// GC root set, so an auto-MinorCollect inside user-code can reclaim
// a cell that's only reachable through a frame register and surface
// later as "Stale heap handle.". Any subsystem that owns such hidden
// roots registers itself with the heap via AddRootSource, and the
// GC calls TraceRoots on every collection.
public interface IHeapRootSource
{
    void TraceRoots(IHeapTracer tracer);
}
