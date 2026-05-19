using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class HeapVerifier
{
    public void Verify(JsHeap heap)
    {
        var cells = heap.GetCellsSnapshotForTest();
        var generations = heap.GetGenerationsSnapshotForTest();
        var freeFlags = heap.GetFreeFlagsSnapshotForTest();
        var freeList = heap.GetFreeListSnapshotForTest();
        if (generations.Count != cells.Count || freeFlags.Count != cells.Count)
        {
            throw new JsEngineFatalException("Heap table length mismatch.");
        }

        var seenFreeIndices = new HashSet<int>();
        foreach (var freeIndex in freeList)
        {
            if ((uint)freeIndex >= (uint)cells.Count)
            {
                throw new JsEngineFatalException($"Free-list index out of range: {freeIndex}.");
            }

            if (!seenFreeIndices.Add(freeIndex))
            {
                throw new JsEngineFatalException($"Duplicate free-list index: {freeIndex}.");
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (generations[i] <= 0)
            {
                throw new JsEngineFatalException($"Invalid generation table value at {i}.");
            }

            if (cell is null)
            {
                if (!freeFlags[i])
                {
                    throw new JsEngineFatalException($"Freed slot {i} is not marked free.");
                }

                if (!seenFreeIndices.Contains(i))
                {
                    throw new JsEngineFatalException($"Freed slot {i} missing from free-list.");
                }

                continue;
            }

            if (freeFlags[i])
            {
                throw new JsEngineFatalException($"Live slot {i} is incorrectly marked free.");
            }

            if (seenFreeIndices.Contains(i))
            {
                throw new JsEngineFatalException($"Live slot {i} appears in free-list.");
            }

            if (cell.Generation <= 0)
            {
                throw new JsEngineFatalException($"Invalid generation at cell {i}.");
            }

            if (cell.Kind == HeapCellKind.Object && cell.Payload is not Objects.JsObject)
            {
                throw new JsEngineFatalException($"Cell kind/payload mismatch at {i}.");
            }
            if (cell.Kind == HeapCellKind.String)
            {
                var handle = new StringHandle(i, cell.Generation);
                _ = heap.GetString(handle);
            }

            if (cell.Kind == HeapCellKind.Symbol)
            {
                var handle = new SymbolHandle(i, cell.Generation);
                _ = heap.GetSymbolDescription(handle);
            }

            cell.Payload.Trace(new ValidatingTracer(heap));
        }

        foreach (var root in heap.GetRootsSnapshotForTest())
        {
            heap.Validate(root);
        }
        foreach (var root in heap.GetStringRootsSnapshotForTest())
        {
            heap.Validate(root);
        }
        foreach (var root in heap.GetSymbolRootsSnapshotForTest())
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

        public void Trace(StringHandle handle)
        {
            _ = _heap.Validate(handle);
        }

        public void Trace(SymbolHandle handle)
        {
            _ = _heap.Validate(handle);
        }
    }
}
