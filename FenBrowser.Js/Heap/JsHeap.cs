using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class JsHeap
{
    private readonly List<HeapCell?> _cells = new();
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
            generation = (_cells[index]?.Generation ?? 0) + 1;
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
        if ((uint)handle.Index >= (uint)_cells.Count)
        {
            throw new JsEngineFatalException("Invalid heap handle index.");
        }

        var cell = _cells[handle.Index];
        if (cell is null || cell.Generation != handle.Generation)
        {
            throw new JsEngineFatalException("Stale heap handle.");
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
        // Mark-sweep implementation lands in subsequent tranche.
    }

    public void FreeForTest(ObjectHandle handle)
    {
        Validate(handle);
        _cells[handle.Index] = null;
        _freeList.Push(handle.Index);
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
}
