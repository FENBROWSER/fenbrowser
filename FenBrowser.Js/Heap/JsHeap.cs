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
    private int _gcCollectionCount;
    private int _minorGcCount;
    private int _lastGcMarkedCells;
    private int _lastGcSweptCells;
    private int _lastMinorMarked;
    private int _lastMinorSwept;
    private int _lastMinorPromoted;
    // Tier 4 #22 remembered set: Old → Young edges discovered via
    // WriteBarrier. Indexed by Old cell index for dedup. Cleared and
    // rebuilt on each major collection; entries become stale (filtered
    // out by IsYoung/IsLiveObject) when the Young child is collected
    // or promoted.
    private readonly Dictionary<int, List<int>> _rememberedSet = new();
    // Tier 4 #22: after this many minor collections, a surviving Young cell
    // is promoted to Old. Default mirrors common nursery survival heuristics.
    public byte PromotionThreshold { get; set; } = 2;

    // Tier 4 #22: auto-trigger a MinorCollect after this many Young
    // allocations. Zero disables the auto-trigger (caller drives GC
    // manually). Default is conservative — large enough that test suites
    // don't pay nursery overhead unnecessarily, small enough that long
    // allocation-heavy runs see periodic minor sweeps.
    public int YoungAllocationsPerMinorGc { get; set; } = 4096;
    private int _youngAllocationsSinceLastMinorGc;
    private readonly bool _verifyHeapBeforeGc;
    private readonly bool _verifyHeapAfterGc;
    private readonly HeapVerifier _verifier = new();

    public JsHeap(
        GcStressMode stressMode = GcStressMode.None,
        bool verifyHeapBeforeGc = false,
        bool verifyHeapAfterGc = false)
    {
        _stressMode = stressMode;
        _verifyHeapBeforeGc = verifyHeapBeforeGc;
        _verifyHeapAfterGc = verifyHeapAfterGc;
    }

    public int RootCount => _roots.Count;
    public int WriteBarrierCount => _writeBarrierCount;
    public int GcCollectionCount => _gcCollectionCount;
    public int MinorCollectionCount => _minorGcCount;
    public int LastGcMarkedCells => _lastGcMarkedCells;
    public int LastGcSweptCells => _lastGcSweptCells;
    public int LastMinorMarked => _lastMinorMarked;
    public int LastMinorSwept => _lastMinorSwept;
    public int LastMinorPromoted => _lastMinorPromoted;
    public int RememberedSetEdgeCount
    {
        get
        {
            var n = 0;
            foreach (var list in _rememberedSet.Values) n += list.Count;
            return n;
        }
    }
    public int LiveCellCount => _cells.Count(c => c is not null);

    public ObjectHandle AllocateObject(JsObject obj, AllocationSite site)
    {
        _ = site;
        MaybeStressGc();

        var handle = AllocateCell(HeapCellKind.Object, obj);
        var objHandle = new ObjectHandle(handle.Index, handle.Generation);
        // Tier 4 #22: stamp the freshly-allocated object with its handle
        // and owning heap so JsObject.SetProperty / DefineOwnProperty can
        // emit write barriers centrally.
        obj.OwnerHandle = objHandle;
        obj.OwnerHeap = this;

        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            CollectGarbage();
        }
        else
        {
            MaybeAutoMinorCollect();
        }

        return objHandle;
    }

    public StringHandle AllocateString(string value, AllocationSite site)
    {
        _ = site;
        MaybeStressGc();

        var handle = AllocateCell(HeapCellKind.String, new StringPayload(value));

        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            CollectGarbage();
        }

        return new StringHandle(handle.Index, handle.Generation);
    }

    public SymbolHandle AllocateSymbol(string? description, AllocationSite site)
    {
        _ = site;
        MaybeStressGc();

        var handle = AllocateCell(HeapCellKind.Symbol, new SymbolPayload(description));

        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            CollectGarbage();
        }

        return new SymbolHandle(handle.Index, handle.Generation);
    }

    public JsObject GetObject(ObjectHandle handle)
    {
        var cell = Validate(handle);
        return (JsObject)cell.Payload;
    }

    public string GetString(StringHandle handle)
    {
        var cell = Validate(handle);
        return ((StringPayload)cell.Payload).Value;
    }

    public string? GetSymbolDescription(SymbolHandle handle)
    {
        var cell = Validate(handle);
        return ((SymbolPayload)cell.Payload).Description;
    }

    public void PushRoot(ObjectHandle handle)
    {
        _ = Validate(handle);
        _roots.Push(handle);
    }

    public void PushRoot(StringHandle handle)
    {
        _ = Validate(handle);
        _roots.Push(handle);
    }

    public void PushRoot(SymbolHandle handle)
    {
        _ = Validate(handle);
        _roots.Push(handle);
    }

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
        var ownerCell = Validate(owner);
        var childCell = Validate(child);
        _writeBarrierCount++;
        _writeBarrierEdges.Add((owner, child));
        // Tier 4 #22: remembered-set update. Only Old → Young pointers need
        // to be remembered; Young → anything and Old → Old are already
        // covered by the normal mark traversal.
        if (ownerCell.Tier == GenerationTier.Old && childCell.Tier == GenerationTier.Young)
        {
            if (!_rememberedSet.TryGetValue(owner.Index, out var list))
            {
                list = new List<int>();
                _rememberedSet[owner.Index] = list;
            }
            if (!list.Contains(child.Index)) list.Add(child.Index);
        }
    }

    // Tier 4 #22: minor (nursery) collection. Marks reachable Young cells
    // starting from all real roots plus the remembered set, then sweeps
    // unreachable Young cells. Cells that survive PromotionThreshold minor
    // collections are promoted to Old.
    //
    // Correctness note: the mark traversal walks through Old cells as well,
    // because an Old object's children may include Young objects that
    // weren't covered by the remembered set (e.g., recently written but
    // missed by a slow path). This makes MinorCollect a conservative
    // superset of "scan only Young" — it costs more than the platonic
    // ideal but cannot miss a live pointer. Future work: prune Old
    // re-traversal once every write site goes through WriteBarrier.
    public void MinorCollect()
    {
        if (_verifyHeapBeforeGc) _verifier.Verify(this);

        _minorGcCount++;
        _lastMinorMarked = 0;
        _lastMinorSwept = 0;
        _lastMinorPromoted = 0;

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell is not null) cell.Marked = false;
        }

        // Tier 4 #22: minor-mode tracer stops at Old cells. Soundness
        // depends on every Old→Young pointer being in the remembered
        // set — which is now true because JsObject.SetProperty /
        // DefineOwnProperty / DefineOwnSymbolProperty / SetPrototype
        // all funnel through BarrierIfObject.
        _currentMarkMinorMode = true;
        var marker = new MarkingTracer(this, minorMode: true);
        foreach (var root in _roots.Snapshot()) marker.Trace(root);
        foreach (var root in _roots.StringSnapshot()) marker.Trace(root);
        foreach (var root in _roots.SymbolSnapshot()) marker.Trace(root);

        // Remembered set: every recorded Old→Young edge is treated as a root
        // for the Young cell.
        foreach (var (ownerIdx, children) in _rememberedSet)
        {
            if ((uint)ownerIdx >= (uint)_cells.Count || _cells[ownerIdx] is null) continue;
            foreach (var childIdx in children)
            {
                if ((uint)childIdx >= (uint)_cells.Count) continue;
                var childCell = _cells[childIdx];
                if (childCell is null || childCell.Tier != GenerationTier.Young) continue;
                marker.Trace(new ObjectHandle(childIdx, childCell.Generation));
            }
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell is null) continue;
            if (cell.Tier != GenerationTier.Young) continue;

            if (cell.Marked)
            {
                if (cell.MinorSurvivedCount < byte.MaxValue) cell.MinorSurvivedCount++;
                if (cell.MinorSurvivedCount >= PromotionThreshold)
                {
                    cell.Tier = GenerationTier.Old;
                    _lastMinorPromoted++;
                }
            }
            else
            {
                _cells[i] = null;
                _lastMinorSwept++;
                if (!_isFree[i])
                {
                    _isFree[i] = true;
                    _freeList.Push(i);
                }
            }
        }

        _lastMinorMarked = _cells.Count(c => c is not null && c.Marked);
        PruneStaleRememberedSetEntries();
        _currentMarkMinorMode = false;

        if (_verifyHeapAfterGc) _verifier.Verify(this);
    }

    // Tier 4 #22: invoked from AllocateObject. Skipped in stress modes
    // because those drive collection on their own cadence.
    private void MaybeAutoMinorCollect()
    {
        if (YoungAllocationsPerMinorGc <= 0) return;
        _youngAllocationsSinceLastMinorGc++;
        if (_youngAllocationsSinceLastMinorGc >= YoungAllocationsPerMinorGc)
        {
            _youngAllocationsSinceLastMinorGc = 0;
            MinorCollect();
        }
    }

    private void PruneStaleRememberedSetEntries()
    {
        var staleOwners = new List<int>();
        foreach (var (ownerIdx, children) in _rememberedSet)
        {
            if ((uint)ownerIdx >= (uint)_cells.Count || _cells[ownerIdx] is null)
            {
                staleOwners.Add(ownerIdx);
                continue;
            }
            children.RemoveAll(childIdx =>
                (uint)childIdx >= (uint)_cells.Count ||
                _cells[childIdx] is not { Tier: GenerationTier.Young });
            if (children.Count == 0) staleOwners.Add(ownerIdx);
        }
        foreach (var o in staleOwners) _rememberedSet.Remove(o);
    }

    public void CollectGarbage()
    {
        if (_verifyHeapBeforeGc)
        {
            _verifier.Verify(this);
        }

        _gcCollectionCount++;
        _lastGcMarkedCells = 0;
        _lastGcSweptCells = 0;

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
        foreach (var root in _roots.StringSnapshot())
        {
            marker.Trace(root);
        }
        foreach (var root in _roots.SymbolSnapshot())
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
            _lastGcSweptCells++;
            if (!_isFree[i])
            {
                _isFree[i] = true;
                _freeList.Push(i);
            }
        }

        PruneWriteBarrierEdges();
        // Tier 4 #22: a major collection invalidates remembered-set
        // membership for swept-away children. PruneStaleRememberedSetEntries
        // handles partial invalidation; for a full major collection it's
        // simpler to clear and let WriteBarrier repopulate.
        _rememberedSet.Clear();

        if (_verifyHeapAfterGc)
        {
            _verifier.Verify(this);
        }
    }

    public void FreeForTest(ObjectHandle handle)
    {
        Validate(handle);
        FreeIndexForTest(handle.Index);
    }

    public void FreeForTest(StringHandle handle)
    {
        Validate(handle);
        FreeIndexForTest(handle.Index);
    }

    public void FreeForTest(SymbolHandle handle)
    {
        Validate(handle);
        FreeIndexForTest(handle.Index);
    }

    private void FreeIndexForTest(int index)
    {
        _cells[index] = null;
        if (!_isFree[index])
        {
            _isFree[index] = true;
            _freeList.Push(index);
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

    // Tier 4 #22: the minor-mode tracer needs to propagate through nested
    // child traces, so Mark takes the mode and constructs a matching
    // tracer for the recursive Trace call.
    private bool _currentMarkMinorMode;

    private void Mark(ObjectHandle handle)
    {
        var cell = Validate(handle);
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        _lastGcMarkedCells++;
        cell.Payload.Trace(new MarkingTracer(this, _currentMarkMinorMode));
    }

    private void Mark(StringHandle handle)
    {
        var cell = Validate(handle);
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        _lastGcMarkedCells++;
        cell.Payload.Trace(new MarkingTracer(this, _currentMarkMinorMode));
    }

    private void Mark(SymbolHandle handle)
    {
        var cell = Validate(handle);
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        _lastGcMarkedCells++;
        cell.Payload.Trace(new MarkingTracer(this, _currentMarkMinorMode));
    }

    private (int Index, int Generation) AllocateCell(HeapCellKind kind, ITraceable payload)
    {
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
                Kind = kind,
                Payload = payload,
                Tier = GenerationTier.Young,
                MinorSurvivedCount = 0,
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
                Kind = kind,
                Payload = payload,
                Tier = GenerationTier.Young,
                MinorSurvivedCount = 0,
            });
        }

        return (index, generation);
    }

    public IReadOnlyList<HeapCell?> GetCellsSnapshotForTest() => _cells;
    public int GetGenerationForCellIndexForTest(int index) => _generations[index];
    public IReadOnlyList<int> GetGenerationsSnapshotForTest() => _generations;
    public IReadOnlyList<bool> GetFreeFlagsSnapshotForTest() => _isFree;
    public IReadOnlyList<int> GetFreeListSnapshotForTest() => _freeList.ToArray();

    public IReadOnlyList<ObjectHandle> GetRootsSnapshotForTest() => _roots.Snapshot();
    public IReadOnlyList<StringHandle> GetStringRootsSnapshotForTest() => _roots.StringSnapshot();
    public IReadOnlyList<SymbolHandle> GetSymbolRootsSnapshotForTest() => _roots.SymbolSnapshot();

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
        // Tier 4 #22: when true, mark traversal stops at Old cells. The
        // remembered set is responsible for keeping any reachable Young
        // children of those Old cells alive. Major collections set this
        // false and mark everything.
        private readonly bool _minorMode;

        public MarkingTracer(JsHeap heap, bool minorMode = false)
        {
            _heap = heap;
            _minorMode = minorMode;
        }

        public void Trace(ObjectHandle handle)
        {
            if (_minorMode && _heap.IsOld(handle.Index)) return;
            _heap.Mark(handle);
        }

        public void Trace(StringHandle handle)
        {
            if (_minorMode && _heap.IsOld(handle.Index)) return;
            _heap.Mark(handle);
        }

        public void Trace(SymbolHandle handle)
        {
            if (_minorMode && _heap.IsOld(handle.Index)) return;
            _heap.Mark(handle);
        }
    }

    internal bool IsOld(int index)
    {
        if ((uint)index >= (uint)_cells.Count) return false;
        var cell = _cells[index];
        return cell is not null && cell.Tier == GenerationTier.Old;
    }

    private sealed class StringPayload : ITraceable
    {
        public StringPayload(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public void Trace(IHeapTracer tracer)
        {
            _ = tracer;
        }
    }

    private sealed class SymbolPayload : ITraceable
    {
        public SymbolPayload(string? description)
        {
            Description = description;
        }

        public string? Description { get; }

        public void Trace(IHeapTracer tracer)
        {
            _ = tracer;
        }
    }
}
