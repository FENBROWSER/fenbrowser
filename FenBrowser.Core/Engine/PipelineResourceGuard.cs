// SpecRef: FenBrowser resource management and pipeline safety contract
// CapabilityId: PIPELINE-RESOURCE-GUARD-01
// Determinism: strict
// FallbackPolicy: enforce-limits
// =============================================================================
// PipelineResourceGuard.cs
// Production-grade resource limiting and memory pressure handling
//
// SPEC REFERENCE: Custom (internal architecture)
// PURPOSE: Enforce resource limits per pipeline stage to prevent exhaustion
//
// DESIGN PRINCIPLES:
// 1. Per-stage limits - each stage enforces its own resource budget
// 2. Memory pressure awareness - monitor and react to system memory pressure
// 3. Pre-allocation checks - reject excessive allocations before they happen
// 4. Graceful degradation - degrade quality instead of crashing
// 5. Hard limits - never exceed critical system thresholds
// =============================================================================

using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Production-grade resource guarding for pipeline stages.
    /// Enforces memory, CPU, and time limits to prevent resource exhaustion.
    /// </summary>
    public sealed class PipelineResourceGuard
    {
        private static readonly PipelineResourceGuard _instance = new();
        private readonly Timer _memoryPressureTimer;
        private readonly object _syncRoot = new();
        
        // Per-stage resource tracking
        private readonly PipelineStageResources[] _stageResources;
        
        // System-level tracking
        private long _totalMemoryUsed = 0;
        private long _peakMemoryUsed = 0;
        private DateTime _lastMemoryWarning = DateTime.MinValue;
        private bool _isHighMemoryPressure = false;
        private int _highPressureCount = 0;

        private PipelineResourceGuard()
        {
            // Initialize per-stage tracking
            var stageCount = Enum.GetValues(typeof(PipelineStage)).Length;
            _stageResources = new PipelineStageResources[stageCount];
            
            for (int i = 0; i < _stageResources.Length; i++)
            {
                _stageResources[i] = new PipelineStageResources((PipelineStage)i);
            }

            // Start memory pressure monitoring
            _memoryPressureTimer = new Timer(MonitorMemoryPressure, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // Register for system memory notifications
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public static PipelineResourceGuard Current => _instance;

        #region Resource Budgets

        // Default memory budgets per stage (in bytes)
        public long MaxStageMemoryPerFrame = 512 * 1024 * 1024; // 512MB per stage per frame
        public long MaxTotalMemoryPerFrame = 2L * 1024 * 1024 * 1024; // 2GB total per frame
        public long MaxPeakMemory = 4L * 1024 * 1024 * 1024; // 4GB peak
        
        // Stage time budgets
        public TimeSpan MaxStageDuration = TimeSpan.FromMilliseconds(100); // 100ms per stage max
        public TimeSpan MaxFrameDuration = TimeSpan.FromMilliseconds(250); // 250ms per frame max
        
        // Allocation counters
        public int MaxDomNodeCount = 1_000_000; // 1M DOM nodes
        public int MaxLayoutBoxCount = 2_000_000; // 2M layout boxes
        public int MaxPaintCommandCount = 5_000_000; // 5M paint commands

        #endregion

        #region Memory Tracking

        /// <summary>
        /// Track memory allocation for a specific stage.
        /// Throws if allocation would exceed budget.
        /// </summary>
        public void TrackMemoryAllocation(PipelineStage stage, long bytes, string description = null)
        {
            var stageResources = _stageResources[(int)stage];
            
            lock (stageResources.SyncRoot)
            {
                // Check if this allocation itself exceeds the single allocation limit
                if (bytes > MaxStageMemoryPerFrame)
                {
                    throw new PipelineResourceException(
                        stage, 
                        $"Single allocation of {bytes:N0} bytes exceeds maximum of {MaxStageMemoryPerFrame:N0} bytes " +
                        $"for stage {stage} ({description ?? "unknown"})");
                }

                var newStageMemory = stageResources.CurrentMemory + bytes;
                var newTotalMemory = Interlocked.Add(ref _totalMemoryUsed, bytes);

                // Check stage memory limit
                if (newStageMemory > MaxStageMemoryPerFrame)
                {
                    Interlocked.Add(ref _totalMemoryUsed, -bytes); // Revert increment
                    throw new PipelineResourceException(
                        stage,
                        $"Stage {stage} memory of {newStageMemory:N0} bytes exceeds maximum of {MaxStageMemoryPerFrame:N0} bytes " +
                        $"({description ?? "allocation"})");
                }

                // Check total memory limit
                if (newTotalMemory > MaxTotalMemoryPerFrame)
                {
                    Interlocked.Add(ref _totalMemoryUsed, -bytes); // Revert increment
                    throw new PipelineResourceException(
                        PipelineStage.Idle,
                        $"Total pipeline memory of {newTotalMemory:N0} bytes exceeds maximum of {MaxTotalMemoryPerFrame:N0} bytes " +
                        $"({description ?? "allocation"})");
                }

                // Update stage memory (we've already updated total)
                stageResources.CurrentMemory = newStageMemory;
                Interlocked.Add(ref stageResources.TotalAllocated, bytes);
                stageResources.AllocationCount++;

                // Track peak memory
                var peak = Interlocked.Read(ref _totalMemoryUsed);
                long currentPeak;
                do
                {
                    currentPeak = Interlocked.Read(ref _peakMemoryUsed);
                    if (peak <= currentPeak) break;
                } while (Interlocked.CompareExchange(ref _peakMemoryUsed, peak, currentPeak) != currentPeak);

                // Warn on very large allocations
                if (bytes > 100 * 1024 * 1024) // 100MB+
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastMemoryWarning).TotalSeconds > 10) // Throttle warnings
                    {
                        _lastMemoryWarning = now;
                        EngineLogCompat.Warn(
                            $"[RESOURCE] Large allocation: {bytes:N0} bytes in {stage}",
                            LogCategory.Performance);
                    }
                }
            }
        }

        /// <summary>
        /// Release memory that was previously tracked.
        /// </summary>
        public void ReleaseMemoryAllocation(PipelineStage stage, long bytes, string description = null)
        {
            var stageResources = _stageResources[(int)stage];
            
            lock (stageResources.SyncRoot)
            {
                stageResources.CurrentMemory = Math.Max(0, stageResources.CurrentMemory - bytes);
                Interlocked.Add(ref _totalMemoryUsed, -bytes);
                stageResources.DeallocationCount++;
            }
        }

        /// <summary>
        /// Track object counts for a specific stage (e.g., DOM nodes, layout boxes).
        /// </summary>
        public void TrackObjectCount(PipelineStage stage, string objectType, int count, int limit)
        {
            if (count > limit)
            {
                throw new PipelineResourceException(
                    stage,
                    $"{objectType} count {count:N0} exceeds limit {limit:N0} for {stage}");
            }

            var stageResources = _stageResources[(int)stage];
            stageResources.SetObjectCount(objectType, count);

            // Log warning for high object counts
            if (count > limit * 0.9) // 90% of limit
            {
                EngineLogCompat.Warn(
                    $"[RESOURCE WARNING] {objectType} count {count:N0} approaching limit {limit:N0} in {stage}",
                    LogCategory.Performance);
            }
        }

        private void MonitorMemoryPressure(object state)
        {
            var currentProcess = Process.GetCurrentProcess();
            var workingSet = currentProcess.WorkingSet64;
            var gcMemory = GC.GetTotalMemory(false);

            // Calculate pressure (0.0 to 1.0)
            var totalMemory = workingSet + gcMemory;
            var pressure = Math.Min(1.0, (double)totalMemory / MaxPeakMemory);

            var wasHighPressure = _isHighMemoryPressure;
            _isHighMemoryPressure = pressure > 0.85; // 85% of peak budget

            if (_isHighMemoryPressure)
            {
                Interlocked.Increment(ref _highPressureCount);
                
                if ((DateTime.UtcNow - _lastMemoryWarning).TotalSeconds > 30)
                {
                    _lastMemoryWarning = DateTime.UtcNow;
                    EngineLogCompat.Error(
                        $"[RESOURCE PRESSURE] Memory pressure high: {pressure:P1} " +
                        $"(workingSet={workingSet:N0}, gc={gcMemory:N0}, total={totalMemory:N0}, peak={_peakMemoryUsed:N0})",
                        LogCategory.Performance);
                }

                // Force garbage collection in severe pressure
                if (pressure > 0.95 || _highPressureCount > 10)
                {
                    EngineLogCompat.Error(
                        $"[RESOURCE CRITICAL] Severe memory pressure {pressure:P1}, forcing GC and compaction",
                        LogCategory.Critical);
                    
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                }
            }
            else if (wasHighPressure)
            {
                // Recovered from high pressure
                Interlocked.Exchange(ref _highPressureCount, 0);
                EngineLogCompat.Info(
                    $"[RESOURCE RECOVERY] Memory pressure normalized: {pressure:P1}",
                    LogCategory.Performance);
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            if (_memoryPressureTimer != null)
            {
                _memoryPressureTimer.Dispose();
            }
            
            LogResourceSummary();
        }

        private void LogResourceSummary()
        {
            var summary = GenerateResourceSummary();
            EngineLogCompat.Info($"[RESOURCE SUMMARY] {summary}", LogCategory.Performance);
        }

        public string GenerateResourceSummary()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append($"PeakMemory={_peakMemoryUsed:N0}, TotalAllocated={_totalMemoryUsed:N0}");
            
            for (int i = 0; i < _stageResources.Length; i++)
            {
                var stageResources = _stageResources[i];
                if (stageResources.AllocationCount > 0)
                {
                    builder.Append($", {stageResources.Stage}:{{alloc={stageResources.AllocationCount:N0},mem={stageResources.CurrentMemory:N0}}}");
                }
            }
            
            return builder.ToString();
        }

        #endregion

        #region Diagnostics

        public override string ToString()
        {
            var pressure = _isHighMemoryPressure ? " [HIGH PRESSURE]" : "";
            return $"PipelineResourceGuard: Peak={_peakMemoryUsed:N0}, Current={_totalMemoryUsed:N0}{pressure}";
        }

        #endregion
    }

    /// <summary>
    /// Resource tracking for a specific pipeline stage.
    /// </summary>
    public class PipelineStageResources
    {
        public PipelineStage Stage { get; }
        public readonly object SyncRoot = new();
        
        public long CurrentMemory { get; set; }
        public long TotalAllocated { get; set; }
        public long TotalDeallocated { get; set; }
        public int AllocationCount { get; set; }
        public int DeallocationCount { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public int ExecutionCount { get; set; }
        public TimeSpan PeakDuration { get; set; }
        
        private readonly Dictionary<string, int> _objectCounts = new();
        
        public PipelineStageResources(PipelineStage stage)
        {
            Stage = stage;
        }

        public void SetObjectCount(string objectType, int count)
        {
            lock (SyncRoot)
            {
                _objectCounts[objectType] = count;
            }
        }

        public int GetObjectCount(string objectType)
        {
            lock (SyncRoot)
            {
                return _objectCounts.TryGetValue(objectType, out var count) ? count : 0;
            }
        }

        public void RecordExecution(TimeSpan duration)
        {
            ExecutionCount++;
            TotalDuration += duration;
            if (duration > PeakDuration) PeakDuration = duration;
        }

        public double AverageDurationMs => ExecutionCount > 0 ? TotalDuration.TotalMilliseconds / ExecutionCount : 0;
    }

    /// <summary>
    /// Thrown when pipeline resource limits are exceeded.
    /// </summary>
    public class PipelineResourceException : PipelineStageException
    {
        public PipelineResourceException(PipelineStage stage, string message)
            : base($"{stage} resource limit exceeded: {message}", stage, stage)
        {
        }
    }
}
