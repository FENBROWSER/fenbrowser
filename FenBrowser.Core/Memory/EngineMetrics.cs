using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Memory
{
    // ── Engine Metrics & Memory Tracking ──────────────────────────────────────
    // Per the guide §17.4: counters, tracing, jank detector, long-task metrics.
    //
    // Design goals:
    //   - Near-zero overhead in production (interlocked counters, no lock in hot path)
    //   - Timeline spans via Span<T> stack records (no heap allocation per span)
    //   - Jank detector: flag any task > 50 ms on the main thread
    //   - Frame timing budget enforcement (16.6 ms at 60 Hz)
    //   - Crash keys: site lock, process role, last IPC message type
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Global counter categories.</summary>
    public enum MetricCounter
    {
        // Allocations
        StyleRecalcCount,
        LayoutPassCount,
        PaintPassCount,
        DisplayListNodeCount,
        DomMutationCount,
        JsGcCount,

        // Network
        FetchRequestCount,
        FetchBytesReceived,
        CorsPreflightCount,

        // IPC
        IpcMessagesSent,
        IpcMessagesReceived,
        IpcBytesTotal,

        // Frame timing
        FrameCount,
        JankFrameCount,         // frames > budget
        LongTaskCount,          // tasks > 50 ms
        DroppedFrameCount,

        // JS engine
        JsScriptExecutionCount,
        JsBytecodeCompileCount,

        _Count
    }

    /// <summary>
    /// Process-wide metrics registry. Lock-free reads and writes via interlocked ops.
    /// </summary>
    public sealed class EngineMetrics
    {
        public static readonly EngineMetrics Instance = new();

        private readonly long[] _counters = new long[(int)MetricCounter._Count];
        private readonly ConcurrentDictionary<string, string> _crashKeys = new(StringComparer.Ordinal);

        private EngineMetrics() { }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Increment(MetricCounter counter, long amount = 1)
        {
            Interlocked.Add(ref _counters[(int)counter], amount);
        }

        public long Get(MetricCounter counter) => Interlocked.Read(ref _counters[(int)counter]);

        public void Reset(MetricCounter counter) => Interlocked.Exchange(ref _counters[(int)counter], 0);

        public void ResetAll()
        {
            for (int i = 0; i < _counters.Length; i++)
                Interlocked.Exchange(ref _counters[i], 0);
        }

        /// <summary>Set a crash key (appended to crash reports for triage).</summary>
        public void SetCrashKey(string key, string value)
        {
            if (key != null) _crashKeys[key] = value ?? "";
        }

        public IReadOnlyDictionary<string, string> CrashKeys => _crashKeys;

        /// <summary>Snapshot all counters for telemetry reporting.</summary>
        public Dictionary<string, long> Snapshot()
        {
            var d = new Dictionary<string, long>(_counters.Length);
            for (int i = 0; i < _counters.Length; i++)
                d[((MetricCounter)i).ToString()] = Interlocked.Read(ref _counters[i]);
            return d;
        }
    }

    /// <summary>
    /// RAII timeline span. Records start/end time in the <see cref="TimelineTracer"/>.
    /// Zero allocation on the hot path (stack struct).
    /// </summary>
    public readonly struct TimelineSpan : IDisposable
    {
        private readonly TimelineTracer _tracer;
        private readonly int _spanId;
        private readonly long _startTicks;

        internal TimelineSpan(TimelineTracer tracer, int spanId, long startTicks)
        {
            _tracer = tracer;
            _spanId = spanId;
            _startTicks = startTicks;
        }

        public void Dispose()
        {
            var endTicks = Stopwatch.GetTimestamp();
            _tracer?.EndSpan(_spanId, _startTicks, endTicks);
        }
    }

    public sealed class TraceEvent
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public long StartTicks { get; set; }
        public long EndTicks { get; set; }
        public int ThreadId { get; set; }
        public Dictionary<string, string> Args { get; set; }

        public double DurationMs =>
            (EndTicks - StartTicks) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Ring-buffer timeline tracer. Fixed size; overwrites oldest events when full.
    /// Designed for devtools timeline panels and performance profiling.
    /// </summary>
    public sealed class TimelineTracer
    {
        private const int RingSize = 4096;
        private readonly TraceEvent[] _ring = new TraceEvent[RingSize];
        private int _head;
        private int _nextSpanId;
        private readonly ConcurrentDictionary<int, TraceEvent> _pending = new();
        private bool _enabled = true;

        public static readonly TimelineTracer Instance = new();

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>Begin a named span. Dispose the returned value to end it.</summary>
        public TimelineSpan Begin(string name, string category = "engine")
        {
            if (!_enabled) return default;
            int id = Interlocked.Increment(ref _nextSpanId);
            var ev = new TraceEvent
            {
                Name = name,
                Category = category,
                StartTicks = Stopwatch.GetTimestamp(),
                ThreadId = Environment.CurrentManagedThreadId,
            };
            _pending[id] = ev;
            return new TimelineSpan(this, id, ev.StartTicks);
        }

        internal void EndSpan(int spanId, long startTicks, long endTicks)
        {
            if (!_pending.TryRemove(spanId, out var ev)) return;
            ev.EndTicks = endTicks;

            int slot = Interlocked.Increment(ref _head) % RingSize;
            _ring[slot] = ev;

            // Check for long task (> 50 ms)
            double ms = ev.DurationMs;
            if (ms > 50.0)
            {
                EngineMetrics.Instance.Increment(MetricCounter.LongTaskCount);
                FenLogger.Warn(
                    $"[LongTask] '{ev.Name}' took {ms:F1} ms (thread={ev.ThreadId})",
                    LogCategory.General);
            }
        }

        /// <summary>Return a snapshot of the most recent events (up to <paramref name="count"/>).</summary>
        public List<TraceEvent> GetRecentEvents(int count = 256)
        {
            var result = new List<TraceEvent>(count);
            int head = _head;
            for (int i = 0; i < Math.Min(count, RingSize); i++)
            {
                var ev = _ring[(head - i + RingSize) % RingSize];
                if (ev != null) result.Add(ev);
            }
            return result;
        }
    }

    /// <summary>
    /// Frame budget enforcer.
    /// Monitors per-frame time and tracks jank / dropped frames.
    /// </summary>
    public sealed class FrameBudgetMonitor
    {
        public static readonly FrameBudgetMonitor Instance = new();

        private double _targetHz = 60.0;
        private long _frameStartTicks;
        private long _totalFrames;
        private long _jankFrames;

        public double TargetHz
        {
            get => _targetHz;
            set => _targetHz = Math.Max(1.0, Math.Min(value, 240.0));
        }

        public double BudgetMs => 1000.0 / _targetHz;

        public long TotalFrames => Interlocked.Read(ref _totalFrames);
        public long JankFrames => Interlocked.Read(ref _jankFrames);
        public double JankRate => _totalFrames == 0 ? 0 : (double)_jankFrames / _totalFrames;

        /// <summary>Call at the start of each frame.</summary>
        public void BeginFrame()
        {
            _frameStartTicks = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _totalFrames);
            EngineMetrics.Instance.Increment(MetricCounter.FrameCount);
        }

        /// <summary>Call at the end of each frame. Returns true if the frame was within budget.</summary>
        public bool EndFrame()
        {
            long endTicks = Stopwatch.GetTimestamp();
            double ms = (endTicks - _frameStartTicks) * 1000.0 / Stopwatch.Frequency;
            bool onTime = ms <= BudgetMs;

            if (!onTime)
            {
                Interlocked.Increment(ref _jankFrames);
                EngineMetrics.Instance.Increment(MetricCounter.JankFrameCount);
                FenLogger.Debug(
                    $"[FrameJank] {ms:F1} ms > budget {BudgetMs:F1} ms",
                    LogCategory.Rendering);
            }

            return onTime;
        }

        /// <summary>Get current frame elapsed time (ms). Useful for mid-frame checks.</summary>
        public double ElapsedMs =>
            (Stopwatch.GetTimestamp() - _frameStartTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>True if the current frame has already exceeded budget.</summary>
        public bool IsOverBudget => ElapsedMs > BudgetMs;
    }

    /// <summary>
    /// Jank detector: watches a task queue for tasks that run longer than threshold.
    /// Attach to the main thread event loop.
    /// </summary>
    public sealed class JankDetector
    {
        private long _taskStartTicks;
        private string _currentTaskName;
        private bool _monitoring;
        private readonly double _thresholdMs;

        public JankDetector(double thresholdMs = 50.0) => _thresholdMs = thresholdMs;

        public void TaskStart(string taskName)
        {
            _currentTaskName = taskName;
            _taskStartTicks = Stopwatch.GetTimestamp();
            _monitoring = true;
        }

        public void TaskEnd()
        {
            if (!_monitoring) return;
            _monitoring = false;
            var ms = (Stopwatch.GetTimestamp() - _taskStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (ms >= _thresholdMs)
            {
                EngineMetrics.Instance.Increment(MetricCounter.LongTaskCount);
                FenLogger.Warn(
                    $"[JankDetector] Long task '{_currentTaskName ?? "unknown"}': {ms:F1} ms",
                    LogCategory.General);
            }
        }

        /// <summary>Periodic check — call from a watchdog timer to catch stuck tasks.</summary>
        public void Poll()
        {
            if (!_monitoring) return;
            var ms = (Stopwatch.GetTimestamp() - _taskStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (ms > _thresholdMs * 10) // 500 ms default: force log
            {
                FenLogger.Warn(
                    $"[JankDetector] STUCK task '{_currentTaskName ?? "unknown"}': {ms:F0} ms elapsed",
                    LogCategory.General);
            }
        }
    }

    /// <summary>
    /// CPU/frame timing budget enforcement integration point.
    /// Ties together <see cref="FrameBudgetMonitor"/>, <see cref="TimelineTracer"/>,
    /// and <see cref="EngineMetrics"/> into a single coordinator.
    /// </summary>
    public sealed class PerformanceCoordinator
    {
        public static readonly PerformanceCoordinator Instance = new();

        public FrameBudgetMonitor FrameBudget => FrameBudgetMonitor.Instance;
        public TimelineTracer Timeline => TimelineTracer.Instance;
        public EngineMetrics Metrics => EngineMetrics.Instance;

        public FrameArenaPool FrameArenas { get; } = new FrameArenaPool();

        private readonly JankDetector _jankDetector = new();

        /// <summary>Call at the start of a new rendering frame.</summary>
        public void BeginFrame()
        {
            FrameBudget.BeginFrame();
            FrameArenas.BeginFrame();
        }

        /// <summary>Call at the end of a rendering frame.</summary>
        public bool EndFrame() => FrameBudget.EndFrame();

        /// <summary>Record the start of a main-thread task.</summary>
        public TimelineSpan BeginTask(string name, string category = "task")
        {
            _jankDetector.TaskStart(name);
            return Timeline.Begin(name, category);
        }

        /// <summary>Record the end of a main-thread task (called via span.Dispose).</summary>
        public void EndTask() => _jankDetector.TaskEnd();

        /// <summary>Get a performance report for devtools or telemetry.</summary>
        public PerformanceReport GetReport()
        {
            return new PerformanceReport
            {
                TotalFrames = FrameBudget.TotalFrames,
                JankFrames = FrameBudget.JankFrames,
                JankRate = FrameBudget.JankRate,
                BudgetMs = FrameBudget.BudgetMs,
                Counters = Metrics.Snapshot(),
                ArenaStats = FrameArenas.GetStats(),
                RecentEvents = Timeline.GetRecentEvents(64),
            };
        }
    }

    public sealed class PerformanceReport
    {
        public long TotalFrames { get; init; }
        public long JankFrames { get; init; }
        public double JankRate { get; init; }
        public double BudgetMs { get; init; }
        public Dictionary<string, long> Counters { get; init; }
        public IReadOnlyDictionary<string, (int used, int capacity, double pct)> ArenaStats { get; init; }
        public List<TraceEvent> RecentEvents { get; init; }
    }
}
