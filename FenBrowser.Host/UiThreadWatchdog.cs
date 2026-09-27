using System;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host;

/// <summary>
/// Monitors the UI thread heartbeat and logs diagnostics when the UI becomes
/// unresponsive. Runs on a dedicated background timer so it survives UI-thread
/// stalls.
///
/// Usage:
///   Call UiThreadWatchdog.Instance.Heartbeat() from the UI thread each frame.
///   The watchdog fires a background timer and logs a warning if the heartbeat
///   has not been updated within the configured threshold.
///
/// The window loop sleeps while nothing needs presenting, so a quiet UI thread
/// produces no frames. Initialize takes a wake callback that each poll uses to
/// poke the loop: an idle thread answers at once and keeps beating, while a
/// thread stuck in work cannot, which is exactly the stall being measured.
/// </summary>
public sealed class UiThreadWatchdog : IDisposable
{
    private static UiThreadWatchdog _instance;
    public static UiThreadWatchdog Instance => _instance ??= new UiThreadWatchdog();

    private readonly object _lock = new();
    private readonly int _managedThreadId;
    private long _heartbeatTimestamp;
    private long _lastHealthyHeartbeatTimestamp;
    private long _lastWarningTimestamp;
    private bool _running;
    private Timer _timer;
    private int _stallCount;
    private long _totalStallTimeSpanTicks;
    private long _maxStallTimeSpanTicks;
    private TimeSpan _stallThreshold;
    private TimeSpan _pollInterval;
    private TimeSpan _warningCooldown;

    // Set via Initialize() — captured once so the watchdog can read it without
    // touching potentially-blocked UI-thread state.
    private Func<(string Status, int ActiveTabId, long CompositorFrameSeq, int TabCount)> _telemetryProvider;
    private Action _wakeUiThread;

    private UiThreadWatchdog()
    {
        _managedThreadId = Environment.CurrentManagedThreadId;
        _stallThreshold = TimeSpan.FromMilliseconds(100);
        _pollInterval = TimeSpan.FromMilliseconds(50);
        _warningCooldown = TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Start the watchdog. Call once from the UI thread during host initialization.
    /// </summary>
    public void Initialize(
        TimeSpan? stallThreshold = null,
        TimeSpan? pollInterval = null,
        Func<(string Status, int ActiveTabId, long CompositorFrameSeq, int TabCount)> telemetryProvider = null,
        Action wakeUiThread = null)
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }

            _stallThreshold = NormalizePositiveDuration(
                stallThreshold ?? TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100));
            _pollInterval = NormalizePositiveDuration(
                pollInterval ?? TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(50));
            _telemetryProvider = telemetryProvider;
            _wakeUiThread = wakeUiThread;

            _running = true;
            _heartbeatTimestamp = Stopwatch.GetTimestamp();
            _lastHealthyHeartbeatTimestamp = _heartbeatTimestamp;
            _lastWarningTimestamp = 0;

            _timer = new Timer(OnTimerTick, null, _pollInterval, _pollInterval);
            EngineLogBridge.Info(
                $"[UiThreadWatchdog] Started (stallThreshold={_stallThreshold.TotalMilliseconds:F0}ms, pollInterval={_pollInterval.TotalMilliseconds:F0}ms)",
                LogCategory.Performance);
        }
    }

    /// <summary>
    /// Called from the UI thread on every wake-up of the window loop. Extremely cheap —
    /// just a monotonic timestamp plus a single interlocked write.
    /// </summary>
    public void Heartbeat()
    {
        Interlocked.Exchange(ref _heartbeatTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Returns true if the UI thread is currently considered stalled.
    /// </summary>
    public bool IsStalled
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            var lastBeat = Interlocked.Read(ref _heartbeatTimestamp);
            return Stopwatch.GetElapsedTime(lastBeat, now) > _stallThreshold;
        }
    }

    /// <summary>
    /// Total number of stall observations detected since startup.
    /// </summary>
    public int StallCount => Volatile.Read(ref _stallCount);

    /// <summary>
    /// Longest observed stall duration, stored in TimeSpan ticks.
    /// </summary>
    public long MaxStallTicks => Interlocked.Read(ref _maxStallTimeSpanTicks);

    public UiThreadWatchdogTelemetry GetTelemetry()
    {
        return new UiThreadWatchdogTelemetry(
            StallCount,
            new TimeSpan(Interlocked.Read(ref _maxStallTimeSpanTicks)),
            new TimeSpan(Interlocked.Read(ref _totalStallTimeSpanTicks)),
            IsStalled);
    }

    private void OnTimerTick(object state)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var lastBeat = Interlocked.Read(ref _heartbeatTimestamp);
            var elapsed = Stopwatch.GetElapsedTime(lastBeat, now);

            // Poke the loop so the next poll sees a fresh beat from an idle thread.
            _wakeUiThread?.Invoke();

            if (elapsed <= _stallThreshold)
            {
                Interlocked.Exchange(ref _lastHealthyHeartbeatTimestamp, lastBeat);
                return;
            }

            Interlocked.Increment(ref _stallCount);
            Interlocked.Add(ref _totalStallTimeSpanTicks, _pollInterval.Ticks);

            // Keep telemetry in TimeSpan ticks. Stopwatch timestamps use
            // Stopwatch.Frequency, which is platform dependent and must never be
            // interpreted directly as TimeSpan ticks.
            var elapsedTicks = elapsed.Ticks;
            long prevMax;
            do
            {
                prevMax = Interlocked.Read(ref _maxStallTimeSpanTicks);
                if (elapsedTicks <= prevMax)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(
                ref _maxStallTimeSpanTicks,
                elapsedTicks,
                prevMax) != prevMax);

            // Rate-limit warnings against the same monotonic clock, converting its
            // frequency explicitly through Stopwatch.GetElapsedTime().
            var lastWarn = Interlocked.Read(ref _lastWarningTimestamp);
            if (lastWarn != 0 && Stopwatch.GetElapsedTime(lastWarn, now) < _warningCooldown)
            {
                return;
            }

            Interlocked.Exchange(ref _lastWarningTimestamp, now);

            var telemetry = _telemetryProvider?.Invoke()
                ?? ("unavailable", 0, 0L, 0);

            EngineLogBridge.Warn(
                $"[UiThreadWatchdog] UI thread STALLED for {elapsed.TotalMilliseconds:F0}ms " +
                $"(stallCount={_stallCount}, activeTab={telemetry.ActiveTabId}, " +
                $"compositorFrame={telemetry.CompositorFrameSeq}, tabs={telemetry.TabCount}, " +
                $"status={telemetry.Status})",
                LogCategory.Performance);

            // On a prolonged stall (>2s), emit a full process diagnostic.
            if (elapsed > TimeSpan.FromSeconds(2) && _stallCount % 10 == 0)
            {
                EmitProcessDiagnostic();
            }
        }
        catch
        {
            // The watchdog itself must never throw — that would kill the timer.
        }
    }

    private static TimeSpan NormalizePositiveDuration(TimeSpan value, TimeSpan fallback)
    {
        return value > TimeSpan.Zero ? value : fallback;
    }

    private static void EmitProcessDiagnostic()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            EngineLogBridge.Warn(
                $"[UiThreadWatchdog] Process diagnostic: " +
                $"threads={proc.Threads.Count}, " +
                $"workingSet={proc.WorkingSet64 / 1024 / 1024}MB, " +
                $"pagedMemory={proc.PagedMemorySize64 / 1024 / 1024}MB, " +
                $"handleCount={proc.HandleCount}, " +
                $"totalProcessorTime={proc.TotalProcessorTime.TotalSeconds:F1}s",
                LogCategory.Performance);
        }
        catch
        {
            // Best-effort diagnostic; swallow failures.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

public readonly record struct UiThreadWatchdogTelemetry(
    int StallCount,
    TimeSpan MaxStallDuration,
    TimeSpan TotalStallDuration,
    bool IsCurrentlyStalled);
