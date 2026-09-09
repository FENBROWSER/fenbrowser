using System.Text;

namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// Coverage and shape of what the register-window loop actually ran, recorded
/// only when <see cref="Interp2Options.Log"/> is set.
/// </summary>
/// <remarks>
/// Two numbers decide whether this loop is worth anything on a real page, and
/// neither is visible from a benchmark: how much of the code is eligible, and
/// how many of the calls it makes it can enter without going back through the
/// old one. Both are counted here, alongside the reason every declined function
/// was declined - a ranked bailout table is the work queue for what to
/// implement next.
///
/// Every call site is behind the one flag, and the counters are plain statics
/// rather than interlocked: an approximate count taken off the hot path is worth
/// more than an exact one that changes what it measures. The flag is off in
/// every configuration that has not asked for it.
/// </remarks>
public static class Interp2Stats
{
    private static readonly long[] BailoutCounts = new long[Enum.GetValues<Interp2Bailout>().Length];

    private static readonly Dictionary<FenBrowser.Js.Bytecode.OpCode, long> UnsupportedOpCodes = new();

    private static long _eligibleFunctions;
    private static long _declinedFunctions;
    private static long _framesEntered;
    private static long _callsInLoop;
    private static long _callsDelegated;
    private static long _maxDepth;
    private static long _maxStackSlots;

    public static long EligibleFunctions => _eligibleFunctions;
    public static long DeclinedFunctions => _declinedFunctions;
    public static long FramesEntered => _framesEntered;

    /// <summary>Calls entered as a new window on the same loop - the point of all this.</summary>
    public static long CallsInLoop => _callsInLoop;

    /// <summary>Calls handed back to the old loop: natives, closures, everything declined.</summary>
    public static long CallsDelegated => _callsDelegated;

    internal static void RecordLayout(FrameLayout layout)
    {
        if (layout.Eligible)
        {
            _eligibleFunctions++;
            return;
        }

        _declinedFunctions++;
        BailoutCounts[(int)layout.Bailout]++;
        if (layout.BailoutOpCode is { } op)
        {
            UnsupportedOpCodes[op] = UnsupportedOpCodes.TryGetValue(op, out var seen) ? seen + 1 : 1;
        }
    }

    internal static void RecordFrameEntered(int depth, int stackSlots)
    {
        _framesEntered++;
        if (depth > _maxDepth) _maxDepth = depth;
        if (stackSlots > _maxStackSlots) _maxStackSlots = stackSlots;
    }

    internal static void RecordCallInLoop() => _callsInLoop++;

    internal static void RecordCallDelegated() => _callsDelegated++;

    public static void Reset()
    {
        Array.Clear(BailoutCounts);
        UnsupportedOpCodes.Clear();
        _eligibleFunctions = 0;
        _declinedFunctions = 0;
        _framesEntered = 0;
        _callsInLoop = 0;
        _callsDelegated = 0;
        _maxDepth = 0;
        _maxStackSlots = 0;
    }

    /// <summary>A one-screen summary, ordered so the largest gap reads first.</summary>
    public static string Report()
    {
        var report = new StringBuilder();
        var functions = _eligibleFunctions + _declinedFunctions;
        var calls = _callsInLoop + _callsDelegated;
        report.Append("[interp2] functions=").Append(functions)
              .Append(" eligible=").Append(_eligibleFunctions)
              .Append(Percent(_eligibleFunctions, functions))
              .Append(" frames=").Append(_framesEntered)
              .Append(" maxDepth=").Append(_maxDepth)
              .Append(" maxStackSlots=").Append(_maxStackSlots)
              .AppendLine();
        report.Append("[interp2] calls=").Append(calls)
              .Append(" inLoop=").Append(_callsInLoop)
              .Append(Percent(_callsInLoop, calls))
              .Append(" delegated=").Append(_callsDelegated)
              .AppendLine();

        var ranked = new List<(Interp2Bailout Reason, long Count)>();
        for (var i = 1; i < BailoutCounts.Length; i++)
        {
            if (BailoutCounts[i] > 0) ranked.Add(((Interp2Bailout)i, BailoutCounts[i]));
        }

        ranked.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        if (ranked.Count > 0)
        {
            report.Append("[interp2] declined:");
            foreach (var (reason, count) in ranked)
            {
                report.Append(' ').Append(reason).Append('=').Append(count);
            }

            report.AppendLine();
        }

        if (UnsupportedOpCodes.Count > 0)
        {
            report.Append("[interp2] unimplemented:");
            foreach (var (op, count) in UnsupportedOpCodes.OrderByDescending(static pair => pair.Value))
            {
                report.Append(' ').Append(op).Append('=').Append(count);
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    private static string Percent(long part, long total)
        => total == 0 ? string.Empty : $"({part * 100.0 / total:F1}%)";
}
