using System.Text;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Diagnostics;

/// <summary>
/// Opt-in inclusive timing and allocation accounting by concrete call target.
/// This complements the stage profiler: it answers which native or bytecode
/// function owns a long call rather than only which part of call setup costs.
/// </summary>
public static class CallTargetProfiler
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_CALLTARGET_TRACE"),
        "1",
        StringComparison.Ordinal) || string.Equals(
        Environment.GetEnvironmentVariable("FEN_FENJS_DEEP_TRACE"),
        "1",
        StringComparison.Ordinal);

    private sealed class TargetStat
    {
        public long Calls;
        public long Ticks;
        public long Bytes;
        public long MaxTicks;
    }

    private sealed class ThreadState
    {
        public readonly object Gate = new();
        public readonly Dictionary<object, TargetStat> Targets =
            new(ReferenceEqualityComparer.Instance);
    }

    [ThreadStatic]
    private static ThreadState? _state;

    private static readonly List<ThreadState> AllStates = new();

    private static ThreadState State
    {
        get
        {
            var state = _state;
            if (state != null)
            {
                return state;
            }

            state = new ThreadState();
            _state = state;
            lock (AllStates)
            {
                AllStates.Add(state);
            }

            return state;
        }
    }

    public readonly record struct Sample(object? Target, long Ticks, long Bytes);

    public static Sample Begin(object? target) => new(
        target,
        System.Diagnostics.Stopwatch.GetTimestamp(),
        GC.GetAllocatedBytesForCurrentThread());

    public static void End(Sample sample)
    {
        if (sample.Target == null)
        {
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - sample.Ticks;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - sample.Bytes;
        var state = State;
        lock (state.Gate)
        {
            if (!state.Targets.TryGetValue(sample.Target, out var stat))
            {
                stat = new TargetStat();
                state.Targets.Add(sample.Target, stat);
            }

            stat.Calls++;
            stat.Ticks += elapsed;
            stat.Bytes += bytes;
            stat.MaxTicks = Math.Max(stat.MaxTicks, elapsed);
        }
    }

    public static string Report(int top = 40)
    {
        ThreadState[] states;
        lock (AllStates)
        {
            states = AllStates.ToArray();
        }

        var merged = new Dictionary<object, TargetStat>(ReferenceEqualityComparer.Instance);
        foreach (var state in states)
        {
            lock (state.Gate)
            {
                foreach (var (target, source) in state.Targets)
                {
                    if (!merged.TryGetValue(target, out var destination))
                    {
                        destination = new TargetStat();
                        merged.Add(target, destination);
                    }

                    destination.Calls += source.Calls;
                    destination.Ticks += source.Ticks;
                    destination.Bytes += source.Bytes;
                    destination.MaxTicks = Math.Max(destination.MaxTicks, source.MaxTicks);
                }
            }
        }

        if (merged.Count == 0)
        {
            return "[FenJsCallTarget] no calls recorded";
        }

        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var ranked = merged
            .OrderByDescending(pair => pair.Value.Ticks)
            .Take(Math.Max(1, top));
        var text = new StringBuilder();
        text.AppendLine("[FenJsCallTarget] inclusive time and allocation by concrete callee");
        text.AppendLine("    target                                                        calls       totalMs       maxMs       MB");
        foreach (var (target, stat) in ranked)
        {
            var name = Describe(target);
            if (name.Length > 60)
            {
                name = name[..60];
            }

            text.Append("    ")
                .Append(name.PadRight(60))
                .Append(stat.Calls.ToString("N0").PadLeft(12))
                .Append((stat.Ticks * 1000.0 / frequency).ToString("F1").PadLeft(14))
                .Append((stat.MaxTicks * 1000.0 / frequency).ToString("F1").PadLeft(12))
                .Append((stat.Bytes / (1024.0 * 1024.0)).ToString("F1").PadLeft(10))
                .AppendLine();
        }

        return text.ToString();
    }

    internal static string Describe(object? target) => target switch
    {
        JsFunctionObject function => BytecodeFunctionSignature.Describe(function.Function),
        NativeFunctionObject native => "native:" + native.Name,
        BoundFunctionObject => "bound-function",
        ProxyObject => "proxy-call",
        null => "<none>",
        _ => target.GetType().Name
    };
}
