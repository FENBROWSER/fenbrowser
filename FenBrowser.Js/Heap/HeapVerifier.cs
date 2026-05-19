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
        }

        foreach (var root in heap.GetRootsSnapshotForTest())
        {
            heap.Validate(root);
        }
    }
}
