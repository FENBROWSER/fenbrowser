using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class JsHeap
{
    private readonly List<HeapCell?> _cells = new();
    private readonly List<int> _generations = new();
    private readonly List<bool> _isFree = new();
    private readonly Stack<int> _freeList = new();
    private readonly RootSet _roots = new();
    private readonly GcStressMode _stressMode;
    private readonly List<(ObjectHandle Owner, ObjectHandle Child)> _writeBarrierEdges = new();
    private int _writeBarrierCount;

    public JsHeap(GcStressMode stressMode = GcStressMode.None)
    {
        _stressMode = stressMode;
    }

    public int RootCount => _roots.Count;
    public int WriteBarrierCount => _writeBarrierCount;

    public ObjectHandle AllocateObject(JsObject obj, AllocationSite site)
    {
        MaybeStressGc();

        int index;
        int generation;

        if (_freeList.Count > 0)
        {
            index = _freeList.Pop();
            _isFree[index] = false;
            generation = _generations[index] + 1;
            _generations[index] = generation;
            _cells[index] = new HeapCell
            {
                Generation = generation,
                Kind = HeapCellKind.Object,
                Payload = obj
            };
        }
        else
        {
            index = _cells.Count;
            generation = 1;
            _generations.Add(generation);
            _isFree.Add(false);
            _cells.Add(new HeapCell
            {
                Generation = generation,
                Kind = HeapCellKind.Object,
                Payload = obj
            });
        }

        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            CollectGarbage();
        }

        return new ObjectHandle(index, generation);
    }

    public JsObject GetObject(ObjectHandle handle)
    {
        var cell = Validate(handle);
        return (JsObject)cell.Payload;
    }

    public void PushRoot(ObjectHandle handle) => _roots.Push(handle);

    public void PopRootsTo(int mark) => _roots.PopTo(mark);

    public HeapCell Validate(ObjectHandle handle)
    {
        return ValidateHandle(handle.Index, handle.Generation, HeapCellKind.Object);
    }

    public HeapCell Validate(StringHandle handle)
    {
        return ValidateHandle(handle.Index, handle.Generation, HeapCellKind.String);
    }

    public HeapCell Validate(SymbolHandle handle)
    {
        return ValidateHandle(handle.Index, handle.Generation, HeapCellKind.Symbol);
    }

    private HeapCell ValidateHandle(int index, int generation, HeapCellKind expectedKind)
    {
        if ((uint)index >= (uint)_cells.Count)
        {
            throw new JsEngineFatalException("Invalid heap handle index.");
        }

        var cell = _cells[index];
        if (cell is null || cell.Generation != generation)
        {
            throw new JsEngineFatalException("Stale heap handle.");
        }

        if (cell.Kind != expectedKind)
        {
            throw new JsEngineFatalException($"Heap handle kind mismatch. Expected {expectedKind}, actual {cell.Kind}.");
        }

        return cell;
    }

    public void WriteBarrier(ObjectHandle owner, ObjectHandle child)
    {
        _ = owner;
        _ = child;
        _writeBarrierCount++;
        _writeBarrierEdges.Add((owner, child));
        // No-op in v1. Required seam for future GC evolution.
    }

    public void CollectGarbage()
    {
        // Clear old mark bits.
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell is not null)
            {
                cell.Marked = false;
            }
        }

        // Mark from explicit roots.
        var marker = new MarkingTracer(this);
        foreach (var root in _roots.Snapshot())
        {
            marker.Trace(root);
        }

        // Sweep unreachable cells.
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell is null || cell.Marked)
            {
                continue;
            }

            _cells[i] = null;
            if (!_isFree[i])
            {
                _isFree[i] = true;
                _freeList.Push(i);
            }
        }

        PruneWriteBarrierEdges();
    }

    public void FreeForTest(ObjectHandle handle)
    {
        Validate(handle);
        _cells[handle.Index] = null;
        if (!_isFree[handle.Index])
        {
            _isFree[handle.Index] = true;
            _freeList.Push(handle.Index);
        }
    }

    private void PruneWriteBarrierEdges()
    {
        for (var i = _writeBarrierEdges.Count - 1; i >= 0; i--)
        {
            if (!IsLiveObject(_writeBarrierEdges[i].Owner) || !IsLiveObject(_writeBarrierEdges[i].Child))
            {
                _writeBarrierEdges.RemoveAt(i);
            }
        }
    }

    private bool IsLiveObject(ObjectHandle handle)
    {
        if ((uint)handle.Index >= (uint)_cells.Count)
        {
            return false;
        }

        var cell = _cells[handle.Index];
        return cell is not null && cell.Generation == handle.Generation && cell.Kind == HeapCellKind.Object;
    }

    private void Mark(ObjectHandle handle)
    {
        var cell = Validate(handle);
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        cell.Payload.Trace(new MarkingTracer(this));
    }

    public IReadOnlyList<HeapCell?> GetCellsSnapshotForTest() => _cells;

    public IReadOnlyList<ObjectHandle> GetRootsSnapshotForTest() => _roots.Snapshot();

    public IReadOnlyList<(ObjectHandle Owner, ObjectHandle Child)> GetWriteBarrierEdgesSnapshotForTest() => _writeBarrierEdges;

    private void MaybeStressGc()
    {
        switch (_stressMode)
        {
            case GcStressMode.BeforeEveryAlloc:
                CollectGarbage();
                break;
            case GcStressMode.Random:
                if (Random.Shared.Next(0, 4) == 0)
                {
                    CollectGarbage();
                }

                break;
        }
    }

    private sealed class MarkingTracer : IHeapTracer
    {
        private readonly JsHeap _heap;

        public MarkingTracer(JsHeap heap)
        {
            _heap = heap;
        }

        public void Trace(ObjectHandle handle)
        {
            _heap.Mark(handle);
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
