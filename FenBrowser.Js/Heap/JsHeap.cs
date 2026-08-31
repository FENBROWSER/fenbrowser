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
    private GcStressMode _stressMode;
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
    private byte[] _cards = Array.Empty<byte>();
    private readonly List<int> _dirtyCards = new();
    // Remembered-environment set: environment records are not heap cells, so a
    // young object stored into a binding of a record reachable only through Old
    // (promoted) cells cannot dirty any card. Such records register here on
    // every object-valued binding store; each minor collection scans them once
    // (not once per referencing closure) and drops the ones whose bindings no
    // longer hold young cells. See EnvironmentRecord.RememberBindingStore.
    private readonly List<FenBrowser.Js.Environments.EnvironmentRecord> _rememberedEnvironments = new();
    private readonly HashSet<FenBrowser.Js.Environments.EnvironmentRecord>? _auditEnvironments;
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
    private readonly bool _auditRememberedSet = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_GC_AUDIT_REMEMBERED"),
        "1",
        StringComparison.Ordinal);
    private readonly HeapVerifier _verifier = new();
    // Diagnostic breadcrumbs: last sweep record per cell index, so a stale
    // handle error can say which collection freed the cell it points at.
    // Stored as a compact struct and formatted only when a stale-handle error
    // actually needs it — eager string formatting here used to dominate
    // collection cost in allocation-heavy workloads.
    private readonly Dictionary<int, SweepRecord> _sweepLog = new();
    // Diagnostic: last allocation site per cell index, so a stale-handle fatal
    // can name the payload's producer. Rewritten on slot reuse.
    private string[] _allocationSites = Array.Empty<string>();
    private string _pendingAllocationSite = string.Empty;

    // Native-interleave allocation pin: values freshly allocated on behalf of a
    // native/CLR caller are held in C# locals the tracer cannot see, and a
    // safe-point collection during a nested call can sweep such an object
    // before the native body stores it somewhere traceable. A bounded FIFO of
    // recent allocations (a mini-nursery above the real one) keeps each new
    // object reachable far longer than any native-local window; entries simply
    // age out as later allocations arrive.
    private const int AllocationPinRingSize = 1024;
    private const int AllocationPinRingMask = AllocationPinRingSize - 1;
    private readonly ObjectHandle[] _allocationPinRing = new ObjectHandle[AllocationPinRingSize];
    private int _allocationPinIndex;
    // Recording is active only while a native body is on the call stack: pure
    // JS execution roots every fresh object through its frame registers, so
    // pinning there would violate observable reclamation (WeakRef, direct
    // collect assertions). The dangerous window is exclusively
    // native-allocates-then-calls-nested-JS.
    private int _nativeExecutionDepth;

    public void BeginNativeExecution() => _nativeExecutionDepth++;
    public void EndNativeExecution() => _nativeExecutionDepth--;

    private void TraceAllocationPinRing(IHeapTracer tracer)
    {
        foreach (var pinned in _allocationPinRing)
        {
            // Aged-out entries may reference swept cells; only live handles
            // still provide reachability. Non-throwing check (see its use in
            // FinalizationRegistry draining).
            if (IsLiveObjectHandle(pinned))
            {
                tracer.Trace(pinned);
            }
        }
    }

    private readonly record struct SweepRecord(int Generation, HeapCellKind Kind, int MinorGc, int MajorGc, string PayloadType, string Collector)
    {
        public string Describe() => $"{Collector}:gen{Generation}/{Kind}({PayloadType}) minor#{MinorGc} major#{MajorGc}";
    }

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
        if (_auditRememberedSet)
        {
            _auditEnvironments = new HashSet<FenBrowser.Js.Environments.EnvironmentRecord>(
                ReferenceEqualityComparer.Instance);
        }
    }

    public int RootCount => _roots.Count;

    // Diagnostic/test hook: enables stress collection after engine construction so
    // tests can exercise unrooted host-to-JS windows without stressing the whole
    // boot sequence. Not a production configuration surface.
    internal void SetStressModeForDiagnostics(GcStressMode mode) => _stressMode = mode;

    // Construction window (audit JSRT rooting family): while open, every cell
    // allocated through this heap is pushed as an explicit root, and the whole
    // window's pins are popped together on close. Host builders create graphs
    // bottom-up (record facades → element arrays → top-level object) where a
    // freshly made cell has no incoming JS edge until its parent materializes;
    // under automatic or stress collection those intermediates sweep and later
    // resurface as stale handles. Over-retention is bounded by the window.
    private int _constructionWindowDepth;
    private int _constructionWindowMark;

    public IDisposable BeginConstructionWindow()
    {
        if (++_constructionWindowDepth == 1)
        {
            _constructionWindowMark = _roots.Count;
        }

        return new ConstructionWindowScope(this);
    }

    private void EndConstructionWindow()
    {
        if (--_constructionWindowDepth == 0)
        {
            _roots.PopTo(_constructionWindowMark);
        }
    }

    private sealed class ConstructionWindowScope : IDisposable
    {
        private JsHeap? _owner;

        public ConstructionWindowScope(JsHeap owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            _owner?.EndConstructionWindow();
            _owner = null;
        }
    }

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
    public int RememberedEnvironmentCount => _rememberedEnvironments.Count;
    public int LiveCellCount => _cells.Count(c => c is not null);

    public ObjectHandle AllocateObject(JsObject obj, AllocationSite site)
    {
        _pendingAllocationSite = site.MemberName;
        MaybeStressGc();

        var handle = AllocateCell(HeapCellKind.Object, obj);
        var objHandle = new ObjectHandle(handle.Index, handle.Generation);
        if (_nativeExecutionDepth > 0)
        {
            _allocationPinRing[_allocationPinIndex] = objHandle;
            _allocationPinIndex = (_allocationPinIndex + 1) & AllocationPinRingMask;
        }
        if (_constructionWindowDepth > 0)
        {
            _roots.Push(objHandle);
        }
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
        if (_constructionWindowDepth > 0)
        {
            _roots.Push(stringHandle);
        }

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
        if (_constructionWindowDepth > 0)
        {
            _roots.Push(symbolHandle);
        }

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
            var sweepInfo = _sweepLog.TryGetValue(index, out var info) ? info.Describe() : "no-sweep-record";
            var allocSite = GetAllocationSiteForDiagnostics(index);
            throw new JsEngineFatalException(
                $"Stale heap handle. idx={index} wantGen={generation} cell={(cell is null ? "null" : $"gen{cell.Generation}/{cell.Kind}")} sweep[{sweepInfo}] allocSite={allocSite} rememberedEnvs={_rememberedEnvironments.Count} envRegs={_rememberedEnvironmentRegistrations} envScanMarks={_rememberedEnvironmentScanMarks} minor#{_minorGcCount} major#{_gcCollectionCount}");
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
        _sharedMarkingTracer ??= new MarkingTracer(this, minorMode: true);
        var marker = _sharedMarkingTracer;
        _roots.Trace(marker);
        TraceAllocationPinRing(marker);

        // Audit §1: external root sources (interpreter frame registers).
        for (var i = 0; i < _rootSources.Count; i++)
        {
            _rootSources[i].TraceRoots(marker);
        }

        ScanDirtyCards(marker);
        ScanRememberedEnvironments(marker);
        if (_auditRememberedSet)
        {
            AuditRememberedSet();
        }
        var auditDirectRoots = _auditRememberedSet ? CaptureDirectRootSources() : null;

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
                    // Dirty (non-sticky) so the promoted cell's remaining Young
                    // references are scanned next minor; the card self-clears
                    // once nothing Young is reachable from it. Internal-slot
                    // stores on Old cells go through write barriers, so no
                    // sticky rescans are needed.
                    DirtyCardForCell(i);
                }
                else
                {
                    survivors.Add(entry);
                }
            }
            else
            {
                if (auditDirectRoots is not null &&
                    cell.Kind == HeapCellKind.Object &&
                    auditDirectRoots.TryGetValue(new ObjectHandle(i, cell.Generation), out var directRootSource))
                {
                    throw new JsEngineFatalException(
                        $"Direct root was not marked. source={directRootSource} " +
                        $"child={i}/{cell.Payload.GetType().Name} " +
                        $"allocSite={GetAllocationSiteForDiagnostics(i)} " +
                        $"minor#{_minorGcCount} major#{_gcCollectionCount}");
                }
                _sweepLog[i] = new SweepRecord(cell.Generation, cell.Kind, _minorGcCount, _gcCollectionCount, cell.Payload.GetType().Name, "minor");
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

    /// <summary>
    /// Registers an environment record whose bindings may hold young cells.
    /// Deduplicated by the record's <c>IsRememberedForMinorGc</c> flag; the
    /// set self-cleans in <see cref="ScanRememberedEnvironments"/> when a minor
    /// collection proves the record's bindings hold no young cells.
    /// </summary>
    private int _rememberedEnvironmentRegistrations;

    public int RememberedEnvironmentRegistrations => _rememberedEnvironmentRegistrations;

    public void RememberEnvironment(FenBrowser.Js.Environments.EnvironmentRecord record)
    {
        _auditEnvironments?.Add(record);
        if (record.IsRememberedForMinorGc)
        {
            return;
        }

        record.IsRememberedForMinorGc = true;
        _rememberedEnvironments.Add(record);
        _rememberedEnvironmentRegistrations++;
    }

    private int _rememberedEnvironmentScanMarks;

    private void ScanRememberedEnvironments(IHeapTracer youngMarker)
    {
        if (_rememberedEnvironments.Count == 0)
        {
            return;
        }

        var marksBefore = _lastMinorMarked;
        var retained = new List<FenBrowser.Js.Environments.EnvironmentRecord>(_rememberedEnvironments.Count);
        var envTracer = new CardTracer(this, youngMarker);
        foreach (var record in _rememberedEnvironments)
        {
            envTracer.Reset();
            // TraceOwnEdges visits only this record's bindings (and subclass
            // extras such as import targets), never the outer chain — exactly
            // the edges the remembered set must cover.
            record.TraceOwnEdges(envTracer);
            if (envTracer.SawYoungReference)
            {
                retained.Add(record);
            }
            else
            {
                record.IsRememberedForMinorGc = false;
            }
        }

        _rememberedEnvironmentScanMarks += _lastMinorMarked - marksBefore;
        _rememberedEnvironments.Clear();
        _rememberedEnvironments.AddRange(retained);
    }

    private void ClearRememberedEnvironments()
    {
        for (var i = 0; i < _rememberedEnvironments.Count; i++)
        {
            _rememberedEnvironments[i].IsRememberedForMinorGc = false;
        }

        _rememberedEnvironments.Clear();
    }

    private void DirtyCardForCell(int cellIndex)
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
    }

    private void ScanDirtyCards(IHeapTracer youngMarker)
    {
        if (_dirtyCards.Count == 0) return;

        var retained = new List<int>(_dirtyCards.Count);
        var cardTracer = new CardTracer(this, youngMarker);
        foreach (var cardIndex in _dirtyCards)
        {
            cardTracer.Reset();
            var start = cardIndex << CardShift;
            var end = Math.Min(start + (1 << CardShift), _cells.Count);
            for (var i = start; i < end; i++)
            {
                if (_cells[i] is not { Tier: GenerationTier.Old } cell) continue;
                _lastMinorScannedOldCells++;
                cell.Payload.Trace(cardTracer);
            }

            // A card stays remembered only while an Old cell inside it still
            // references a Young cell; otherwise it self-clears.
            var keepDirty = cardTracer.SawYoungReference;
            _cards[cardIndex] = keepDirty ? DirtyCard : (byte)0;
            if (keepDirty) retained.Add(cardIndex);
        }

        _dirtyCards.Clear();
        _dirtyCards.AddRange(retained);
    }

    private void AuditRememberedSet()
    {
        for (var i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is not { Tier: GenerationTier.Old } cell)
            {
                continue;
            }

            cell.Payload.Trace(new RememberedSetAuditTracer(this, i, cell.Payload.GetType().Name));
        }

        if (_auditEnvironments is null)
        {
            return;
        }

        foreach (var record in _auditEnvironments)
        {
            record.TraceOwnEdges(new RememberedSetAuditTracer(
                this,
                -1,
                $"env:{record.GetType().Name}:remembered={record.IsRememberedForMinorGc}"));
        }
    }

    private Dictionary<ObjectHandle, string> CaptureDirectRootSources()
    {
        var roots = new Dictionary<ObjectHandle, string>();
        foreach (var root in _roots.Snapshot())
        {
            roots.TryAdd(root, "HeapRootSet");
        }
        var tracer = new DirectRootCaptureTracer(roots);
        for (var i = 0; i < _rootSources.Count; i++)
        {
            tracer.Source = _rootSources[i].GetType().FullName ?? _rootSources[i].GetType().Name;
            _rootSources[i].TraceRoots(tracer);
        }

        return roots;
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
        _sharedMarkingTracer ??= new MarkingTracer(this);
        var marker = _sharedMarkingTracer;
        _roots.Trace(marker);
        TraceAllocationPinRing(marker);

        // Audit §1: roots held by external subsystems (interpreter frames).
        for (var i = 0; i < _rootSources.Count; i++)
        {
            _rootSources[i].TraceRoots(marker);
        }

        var auditDirectRoots = _auditRememberedSet ? CaptureDirectRootSources() : null;
        if (_auditRememberedSet)
        {
            for (var i = 0; i < _cells.Count; i++)
            {
                if (_cells[i] is not { Marked: true } markedCell)
                {
                    continue;
                }

                markedCell.Payload.Trace(new MajorReachabilityAuditTracer(
                    this,
                    i,
                    markedCell.Payload.GetType().Name));
            }
        }

        // Sweep unreachable cells.
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell is null || cell.Marked)
            {
                continue;
            }


            if (auditDirectRoots is not null &&
                cell.Kind == HeapCellKind.Object &&
                auditDirectRoots.TryGetValue(new ObjectHandle(i, cell.Generation), out var directRootSource))
            {
                throw new JsEngineFatalException(
                    $"Direct root was not marked by major GC. source={directRootSource} " +
                    $"child={i}/{cell.Payload.GetType().Name} " +
                    $"allocSite={GetAllocationSiteForDiagnostics(i)} " +
                    $"minorMode={_currentMarkMinorMode} minor#{_minorGcCount} major#{_gcCollectionCount}");
            }

            _sweepLog[i] = new SweepRecord(cell.Generation, cell.Kind, _minorGcCount, _gcCollectionCount, cell.Payload.GetType().Name, "major");
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
        // After a major collection every surviving cell is Old and the nursery
        // is empty, so no old→young edges exist yet: remembered-set cards and
        // remembered environments re-populate through write barriers and
        // binding stores from this point on. (Previously every Old non-plain
        // object was re-marked sticky here, which made each minor collection
        // rescan the entire Old population — the dominant cost of long-running
        // MessagePort/promise workloads.)
        ClearRememberedEnvironments();

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

    // Explicit worklist backing the iterative object-mark traversal. Kept on
    // the heap instance so nested Mark() re-entry (payload.Trace callbacks)
    // shares one queue instead of growing the call stack.
    private readonly Stack<ObjectHandle> _markWorklist = new();
    private bool _isDrainingMarkWorklist;
    // MarkingTracer is stateless (heap reference only), so one instance serves
    // every Trace callback of a collection instead of allocating a tracer per
    // marked cell — a full collection marks the entire live heap.
    private MarkingTracer? _sharedMarkingTracer;

    private MarkingTracer GetMarkingTracer()
    {
        return _sharedMarkingTracer ??= new MarkingTracer(this, _currentMarkMinorMode);
    }

    private void Mark(ObjectHandle handle)
    {
        // Iterative depth-first marking with an explicit worklist. Object
        // payloads are the only recursive edge (strings and symbols trace no
        // children), and real-world bundles build graphs far deeper than the
        // native stack can recurse over during GC. Push-and-drain keeps the
        // reachable set identical to the previous recursive traversal while
        // using constant call-stack depth; duplicate pushes are discarded by
        // the Marked check on pop.
        var worklist = _markWorklist;
        worklist.Push(handle);
        if (_isDrainingMarkWorklist)
        {
            return;
        }

        _isDrainingMarkWorklist = true;
        try
        {
            while (worklist.Count > 0)
            {
                var cell = Validate(worklist.Pop());
                if (_currentMarkMinorMode && cell.Tier == GenerationTier.Old) continue;
                if (cell.Marked)
                {
                    continue;
                }

                cell.Marked = true;
                if (_currentMarkMinorMode) _lastMinorMarked++;
                else _lastGcMarkedCells++;
                cell.Payload.Trace(GetMarkingTracer());
            }
        }
        finally
        {
            worklist.Clear();
            _isDrainingMarkWorklist = false;
        }
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
        cell.Payload.Trace(GetMarkingTracer());
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
        cell.Payload.Trace(GetMarkingTracer());
    }

    private string GetAllocationSiteForDiagnostics(int index) =>
        (uint)index < (uint)_allocationSites.Length ? _allocationSites[index] : string.Empty;

    private void RecordAllocationSite(int index)
    {
        if (index >= _allocationSites.Length)
        {
            Array.Resize(ref _allocationSites, Math.Max(index + 1, _allocationSites.Length * 2));
        }

        _allocationSites[index] = _pendingAllocationSite;
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
            RecordAllocationSite(index);
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
            RecordAllocationSite(index);
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

        // Reused across cards/records within one scan: clears the per-item flag.
        public void Reset() => SawYoungReference = false;

        public bool TraceEnvironmentChains => false;

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

    private sealed class RememberedSetAuditTracer : IHeapTracer
    {
        private readonly JsHeap _heap;
        private readonly int _ownerIndex;
        private readonly string _ownerType;

        public RememberedSetAuditTracer(JsHeap heap, int ownerIndex, string ownerType)
        {
            _heap = heap;
            _ownerIndex = ownerIndex;
            _ownerType = ownerType;
        }

        public bool TraceEnvironmentChains => false;

        public void Trace(ObjectHandle handle)
        {
            var child = _heap.Validate(handle);
            if (child.Tier == GenerationTier.Young && !child.Marked)
            {
                throw new JsEngineFatalException(
                    $"Missing remembered-set edge. owner={_ownerIndex}/{_ownerType} " +
                    $"child={handle.Index}/{child.Payload.GetType().Name} " +
                    $"allocSite={_heap.GetAllocationSiteForDiagnostics(handle.Index)} " +
                    $"minor#{_heap._minorGcCount} major#{_heap._gcCollectionCount}");
            }
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

    private sealed class DirectRootCaptureTracer : IHeapTracer
    {
        private readonly Dictionary<ObjectHandle, string> _roots;

        public DirectRootCaptureTracer(Dictionary<ObjectHandle, string> roots)
        {
            _roots = roots;
        }

        public string Source { get; set; } = string.Empty;

        public void Trace(ObjectHandle handle)
        {
            _roots.TryAdd(handle, Source);
        }

        public void Trace(StringHandle handle) => _ = handle;

        public void Trace(SymbolHandle handle) => _ = handle;
    }

    private sealed class MajorReachabilityAuditTracer : IHeapTracer
    {
        private readonly JsHeap _heap;
        private readonly int _ownerIndex;
        private readonly string _ownerType;

        public MajorReachabilityAuditTracer(JsHeap heap, int ownerIndex, string ownerType)
        {
            _heap = heap;
            _ownerIndex = ownerIndex;
            _ownerType = ownerType;
        }

        public void Trace(ObjectHandle handle)
        {
            var child = _heap.Validate(handle);
            if (!child.Marked)
            {
                throw new JsEngineFatalException(
                    $"Major GC missed reachable edge. owner={_ownerIndex}/{_ownerType} " +
                    $"child={handle.Index}/{child.Payload.GetType().Name} " +
                    $"allocSite={_heap.GetAllocationSiteForDiagnostics(handle.Index)} " +
                    $"minor#{_heap._minorGcCount} major#{_heap._gcCollectionCount}");
            }
        }

        public void Trace(StringHandle handle) => _ = _heap.Validate(handle);

        public void Trace(SymbolHandle handle) => _ = _heap.Validate(handle);
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
