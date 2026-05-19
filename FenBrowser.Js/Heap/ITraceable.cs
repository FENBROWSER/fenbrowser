namespace FenBrowser.Js.Heap;

public interface ITraceable
{
    void Trace(IHeapTracer tracer);
}
