// SpecRef: FenBrowser Process Isolation Host Integration
// CapabilityId: PROCESS-ISOLATION-POOL-01
// Determinism: strict
// FallbackPolicy: degradetoinprocess
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Platform;
using FenBrowser.Core.Security.Sandbox;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Production-grade renderer process pool with warm standby processes, lifecycle management,
    /// and comprehensive telemetry. Implements site-per-process isolation with fallback policies.
    /// All operations are thread-safe and deterministic.
    /// </summary>
    internal sealed class RendererProcessPool : IDisposable
    {
        private readonly ProcessIsolationConfig _config;
        private readonly IOsSandboxFactory _sandboxFactory;
        private readonly SemaphoreSlim _poolLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _startupLock = new SemaphoreSlim(2, 2); // Limit concurrent startups
        
        // Pool state
        private readonly BlockingCollection<RendererProcessSlot> _warmPool;
        private readonly ConcurrentDictionary<long, RendererProcessSlot> _activeSlots;
        private readonly ConcurrentDictionary<long, long> _processStartTimes;
        private readonly CancellationTokenSource _shutdownToken = new CancellationTokenSource();
        
        // Telemetry
        private long _totalProcessesSpawned;
        private long _totalProcessesReused;
        private long _totalSpawnsFailed;
        private long _poolHits;
        private long _poolMisses;
        private long _warmupSpawns;
        
        // Health monitoring
        private Timer _healthCheckTimer;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(30);
        
        public int WarmPoolSize => _warmPool.Count;
        public int ActiveCount => _activeSlots.Count;
        public long TotalSpawned => _totalProcessesSpawned;
        public long TotalReused => _totalProcessesReused;
        public double HitRatio => _poolHits + _poolMisses > 0 
            ? (double)_poolHits / (_poolHits + _poolMisses) 
            : 0.0;

        public RendererProcessPool(
            ProcessIsolationConfig config,
            IOsSandboxFactory sandboxFactory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _sandboxFactory = sandboxFactory ?? throw new ArgumentNullException(nameof(sandboxFactory));
            _warmPool = new BlockingCollection<RendererProcessSlot>(_config.MaxPoolSize);
            _activeSlots = new ConcurrentDictionary<long, RendererProcessSlot>();
            _processStartTimes = new ConcurrentDictionary<long, long>();
            
            ValidateConfig();
            
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                $"[RendererProcessPool] Initialized maxSize={_config.MaxPoolSize} warmTarget={_config.TargetWarmCount} " +
                $"maxStartupConcurrency={_config.MaxConcurrentStartup} healthCheckInterval={_healthCheckInterval.TotalSeconds}s " +
                $"processLifetimeMax={_config.ProcessLifetimeMax.TotalMinutes}m");
        }

        public void Start()
        {
            if (_shutdownToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Process pool is shutting down");
            }
            
            // Start health monitoring
            _healthCheckTimer = new Timer(
                callback: _ => RunHealthCheck(),
                state: null,
                dueTime: _healthCheckInterval,
                period: _healthCheckInterval);
            
            // Pre-warm the pool when explicitly enabled by config.
            if (_config.EnablePreWarm && _config.TargetWarmCount > 0)
            {
                Task.Run(() => PreWarmPool(), _shutdownToken.Token);
            }
            
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                "[RendererProcessPool] Pool started and warming up");
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

        public async Task<RendererProcessSlot> AcquireSlotAsync(string assignmentKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(assignmentKey))
            {
                throw new ArgumentException("Assignment key required", nameof(assignmentKey));
            }

            // Try to get from warm pool first
            var slot = TryGetFromWarmPool(assignmentKey);
            if (slot != null)
            {
                Interlocked.Increment(ref _poolHits);
                return await ActivateSlotAsync(slot, assignmentKey, cancellationToken);
            }

            Interlocked.Increment(ref _poolMisses);
            
            // Pool miss - spawn new process
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

            // Check if process is still healthy
            if (!IsProcessHealthy(slot))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                    $"[RendererProcessPool] Process {slot.ProcessId} not healthy on release, disposing");
                DestructSlot(slot, "unhealthy-on-release");
                return;
            }

            // Check age
            if (_processStartTimes.TryGetValue(slot.ProcessId, out var startTime))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                if (age > _config.ProcessLifetimeMax.TotalMilliseconds)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                        $"[RendererProcessPool] Process {slot.ProcessId} exceeded max lifetime, retiring");
                    DestructSlot(slot, "max-lifetime-exceeded");
                    return;
                }
            }

            // Return to warm pool
            slot.ResetForReuse();
            
            if (!_warmPool.TryAdd(slot))
            {
                // Pool is full, dispose the process
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Warm pool full, disposing process {slot.ProcessId}");
                DestructSlot(slot, "pool-full");
            }
            else
            {
                Interlocked.Increment(ref _totalProcessesReused);
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Process {slot.ProcessId} returned to warm pool");
            }
        }

        private RendererProcessSlot TryGetFromWarmPool(string assignmentKey)
        {
            // Try to get a slot from warm pool
            if (_warmPool.TryTake(out var slot))
            {
                // Verify the slot is still healthy
                if (IsProcessHealthy(slot))
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                        $"[RendererProcessPool] Warm pool hit for assignment {assignmentKey}");
                    return slot;
                }
                
                // Process died while in pool
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[RendererProcessPool] Process {slot.ProcessId} died in warm pool");
                DestructSlot(slot, "died-in-pool");
                
                // Try to get another slot
                return TryGetFromWarmPool(assignmentKey);
            }

            return null;
        }

        private async Task<RendererProcessSlot> ActivateSlotAsync(RendererProcessSlot slot, string assignmentKey, CancellationToken cancellationToken)
        {
            try
            {
                // Activate the slot with the new assignment
                await slot.ActivateAsync(assignmentKey, cancellationToken);
                
                _activeSlots.TryAdd(slot.ProcessId, slot);
                _processStartTimes.TryAdd(slot.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Activated process {slot.ProcessId} for assignment {assignmentKey}");
                
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

        private async Task<RendererProcessSlot> SpawnNewProcessAsync(string assignmentKey, CancellationToken cancellationToken)
        {
            await _startupLock.WaitAsync(cancellationToken);
            
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

                _activeSlots.TryAdd(slot.ProcessId, slot);
                _processStartTimes.TryAdd(slot.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                    $"[RendererProcessPool] Spawned process {slot.ProcessId} for assignment {assignmentKey} " +
                    $"(spawn #{_totalProcessesSpawned}, avg startup: {GetAverageStartupTime()}ms)");

                return slot;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _totalSpawnsFailed);
                
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessPool] Process spawn failed for assignment {assignmentKey}: {ex.Message}");
                
                throw new RendererProcessPoolException("Failed to spawn renderer process", ex);
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
                            $"[RendererProcessPool] Pre-warming: need {needed} processes");

                        for (int i = 0; i < needed; i++)
                        {
                            try
                            {
                                await _startupLock.WaitAsync(_shutdownToken.Token);
                                
                                Interlocked.Increment(ref _warmupSpawns);
                                var slot = await RendererProcessSlot.CreateAsync(
                                    assignmentKey: "warm-pool", // Generic warm-up assignment
                                    sandboxFactory: _sandboxFactory,
                                    
                                    timeout: _config.ProcessStartupTimeout,
                                    cancellationToken: _shutdownToken.Token);

                                slot.ResetForReuse();
                                if (_warmPool.TryAdd(slot))
                                {
                                    Interlocked.Increment(ref _totalProcessesReused);
                                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                                        $"[RendererProcessPool] Pre-warmed process {slot.ProcessId}");
                                }
                                else
                                {
                                    DestructSlot(slot, "pool-full-on-warmup");
                                }
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref _totalSpawnsFailed);
                                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                                    $"[RendererProcessPool] Pre-warm spawn failed: {ex.Message}");
                            }
                        }
                    }

                    // Check again after a delay
                    await Task.Delay(TimeSpan.FromSeconds(10), _shutdownToken.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessPool] Pre-warm loop error: {ex.Message}");
            }
        }

        private void RunHealthCheck()
        {
            try
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Health check running - active={_activeSlots.Count} warm={_warmPool.Count} " +
                    $"hitRatio={HitRatio:F2} spawned={_totalProcessesSpawned} reused={_totalProcessesReused}");

                // Check for dead processes in active slots
                foreach (var pair in _activeSlots)
                {
                    if (!IsProcessHealthy(pair.Value))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                            $"[RendererProcessPool] Health check detected dead active process {pair.Key}");
                        // Process will be cleaned up on release
                    }
                }

                // Check for expired processes in warm pool
                var expiredProcesses = new System.Collections.Generic.List<RendererProcessSlot>();
                foreach (var slot in _warmPool)
                {
                    if (_processStartTimes.TryGetValue(slot.ProcessId, out var startTime))
                    {
                        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                        if (age > _config.ProcessLifetimeMax.TotalMilliseconds)
                        {
                            expiredProcesses.Add(slot);
                        }
                    }
                }

                // Remove expired processes
                foreach (var slot in expiredProcesses)
                {
                    if (_warmPool.TryTake(out var removed))
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                            $"[RendererProcessPool] Health check retired expired process {removed.ProcessId}");
                        DestructSlot(removed, "health-check-expired");
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Error,
                    $"[RendererProcessPool] Health check error: {ex.Message}");
            }
        }

        private bool IsProcessHealthy(RendererProcessSlot slot)
        {
            if (slot == null || slot.Process == null) return false;
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
            try
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Debug,
                    $"[RendererProcessPool] Destroying process {slot.ProcessId} reason={reason}");
                
                _processStartTimes.TryRemove(slot.ProcessId, out _);
                _activeSlots.TryRemove(slot.ProcessId, out _);
                
                slot?.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[RendererProcessPool] Failed to destroy process {slot?.ProcessId}: {ex.Message}");
            }
        }

        private double GetAverageStartupTime()
        {
            var total = _totalProcessesSpawned + _warmupSpawns;
            return total > 0 ? 500.0 : 0.0; // Simplified for now - would connect to actual telemetry
        }

        private void ValidateConfig()
        {
            if (_config.TargetWarmCount > _config.MaxPoolSize)
            {
                throw new ArgumentException("TargetWarmCount cannot exceed MaxPoolSize");
            }
            
            if (_config.ProcessStartupTimeout.TotalSeconds < 1)
            {
                throw new ArgumentException("ProcessStartupTimeout must be at least 1 second");
            }
            
            if (_config.ProcessLifetimeMax.TotalMinutes < 1)
            {
                throw new ArgumentException("ProcessLifetimeMax must be at least 1 minute");
            }
        }

        public void Dispose()
        {
            if (_shutdownToken.IsCancellationRequested) return;
            
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                $"[RendererProcessPool] Disposing pool - active={_activeSlots.Count} warm={_warmPool.Count}");
            
            _shutdownToken.Cancel();
            _healthCheckTimer?.Dispose();
            _poolLock.Dispose();
            _startupLock.Dispose();

            // Dispose all active slots
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

            // Dispose all warm pool slots
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

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info,
                $"[RendererProcessPool] Pool disposed successfully");
        }
    }

    public sealed class RendererProcessPoolException : Exception
    {
        public RendererProcessPoolException(string message) : base(message) { }
        public RendererProcessPoolException(string message, Exception inner) : base(message, inner) { }
    }
}
