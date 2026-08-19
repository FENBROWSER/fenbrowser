using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class JsHeap
{
    private readonly List<HeapCell?> _cells = new();
    private readonly List<int> _generations = new();
    private readonly List<bool> _isFree = new();
    private readonly Stack<int> _freeList = new();
    private readonly List<(int Index, int Generation)> _nursery = new();
    private readonly RootSet _roots = new();
    private readonly List<IHeapRootSource> _rootSources = new();
    private readonly GcStressMode _stressMode;
    private readonly List<(ObjectHandle Owner, ObjectHandle Child)>? _writeBarrierEdges;
    private int _writeBarrierCount;
    private int _gcCollectionCount;
    private int _minorGcCount;
    private int _lastGcMarkedCells;
    private int _lastGcSweptCells;
    private int _lastMinorMarked;
    private int _lastMinorSwept;
    private int _lastMinorPromoted;
    private int _lastMinorScannedOldCells;
    private long _allocationCount;
    // Weak-target registry: a WeakRef or FinalizationRegistry entry holds a
    // handle to a target that must NOT keep the target alive. The target handle
    // is only an index+generation pair; nothing here marks it. When the target
    // cell dies in a collection, the registered callback fires so the owner can
    // null its reference (WeakRef) or enqueue a cleanup job (FinalizationRegistry).
    private readonly List<WeakTargetRecord> _weakTargets = new();
    private int _weakTargetCallbacksFired;

    private sealed class WeakTargetRecord
    {
        public int OwnerIndex;
        public ObjectHandle Target;
        public Action? OnCollected;
    }
    private const int CardShift = 6;
    private const byte DirtyCard = 1;
    private const byte StickyCard = 2;
    private byte[] _cards = Array.Empty<byte>();
    private readonly List<int> _dirtyCards = new();
    // Tier 4 #22: after this many minor collections, a surviving Young cell
    // is promoted to Old. Default mirrors common nursery survival heuristics.
    public byte PromotionThreshold { get; set; } = 2;

    // Tier 4 #22: auto-trigger a MinorCollect after this many Young
    // allocations. Zero disables the auto-trigger (caller drives GC
    // manually). Default is conservative — large enough that test suites
    // don't pay nursery overhead unnecessarily, small enough that long
    // allocation-heavy runs see periodic minor sweeps.
    public int YoungAllocationsPerMinorGc { get; set; } = 4096;
    public bool DeferAutomaticCollectionUntilSafePoint { get; set; }
    // Long-running browser workloads can keep temporary objects alive across
    // enough nursery collections to promote them. Without a periodic major
    // collection those dead Old cells accumulate until the whole JS realm is
    // discarded (for example, during reCAPTCHA MessagePort/promise churn).
    // Zero disables the automatic major collection cadence.
    public int MinorCollectionsPerMajorGc { get; set; } = 32;
    private int _youngAllocationsSinceLastMinorGc;
    private bool _minorCollectionPending;
    private readonly bool _verifyHeapBeforeGc;
    private readonly bool _verifyHeapAfterGc;
    private readonly HeapVerifier _verifier = new();
    // Diagnostic breadcrumbs: last sweep record per cell index, so a stale
    // handle error can say which collection freed the cell it points at.
    private readonly Dictionary<int, string> _sweepLog = new();

    public JsHeap(
        GcStressMode stressMode = GcStressMode.None,
        bool verifyHeapBeforeGc = false,
        bool verifyHeapAfterGc = false)
    {
        _stressMode = stressMode;
        _verifyHeapBeforeGc = verifyHeapBeforeGc;
        _verifyHeapAfterGc = verifyHeapAfterGc;

        // Historical barrier edges are diagnostic-only. Avoid retaining every
        // object-to-object store during normal browsing when verification is disabled.
        if (_verifyHeapBeforeGc || _verifyHeapAfterGc)
        {
            _writeBarrierEdges = new List<(ObjectHandle Owner, ObjectHandle Child)>();
        }
    }

    public int RootCount => _roots.Count;

    // Audit §1: subsystems whose live JsValue Objects aren't visible through
    // the heap's RootSet (e.g. BytecodeInterpreter's active InterpreterFrame
    // Registers) register here so GC honours those references too.
    public void AddRootSource(IHeapRootSource source) => _rootSources.Add(source);
    public bool RemoveRootSource(IHeapRootSource source) => _rootSources.Remove(source);
    public int WriteBarrierCount => _writeBarrierCount;
    public int GcCollectionCount => _gcCollectionCount;
    public int MinorCollectionCount => _minorGcCount;
    public int LastGcMarkedCells => _lastGcMarkedCells;
    public int LastGcSweptCells => _lastGcSweptCells;
    public int LastMinorMarked => _lastMinorMarked;
    public int LastMinorSwept => _lastMinorSwept;
    public int LastMinorPromoted => _lastMinorPromoted;
    public int LastMinorScannedOldCells => _lastMinorScannedOldCells;
    public long AllocationCount => _allocationCount;
    public int RememberedSetEdgeCount
    {
        get
        {
            return _dirtyCards.Count;
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

        // A GC triggered by this very allocation — the stress-mode full
        // collection below, or the periodic auto-minor collection — runs
        // before the caller can store the returned handle anywhere the
        // tracer can reach (a register, a property). The fresh object is
        // therefore unreachable at collection time and would be swept out
        // from under the caller, leaving the returned handle dangling
        // ("Stale heap handle."). Pin it across the collection it provokes.
        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            var mark = _roots.Count;
            _roots.Push(objHandle);
            CollectGarbage();
            _roots.PopTo(mark);
        }
        else
        {
            MaybeAutoMinorCollect(objHandle);
        }

        return objHandle;
    }

    public StringHandle AllocateString(string value, AllocationSite site)
    {
        _ = site;
        MaybeStressGc();

        var handle = AllocateCell(HeapCellKind.String, new StringPayload(value));
        var stringHandle = new StringHandle(handle.Index, handle.Generation);

        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            // Pin the fresh string across the stress collection it triggers
            // (see AllocateObject for why the in-flight handle must survive).
            var mark = _roots.Count;
            _roots.Push(stringHandle);
            CollectGarbage();
            _roots.PopTo(mark);
        }
        else
        {
            MaybeAutoMinorCollect(stringHandle);
        }

        return stringHandle;
    }

    public SymbolHandle AllocateSymbol(string? description, AllocationSite site)
    {
        _ = site;
        MaybeStressGc();

        var handle = AllocateCell(HeapCellKind.Symbol, new SymbolPayload(description));
        var symbolHandle = new SymbolHandle(handle.Index, handle.Generation);

        if (_stressMode == GcStressMode.AfterEveryAlloc)
        {
            // Pin the fresh symbol across the stress collection it triggers
            // (see AllocateObject for why the in-flight handle must survive).
            var mark = _roots.Count;
            _roots.Push(symbolHandle);
            CollectGarbage();
            _roots.PopTo(mark);
        }
        else
        {
            MaybeAutoMinorCollect(symbolHandle);
        }

        return symbolHandle;
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
            var sweepInfo = _sweepLog.TryGetValue(index, out var info) ? info : "no-sweep-record";
            throw new JsEngineFatalException(
                $"Stale heap handle. idx={index} wantGen={generation} cell={(cell is null ? "null" : $"gen{cell.Generation}/{cell.Kind}")} sweep[{sweepInfo}] minor#{_minorGcCount} major#{_gcCollectionCount}");
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
        _writeBarrierEdges?.Add((owner, child));
        if (ownerCell.Tier == GenerationTier.Old && childCell.Tier == GenerationTier.Young)
        {
            DirtyCardForCell(owner.Index);
        }
    }

    public void MinorCollect()
    {
        if (_verifyHeapBeforeGc) _verifier.Verify(this);

        _minorGcCount++;
        _lastMinorMarked = 0;
        _lastMinorSwept = 0;
        _lastMinorPromoted = 0;
        _lastMinorScannedOldCells = 0;

        foreach (var (index, generation) in _nursery)
        {
            if ((uint)index >= (uint)_cells.Count) continue;
            var cell = _cells[index];
            if (cell is { Tier: GenerationTier.Young } && cell.Generation == generation)
            {
                cell.Marked = false;
            }
        }

        _currentMarkMinorMode = true;
        var marker = new MarkingTracer(this, minorMode: true);
        _roots.Trace(marker);

        // Audit §1: external root sources (interpreter frame registers).
        for (var i = 0; i < _rootSources.Count; i++)
        {
            _rootSources[i].TraceRoots(marker);
        }

        ScanDirtyCards(marker);

        var survivors = new List<(int Index, int Generation)>(_nursery.Count);
        foreach (var entry in _nursery)
        {
            var i = entry.Index;
            if ((uint)i >= (uint)_cells.Count) continue;
            var cell = _cells[i];
            if (cell is null || cell.Generation != entry.Generation || cell.Tier != GenerationTier.Young) continue;

            if (cell.Marked)
            {
                if (cell.MinorSurvivedCount < byte.MaxValue) cell.MinorSurvivedCount++;
                if (cell.MinorSurvivedCount >= PromotionThreshold)
                {
                    cell.Tier = GenerationTier.Old;
                    _lastMinorPromoted++;
                    DirtyCardForCell(i, cell.Payload is JsObject obj && obj.GetType() != typeof(JsObject));
                }
                else
                {
                    survivors.Add(entry);
                }
            }
            else
            {
                _sweepLog[i] = $"gen{cell.Generation}/{cell.Kind} minorGc#{_minorGcCount}";
                _cells[i] = null;
                _lastMinorSwept++;
                if (!_isFree[i])
                {
                    _isFree[i] = true;
                    _freeList.Push(i);
                }
            }
        }

        _nursery.Clear();
        _nursery.AddRange(survivors);
        _currentMarkMinorMode = false;

        // Weak targets whose Young cell died must fire even in a minor
        // collection (e.g. a WeakRef to a nursery-allocated object).
        FireCollectedWeakTargets();

        if (_verifyHeapAfterGc) _verifier.Verify(this);
    }

    // Tier 4 #22: invoked from AllocateObject. Skipped in stress modes
    // because those drive collection on their own cadence.
    private void MaybeAutoMinorCollect(ObjectHandle pin)
    {
        if (YoungAllocationsPerMinorGc <= 0) return;
        _youngAllocationsSinceLastMinorGc++;
        if (_youngAllocationsSinceLastMinorGc >= YoungAllocationsPerMinorGc)
        {
            if (DeferAutomaticCollectionUntilSafePoint)
            {
                _minorCollectionPending = true;
                return;
            }

            _youngAllocationsSinceLastMinorGc = 0;
            // Keep the freshly-allocated object (which triggered this minor
            // collection but is not yet referenced by any root) alive across
            // the sweep.
            var mark = _roots.Count;
            _roots.Push(pin);
            RunAutomaticCollection();
            _roots.PopTo(mark);
        }
    }

    private void MaybeAutoMinorCollect(StringHandle pin)
    {
        if (YoungAllocationsPerMinorGc <= 0) return;
        _youngAllocationsSinceLastMinorGc++;
        if (_youngAllocationsSinceLastMinorGc < YoungAllocationsPerMinorGc) return;
        if (DeferAutomaticCollectionUntilSafePoint)
        {
            _minorCollectionPending = true;
            return;
        }

        _youngAllocationsSinceLastMinorGc = 0;
        var mark = _roots.Count;
        _roots.Push(pin);
        RunAutomaticCollection();
        _roots.PopTo(mark);
    }

    private void MaybeAutoMinorCollect(SymbolHandle pin)
    {
        if (YoungAllocationsPerMinorGc <= 0) return;
        _youngAllocationsSinceLastMinorGc++;
        if (_youngAllocationsSinceLastMinorGc < YoungAllocationsPerMinorGc) return;
        if (DeferAutomaticCollectionUntilSafePoint)
        {
            _minorCollectionPending = true;
            return;
        }

        _youngAllocationsSinceLastMinorGc = 0;
        var mark = _roots.Count;
        _roots.Push(pin);
        RunAutomaticCollection();
        _roots.PopTo(mark);
    }

    public void CollectAtSafePointIfRequested()
    {
        if (!_minorCollectionPending) return;

        _minorCollectionPending = false;
        _youngAllocationsSinceLastMinorGc = 0;
        RunAutomaticCollection();
    }

    private void RunAutomaticCollection()
    {
        MinorCollect();
        if (MinorCollectionsPerMajorGc > 0 &&
            _minorGcCount % MinorCollectionsPerMajorGc == 0)
        {
            CollectGarbage();
        }
    }

    private void DirtyCardForCell(int cellIndex, bool sticky = false)
    {
        var cardIndex = cellIndex >> CardShift;
        if (cardIndex >= _cards.Length)
        {
            Array.Resize(ref _cards, Math.Max(cardIndex + 1, Math.Max(4, _cards.Length * 2)));
        }

        if ((_cards[cardIndex] & DirtyCard) == 0)
        {
            _cards[cardIndex] |= DirtyCard;
            _dirtyCards.Add(cardIndex);
        }
        if (sticky) _cards[cardIndex] |= StickyCard;
    }

    private void ScanDirtyCards(IHeapTracer youngMarker)
    {
        if (_dirtyCards.Count == 0) return;

        var retained = new List<int>(_dirtyCards.Count);
        foreach (var cardIndex in _dirtyCards)
        {
            var cardTracer = new CardTracer(this, youngMarker);
            var start = cardIndex << CardShift;
            var end = Math.Min(start + (1 << CardShift), _cells.Count);
            for (var i = start; i < end; i++)
            {
                if (_cells[i] is not { Tier: GenerationTier.Old } cell) continue;
                _lastMinorScannedOldCells++;
                cell.Payload.Trace(cardTracer);
            }

            var sticky = (_cards[cardIndex] & StickyCard) != 0;
            var keepDirty = sticky || cardTracer.SawYoungReference;
            _cards[cardIndex] = (byte)((sticky ? StickyCard : 0) | (keepDirty ? DirtyCard : 0));
            if (keepDirty) retained.Add(cardIndex);
        }

        _dirtyCards.Clear();
        _dirtyCards.AddRange(retained);
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
        _roots.Trace(marker);

        // Audit §1: roots held by external subsystems (interpreter frames).
        for (var i = 0; i < _rootSources.Count; i++)
        {
            _rootSources[i].TraceRoots(marker);
        }

        // Sweep unreachable cells.
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell is null || cell.Marked)
            {
                continue;
            }

            _sweepLog[i] = $"gen{cell.Generation}/{cell.Kind} majorGc#{_gcCollectionCount}";
            _cells[i] = null;
            _lastGcSweptCells++;
            if (!_isFree[i])
            {
                _isFree[i] = true;
                _freeList.Push(i);
            }
        }

        PruneWriteBarrierEdges();
        foreach (var entry in _nursery)
        {
            if ((uint)entry.Index >= (uint)_cells.Count) continue;
            if (_cells[entry.Index] is { Tier: GenerationTier.Young } cell &&
                cell.Generation == entry.Generation)
            {
                cell.Tier = GenerationTier.Old;
            }
        }
        _nursery.Clear();
        Array.Clear(_cards);
        _dirtyCards.Clear();
        for (var i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is { Tier: GenerationTier.Old, Payload: JsObject obj } &&
                obj.GetType() != typeof(JsObject))
            {
                DirtyCardForCell(i, sticky: true);
            }
        }

        FireCollectedWeakTargets();

        if (_verifyHeapAfterGc)
        {
            _verifier.Verify(this);
        }
    }

    /// <summary>
    /// Registers a weak target: <paramref name="target"/> is observable by
    /// <paramref name="ownerIndex"/> but does NOT keep the target alive.
    /// When the target is collected, <paramref name="onCollected"/> fires.
    /// </summary>
    public void RegisterWeakTarget(int ownerIndex, ObjectHandle target, Action onCollected)
    {
        if (ownerIndex < 0 || (uint)ownerIndex >= (uint)_cells.Count)
        {
            return;
        }

        if (onCollected == null)
        {
            throw new ArgumentNullException(nameof(onCollected));
        }

        _weakTargets.Add(new WeakTargetRecord
        {
            OwnerIndex = ownerIndex,
            Target = target,
            OnCollected = onCollected
        });
    }

    /// <summary>
    /// Removes a previously registered weak target for an owner.
    /// </summary>
    public void UnregisterWeakTargets(int ownerIndex)
    {
        _weakTargets.RemoveAll(r => r.OwnerIndex == ownerIndex);
    }

    /// <summary>
    /// Number of weak-target callbacks fired since startup (diagnostics).
    /// </summary>
    public int WeakTargetCallbacksFired => Volatile.Read(ref _weakTargetCallbacksFired);

    private void FireCollectedWeakTargets()
    {
        for (var i = _weakTargets.Count - 1; i >= 0; i--)
        {
            var record = _weakTargets[i];
            bool targetDead = !IsLiveObject(record.Target);
            bool ownerDead = (uint)record.OwnerIndex >= (uint)_cells.Count ||
                             _cells[record.OwnerIndex] is null;

            if (ownerDead)
            {
                // Owner gone; the weak edge is meaningless.
                _weakTargets.RemoveAt(i);
                continue;
            }

            if (!targetDead)
            {
                continue;
            }

            _weakTargets.RemoveAt(i);
            Interlocked.Increment(ref _weakTargetCallbacksFired);
            try
            {
                record.OnCollected?.Invoke();
            }
            catch
            {
                // A misbehaving owner callback must never break collection.
            }
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
        if (_writeBarrierEdges == null)
        {
            return;
        }

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

    /// <summary>
    /// Non-throwing liveness check for an object handle. Used by
    /// FinalizationRegistry cleanup draining to skip held values that were
    /// collected in the same pass.
    /// </summary>
    public bool IsLiveObjectHandle(ObjectHandle handle)
    {
        return IsLiveObject(handle);
    }

    // Tier 4 #22: the minor-mode tracer needs to propagate through nested
    // child traces, so Mark takes the mode and constructs a matching
    // tracer for the recursive Trace call.
    private bool _currentMarkMinorMode;

    private void Mark(ObjectHandle handle)
    {
        var cell = Validate(handle);
        if (_currentMarkMinorMode && cell.Tier == GenerationTier.Old) return;
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        if (_currentMarkMinorMode) _lastMinorMarked++;
        else _lastGcMarkedCells++;
        cell.Payload.Trace(new MarkingTracer(this, _currentMarkMinorMode));
    }

    private void Mark(StringHandle handle)
    {
        var cell = Validate(handle);
        if (_currentMarkMinorMode && cell.Tier == GenerationTier.Old) return;
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        if (_currentMarkMinorMode) _lastMinorMarked++;
        else _lastGcMarkedCells++;
        cell.Payload.Trace(new MarkingTracer(this, _currentMarkMinorMode));
    }

    private void Mark(SymbolHandle handle)
    {
        var cell = Validate(handle);
        if (_currentMarkMinorMode && cell.Tier == GenerationTier.Old) return;
        if (cell.Marked)
        {
            return;
        }

        cell.Marked = true;
        if (_currentMarkMinorMode) _lastMinorMarked++;
        else _lastGcMarkedCells++;
        cell.Payload.Trace(new MarkingTracer(this, _currentMarkMinorMode));
    }

    private (int Index, int Generation) AllocateCell(HeapCellKind kind, ITraceable payload)
    {
        _allocationCount++;
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

        _nursery.Add((index, generation));
        return (index, generation);
    }

    public IReadOnlyList<HeapCell?> GetCellsSnapshotForTest() => _cells;
    public int GetGenerationForCellIndexForTest(int index) => _generations[index];

    /// <summary>
    /// Returns the live cell payload at an index, or null when the cell is free.
    /// Used by weak-target callbacks that must reach their owner object's payload
    /// after a collection has completed.
    /// </summary>
    public HeapCell? GetCellForIndexForTest(int index)
    {
        if ((uint)index >= (uint)_cells.Count)
        {
            return null;
        }

        return _cells[index];
    }
    public IReadOnlyList<int> GetGenerationsSnapshotForTest() => _generations;
    public IReadOnlyList<bool> GetFreeFlagsSnapshotForTest() => _isFree;
    public IReadOnlyList<int> GetFreeListSnapshotForTest() => _freeList.ToArray();

    public IReadOnlyList<ObjectHandle> GetRootsSnapshotForTest() => _roots.Snapshot();
    public IReadOnlyList<StringHandle> GetStringRootsSnapshotForTest() => _roots.StringSnapshot();
    public IReadOnlyList<SymbolHandle> GetSymbolRootsSnapshotForTest() => _roots.SymbolSnapshot();

    public IReadOnlyList<(ObjectHandle Owner, ObjectHandle Child)> GetWriteBarrierEdgesSnapshotForTest() =>
        _writeBarrierEdges is { } edges
            ? edges
            : Array.Empty<(ObjectHandle Owner, ObjectHandle Child)>();

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

        private readonly bool _minorMode;

        public MarkingTracer(JsHeap heap, bool minorMode = false)
        {
            _heap = heap;
            _minorMode = minorMode;
            _ = _minorMode;
        }

        public void Trace(ObjectHandle handle)
        {
            _heap.Mark(handle);
        }

        public void Trace(StringHandle handle)
        {
            _heap.Mark(handle);
        }

        public void Trace(SymbolHandle handle)
        {
            _heap.Mark(handle);
        }
    }

    private sealed class CardTracer : IHeapTracer
    {
        private readonly JsHeap _heap;
        private readonly IHeapTracer _youngMarker;

        public CardTracer(JsHeap heap, IHeapTracer youngMarker)
        {
            _heap = heap;
            _youngMarker = youngMarker;
        }

        public bool SawYoungReference { get; private set; }

        public void Trace(ObjectHandle handle)
        {
            if (_heap.Validate(handle).Tier != GenerationTier.Young) return;
            SawYoungReference = true;
            _youngMarker.Trace(handle);
        }

        public void Trace(StringHandle handle)
        {
            if (_heap.Validate(handle).Tier != GenerationTier.Young) return;
            SawYoungReference = true;
            _youngMarker.Trace(handle);
        }

        public void Trace(SymbolHandle handle)
        {
            if (_heap.Validate(handle).Tier != GenerationTier.Young) return;
            SawYoungReference = true;
            _youngMarker.Trace(handle);
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
