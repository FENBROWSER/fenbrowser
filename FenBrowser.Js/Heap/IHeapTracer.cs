using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public interface IHeapTracer
{
    void Trace(ObjectHandle handle);
    void Trace(StringHandle handle);
    void Trace(SymbolHandle handle);
}
