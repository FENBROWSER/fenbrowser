using System;
using System.Diagnostics;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host;

/// <summary>
/// Monitors the UI thread heartbeat and logs diagnostics when the UI becomes
/// unresponsive.  Runs on a dedicated background thread so it survives UI-thread
/// stalls.
///
/// Usage:
///   Call UiThreadWatchdog.Instance.Heartbeat() from the UI thread each frame.
///   The watchdog fires a background timer and logs a warning if the heartbeat
///   has not been updated within the configured threshold.
/// </summary>
public sealed class UiThreadWatchdog : IDisposable
{
    private static UiThreadWatchdog _instance;
    public static UiThreadWatchdog Instance => _instance ??= new UiThreadWatchdog();

    private readonly object _lock = new();
    private readonly int _managedThreadId;
    private long _heartbeatTicks;
    private long _lastHealthyHeartbeatTicks;
    private long _lastWarningTicks;
    private bool _running;
    private Timer _timer;
    private int _stallCount;
    private long _totalStallTicks;
    private long _maxStallTicks;
    private TimeSpan _stallThreshold;
    private TimeSpan _pollInterval;
    private TimeSpan _warningCooldown;

    // Set via Initialize() — captured once so the watchdog can read them without
    // touching any potentially-blocked UI-thread state.
    private Func<(string Status, int ActiveTabId, long CompositorFrameSeq, int TabCount)> _telemetryProvider;

    private UiThreadWatchdog()
    {
        _managedThreadId = Environment.CurrentManagedThreadId;
        _stallThreshold = TimeSpan.FromMilliseconds(100);
        _pollInterval = TimeSpan.FromMilliseconds(50);
        _warningCooldown = TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Start the watchdog.  Call once from the UI thread during host initialization.
    /// </summary>
    public void Initialize(
        TimeSpan? stallThreshold = null,
        TimeSpan? pollInterval = null,
        Func<(string Status, int ActiveTabId, long CompositorFrameSeq, int TabCount)> telemetryProvider = null)
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }

            _stallThreshold = stallThreshold ?? TimeSpan.FromMilliseconds(100);
            _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
            _telemetryProvider = telemetryProvider;

            _running = true;
            _heartbeatTicks = Stopwatch.GetTimestamp();
            _lastHealthyHeartbeatTicks = _heartbeatTicks;

            _timer = new Timer(OnTimerTick, null, _pollInterval, _pollInterval);
            EngineLogBridge.Info(
                $"[UiThreadWatchdog] Started (stallThreshold={_stallThreshold.TotalMilliseconds:F0}ms, pollInterval={_pollInterval.TotalMilliseconds:F0}ms)",
                LogCategory.Performance);
        }
    }

    /// <summary>
    /// Called from the UI thread every frame render.  Extremely cheap —
    /// just a single interlocked write.
    /// </summary>
    public void Heartbeat()
    {
        Interlocked.Exchange(ref _heartbeatTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Returns true if the UI thread is currently considered stalled.
    /// </summary>
    public bool IsStalled
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            var lastBeat = Interlocked.Read(ref _heartbeatTicks);
            return (now - lastBeat) > _stallThreshold.Ticks;
        }
    }

    /// <summary>
    /// Total number of stall episodes detected since startup.
    /// </summary>
    public int StallCount => Volatile.Read(ref _stallCount);

    /// <summary>
    /// Longest single stall duration in ticks.
    /// </summary>
    public long MaxStallTicks => Interlocked.Read(ref _maxStallTicks);

    public UiThreadWatchdogTelemetry GetTelemetry()
    {
        return new UiThreadWatchdogTelemetry(
            StallCount,
            new TimeSpan(Interlocked.Read(ref _maxStallTicks)),
            new TimeSpan(Interlocked.Read(ref _totalStallTicks)),
            IsStalled);
    }

    private void OnTimerTick(object state)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var lastBeat = Interlocked.Read(ref _heartbeatTicks);
            var elapsed = now - lastBeat;

            if (elapsed <= _stallThreshold.Ticks)
            {
                // Healthy — update the healthy heartbeat marker.
                Interlocked.Exchange(ref _lastHealthyHeartbeatTicks, lastBeat);
                return;
            }

            // UI thread is stalled.
            var stallDuration = new TimeSpan(elapsed);
            Interlocked.Increment(ref _stallCount);
            Interlocked.Add(ref _totalStallTicks, _pollInterval.Ticks);

            // Track max stall.
            long prevMax;
            do
            {
                prevMax = Interlocked.Read(ref _maxStallTicks);
                if (elapsed <= prevMax) break;
            }
            while (Interlocked.CompareExchange(ref _maxStallTicks, elapsed, prevMax) != prevMax);

            // Rate-limit warnings so we don't flood the log during a sustained stall.
            var lastWarn = Interlocked.Read(ref _lastWarningTicks);
            if (now - lastWarn < _warningCooldown.Ticks)
            {
                return;
            }

            Interlocked.Exchange(ref _lastWarningTicks, now);

            var telemetry = _telemetryProvider?.Invoke()
                ?? ("unavailable", 0, 0L, 0);

            EngineLogBridge.Warn(
                $"[UiThreadWatchdog] UI thread STALLED for {stallDuration.TotalMilliseconds:F0}ms " +
                $"(stallCount={_stallCount}, activeTab={telemetry.ActiveTabId}, " +
                $"compositorFrame={telemetry.CompositorFrameSeq}, tabs={telemetry.TabCount}, " +
                $"status={telemetry.Status})",
                LogCategory.Performance);

            // On a prolonged stall (>2s), emit a full process diagnostic.
            if (stallDuration.TotalMilliseconds > 2000 && _stallCount % 10 == 0)
            {
                EmitProcessDiagnostic();
            }
        }
        catch
        {
            // The watchdog itself must never throw — that would kill the timer.
        }
    }

    private static void EmitProcessDiagnostic()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
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
