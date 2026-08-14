// SpecRef: FenBrowser Process Isolation Host Integration
// CapabilityId: PROCESS-ISOLATION-POOL-01
// Determinism: strict
// FallbackPolicy: degradetoinprocess
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Renderer process pool with fresh warm-standby processes and lifecycle management.
    ///
    /// IMPORTANT SECURITY INVARIANT:
    /// A process that has executed web content is never returned to the generic warm pool.
    /// Until renderer reset IPC exists and is acknowledged by the child, process destruction
    /// is the only trustworthy way to clear DOM/JS/native state between assignments.
    /// </summary>
    internal sealed class RendererProcessPool : IDisposable
    {
        private readonly ProcessIsolationConfig _config;
        private readonly IOsSandboxFactory _sandboxFactory;
        private readonly SemaphoreSlim _startupLock;

        // Warm slots are fresh, never-assigned processes only. Each is consumed once.
        private readonly BlockingCollection<RendererProcessSlot> _warmPool;
        private readonly ConcurrentDictionary<long, RendererProcessSlot> _activeSlots;
        private readonly ConcurrentDictionary<long, long> _processStartTimes;
        private readonly CancellationTokenSource _shutdownToken = new();

        // Telemetry
        private long _totalProcessesSpawned;
        private long _totalProcessesReused;
        private long _totalSpawnsFailed;
        private long _poolHits;
        private long _poolMisses;
        private long _warmupSpawns;

        // Health monitoring
        private Timer _healthCheckTimer;
        private readonly TimeSpan _healthCheckInterval;

        public int WarmPoolSize => _warmPool.Count;
        public int ActiveCount => _activeSlots.Count;
        public long TotalSpawned => Interlocked.Read(ref _totalProcessesSpawned);
        public long TotalReused => Interlocked.Read(ref _totalProcessesReused);
        public double HitRatio
        {
            get
            {
                var hits = Interlocked.Read(ref _poolHits);
                var misses = Interlocked.Read(ref _poolMisses);
                var total = hits + misses;
                return total > 0 ? (double)hits / total : 0.0;
            }
        }

        public RendererProcessPool(
            ProcessIsolationConfig config,
            IOsSandboxFactory sandboxFactory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _sandboxFactory = sandboxFactory ?? throw new ArgumentNullException(nameof(sandboxFactory));

            // ProcessIsolationConfig validates these values at construction.
            _warmPool = new BlockingCollection<RendererProcessSlot>(_config.MaxPoolSize);
            _activeSlots = new ConcurrentDictionary<long, RendererProcessSlot>();
            _processStartTimes = new ConcurrentDictionary<long, long>();
            _startupLock = new SemaphoreSlim(_config.MaxConcurrentStartup, _config.MaxConcurrentStartup);
            _healthCheckInterval = _config.HealthCheckInterval;

            ValidateConfig();

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                $"[RendererProcessPool] Initialized maxSize={_config.MaxPoolSize} warmTarget={_config.TargetWarmCount} " +
                $"maxStartupConcurrency={_config.MaxConcurrentStartup} healthCheckInterval={_healthCheckInterval.TotalSeconds}s " +
                $"processLifetimeMax={_config.ProcessLifetimeMax.TotalMinutes}m singleUseAfterAssignment=true");
        }

        public void Start()
        {
            if (_shutdownToken.IsCancellationRequested)
                throw new InvalidOperationException("Process pool is shutting down");

            _healthCheckTimer = new Timer(
                callback: _ => RunHealthCheck(),
                state: null,
                dueTime: _healthCheckInterval,
                period: _healthCheckInterval);

            if (_config.EnablePreWarm && _config.TargetWarmCount > 0)
            {
                _ = Task.Run(() => PreWarmPool(), _shutdownToken.Token);
            }

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                "[RendererProcessPool] Pool started with fresh-process prewarming");
        }

        public void RetireSlot(RendererProcessSlot slot, string reason)
        {
            if (slot == null) throw new ArgumentNullException(nameof(slot));

            _activeSlots.TryRemove(slot.ProcessId, out _);
            _processStartTimes.TryRemove(slot.ProcessId, out _);

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                $"[RendererProcessPool] Retiring process {slot.ProcessId} reason={reason}");
            DestructSlot(slot, $"retired:{reason}");
        }

        public async Task<RendererProcessSlot> AcquireSlotAsync(
            string assignmentKey,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(assignmentKey))
                throw new ArgumentException("Assignment key required", nameof(assignmentKey));
            if (_shutdownToken.IsCancellationRequested)
                throw new ObjectDisposedException(nameof(RendererProcessPool));

            // Warm slots have never executed page content. They are safe to consume once.
            var slot = TryGetFreshWarmSlot(assignmentKey);
            if (slot != null)
            {
                try
                {
                    var activated = await ActivateSlotAsync(slot, assignmentKey, cancellationToken);
                    Interlocked.Increment(ref _poolHits);
                    Interlocked.Increment(ref _totalProcessesReused);
                    return activated;
                }
                catch
                {
                    // ActivateSlotAsync owns destruction on failure.
                    throw;
                }
            }

            Interlocked.Increment(ref _poolMisses);
            return await SpawnNewProcessAsync(assignmentKey, cancellationToken);
        }

        public void ReleaseSlot(RendererProcessSlot slot)
        {
            if (slot == null) throw new ArgumentNullException(nameof(slot));

            if (!_activeSlots.TryRemove(slot.ProcessId, out _))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[RendererProcessPool] ReleaseSlot called for untracked process {slot.ProcessId}");
                return;
            }

            // P0 isolation rule: ResetForAssignmentAsync currently has no reset IPC and
            // no renderer acknowledgement. A renderer that executed one assignment may
            // contain DOM, JS heap, storage/cache handles, decoded resources, native state,
            // or secrets from that assignment. Never put it into a generic cross-site pool.
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                $"[RendererProcessPool] Destroying used process {slot.ProcessId} on release; " +
                "renderer reuse is disabled until authenticated reset acknowledgement exists");
            DestructSlot(slot, "single-use-after-assignment");
        }

        private RendererProcessSlot TryGetFreshWarmSlot(string assignmentKey)
        {
            while (_warmPool.TryTake(out var slot))
            {
                if (!IsProcessHealthy(slot))
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                        $"[RendererProcessPool] Fresh warm process {slot.ProcessId} died before assignment");
                    DestructSlot(slot, "died-in-warm-pool");
                    continue;
                }

                if (HasExceededLifetime(slot.ProcessId))
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                        $"[RendererProcessPool] Fresh warm process {slot.ProcessId} expired before assignment");
                    DestructSlot(slot, "expired-in-warm-pool");
                    continue;
                }

                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Fresh warm pool hit for assignment {assignmentKey} pid={slot.ProcessId}");
                return slot;
            }

            return null;
        }

        private async Task<RendererProcessSlot> ActivateSlotAsync(
            RendererProcessSlot slot,
            string assignmentKey,
            CancellationToken cancellationToken)
        {
            try
            {
                await slot.ActivateAsync(assignmentKey, cancellationToken);

                if (!_activeSlots.TryAdd(slot.ProcessId, slot))
                    throw new RendererProcessPoolException($"Process {slot.ProcessId} is already active");

                // A prewarmed process already has its actual process-start timestamp.
                _processStartTimes.TryAdd(slot.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Activated fresh process {slot.ProcessId} for assignment {assignmentKey}");

                return slot;
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessPool] Failed to activate process {slot.ProcessId}: {ex.Message}");
                DestructSlot(slot, "activation-failed");
                throw;
            }
        }

        private async Task<RendererProcessSlot> SpawnNewProcessAsync(
            string assignmentKey,
            CancellationToken cancellationToken)
        {
            await _startupLock.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref _totalProcessesSpawned);

                try
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                        $"[RendererProcessPool] Spawning new process for assignment {assignmentKey}");

                    var slot = await RendererProcessSlot.CreateAsync(
                        assignmentKey: assignmentKey,
                        sandboxFactory: _sandboxFactory,
                        timeout: _config.ProcessStartupTimeout,
                        cancellationToken: cancellationToken);

                    if (!_activeSlots.TryAdd(slot.ProcessId, slot))
                    {
                        DestructSlot(slot, "duplicate-active-process-id");
                        throw new RendererProcessPoolException($"Process {slot.ProcessId} is already active");
                    }

                    _processStartTimes[slot.ProcessId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                        $"[RendererProcessPool] Spawned process {slot.ProcessId} for assignment {assignmentKey} " +
                        $"(spawn #{Interlocked.Read(ref _totalProcessesSpawned)})");

                    return slot;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref _totalSpawnsFailed);
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                        $"[RendererProcessPool] Process spawn failed for assignment {assignmentKey}: {ex.Message}");
                    throw new RendererProcessPoolException("Failed to spawn renderer process", ex);
                }
            }
            finally
            {
                _startupLock.Release();
            }
        }

        private async Task PreWarmPool()
        {
            try
            {
                while (!_shutdownToken.Token.IsCancellationRequested)
                {
                    var currentWarmCount = _warmPool.Count;
                    var targetCount = _config.TargetWarmCount;

                    if (currentWarmCount < targetCount)
                    {
                        var needed = targetCount - currentWarmCount;
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                            $"[RendererProcessPool] Pre-warming {needed} fresh renderer process(es)");

                        for (var i = 0; i < needed && !_shutdownToken.Token.IsCancellationRequested; i++)
                        {
                            try
                            {
                                await _startupLock.WaitAsync(_shutdownToken.Token);
                                try
                                {
                                    Interlocked.Increment(ref _warmupSpawns);
                                    var slot = await RendererProcessSlot.CreateAsync(
                                        assignmentKey: "warm-pool",
                                        sandboxFactory: _sandboxFactory,
                                        timeout: _config.ProcessStartupTimeout,
                                        cancellationToken: _shutdownToken.Token);

                                    var processId = slot.ProcessId;
                                    _processStartTimes[processId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                    // The process is fresh: it has never been activated for web content.
                                    if (_warmPool.TryAdd(slot))
                                    {
                                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                                            $"[RendererProcessPool] Pre-warmed fresh process {processId}");
                                    }
                                    else
                                    {
                                        DestructSlot(slot, "pool-full-on-warmup");
                                    }
                                }
                                finally
                                {
                                    _startupLock.Release();
                                }
                            }
                            catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref _totalSpawnsFailed);
                                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                                    $"[RendererProcessPool] Pre-warm spawn failed: {ex.Message}");
                            }
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), _shutdownToken.Token);
                }
            }
            catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessPool] Pre-warm loop error: {ex.Message}");
            }
        }

        private void RunHealthCheck()
        {
            if (_shutdownToken.IsCancellationRequested)
                return;

            try
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Health check active={_activeSlots.Count} warm={_warmPool.Count} " +
                    $"hitRatio={HitRatio:F2} spawned={Interlocked.Read(ref _totalProcessesSpawned)} " +
                    $"warmHits={Interlocked.Read(ref _totalProcessesReused)}");

                foreach (var pair in _activeSlots)
                {
                    if (!IsProcessHealthy(pair.Value))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                            $"[RendererProcessPool] Health check detected dead active process {pair.Key}");
                    }
                }

                // Do not enumerate a BlockingCollection and then TryTake an arbitrary
                // item to retire a specific expired slot. Warm slots are validated for
                // health and lifetime when the exact slot is consumed by AcquireSlotAsync.
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessPool] Health check error: {ex.Message}");
            }
        }

        private bool HasExceededLifetime(long processId)
        {
            if (!_processStartTimes.TryGetValue(processId, out var startTime))
                return false;

            var ageMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
            return ageMilliseconds > _config.ProcessLifetimeMax.TotalMilliseconds;
        }

        private bool IsProcessHealthy(RendererProcessSlot slot)
        {
            if (slot == null || slot.Process == null)
                return false;

            try
            {
                return !slot.Process.HasExited;
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Process health check failed for {slot.ProcessId}: {ex.Message}");
                return false;
            }
        }

        private void DestructSlot(RendererProcessSlot slot, string reason)
        {
            if (slot == null)
                return;

            try
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Destroying process {slot.ProcessId} reason={reason}");

                _processStartTimes.TryRemove(slot.ProcessId, out _);
                _activeSlots.TryRemove(slot.ProcessId, out _);
                slot.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[RendererProcessPool] Failed to destroy process {slot.ProcessId}: {ex.Message}");
            }
        }

        private void ValidateConfig()
        {
            if (_config.TargetWarmCount > _config.MaxPoolSize)
                throw new ArgumentException("TargetWarmCount cannot exceed MaxPoolSize");
            if (_config.MaxConcurrentStartup < 1)
                throw new ArgumentException("MaxConcurrentStartup must be at least 1");
            if (_config.HealthCheckInterval <= TimeSpan.Zero)
                throw new ArgumentException("HealthCheckInterval must be positive");
            if (_config.ProcessStartupTimeout.TotalSeconds < 1)
                throw new ArgumentException("ProcessStartupTimeout must be at least 1 second");
            if (_config.ProcessLifetimeMax.TotalMinutes < 1)
                throw new ArgumentException("ProcessLifetimeMax must be at least 1 minute");
        }

        public void Dispose()
        {
            if (_shutdownToken.IsCancellationRequested)
                return;

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                $"[RendererProcessPool] Disposing pool - active={_activeSlots.Count} warm={_warmPool.Count}");

            _shutdownToken.Cancel();
            _healthCheckTimer?.Dispose();

            foreach (var slot in _activeSlots.Values)
            {
                try
                {
                    slot?.Dispose();
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                        $"[RendererProcessPool] Error disposing active slot: {ex.Message}");
                }
            }
            _activeSlots.Clear();

            while (_warmPool.TryTake(out var slot))
            {
                try
                {
                    slot?.Dispose();
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                        $"[RendererProcessPool] Error disposing warm slot: {ex.Message}");
                }
            }

            _processStartTimes.Clear();
            _startupLock.Dispose();
            _warmPool.Dispose();
            _shutdownToken.Dispose();

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                "[RendererProcessPool] Pool disposed successfully");
        }
    }

    public sealed class RendererProcessPoolException : Exception
    {
        public RendererProcessPoolException(string message) : base(message) { }
        public RendererProcessPoolException(string message, Exception inner) : base(message, inner) { }
    }
}
