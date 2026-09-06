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
public static class InterpreterProfiler
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_PROFILE"),
        "1",
        StringComparison.Ordinal);

    private static readonly long[] OpCodeCounts = new long[512];
    private static long _total;

    // Adjacent pairs, indexed [previous * PairStride + current]. A histogram
    // says which operation dominates; only pairs say whether that operation is
    // work or plumbing - "LoadConst then an arithmetic op" is a constant the
    // instruction could have carried itself, and a Move straight into a use is
    // a register the allocator did not have to spend. Both are removable
    // without touching semantics, and half of everything executed on a real
    // page is that kind of data movement, so the distinction decides whether a
    // peephole pass is worth writing.
    private const int PairStride = 256;
    private static readonly long[] OpCodePairCounts = new long[PairStride * PairStride];
    private static int _previousOpCode = -1;
    private static object? _previousFunction;

    public static long Total => Interlocked.Read(ref _total);

    public static void RecordOpCode(OpCode opCode)
    {
        var index = (int)opCode;
        if ((uint)index < (uint)OpCodeCounts.Length)
        {
            OpCodeCounts[index]++;
        }

        _total++;
    }

    /// <summary>
    /// Records an executed opcode along with the function it belongs to, so the
    /// pair histogram only counts instructions that really followed one another
    /// in the same body - a call's last opcode is not adjacent to the callee's
    /// first, and pairing them would invent patterns no peephole could match.
    /// </summary>
    public static void RecordOpCode(OpCode opCode, object function)
    {
        RecordOpCode(opCode);

        var index = (int)opCode;
        if (ReferenceEquals(function, _previousFunction))
        {
            if ((uint)index < PairStride && _previousOpCode >= 0)
            {
                OpCodePairCounts[(_previousOpCode * PairStride) + index]++;
            }
        }
        else
        {
            // Control reached another body: bank what the last one ran before
            // starting a tally for this one.
            BankCurrentFunction();
            _previousFunction = function;
            RecordFunctionEntry(function);
        }

        _currentFunctionInstructions++;
        _previousOpCode = (uint)index < PairStride ? index : -1;
    }

    // Instructions banked against the body that ran them. Attributing on every
    // step would mean a hash lookup per instruction; control stays inside one
    // body for long stretches, so this keeps a running tally and pays the
    // lookup only when the function changes.
    private sealed class FunctionStat
    {
        public long Instructions;
        public long Entries;
    }

    private static readonly Dictionary<object, FunctionStat> FunctionCounts =
        new(ReferenceEqualityComparer.Instance);

    private static long _currentFunctionInstructions;

    private static void BankCurrentFunction()
    {
        if (_previousFunction is null || _currentFunctionInstructions == 0)
        {
            _currentFunctionInstructions = 0;
            return;
        }

        if (!FunctionCounts.TryGetValue(_previousFunction, out var stat))
        {
            stat = new FunctionStat();
            FunctionCounts[_previousFunction] = stat;
        }

        stat.Instructions += _currentFunctionInstructions;
        _currentFunctionInstructions = 0;
    }

    private static void RecordFunctionEntry(object function)
    {
        if (!FunctionCounts.TryGetValue(function, out var stat))
        {
            stat = new FunctionStat();
            FunctionCounts[function] = stat;
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
    private static long _varGlobal;
    private static long _varLocal;
    private static long _varDepth;

    public static void RecordVarResolve(bool onObjectRecord, int depth)
    {
        if (onObjectRecord) _varGlobal++; else _varLocal++;
        _varDepth += depth;
    }

    public static string VarReport()
    {
        var total = _varGlobal + _varLocal;
        if (total == 0) return "[FenJsProfile] no identifier reads recorded";
        return $"[FenJsProfile] identifier reads={total:N0} onGlobalObject={100.0*_varGlobal/total:F1}% " +
               $"inLocalScope={100.0*_varLocal/total:F1}% avgScopesWalked={(double)_varDepth/total:F2}";
    }

    // Compiled code that had to call an interpreter helper anyway. The JIT emits
    // an inline fast path per operator behind a type guard; when the guard fails
    // the operator costs a call plus a run-time switch instead of a few
    // instructions. A CPU profile shows the helpers are hot but not which
    // operator sends work to them, and that is the fact a fix needs.
    private static readonly long[] DeoptCounts = new long[512];
    private static long _deoptTotal;

    public static void RecordJitDeopt(OpCode opCode)
    {
        var index = (int)opCode;
        if ((uint)index < (uint)DeoptCounts.Length)
        {
            DeoptCounts[index]++;
        }

        _deoptTotal++;
    }

    public static void Reset()
    {
        Array.Clear(OpCodeCounts);
        Array.Clear(OpCodePairCounts);
        Array.Clear(DeoptCounts);
        _previousOpCode = -1;
        _previousFunction = null;
        FunctionCounts.Clear();
        _currentFunctionInstructions = 0;
        Interlocked.Exchange(ref _total, 0);
        Interlocked.Exchange(ref _deoptTotal, 0);
    }

    // Renders the top opcodes and functions by share of total executed
    // instructions. Percentages are of Total, so a report reads as a profile
    // rather than as raw counters.
    public static string Report(int top = 20)
    {
        var total = Total;
        var builder = new StringBuilder();
        builder.Append("[FenJsProfile] total instructions=").Append(total).AppendLine();

        if (total == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine("  -- by opcode --");
        var opcodes = new List<(OpCode Op, long Count)>();
        for (var i = 0; i < OpCodeCounts.Length; i++)
        {
            if (OpCodeCounts[i] > 0)
            {
                opcodes.Add(((OpCode)i, OpCodeCounts[i]));
            }
        }

        opcodes.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        foreach (var (op, count) in opcodes.Take(top))
        {
            builder.Append("    ").Append(op.ToString().PadRight(28))
                .Append(count.ToString().PadLeft(14))
                .Append("  ").Append((100.0 * count / total).ToString("F2")).AppendLine("%");
        }

        // Bank whatever the currently-running body has accumulated so a report
        // taken mid-run does not omit the function that is hot right now.
        BankCurrentFunction();

        if (FunctionCounts.Count > 0)
        {
            builder.AppendLine("  -- by function (self, interpreted only) --");
            var functions = new List<(string Name, long Instructions, long Entries, int Size)>();
            foreach (var (function, stat) in FunctionCounts)
            {
                var size = function is BytecodeFunction bytecode ? bytecode.InstructionArray.Length : 0;
                functions.Add((DescribeFunction(function), stat.Instructions, stat.Entries, size));
            }

            functions.Sort(static (a, b) => b.Instructions.CompareTo(a.Instructions));
            foreach (var (name, instructions, entries, size) in functions.Take(top))
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

        var pairs = new List<(int Previous, int Current, long Count)>();
        for (var previous = 0; previous < PairStride; previous++)
        {
            var rowStart = previous * PairStride;
            for (var current = 0; current < PairStride; current++)
            {
                var count = OpCodePairCounts[rowStart + current];
                if (count > 0)
                {
                    pairs.Add((previous, current, count));
                }
            }
        }

        if (_deoptTotal > 0)
        {
            builder.AppendLine("  -- compiled code falling back to an interpreter helper --");
            var deopts = new List<(OpCode Op, long Count)>();
            for (var i = 0; i < DeoptCounts.Length; i++)
            {
                if (DeoptCounts[i] > 0)
                {
                    deopts.Add(((OpCode)i, DeoptCounts[i]));
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
