using System.Text;

namespace FenBrowser.Js.Diagnostics;

/// <summary>
/// Stage-level accounting for one JS function invocation, enabled by
/// FEN_FENJS_CALLPROF=1.
/// </summary>
/// <remarks>
/// A call is not one cost, it is seven, and a whole-page number cannot say
/// which. Each stage records time and bytes: an allocation shows up as bytes
/// even when its own time is small, because most of what it costs is paid later
/// by the collector, which no stopwatch around the allocation would ever see.
///
/// Stages nest - Body contains the callee's own stages - so only the leaf
/// stages sum to the total. Counting the same work twice is the trap here, so
/// the report says which stages are self and which are inclusive.
/// </remarks>
public static class CallPathProfiler
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_CALLPROF"),
        "1",
        StringComparison.Ordinal);

    public enum Stage
    {
        ArgumentArray,
        ThisBinding,
        EnvironmentCreate,
        SlotAttach,
        BindThisAndName,
        FrameCreate,
        ParameterBinding,
        Declarations,
        Body,
        Prologue,
        CallTotal,
        Teardown,
        Count,
    }

    private sealed class StageState
    {
        public readonly long[] Ticks = new long[(int)Stage.Count];
        public readonly long[] Bytes = new long[(int)Stage.Count];
        public readonly long[] Calls = new long[(int)Stage.Count];
    }

    [ThreadStatic]
    private static StageState? _state;

    private static readonly List<StageState> AllStates = new();

    private static StageState State
    {
        get
        {
            var state = _state;
            if (state is null)
            {
                state = new StageState();
                _state = state;
                lock (AllStates) AllStates.Add(state);
            }

            return state;
        }
    }

    // Cost of one sample pair, subtracted from every measurement so a stage is
    // not billed for the instrument that watched it.
    private static readonly double OverheadTicks = MeasureOverhead();

    private static long _sink;

    private static double MeasureOverhead()
    {
        if (!Enabled) return 0;

        for (var i = 0; i < 10_000; i++) _sink += System.Diagnostics.Stopwatch.GetTimestamp();

        const int Iterations = 200_000;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < Iterations; i++)
        {
            var first = System.Diagnostics.Stopwatch.GetTimestamp();
            _sink += GC.GetAllocatedBytesForCurrentThread();
            var second = System.Diagnostics.Stopwatch.GetTimestamp();
            _sink += second - first;
        }

        return (double)(System.Diagnostics.Stopwatch.GetTimestamp() - start) / Iterations;
    }

    public readonly struct Sample
    {
        public readonly long Ticks;
        public readonly long Bytes;
        public Sample(long ticks, long bytes) { Ticks = ticks; Bytes = bytes; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Sample Begin() =>
        new(System.Diagnostics.Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    public static void End(Stage stage, Sample start)
    {
        var state = State;
        var index = (int)stage;
        state.Ticks[index] += System.Diagnostics.Stopwatch.GetTimestamp() - start.Ticks;
        state.Bytes[index] += GC.GetAllocatedBytesForCurrentThread() - start.Bytes;
        state.Calls[index]++;
    }

    public static string Report()
    {
        StageState[] states;
        lock (AllStates) states = AllStates.ToArray();
        if (states.Length == 0) return "[FenJsCallPath] nothing recorded";

        var ticks = new long[(int)Stage.Count];
        var bytes = new long[(int)Stage.Count];
        var calls = new long[(int)Stage.Count];
        foreach (var state in states)
        {
            for (var i = 0; i < (int)Stage.Count; i++)
            {
                ticks[i] += state.Ticks[i];
                bytes[i] += state.Bytes[i];
                calls[i] += state.Calls[i];
            }
        }

        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var text = new StringBuilder();
        text.AppendLine("[FenJsCallPath] per-stage cost (overhead-corrected; Body is inclusive of nested calls)");
        text.AppendLine("    stage                    calls        net ms     ns/call    bytes/call");
        for (var i = 0; i < (int)Stage.Count; i++)
        {
            if (calls[i] == 0) continue;
            var netTicks = ticks[i] - (OverheadTicks * calls[i]);
            if (netTicks < 0) netTicks = 0;
            var ms = 1000.0 * netTicks / frequency;
            var nsPerCall = 1e9 * netTicks / frequency / calls[i];
            var bytesPerCall = (double)bytes[i] / calls[i];
            text.Append("    ")
                .Append(((Stage)i).ToString().PadRight(22))
                .Append(calls[i].ToString().PadLeft(10))
                .Append(ms.ToString("F1").PadLeft(12))
                .Append(nsPerCall.ToString("F0").PadLeft(12))
                .Append(bytesPerCall.ToString("F0").PadLeft(14))
                .AppendLine();
        }

        return text.ToString();
    }
}
