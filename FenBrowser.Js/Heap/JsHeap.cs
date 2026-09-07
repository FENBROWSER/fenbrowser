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

    // Construction-window pins live in their own set, never on _roots. Permanent
    // intrinsic roots are pushed onto _roots by the builtin installers (PushRoot,
    // never popped); if a window marked and popped _roots it would discard any
    // permanent root a wrapped installer pushed while the window was open.
    private readonly RootSet _constructionPins = new();
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
    // every Young-object binding store; each minor collection scans them once
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
    // A minor collection every 4096 young allocations is far too eager for a
    // real page: Google's robot check ran 299 of them inside a single
    // callback, which was about a fifth of that callback's 22 seconds. Eight
    // times the budget is still only a few megabytes of nursery before a
    // collection, and it takes that to roughly forty.
    public int YoungAllocationsPerMinorGc { get; set; } = 131072;
    public bool DeferAutomaticCollectionUntilSafePoint { get; set; }
    // Long-running browser workloads can keep temporary objects alive across
    // enough nursery collections to promote them. Without a periodic major
    // collection those dead Old cells accumulate until the whole JS realm is
    // discarded (for example, during reCAPTCHA MessagePort/promise churn).
    // Zero disables the automatic major collection cadence.
    public int MinorCollectionsPerMajorGc { get; set; } = 32;

    // Only a major collection reclaims Old cells, and only a major drains the
    // remembered-environment set - which is a root set, so until one runs, an
    // environment record belonging to a dead closure keeps its bindings alive
    // (textbook nepotism). Counting minor collections ties that to the
    // allocation *rate* rather than to how much garbage has accumulated: on
    // google.com/recaptcha/api2/demo one callback allocated 1.13M cells in 15s,
    // never reached 32 minors, and ended with 1.09M of them still live and
    // 280,242 remembered records marking 92% of every nursery it collected.
    //
    // So also collect when the live set has grown by this factor since the last
    // major. That bounds retained garbage by a multiple of what is genuinely
    // live instead of by how fast the page happens to allocate.
    public double MajorGcHeapGrowthFactor { get; set; } = 2.0;

    // Below this the heap is small enough that the growth rule would fire on
    // noise; a major there costs more than the garbage it reclaims.
    public int MajorGcGrowthFloorCells { get; set; } = 65536;
    private int _liveCellsAfterLastMajor;
    private int _youngAllocationsSinceLastMinorGc;
    private bool _minorCollectionPending;
    private readonly bool _verifyHeapBeforeGc;
    private readonly bool _verifyHeapAfterGc;
    private static readonly bool SweepLogRequested = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_GC_SWEEPLOG"),
        "1",
        StringComparison.Ordinal);

    private readonly bool _auditRememberedSet = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_GC_AUDIT_REMEMBERED"),
        "1",
        StringComparison.Ordinal);

    // Root-path auditing. A dangling entry in the root set kills the collector
    // from inside its own mark phase, and the run ends at the first one, so a
    // live page yields one sample and no idea whether there are others. With
    // this on, a root handle that does not resolve is reported and skipped
    // instead of thrown on, and the collection completes — one run enumerates
    // every bad root rather than aborting at the first.
    private static readonly bool RootAuditRequested = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_GC_ROOT_AUDIT"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Where root-audit reports go. The heap cannot reach the host's logger,
    /// and a diagnostic nobody reads is not a diagnostic; the embedder points
    /// this at its trace log.
    /// </summary>
    public static Action<string>? DiagnosticSink { get; set; }

    /// <summary>True when <c>FEN_FENJS_GC_ROOT_AUDIT=1</c> asked for non-fatal root auditing.</summary>
    public static bool RootAuditEnabled => RootAuditRequested;

    /// <summary>
    /// True when a diagnostic that reads per-cell allocation-site tags is on.
    /// Producers of those tags check this before building a richer label than
    /// the caller-member name: an identifying tag costs a string per
    /// allocation, which is not a price to pay when nothing reads it.
    /// </summary>
    public static bool AllocationTaggingEnabled => SweepLogRequested || RootAuditRequested;

    // Which root slot the collector is walking, for the stale-handle message.
    private string _rootTraceContext = string.Empty;

    // Which root source marked how many young cells in the last minor
    // collection. A nursery that survives says nothing on its own; the caller
    // needs to know what was holding it, and that is only knowable here, while
    // the root walk is in progress.
    private readonly Dictionary<string, int> _minorRootMarks = new(StringComparer.Ordinal);
    private int _rootTraceMarkBaseline;
    private int _lastMinorNurserySize;
    // One report per (context, handle): a bad root is re-walked by every
    // collection, and repeating it drowns the log it is meant to inform.
    private readonly HashSet<string> _reportedDeadRoots = new(StringComparer.Ordinal);
    private readonly List<string> _rootAuditReports = new();
    private readonly HeapVerifier _verifier = new();
    // Diagnostic breadcrumbs: last sweep record per cell index, so a stale
    // handle error can say which collection freed the cell it points at.
    // Stored as a compact struct and formatted only when a stale-handle error
    // actually needs it — eager string formatting here used to dominate
    // collection cost in allocation-heavy workloads.
    // What was last swept from each slot, for the stale-handle message. One
    // dictionary write and one GetType().Name per swept cell is far too much to
    // pay on every collection of every page -- and nothing ever cleared it, so
    // it grew a string per slot for the life of the heap. Off unless asked for.
    private readonly Dictionary<int, SweepRecord>? _sweepLog;
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
        for (var i = 0; i < _allocationPinRing.Length; i++)
        {
            var pinned = _allocationPinRing[i];
            // Aged-out entries may reference swept cells; only live handles
            // still provide reachability. Non-throwing check (see its use in
            // FinalizationRegistry draining).
            if (IsLiveObjectHandle(pinned))
            {
                tracer.TraceRoot($"heap.allocationPinRing[{i}]", pinned);
            }
        }
    }

    private readonly record struct SweepRecord(
        int Generation,
        HeapCellKind Kind,
        int MinorGc,
        int MajorGc,
        string PayloadType,
        string AllocationSite,
        string Collector)
    {
        public string Describe() =>
            $"{Collector}:gen{Generation}/{Kind}({PayloadType}) allocSite={AllocationSite} minor#{MinorGc} major#{MajorGc}";
    }

    // Every live heap, for the stale-handle diagnostic below only. Weak so a
    // heap that goes away is not kept alive by being on this list.
    private static readonly object LiveHeapsGate = new();
    private static readonly List<WeakReference<JsHeap>> LiveHeaps = new();
    private static int _heapIdCounter;

    /// <summary>Identifies this heap in diagnostics. Heaps are per-realm.</summary>
    public int HeapId { get; }

    public JsHeap(
        GcStressMode stressMode = GcStressMode.None,
        bool verifyHeapBeforeGc = false,
        bool verifyHeapAfterGc = false)
    {
        _stressMode = stressMode;
        _verifyHeapBeforeGc = verifyHeapBeforeGc;
        _verifyHeapAfterGc = verifyHeapAfterGc;
        HeapId = System.Threading.Interlocked.Increment(ref _heapIdCounter);
        lock (LiveHeapsGate)
        {
            LiveHeaps.Add(new WeakReference<JsHeap>(this));
        }

        // Historical barrier edges are diagnostic-only. Avoid retaining every
        // object-to-object store during normal browsing when verification is disabled.
        if (_verifyHeapBeforeGc || _verifyHeapAfterGc)
        {
            _writeBarrierEdges = new List<(ObjectHandle Owner, ObjectHandle Child)>();
        }

        if (SweepLogRequested || _verifyHeapBeforeGc || _verifyHeapAfterGc)
        {
            _sweepLog = new Dictionary<int, SweepRecord>();
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
    // allocated through this heap is pinned in _constructionPins, and the whole
    // window's pins are released together on close. Host builders create graphs
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
            _constructionWindowMark = _constructionPins.Count;
        }

        return new ConstructionWindowScope(this);
    }

    private void EndConstructionWindow()
    {
        if (--_constructionWindowDepth == 0)
        {
            _constructionPins.PopTo(_constructionWindowMark);
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
    /// <summary>
    /// Names the root slot being traced, so a handle that fails to resolve
    /// during marking reports the root that held it. Cleared by
    /// <see cref="EndRootTrace"/> once the root walk is over, so an edge found
    /// inside an object payload is not mistaken for a root.
    /// </summary>
    internal void BeginRootTrace(string context)
    {
        RecordRootTraceMarks();
        _rootTraceContext = context;
        _rootTraceMarkBaseline = _lastMinorMarked;
    }

    private void RecordRootTraceMarks()
    {
        if (!_currentMarkMinorMode || _rootTraceContext.Length == 0) return;
        var delta = _lastMinorMarked - _rootTraceMarkBaseline;
        if (delta <= 0) return;
        _minorRootMarks[_rootTraceContext] =
            _minorRootMarks.TryGetValue(_rootTraceContext, out var previous) ? previous + delta : delta;
    }

    internal void EndRootTrace()
    {
        RecordRootTraceMarks();
        _rootTraceContext = string.Empty;
        _rootTraceMarkBaseline = _lastMinorMarked;
    }

    /// <summary>
    /// Last minor collection, one entry per root source: how many young cells
    /// that source was responsible for marking, largest first. Empty until a
    /// minor collection has run.
    /// </summary>
    public string LastMinorRootBreakdown
    {
        get
        {
            if (_minorRootMarks.Count == 0) return string.Empty;
            var parts = new List<KeyValuePair<string, int>>(_minorRootMarks);
            parts.Sort((a, b) => b.Value.CompareTo(a.Value));
            return string.Join(" ", parts.ConvertAll(entry => $"{entry.Key}={entry.Value}"));
        }
    }

    public int LastMinorNurserySize => _lastMinorNurserySize;
    public int NurserySize => _nursery.Count;

    /// <summary>
    /// Root-audit hook: reports a root handle that does not resolve in this
    /// heap and tells the caller to skip it. Returns false when auditing is
    /// off or the handle is fine, and the root is traced normally.
    /// </summary>
    internal bool ShouldSkipDeadRoot(string context, ObjectHandle handle)
    {
        if (!RootAuditRequested || IsLiveObject(handle))
        {
            return false;
        }

        var key = $"{context}#{handle.Index}/{handle.Generation}";
        if (_reportedDeadRoots.Add(key))
        {
            var report =
                $"Dead GC root. heap#{HeapId} rootCtx={context} " +
                $"idx={handle.Index} wantGen={handle.Generation} " +
                $"cell={DescribeCellForDiagnostics(handle.Index)} " +
                $"allocSite={GetAllocationSiteForDiagnostics(handle.Index)} " +
                $"{DescribeHandleOwner(handle.Index, handle.Generation)} " +
                $"minor#{_minorGcCount} major#{_gcCollectionCount}";
            _rootAuditReports.Add(report);
            DiagnosticSink?.Invoke(report);
        }

        return true;
    }

    /// <summary>
    /// The root-audit reports collected so far, newest last. Bounded only by
    /// the number of distinct bad roots, which is the point of collecting them.
    /// </summary>
    public IReadOnlyList<string> RootAuditReports => _rootAuditReports;

    /// <summary>
    /// Non-throwing description of what a handle currently points at in this
    /// heap, and which other live heap owns it if this one does not. For
    /// callers auditing a handle before it reaches the root set.
    /// </summary>
    public string DescribeHandleForDiagnostics(ObjectHandle handle)
    {
        if ((uint)handle.Index >= (uint)_cells.Count)
        {
            return $"heap#{HeapId} idx={handle.Index} out-of-range(cells={_cells.Count})";
        }

        return
            $"heap#{HeapId} idx={handle.Index} wantGen={handle.Generation} " +
            $"cell={DescribeCellForDiagnostics(handle.Index)} " +
            $"allocSite={GetAllocationSiteForDiagnostics(handle.Index)} " +
            $"{DescribeHandleOwner(handle.Index, handle.Generation)}";
    }

    private string DescribeCellForDiagnostics(int index)
    {
        if ((uint)index >= (uint)_cells.Count)
        {
            return "out-of-range";
        }

        var cell = _cells[index];
        return cell is null
            ? "null"
            : $"gen{cell.Generation}/{cell.Kind}({cell.Payload.GetType().Name})";
    }

    public void AddRootSource(IHeapRootSource source) => _rootSources.Add(source);
    public bool RemoveRootSource(IHeapRootSource source) => _rootSources.Remove(source);
    public int WriteBarrierCount => _writeBarrierCount;
    public int GcCollectionCount => _gcCollectionCount;
    public int MinorCollectionCount => _minorGcCount;

    // Collection time, so "the page spent twenty seconds in one callback" can
    // be attributed rather than guessed at.
    private long _majorGcTicks;
    private long _minorGcTicks;

    public double MajorGcMilliseconds => _majorGcTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public double MinorGcMilliseconds => _minorGcTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    /// <summary>Slots the heap has ever grown to, live or free.</summary>
    public int CellSlotCount => _cells.Count;

    private long _lastGcPropertySlots;

    /// <summary>Property slots walked by the last collection's marking phase.</summary>
    public long LastGcPropertySlots => _lastGcPropertySlots;

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
            _constructionPins.Push(objHandle);
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
            _constructionPins.Push(stringHandle);
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
            _constructionPins.Push(symbolHandle);
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

    /// <summary>
    /// Names the heap a stale handle actually belongs to, if another live one
    /// holds that exact (index, generation).
    ///
    /// A slot's generation only ever goes up and the cell list never shrinks, so
    /// a handle wanting a generation *higher* than the cell present cannot have
    /// come from this heap at all - it came from another realm's. Saying which,
    /// and where that object was allocated, is the difference between a lost
    /// afternoon and a fix.
    /// </summary>
    private string DescribeHandleOwner(int index, int generation)
    {
        List<JsHeap> others = new();
        lock (LiveHeapsGate)
        {
            for (var i = LiveHeaps.Count - 1; i >= 0; i--)
            {
                if (!LiveHeaps[i].TryGetTarget(out var heap))
                {
                    LiveHeaps.RemoveAt(i);
                    continue;
                }

                if (!ReferenceEquals(heap, this))
                {
                    others.Add(heap);
                }
            }
        }

        foreach (var heap in others)
        {
            if ((uint)index < (uint)heap._cells.Count &&
                heap._cells[index] is { } candidate &&
                candidate.Generation == generation)
            {
                return $"ownedBy=heap#{heap.HeapId}/{candidate.Kind}/" +
                    $"{candidate.Payload.GetType().Name}@{heap.GetAllocationSiteForDiagnostics(index)}";
            }
        }

        return $"ownedBy=none-of-{others.Count}-other-heaps";
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
            var sweepInfo = _sweepLog is null
                ? "sweep-log-off (set FEN_FENJS_GC_SWEEPLOG=1)"
                : _sweepLog.TryGetValue(index, out var info) ? info.Describe() : "no-sweep-record";
            var allocSite = GetAllocationSiteForDiagnostics(index);
            throw new JsEngineFatalException(
                $"Stale heap handle. heap#{HeapId} idx={index} wantGen={generation} cell={(cell is null ? "null" : $"gen{cell.Generation}/{cell.Kind}")} sweep[{sweepInfo}] allocSite={allocSite} rootCtx={(_rootTraceContext.Length == 0 ? "<not-a-root-walk>" : _rootTraceContext)} {DescribeHandleOwner(index, generation)} rememberedEnvs={_rememberedEnvironments.Count} envRegs={_rememberedEnvironmentRegistrations} envScanMarks={_rememberedEnvironmentScanMarks} minor#{_minorGcCount} major#{_gcCollectionCount}");
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
        var minorStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            MinorCollectCore();
        }
        finally
        {
            _minorGcTicks += System.Diagnostics.Stopwatch.GetTimestamp() - minorStart;
        }
    }

    private void MinorCollectCore()
    {
        if (_verifyHeapBeforeGc) _verifier.Verify(this);

        _minorGcCount++;
        _lastMinorMarked = 0;
        _lastMinorSwept = 0;
        _lastMinorPromoted = 0;
        _lastMinorScannedOldCells = 0;
        _lastMinorNurserySize = _nursery.Count;
        _minorRootMarks.Clear();
        _rootTraceMarkBaseline = 0;
        _sharedMarkingTracer?.ForgetTracedEnvironments();

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
        try
        {
            _sharedMarkingTracer ??= new MarkingTracer(this, minorMode: true);
            var marker = _sharedMarkingTracer;
            BeginRootTrace("heap.roots");
            _roots.Trace(marker, "heap.roots");
            BeginRootTrace("heap.constructionPins");
            _constructionPins.Trace(marker, "heap.constructionPins");
            BeginRootTrace("heap.allocationPinRing");
            TraceAllocationPinRing(marker);

            // Audit §1: external root sources (interpreter frame registers).
            for (var i = 0; i < _rootSources.Count; i++)
            {
                BeginRootTrace(_rootSources[i].GetType().Name);
                _rootSources[i].TraceRoots(marker);
            }

            BeginRootTrace("heap.dirtyCards");
            ScanDirtyCards(marker);
            BeginRootTrace("heap.rememberedEnvs");
            ScanRememberedEnvironments(marker);
            EndRootTrace();
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
                    if (_sweepLog is not null)
                    {
                        _sweepLog[i] = new SweepRecord(
                            cell.Generation, cell.Kind, _minorGcCount, _gcCollectionCount,
                            cell.Payload.GetType().Name, GetAllocationSiteForDiagnostics(i), "minor");
                    }

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
        }
        finally
        {
            // Marking calls Validate(), which throws JsEngineFatalException on a stale
            // handle. If that escapes, the minor-traversal flag must not survive into the
            // next major collection: Mark() skips Old cells while it is set, so a major GC
            // would leave the whole old generation unmarked and sweep it as garbage.
            _currentMarkMinorMode = false;
        }

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

    // Called once per bytecode instruction, so the common answer - no
    // collection pending - has to be a field test and nothing more.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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
        if (ShouldRunAutomaticMajorCollection())
        {
            CollectGarbage();
        }
    }

    private bool ShouldRunAutomaticMajorCollection()
    {
        if (MinorCollectionsPerMajorGc > 0 &&
            _minorGcCount % MinorCollectionsPerMajorGc == 0)
        {
            return true;
        }

        if (MajorGcHeapGrowthFactor <= 0)
        {
            return false;
        }

        var live = ApproximateLiveCellCount;
        if (live < MajorGcGrowthFloorCells)
        {
            return false;
        }

        // The first major after the floor is crossed establishes the baseline
        // everything afterwards is measured against.
        var baseline = Math.Max(_liveCellsAfterLastMajor, MajorGcGrowthFloorCells);
        return live >= baseline * MajorGcHeapGrowthFactor;
    }

    /// <summary>
    /// Live cells without the O(n) scan <see cref="LiveCellCount"/> does: every
    /// allocated slot minus the ones the sweeper has handed back. Used on the
    /// collection path, which must not walk the heap to decide whether to walk
    /// the heap.
    /// </summary>
    public int ApproximateLiveCellCount => _cells.Count - _freeList.Count;

    /// <summary>
    /// Registers an environment record whose bindings may hold young cells.
    /// Deduplicated by the record's <c>IsRememberedForMinorGc</c> flag; the
    /// set self-cleans in <see cref="ScanRememberedEnvironments"/> when a minor
    /// collection proves the record's bindings hold no young cells.
    /// </summary>
    private int _rememberedEnvironmentRegistrations;

    public int RememberedEnvironmentRegistrations => _rememberedEnvironmentRegistrations;

    public void RememberEnvironment(
        FenBrowser.Js.Environments.EnvironmentRecord record,
        ObjectHandle? storedObject = null)
    {
        _auditEnvironments?.Add(record);
        if (record.IsRememberedForMinorGc)
        {
            return;
        }

        // Old objects cannot introduce a nursery edge. Remembering every
        // method receiver/argument instead retains one environment per call
        // until another collection, even when the loop allocates no JS cells.
        // A bulk binding copy has no single stored object and remains
        // conservative; individual stores supply their handle.
        if (storedObject is { } handle && Validate(handle).Tier != GenerationTier.Young)
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
        foreach (var root in _constructionPins.Snapshot())
        {
            roots.TryAdd(root, "ConstructionWindow");
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
        var majorStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            CollectGarbageCore();
        }
        finally
        {
            _majorGcTicks += System.Diagnostics.Stopwatch.GetTimestamp() - majorStart;
        }
    }

    private void CollectGarbageCore()
    {
        // Reset on the way out, so the next growth check measures against what
        // this collection actually left behind.
        try
        {
            CollectGarbageCoreInner();
        }
        finally
        {
            _liveCellsAfterLastMajor = ApproximateLiveCellCount;
        }
    }

    private void CollectGarbageCoreInner()
    {
        // Full GC must traverse old-generation cells even if a previous minor
        // collection was interrupted before it could clear its traversal mode.
        _currentMarkMinorMode = false;

        if (_verifyHeapBeforeGc)
        {
            _verifier.Verify(this);
        }

        _gcCollectionCount++;
        _lastGcMarkedCells = 0;
        _lastGcSweptCells = 0;
        _lastGcPropertySlots = 0;
        _sharedMarkingTracer?.ForgetTracedEnvironments();

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
        BeginRootTrace("heap.roots");
        _roots.Trace(marker, "heap.roots");
        BeginRootTrace("heap.constructionPins");
        _constructionPins.Trace(marker, "heap.constructionPins");
        BeginRootTrace("heap.allocationPinRing");
        TraceAllocationPinRing(marker);

        // Audit §1: roots held by external subsystems (interpreter frames).
        for (var i = 0; i < _rootSources.Count; i++)
        {
            BeginRootTrace(_rootSources[i].GetType().Name);
            _rootSources[i].TraceRoots(marker);
        }

        EndRootTrace();

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

            if (_sweepLog is not null)
            {
                _sweepLog[i] = new SweepRecord(
                    cell.Generation, cell.Kind, _minorGcCount, _gcCollectionCount,
                    cell.Payload.GetType().Name, GetAllocationSiteForDiagnostics(i), "major");
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
                if (cell.Payload is FenBrowser.Js.Objects.JsObject marked)
                {
                    _lastGcPropertySlots += marked.PropertySlotCount;
                }

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

        // Reference identity: two distinct records are never interchangeable,
        // and a record has no value equality to fall back on.
        private readonly HashSet<FenBrowser.Js.Environments.EnvironmentRecord> _tracedEnvironments =
            new(ReferenceEqualityComparer.Instance);

        public MarkingTracer(JsHeap heap, bool minorMode = false)
        {
            _heap = heap;
            _minorMode = minorMode;
            _ = _minorMode;
        }

        public bool BeginEnvironment(FenBrowser.Js.Environments.EnvironmentRecord record) =>
            _tracedEnvironments.Add(record);

        // Must run at the start of every collection. A record left in the set
        // would be skipped next time and its objects swept while still live.
        internal void ForgetTracedEnvironments() => _tracedEnvironments.Clear();

        public void Trace(ObjectHandle handle)
        {
            _heap.Mark(handle);
        }

        public void TraceRoot(string context, ObjectHandle handle)
        {
            _heap.BeginRootTrace(context);
            if (_heap.ShouldSkipDeadRoot(context, handle))
            {
                return;
            }

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
