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

    public static void Reset()
    {
        Array.Clear(OpCodeCounts);
        Interlocked.Exchange(ref _total, 0);
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

        return builder.ToString();
    }
}
