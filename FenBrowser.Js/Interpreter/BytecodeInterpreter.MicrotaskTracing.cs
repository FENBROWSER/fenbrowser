using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

/// <summary>
/// One opt-in diagnostic milestone from a microtask checkpoint. The interpreter
/// remains logging-framework agnostic; browser hosts attach a sink only for an
/// explicitly requested trace run.
/// </summary>
public sealed record FenJsMicrotaskTraceEvent(
    long CheckpointId,
    int CheckpointDepth,
    long Sequence,
    string Stage,
    int Pass,
    int JobIndex,
    string JobKind,
    string Detail,
    int PendingMicrotasks,
    int PendingCleanupJobs,
    int InstructionCount,
    long ElapsedMilliseconds,
    string ActiveFrame);

public sealed partial class BytecodeInterpreter
{
    private sealed class MicrotaskTraceContext
    {
        public required long Id { get; init; }
        public required int Depth { get; init; }
        public required long StartedTimestamp { get; init; }
        public required MicrotaskTraceContext? Parent { get; init; }
        public long Sequence { get; set; }
        public int Pass { get; set; }
        public int NextJobIndex { get; set; }
        public int JobIndex { get; set; }
        public string JobKind { get; set; } = string.Empty;
        public string JobDetail { get; set; } = string.Empty;
        public bool LogCurrentJob { get; set; }
        public long NextExecutionSampleTick { get; set; }
    }

    private readonly record struct MicrotaskTraceJobState(
        int PreviousJobIndex,
        string PreviousJobKind,
        string PreviousJobDetail,
        bool PreviousLogCurrentJob,
        long StartedTimestamp,
        int StartedInstructions);

    private long _microtaskTraceCheckpointSequence;
    private MicrotaskTraceContext? _microtaskTraceContext;

    /// <summary>
    /// Optional diagnostic sink. Null is the normal production path and leaves
    /// all detailed tracing disabled.
    /// </summary>
    public Action<FenJsMicrotaskTraceEvent>? MicrotaskTraceSink { get; set; }

    public int MicrotaskTraceDetailedJobLimit { get; set; } = 2_000;
    public int MicrotaskTraceJobSampleInterval { get; set; } = 250;
    public int MicrotaskTraceExecutionSampleIntervalMs { get; set; } = 500;

    private MicrotaskTraceContext? BeginMicrotaskTraceCheckpoint()
    {
        if (MicrotaskTraceSink == null)
        {
            return null;
        }

        var parent = _microtaskTraceContext;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var context = new MicrotaskTraceContext
        {
            Id = ++_microtaskTraceCheckpointSequence,
            Depth = (parent?.Depth ?? 0) + 1,
            StartedTimestamp = now,
            Parent = parent,
            NextExecutionSampleTick = Environment.TickCount64 +
                Math.Max(100, MicrotaskTraceExecutionSampleIntervalMs)
        };
        _microtaskTraceContext = context;
        TraceMicrotaskStage("checkpoint-enter", force: true);
        return context;
    }

    private void EndMicrotaskTraceCheckpoint(MicrotaskTraceContext? context, Exception? failure)
    {
        if (context == null)
        {
            return;
        }

        TraceMicrotaskStage(
            failure == null ? "checkpoint-complete" : "checkpoint-failed",
            failure == null ? string.Empty : DescribeMicrotaskTraceException(failure),
            force: true);
        _microtaskTraceContext = context.Parent;
    }

    private void BeginMicrotaskTracePass(int pass)
    {
        if (_microtaskTraceContext is not { } context)
        {
            return;
        }

        context.Pass = pass;
        TraceMicrotaskStage("pass-enter", force: true);
    }

    private MicrotaskTraceJobState BeginMicrotaskTraceJob(string kind, string detail)
    {
        if (_microtaskTraceContext is not { } context)
        {
            return default;
        }

        var state = new MicrotaskTraceJobState(
            context.JobIndex,
            context.JobKind,
            context.JobDetail,
            context.LogCurrentJob,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            _instructionCount);
        context.JobIndex = ++context.NextJobIndex;
        context.JobKind = kind ?? string.Empty;
        context.JobDetail = detail ?? string.Empty;
        context.LogCurrentJob = context.JobIndex <= Math.Max(0, MicrotaskTraceDetailedJobLimit) ||
            (MicrotaskTraceJobSampleInterval > 0 &&
             context.JobIndex % MicrotaskTraceJobSampleInterval == 0);
        TraceMicrotaskStage("job-enter");
        return state;
    }

    private void EndMicrotaskTraceJob(MicrotaskTraceJobState state, Exception? failure = null)
    {
        if (_microtaskTraceContext is not { } context)
        {
            return;
        }

        var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - state.StartedTimestamp;
        var elapsedMs = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var instructionDelta = _instructionCount - state.StartedInstructions;
        var detail = $"elapsedMs={elapsedMs:F3}; instructionDelta={instructionDelta}";
        if (failure != null)
        {
            detail += "; exception=" + DescribeMicrotaskTraceException(failure);
        }

        TraceMicrotaskStage(failure == null ? "job-complete" : "job-failed", detail);
        context.JobIndex = state.PreviousJobIndex;
        context.JobKind = state.PreviousJobKind;
        context.JobDetail = state.PreviousJobDetail;
        context.LogCurrentJob = state.PreviousLogCurrentJob;
    }

    private void TraceMicrotaskStage(string stage, string detail = "", bool force = false)
    {
        var sink = MicrotaskTraceSink;
        var context = _microtaskTraceContext;
        if (sink == null || context == null || (!force && !context.LogCurrentJob))
        {
            return;
        }

        var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - context.StartedTimestamp;
        var eventDetail = context.JobDetail;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            eventDetail = string.IsNullOrWhiteSpace(eventDetail)
                ? detail
                : eventDetail + "; " + detail;
        }

        var traceEvent = new FenJsMicrotaskTraceEvent(
            context.Id,
            context.Depth,
            ++context.Sequence,
            stage ?? string.Empty,
            context.Pass,
            context.JobIndex,
            context.JobKind,
            eventDetail,
            _jobQueue.Count,
            _finalizationCleanupJobs.Count,
            _instructionCount,
            (long)(elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency),
            DescribeActiveMicrotaskTraceFrame());

        try
        {
            sink(traceEvent);
        }
        catch
        {
            // Diagnostics must never replace or perturb the JS outcome.
        }
    }

    private void TraceMicrotaskExecutionProgressIfDue()
    {
        if (_microtaskTraceContext is not { } context || MicrotaskTraceSink == null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < context.NextExecutionSampleTick)
        {
            return;
        }

        context.NextExecutionSampleTick = now +
            Math.Max(100, MicrotaskTraceExecutionSampleIntervalMs);
        TraceMicrotaskStage("execution-progress", force: true);
    }

    private string DescribeActiveMicrotaskTraceFrame()
    {
        try
        {
            if (_activeFrames.Count == 0)
            {
                return "<host>";
            }

            var frame = _activeFrames.Peek();
            var function = frame.Function;
            var ip = Math.Clamp(
                frame.InstructionPointer - 1,
                0,
                Math.Max(0, function.Instructions.Count - 1));
            var opcode = function.Instructions.Count == 0
                ? "none"
                : function.Instructions[ip].OpCode.ToString();
            return $"{FenBrowser.Js.Bytecode.BytecodeFunctionSignature.Describe(function)} " +
                $"ip={ip} op={opcode} depth={_activeFrames.Count}";
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private string DescribeMicrotaskCallbackForTrace(JsValue callback) =>
        DescribeCalleeForDiagnostics(callback);

    private string DescribePromiseJobForTrace(PromiseJob job)
    {
        return job switch
        {
            PromiseReactionJob reaction =>
                $"reaction realm={reaction.RealmId} type={reaction.Reaction.Type} " +
                $"handler={DescribeCalleeForDiagnostics(reaction.Reaction.Handler)} " +
                $"argument={reaction.Argument.Tag}",
            HostCallbackJob callback =>
                "callback=" + DescribeMicrotaskCallbackForTrace(callback.Callback),
            PromiseResolveThenableJob thenable =>
                $"resolve-thenable realm={thenable.RealmId} " +
                $"then={DescribeCalleeForDiagnostics(thenable.Then)} " +
                $"thenable={thenable.Thenable.Tag}",
            _ => $"{job.GetType().Name} realm={job.RealmId}"
        };
    }

    private static string DescribeMicrotaskTraceException(Exception exception)
    {
        var text = exception.GetType().Name + ": " + exception.Message;
        return text.Length <= 512 ? text : text[..512] + "...";
    }
}
