using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Memory
{
    public enum MetricCounter
    {
        StyleRecalcCount,
        LayoutPassCount,
        PaintPassCount,
        DisplayListNodeCount,
        DomMutationCount,
        JsGcCount,
        FetchRequestCount,
        FetchBytesReceived,
        CorsPreflightCount,
        IpcMessagesSent,
        IpcMessagesReceived,
        IpcBytesTotal,
        FrameCount,
        JankFrameCount,
        LongTaskCount,
        DroppedFrameCount,
        JsScriptExecutionCount,
        JsBytecodeCompileCount,
        _Count
    }

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
            for (var i = 0; i < _counters.Length; i++)
            {
                Interlocked.Exchange(ref _counters[i], 0);
            }
        }

        public void SetCrashKey(string key, string value)
        {
            if (key != null)
            {
                _crashKeys[key] = value ?? string.Empty;
            }
        }

        public IReadOnlyDictionary<string, string> CrashKeys => _crashKeys;

        public Dictionary<string, long> Snapshot()
        {
            var snapshot = new Dictionary<string, long>(_counters.Length);
            for (var i = 0; i < _counters.Length; i++)
            {
                snapshot[((MetricCounter)i).ToString()] = Interlocked.Read(ref _counters[i]);
            }
            return snapshot;
        }
    }

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

        public double DurationMs => (EndTicks - StartTicks) * 1000.0 / Stopwatch.Frequency;
    }

    public sealed class TimelineTracer
    {
        private const int RingSize = 4096;
        private readonly TraceEvent[] _ring = new TraceEvent[RingSize];
        private int _head;
        private int _nextSpanId;
        private readonly ConcurrentDictionary<int, TraceEvent> _pending = new();
        private volatile bool _enabled = true;

        public static readonly TimelineTracer Instance = new();

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public TimelineSpan Begin(string name, string category = "engine")
        {
            if (!_enabled)
            {
                return default;
            }

            var id = Interlocked.Increment(ref _nextSpanId);
            var traceEvent = new TraceEvent
            {
                Name = name,
                Category = category,
                StartTicks = Stopwatch.GetTimestamp(),
                ThreadId = Environment.CurrentManagedThreadId,
            };
            _pending[id] = traceEvent;
            return new TimelineSpan(this, id, traceEvent.StartTicks);
        }

        internal void EndSpan(int spanId, long startTicks, long endTicks)
        {
            if (!_pending.TryRemove(spanId, out var traceEvent))
            {
                return;
            }

            traceEvent.EndTicks = endTicks;
            var slot = (Interlocked.Increment(ref _head) & int.MaxValue) % RingSize;
            Volatile.Write(ref _ring[slot], traceEvent);

            var ms = traceEvent.DurationMs;
            if (ms > 50.0)
            {
                EngineMetrics.Instance.Increment(MetricCounter.LongTaskCount);
                EngineLogCompat.Warn(
                    $"[LongTask] '{traceEvent.Name}' took {ms:F1} ms (thread={traceEvent.ThreadId})",
                    LogCategory.General);
            }
        }

        public List<TraceEvent> GetRecentEvents(int count = 256)
        {
            count = Math.Clamp(count, 0, RingSize);
            var result = new List<TraceEvent>(count);
            var head = Volatile.Read(ref _head) & int.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var slot = (head - i + RingSize) % RingSize;
                var traceEvent = Volatile.Read(ref _ring[slot]);
                if (traceEvent != null)
                {
                    result.Add(traceEvent);
                }
            }
            return result;
        }
    }

    public sealed class FrameBudgetMonitor
    {
        public static readonly FrameBudgetMonitor Instance = new();

        private double _targetHz = 60.0;
        private long _frameStartTicks;
        private long _totalFrames;
        private long _jankFrames;

        public double TargetHz
        {
            get => Volatile.Read(ref _targetHz);
            set => Volatile.Write(ref _targetHz, Math.Clamp(value, 1.0, 240.0));
        }

        public double BudgetMs => 1000.0 / TargetHz;

        public long TotalFrames => Interlocked.Read(ref _totalFrames);
        public long JankFrames => Interlocked.Read(ref _jankFrames);
        public double JankRate
        {
            get
            {
                var total = Interlocked.Read(ref _totalFrames);
                if (total == 0)
                {
                    return 0;
                }

                return (double)Interlocked.Read(ref _jankFrames) / total;
            }
        }

        public void BeginFrame()
        {
            Interlocked.Exchange(ref _frameStartTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _totalFrames);
            EngineMetrics.Instance.Increment(MetricCounter.FrameCount);
        }

        public bool EndFrame()
        {
            var startTicks = Interlocked.Read(ref _frameStartTicks);
            if (startTicks == 0)
            {
                return true;
            }

            var ms = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            var budgetMs = BudgetMs;
            var onTime = ms <= budgetMs;

            if (!onTime)
            {
                Interlocked.Increment(ref _jankFrames);
                EngineMetrics.Instance.Increment(MetricCounter.JankFrameCount);
                EngineLogCompat.Debug(
                    $"[FrameJank] {ms:F1} ms > budget {budgetMs:F1} ms",
                    LogCategory.Rendering);
            }

            return onTime;
        }

        public double ElapsedMs
        {
            get
            {
                var startTicks = Interlocked.Read(ref _frameStartTicks);
                return startTicks == 0 ? 0 : Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            }
        }

        public bool IsOverBudget => ElapsedMs > BudgetMs;
    }

    /// <summary>
    /// Main-thread task jank detector. TaskStart/TaskEnd are normally called by the
    /// event loop while Poll is called by a watchdog thread, so publication is atomic
    /// and stuck-task diagnostics are rate-limited.
    /// </summary>
    public sealed class JankDetector
    {
        private readonly double _thresholdMs;
        private readonly long _warningCooldownTicks;
        private long _taskStartTicks;
        private long _lastStuckWarningTicks;
        private string _currentTaskName;
        private int _monitoring;

        public JankDetector(double thresholdMs = 50.0)
        {
            _thresholdMs = double.IsFinite(thresholdMs) && thresholdMs > 0
                ? thresholdMs
                : 50.0;
            _warningCooldownTicks = Math.Max(
                1,
                (long)(Stopwatch.Frequency * Math.Max(1.0, _thresholdMs * 10.0 / 1000.0)));
        }

        public void TaskStart(string taskName)
        {
            Volatile.Write(ref _currentTaskName, taskName);
            Interlocked.Exchange(ref _lastStuckWarningTicks, 0);
            Interlocked.Exchange(ref _taskStartTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _monitoring, 1);
        }

        public void TaskEnd()
        {
            if (Interlocked.Exchange(ref _monitoring, 0) == 0)
            {
                return;
            }

            var startTicks = Interlocked.Exchange(ref _taskStartTicks, 0);
            var taskName = Volatile.Read(ref _currentTaskName);
            Volatile.Write(ref _currentTaskName, null);
            Interlocked.Exchange(ref _lastStuckWarningTicks, 0);

            if (startTicks == 0)
            {
                return;
            }

            var ms = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            if (ms >= _thresholdMs)
            {
                EngineMetrics.Instance.Increment(MetricCounter.LongTaskCount);
                EngineLogCompat.Warn(
                    $"[JankDetector] Long task '{taskName ?? "unknown"}': {ms:F1} ms",
                    LogCategory.General);
            }
        }

        public void Poll()
        {
            if (Volatile.Read(ref _monitoring) == 0)
            {
                return;
            }

            var startTicks = Interlocked.Read(ref _taskStartTicks);
            if (startTicks == 0)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var ms = Stopwatch.GetElapsedTime(startTicks, now).TotalMilliseconds;
            if (ms <= _thresholdMs * 10)
            {
                return;
            }

            while (true)
            {
                var lastWarning = Interlocked.Read(ref _lastStuckWarningTicks);
                if (lastWarning != 0 && now - lastWarning < _warningCooldownTicks)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _lastStuckWarningTicks, now, lastWarning) == lastWarning)
                {
                    break;
                }
            }

            var taskName = Volatile.Read(ref _currentTaskName);
            EngineLogCompat.Warn(
                $"[JankDetector] STUCK task '{taskName ?? "unknown"}': {ms:F0} ms elapsed",
                LogCategory.General);
        }
    }

    public sealed class PerformanceCoordinator
    {
        public static readonly PerformanceCoordinator Instance = new();

        public FrameBudgetMonitor FrameBudget => FrameBudgetMonitor.Instance;
        public TimelineTracer Timeline => TimelineTracer.Instance;
        public EngineMetrics Metrics => EngineMetrics.Instance;

        public FrameArenaPool FrameArenas { get; } = new FrameArenaPool();

        private readonly JankDetector _jankDetector = new();

        public void BeginFrame()
        {
            FrameBudget.BeginFrame();
            FrameArenas.BeginFrame();
        }

        public bool EndFrame() => FrameBudget.EndFrame();

        public TimelineSpan BeginTask(string name, string category = "task")
        {
            _jankDetector.TaskStart(name);
            return Timeline.Begin(name, category);
        }

        public void EndTask() => _jankDetector.TaskEnd();

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
