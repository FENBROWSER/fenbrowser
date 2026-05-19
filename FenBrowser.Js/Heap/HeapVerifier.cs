using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class HeapVerifier
{
    public void Verify(JsHeap heap)
    {
        var cells = heap.GetCellsSnapshotForTest();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell is null)
            {
                continue;
            }

            if (cell.Generation <= 0)
            {
                throw new JsEngineFatalException($"Invalid generation at cell {i}.");
            }

            if (cell.Kind == HeapCellKind.Object && cell.Payload is not Objects.JsObject)
            {
                throw new JsEngineFatalException($"Cell kind/payload mismatch at {i}.");
            }

            cell.Payload.Trace(new ValidatingTracer(heap));
        }

        foreach (var root in heap.GetRootsSnapshotForTest())
        {
            heap.Validate(root);
        }

        foreach (var edge in heap.GetWriteBarrierEdgesSnapshotForTest())
        {
            _ = heap.Validate(edge.Owner);
            _ = heap.Validate(edge.Child);
        }
    }

    private sealed class ValidatingTracer : IHeapTracer
    {
        private readonly JsHeap _heap;

        public ValidatingTracer(JsHeap heap)
        {
            _heap = heap;
        }

        public void Trace(ObjectHandle handle)
        {
            _ = _heap.Validate(handle);
        }
    }
}
