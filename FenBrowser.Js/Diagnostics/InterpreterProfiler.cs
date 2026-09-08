using System.Linq;
using System.Text;
using FenBrowser.Js.Bytecode;

namespace FenBrowser.Js.Diagnostics;

// Opt-in bytecode profiler for triaging pathologically slow real-world
// bundles. Enabled by FEN_FENJS_PROFILE=1; when disabled every hook is a
// single static bool test that the JIT folds away, so the dispatch loop pays
// nothing on the normal path.
//
// Counts are per-opcode and per-function so a report answers both "which
// operation dominates" and "whose code is running", which together locate a
// hot loop without a native profiler attached to the browser process.
//
// All counters live in per-thread state. A page runs script on more than one
// thread (a Worker is a second one), and the report is pulled by a watchdog
// timer on a threadpool thread while script is still running - so a single
// shared Dictionary is written by the interpreter and read by the reporter at
// the same time, which corrupts it and fails the script job that happened to
// be executing. Per-thread state removes the sharing; the only cross-thread
// contact left is the merge at report time, which takes each state's lock.
public static class InterpreterProfiler
{
    private static readonly bool DeepTraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_DEEP_TRACE"),
        "1",
        StringComparison.Ordinal);

    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_PROFILE"),
        "1",
        StringComparison.Ordinal) || DeepTraceEnabled;

    // Per-opcode self time, enabled by FEN_FENJS_OPTIME=1. Counting which
    // opcode runs most says nothing about where a page's seconds went: a
    // histogram cannot separate an opcode that is executed ten million times
    // for twenty nanoseconds each from one executed a hundred thousand times
    // for two microseconds. Kept behind its own switch because it takes a
    // timestamp per instruction, which the plain histogram does not.
    public static readonly bool OpTimingEnabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_OPTIME"),
        "1",
        StringComparison.Ordinal) || DeepTraceEnabled ||
        string.Equals(
            Environment.GetEnvironmentVariable("FEN_FENJS_OPALLOC"),
            "1",
            StringComparison.Ordinal);

    // Managed bytes per opcode, enabled by FEN_FENJS_OPALLOC=1. A page whose
    // time is spent in the collector needs to know which operation produced the
    // garbage, and neither a count nor a duration answers that: an opcode can
    // be fast every time it runs and still be the reason the heap is churning.
    // Rides on the op-timing boundary, so it turns that on as well.
    public static readonly bool OpAllocationEnabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_OPALLOC"),
        "1",
        StringComparison.Ordinal);

    // Adjacent pairs, indexed [previous * PairStride + current]. A histogram
    // says which operation dominates; only pairs say whether that operation is
    // work or plumbing - "LoadConst then an arithmetic op" is a constant the
    // instruction could have carried itself, and a Move straight into a use is
    // a register the allocator did not have to spend. Both are removable
    // without touching semantics, and half of everything executed on a real
    // page is that kind of data movement, so the distinction decides whether a
    // peephole pass is worth writing.
    private const int PairStride = 256;

    private sealed class FunctionStat
    {
        public long Instructions;
        public long Entries;
    }

    // One of these per script thread. Only its owning thread writes the
    // counters; the reporting thread reads them, taking Gate for the parts
    // that are not a single aligned 64-bit field.
    private sealed class ThreadState
    {
        public readonly object Gate = new();
        public readonly long[] OpCodeCounts = new long[512];
        public readonly long[] OpCodePairCounts = new long[PairStride * PairStride];
        public readonly long[] DeoptCounts = new long[512];
        public readonly Dictionary<object, FunctionStat> FunctionCounts =
            new(ReferenceEqualityComparer.Instance);

        public long Total;
        public long DeoptTotal;
        public int PreviousOpCode = -1;
        public object? PreviousFunction;
        public long CurrentFunctionInstructions;

        public long VarGlobal;
        public long VarLocal;
        public long VarDepth;
        public long VarSlowTicks;
        public long VarSlowSamples;

        public long ExecTicks;
        public long ExecStart;

        // Self time per opcode. The clock is read once per instruction: that
        // reading both closes the previous opcode and opens this one, so a
        // nested call's own instructions close the call opcode at frame setup
        // and the time of the callee's body is billed to the callee's opcodes,
        // not to the call.
        public readonly long[] OpTicks = new long[512];
        public readonly long[] OpSamples = new long[512];
        // Managed bytes charged to each opcode, on the same open/close boundary
        // as the ticks. Time says which opcode is slow; bytes say whether it is
        // slow because it is allocating, which is a different fix.
        public readonly long[] OpBytes = new long[512];
        public int PendingOp = -1;
        public long PendingStart;
        public long PendingBytes;
    }

    [ThreadStatic]
    private static ThreadState? _state;

    // Every state ever created, so a report can merge threads that are busy or
    // already finished. Only touched when a thread first records something.
    private static readonly List<ThreadState> AllStates = new();

    private static ThreadState State
    {
        get
        {
            var state = _state;
            if (state is null)
            {
                state = new ThreadState();
                _state = state;
                lock (AllStates)
                {
                    AllStates.Add(state);
                }
            }

            return state;
        }
    }

    private static ThreadState[] SnapshotStates()
    {
        lock (AllStates)
        {
            return AllStates.ToArray();
        }
    }

    public static long Total
    {
        get
        {
            long total = 0;
            foreach (var state in SnapshotStates())
            {
                total += Interlocked.Read(ref state.Total);
            }

            return total;
        }
    }

    public static void RecordOpCode(OpCode opCode) => RecordOpCode(State, opCode);

    private static void RecordOpCode(ThreadState state, OpCode opCode)
    {
        var index = (int)opCode;
        if ((uint)index < (uint)state.OpCodeCounts.Length)
        {
            state.OpCodeCounts[index]++;
        }

        state.Total++;
    }

    /// <summary>
    /// Records an executed opcode along with the function it belongs to, so the
    /// pair histogram only counts instructions that really followed one another
    /// in the same body - a call's last opcode is not adjacent to the callee's
    /// first, and pairing them would invent patterns no peephole could match.
    /// </summary>
    public static void RecordOpCode(OpCode opCode, object function)
    {
        var state = State;
        RecordOpCode(state, opCode);

        var index = (int)opCode;
        if (ReferenceEquals(function, state.PreviousFunction))
        {
            if ((uint)index < PairStride && state.PreviousOpCode >= 0)
            {
                state.OpCodePairCounts[(state.PreviousOpCode * PairStride) + index]++;
            }
        }
        else
        {
            // Control reached another body: bank what the last one ran before
            // starting a tally for this one.
            lock (state.Gate)
            {
                BankCurrentFunction(state);
                state.PreviousFunction = function;
                RecordFunctionEntry(state, function);
            }
        }

        state.CurrentFunctionInstructions++;
        state.PreviousOpCode = (uint)index < PairStride ? index : -1;
    }

    // Instructions banked against the body that ran them. Attributing on every
    // step would mean a hash lookup per instruction; control stays inside one
    // body for long stretches, so this keeps a running tally and pays the
    // lookup only when the function changes.
    private static void BankCurrentFunction(ThreadState state)
    {
        if (state.PreviousFunction is null || state.CurrentFunctionInstructions == 0)
        {
            state.CurrentFunctionInstructions = 0;
            return;
        }

        if (!state.FunctionCounts.TryGetValue(state.PreviousFunction, out var stat))
        {
            stat = new FunctionStat();
            state.FunctionCounts[state.PreviousFunction] = stat;
        }

        stat.Instructions += state.CurrentFunctionInstructions;
        state.CurrentFunctionInstructions = 0;
    }

    private static void RecordFunctionEntry(ThreadState state, object function)
    {
        if (!state.FunctionCounts.TryGetValue(function, out var stat))
        {
            stat = new FunctionStat();
            state.FunctionCounts[function] = stat;
        }

        stat.Entries++;
    }

    /// <summary>
    /// Names a function well enough to find it in a minified bundle: its own
    /// name where it has one, otherwise the head of its source text, which for
    /// an anonymous callback is the only thing that identifies it.
    /// </summary>
    private static string DescribeFunction(object function)
    {
        if (function is not BytecodeFunction bytecode)
        {
            return function.GetType().Name;
        }

        if (!string.IsNullOrEmpty(bytecode.Name))
        {
            return bytecode.Name!;
        }

        var source = bytecode.SourceText;
        if (string.IsNullOrWhiteSpace(source))
        {
            // A minified bundle strips names and we may hold no source text, but
            // the parameter list and the first string constants are still
            // distinctive enough to find the body again in the original file.
            var fingerprint = new StringBuilder(64);
            fingerprint.Append("fn(").Append(string.Join(",", bytecode.ParameterNames)).Append(')');
            if (bytecode.VarDeclarationNames.Count > 0)
            {
                fingerprint.Append(" var ")
                    .Append(string.Join(",", bytecode.VarDeclarationNames.Take(6)));
            }

            return fingerprint.ToString();
        }

        var collapsed = new StringBuilder(48);
        var lastWasSpace = false;
        foreach (var ch in source)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && collapsed.Length > 0) collapsed.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                collapsed.Append(ch);
                lastWasSpace = false;
            }

            if (collapsed.Length >= 44) break;
        }

        return "<anon> " + collapsed.ToString();
    }

    // Where identifier reads actually land, and how many scopes they walk
    // past first - the two facts that decide which cache is worth building.
    public static void RecordVarResolve(bool onObjectRecord, int depth)
    {
        var state = State;
        if (onObjectRecord) state.VarGlobal++; else state.VarLocal++;
        state.VarDepth += depth;
    }

    public static string VarReport()
    {
        long global = 0, local = 0, depth = 0;
        foreach (var state in SnapshotStates())
        {
            global += state.VarGlobal;
            local += state.VarLocal;
            depth += state.VarDepth;
        }

        var total = global + local;
        if (total == 0) return "[FenJsProfile] no identifier reads recorded";
        return $"[FenJsProfile] identifier reads={total:N0} onGlobalObject={100.0 * global / total:F1}% " +
               $"inLocalScope={100.0 * local / total:F1}% avgScopesWalked={(double)depth / total:F2}";
    }

    // How long the name-keyed chain walk actually costs. Counting how often a
    // slow path runs says nothing about whether replacing it is worth the
    // work: the answer is a fraction of running time, and only a clock gives
    // that. Reading the clock is itself not free, so the cost of one read-pair
    // is measured once at startup and subtracted from every sample before the
    // share is reported - otherwise the instrument's own overhead would be
    // attributed to the thing it is measuring, and a cheap path sampled often
    // would look expensive purely because it was sampled.
    private static readonly double TimestampOverheadTicks = MeasureTimestampOverhead();

    private static long _timestampSink;

    private static double MeasureTimestampOverhead()
    {
        if (!Enabled) return 0;

        const int Iterations = 200_000;
        // Warm the path so JIT compilation of GetTimestamp is not billed to it.
        for (var i = 0; i < 10_000; i++)
        {
            _timestampSink += System.Diagnostics.Stopwatch.GetTimestamp();
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < Iterations; i++)
        {
            var first = System.Diagnostics.Stopwatch.GetTimestamp();
            var second = System.Diagnostics.Stopwatch.GetTimestamp();
            _timestampSink += second - first;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        return (double)elapsed / Iterations;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static long StartSample() => System.Diagnostics.Stopwatch.GetTimestamp();

    public static void RecordVarSlowPath(long startTicks)
    {
        var state = State;
        state.VarSlowTicks += System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
        state.VarSlowSamples++;
    }

    /// <summary>
    /// Brackets the outermost interpreter activation. <paramref name="depth"/>
    /// is the call depth after entering, so 1 is the outermost frame; nested
    /// calls are already inside the window and must not restart the clock.
    /// </summary>
    public static void EnterExecute(int depth)
    {
        if (depth != 1) return;
        State.ExecStart = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public static void ExitExecute(int depth)
    {
        if (depth != 1) return;
        var state = State;
        if (state.ExecStart == 0) return;
        state.ExecTicks += System.Diagnostics.Stopwatch.GetTimestamp() - state.ExecStart;
        state.ExecStart = 0;
    }

    public static string TimingReport()
    {
        long slowTicks = 0, slowSamples = 0, execTicks = 0;
        foreach (var state in SnapshotStates())
        {
            slowTicks += state.VarSlowTicks;
            slowSamples += state.VarSlowSamples;
            execTicks += state.ExecTicks;
        }

        if (slowSamples == 0)
        {
            return "[FenJsProfile] no identifier slow-path samples recorded";
        }

        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var rawMs = 1000.0 * slowTicks / frequency;
        var overheadTicks = TimestampOverheadTicks * slowSamples;
        var netTicks = Math.Max(0, slowTicks - overheadTicks);
        var netMs = 1000.0 * netTicks / frequency;
        var execMs = 1000.0 * execTicks / frequency;
        // Summing threads answers "how much execution happened"; it does not
        // answer "how long was the page waiting", because threads overlap. Per
        // thread, the largest is the one that could have been the critical path.
        var perThread = new StringBuilder();
        foreach (var state in SnapshotStates())
        {
            if (state.ExecTicks == 0) continue;
            if (perThread.Length > 0) perThread.Append(',');
            perThread.Append((1000.0 * state.ExecTicks / frequency).ToString("F0")).Append("ms");
        }

        var share = execMs > 0 ? 100.0 * netMs / execMs : 0.0;
        var perSampleNs = 1e9 * netTicks / frequency / slowSamples;

        return $"[FenJsProfile] identifier slow path: samples={slowSamples:N0} " +
               $"raw={rawMs:F1}ms measurementOverhead={1000.0 * overheadTicks / frequency:F1}ms " +
               $"net={netMs:F1}ms ({perSampleNs:F0}ns each) " +
               $"interpretedExecution={execMs:F1}ms perThread=[{perThread}] shareOfExecution={share:F2}%";
    }

    // Compiled code that had to call an interpreter helper anyway. The JIT emits
    // an inline fast path per operator behind a type guard; when the guard fails
    // the operator costs a call plus a run-time switch instead of a few
    // instructions. A CPU profile shows the helpers are hot but not which
    // operator sends work to them, and that is the fact a fix needs.
    public static void RecordJitDeopt(OpCode opCode)
    {
        var state = State;
        var index = (int)opCode;
        if ((uint)index < (uint)state.DeoptCounts.Length)
        {
            state.DeoptCounts[index]++;
        }

        state.DeoptTotal++;
    }

    /// <summary>
    /// Closes the previous instruction's self time and opens this one's, with a
    /// single clock read. Call it immediately before dispatching an opcode.
    /// </summary>
    public static void BeginOp(OpCode opCode)
    {
        var state = State;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var bytes = OpAllocationEnabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var pending = state.PendingOp;
        if (pending >= 0)
        {
            state.OpTicks[pending] += now - state.PendingStart;
            state.OpSamples[pending]++;
            if (OpAllocationEnabled)
            {
                state.OpBytes[pending] += bytes - state.PendingBytes;
            }
        }

        var index = (int)opCode;
        state.PendingOp = (uint)index < (uint)state.OpTicks.Length ? index : -1;
        state.PendingStart = now;
        state.PendingBytes = bytes;
    }

    /// <summary>
    /// Closes the last instruction of a top-level job. Without this the opcode
    /// that ended the job stays open across the thread's idle wait and is
    /// billed every millisecond until the next job starts, which made the
    /// final Return of each job look like the most expensive opcode in the
    /// engine by two orders of magnitude.
    /// </summary>
    public static void EndOpBatch()
    {
        var state = State;
        var pending = state.PendingOp;
        if (pending < 0)
        {
            return;
        }

        state.OpTicks[pending] += System.Diagnostics.Stopwatch.GetTimestamp() - state.PendingStart;
        state.OpSamples[pending]++;
        if (OpAllocationEnabled)
        {
            state.OpBytes[pending] += GC.GetAllocatedBytesForCurrentThread() - state.PendingBytes;
        }

        state.PendingOp = -1;
    }

    /// <summary>
    /// Per-opcode self time, most expensive first. Each sample paid for one
    /// clock read, so that cost is subtracted per sample rather than left to
    /// inflate whichever opcode ran most often.
    /// </summary>
    public static string OpTimeReport(int top = 25)
    {
        if (!OpTimingEnabled)
        {
            return "[FenJsOpTime] disabled (set FEN_FENJS_OPTIME=1)";
        }

        var ticks = new long[512];
        var samples = new long[512];
        var bytes = new long[512];
        foreach (var state in SnapshotStates())
        {
            lock (state.Gate)
            {
                for (var i = 0; i < ticks.Length; i++)
                {
                    ticks[i] += state.OpTicks[i];
                    samples[i] += state.OpSamples[i];
                    bytes[i] += state.OpBytes[i];
                }
            }
        }

        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        double totalNet = 0;
        long totalSamples = 0;
        var net = new double[ticks.Length];
        for (var i = 0; i < ticks.Length; i++)
        {
            if (samples[i] == 0) continue;
            net[i] = Math.Max(0, ticks[i] - TimestampOverheadTicks * samples[i]);
            totalNet += net[i];
            totalSamples += samples[i];
        }

        if (totalSamples == 0)
        {
            return "[FenJsOpTime] no samples";
        }

        var order = new List<int>();
        for (var i = 0; i < ticks.Length; i++)
        {
            if (samples[i] > 0) order.Add(i);
        }

        order.Sort((a, b) => net[b].CompareTo(net[a]));

        var builder = new StringBuilder();
        builder.Append("[FenJsOpTime] self time per opcode, overhead-corrected. total=")
            .Append((1000.0 * totalNet / frequency).ToString("F0"))
            .Append("ms over ")
            .Append(totalSamples.ToString("N0"))
            .Append(" instructions (")
            .Append((1e9 * totalNet / frequency / totalSamples).ToString("F1"))
            .AppendLine("ns each)");
        builder.AppendLine("    opcode                              count        ms     ns/op    share");
        foreach (var index in order.Take(top))
        {
            var ms = 1000.0 * net[index] / frequency;
            builder.Append("    ")
                .Append(((OpCode)index).ToString().PadRight(28))
                .Append(samples[index].ToString("N0").PadLeft(12))
                .Append(ms.ToString("F0").PadLeft(10))
                .Append((1e9 * net[index] / frequency / samples[index]).ToString("F0").PadLeft(10))
                .Append((100.0 * net[index] / totalNet).ToString("F2").PadLeft(8))
                .AppendLine("%");
        }

        if (OpAllocationEnabled)
        {
            builder.Append(OpAllocationSection(bytes, samples, top));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Managed bytes charged to each opcode, largest first. Ordered separately
    /// from time because the opcode that allocates most is often not the one
    /// that takes longest — the cost of the garbage lands later, in a
    /// collection somebody else pays for.
    /// </summary>
    private static string OpAllocationSection(long[] bytes, long[] samples, int top)
    {
        long totalBytes = 0;
        var order = new List<int>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] <= 0) continue;
            totalBytes += bytes[i];
            order.Add(i);
        }

        if (totalBytes == 0)
        {
            return "  -- no managed allocation recorded --" + Environment.NewLine;
        }

        order.Sort((a, b) => bytes[b].CompareTo(bytes[a]));

        var builder = new StringBuilder();
        builder.Append("[FenJsOpAlloc] managed bytes per opcode. total=")
            .Append((totalBytes / (1024.0 * 1024.0)).ToString("F0"))
            .AppendLine("MB");
        builder.AppendLine("    opcode                              count        MB  bytes/op    share");
        foreach (var index in order.Take(top))
        {
            var perOp = samples[index] > 0 ? (double)bytes[index] / samples[index] : 0.0;
            builder.Append("    ")
                .Append(((OpCode)index).ToString().PadRight(28))
                .Append(samples[index].ToString("N0").PadLeft(12))
                .Append((bytes[index] / (1024.0 * 1024.0)).ToString("F0").PadLeft(10))
                .Append(perOp.ToString("F0").PadLeft(10))
                .Append((100.0 * bytes[index] / totalBytes).ToString("F2").PadLeft(8))
                .AppendLine("%");
        }

        return builder.ToString();
    }

    public static void Reset()
    {
        foreach (var state in SnapshotStates())
        {
            lock (state.Gate)
            {
                Array.Clear(state.OpCodeCounts);
                Array.Clear(state.OpCodePairCounts);
                Array.Clear(state.DeoptCounts);
                state.FunctionCounts.Clear();
                state.PreviousOpCode = -1;
                state.PreviousFunction = null;
                state.CurrentFunctionInstructions = 0;
                state.Total = 0;
                state.DeoptTotal = 0;
                state.VarGlobal = 0;
                state.VarLocal = 0;
                state.VarDepth = 0;
                Array.Clear(state.OpTicks);
                Array.Clear(state.OpSamples);
                Array.Clear(state.OpBytes);
                state.PendingOp = -1;
                state.PendingStart = 0;
                state.PendingBytes = 0;
                state.VarSlowTicks = 0;
                state.VarSlowSamples = 0;
                state.ExecTicks = 0;
                state.ExecStart = 0;
            }
        }
    }

    // Renders the top opcodes and functions by share of total executed
    // instructions. Percentages are of Total, so a report reads as a profile
    // rather than as raw counters.
    public static string Report(int top = 20)
    {
        var states = SnapshotStates();
        var builder = new StringBuilder();

        var opCodeCounts = new long[512];
        var deoptCounts = new long[512];
        var pairCounts = new long[PairStride * PairStride];
        var functions = new Dictionary<object, FunctionStat>(ReferenceEqualityComparer.Instance);
        long total = 0;
        long deoptTotal = 0;

        foreach (var state in states)
        {
            total += state.Total;
            deoptTotal += state.DeoptTotal;
            for (var i = 0; i < opCodeCounts.Length; i++) opCodeCounts[i] += state.OpCodeCounts[i];
            for (var i = 0; i < deoptCounts.Length; i++) deoptCounts[i] += state.DeoptCounts[i];
            for (var i = 0; i < pairCounts.Length; i++) pairCounts[i] += state.OpCodePairCounts[i];

            // Merge under the owner's gate: it is still executing, and its
            // dictionary must not be enumerated while it inserts.
            lock (state.Gate)
            {
                foreach (var (function, stat) in state.FunctionCounts)
                {
                    if (!functions.TryGetValue(function, out var merged))
                    {
                        merged = new FunctionStat();
                        functions[function] = merged;
                    }

                    merged.Instructions += stat.Instructions;
                    merged.Entries += stat.Entries;
                }

                // Whatever the thread is running right now has not been banked
                // yet. Add it to the merged view rather than banking it here,
                // so a report never mutates another thread's tally.
                if (state.PreviousFunction is { } running && state.CurrentFunctionInstructions > 0)
                {
                    if (!functions.TryGetValue(running, out var pending))
                    {
                        pending = new FunctionStat();
                        functions[running] = pending;
                    }

                    pending.Instructions += state.CurrentFunctionInstructions;
                }
            }
        }

        builder.Append("[FenJsProfile] total instructions=").Append(total)
            .Append(" scriptThreads=").Append(states.Length).AppendLine();

        if (total == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine("  -- by opcode --");
        var opcodes = new List<(OpCode Op, long Count)>();
        for (var i = 0; i < opCodeCounts.Length; i++)
        {
            if (opCodeCounts[i] > 0)
            {
                opcodes.Add(((OpCode)i, opCodeCounts[i]));
            }
        }

        opcodes.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        foreach (var (op, count) in opcodes.Take(top))
        {
            builder.Append("    ").Append(op.ToString().PadRight(28))
                .Append(count.ToString().PadLeft(14))
                .Append("  ").Append((100.0 * count / total).ToString("F2")).AppendLine("%");
        }

        if (functions.Count > 0)
        {
            builder.AppendLine("  -- by function (self, interpreted only) --");
            var ranked = new List<(string Name, long Instructions, long Entries, int Size)>();
            foreach (var (function, stat) in functions)
            {
                var size = function is BytecodeFunction bytecode ? bytecode.InstructionArray.Length : 0;
                ranked.Add((DescribeFunction(function), stat.Instructions, stat.Entries, size));
            }

            ranked.Sort(static (a, b) => b.Instructions.CompareTo(a.Instructions));
            foreach (var (name, instructions, entries, size) in ranked.Take(top))
            {
                builder.Append("    ")
                    .Append(name.Length > 52 ? name.Substring(0, 52) : name.PadRight(52))
                    .Append(instructions.ToString().PadLeft(14))
                    .Append("  ").Append((100.0 * instructions / total).ToString("F2").PadLeft(6)).Append("%")
                    .Append("  entries=").Append(entries.ToString().PadLeft(9))
                    .Append("  bodyOps=").Append(size.ToString().PadLeft(6))
                    .AppendLine();
            }
        }

        if (deoptTotal > 0)
        {
            builder.AppendLine("  -- compiled code falling back to an interpreter helper --");
            var deopts = new List<(OpCode Op, long Count)>();
            for (var i = 0; i < deoptCounts.Length; i++)
            {
                if (deoptCounts[i] > 0)
                {
                    deopts.Add(((OpCode)i, deoptCounts[i]));
                }
            }

            deopts.Sort(static (a, b) => b.Count.CompareTo(a.Count));
            foreach (var (op, count) in deopts.Take(top))
            {
                builder.Append("    ").Append(op.ToString().PadRight(28))
                    .Append(count.ToString().PadLeft(14))
                    .Append("  ").Append((100.0 * count / total).ToString("F2")).AppendLine("% of all instructions");
            }
        }

        var pairs = new List<(int Previous, int Current, long Count)>();
        for (var previous = 0; previous < PairStride; previous++)
        {
            var rowStart = previous * PairStride;
            for (var current = 0; current < PairStride; current++)
            {
                var count = pairCounts[rowStart + current];
                if (count > 0)
                {
                    pairs.Add((previous, current, count));
                }
            }
        }

        if (pairs.Count > 0)
        {
            pairs.Sort(static (a, b) => b.Count.CompareTo(a.Count));
            builder.AppendLine("  -- by adjacent pair --");
            foreach (var (previous, current, count) in pairs.Take(top))
            {
                builder.Append("    ")
                    .Append(((OpCode)previous).ToString())
                    .Append(" -> ")
                    .Append(((OpCode)current).ToString().PadRight(24))
                    .Append(count.ToString().PadLeft(14))
                    .Append("  ").Append((100.0 * count / total).ToString("F2")).AppendLine("%");
            }
        }

        return builder.ToString();
    }
}
