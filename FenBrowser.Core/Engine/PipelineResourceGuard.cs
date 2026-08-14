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

        // Per-stage resource tracking
        private readonly PipelineStageResources[] _stageResources;

        // System-level tracking
        private long _totalMemoryUsed;
        private long _peakMemoryUsed;
        private DateTime _lastMemoryWarning = DateTime.MinValue;
        private bool _isHighMemoryPressure;
        private int _highPressureCount;

        private PipelineResourceGuard()
        {
            var stageCount = Enum.GetValues(typeof(PipelineStage)).Length;
            _stageResources = new PipelineStageResources[stageCount];

            for (int i = 0; i < _stageResources.Length; i++)
            {
                _stageResources[i] = new PipelineStageResources((PipelineStage)i);
            }

            _memoryPressureTimer = new Timer(
                MonitorMemoryPressure,
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public static PipelineResourceGuard Current => _instance;

        #region Resource Budgets

        public long MaxStageMemoryPerFrame = 512 * 1024 * 1024;
        public long MaxTotalMemoryPerFrame = 2L * 1024 * 1024 * 1024;
        public long MaxPeakMemory = 4L * 1024 * 1024 * 1024;

        public TimeSpan MaxStageDuration = TimeSpan.FromMilliseconds(100);
        public TimeSpan MaxFrameDuration = TimeSpan.FromMilliseconds(250);

        public int MaxDomNodeCount = 1_000_000;
        public int MaxLayoutBoxCount = 2_000_000;
        public int MaxPaintCommandCount = 5_000_000;

        #endregion

        #region Budget Validation

        /// <summary>
        /// Validate coarse stage budget constraints before stage work begins.
        /// </summary>
        public void ValidateStageBudget(PipelineStage stage, string operationName)
        {
            var stageResources = _stageResources[(int)stage];
            lock (stageResources.SyncRoot)
            {
                if (stageResources.CurrentMemory > MaxStageMemoryPerFrame)
                {
                    throw new PipelineResourceException(
                        stage,
                        $"Pre-stage memory {stageResources.CurrentMemory:N0} exceeds stage budget {MaxStageMemoryPerFrame:N0} for {operationName}");
                }
            }

            if (Interlocked.Read(ref _totalMemoryUsed) > MaxTotalMemoryPerFrame)
            {
                throw new PipelineResourceException(
                    stage,
                    $"Total tracked memory exceeds frame budget before {operationName}");
            }
        }

        #endregion

        #region Memory Tracking

        /// <summary>
        /// Track memory allocation for a specific stage.
        /// Throws if allocation would exceed budget.
        /// </summary>
        public void TrackMemoryAllocation(PipelineStage stage, long bytes, string description = null)
        {
            if (bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(bytes), "Tracked allocation size cannot be negative.");
            if (bytes == 0)
                return;

            var stageResources = _stageResources[(int)stage];

            lock (stageResources.SyncRoot)
            {
                if (bytes > MaxStageMemoryPerFrame)
                {
                    throw new PipelineResourceException(
                        stage,
                        $"Single allocation of {bytes:N0} bytes exceeds maximum of {MaxStageMemoryPerFrame:N0} bytes " +
                        $"for stage {stage} ({description ?? "unknown"})");
                }

                var newStageMemory = stageResources.CurrentMemory + bytes;
                var newTotalMemory = Interlocked.Add(ref _totalMemoryUsed, bytes);

                if (newStageMemory > MaxStageMemoryPerFrame)
                {
                    SubtractTrackedMemory(bytes);
                    throw new PipelineResourceException(
                        stage,
                        $"Stage {stage} memory of {newStageMemory:N0} bytes exceeds maximum of {MaxStageMemoryPerFrame:N0} bytes " +
                        $"({description ?? "allocation"})");
                }

                if (newTotalMemory > MaxTotalMemoryPerFrame)
                {
                    SubtractTrackedMemory(bytes);
                    throw new PipelineResourceException(
                        PipelineStage.Idle,
                        $"Total pipeline memory of {newTotalMemory:N0} bytes exceeds maximum of {MaxTotalMemoryPerFrame:N0} bytes " +
                        $"({description ?? "allocation"})");
                }

                stageResources.CurrentMemory = newStageMemory;
                stageResources.TotalAllocated += bytes;
                stageResources.AllocationCount++;

                var peak = Interlocked.Read(ref _totalMemoryUsed);
                long currentPeak;
                do
                {
                    currentPeak = Interlocked.Read(ref _peakMemoryUsed);
                    if (peak <= currentPeak)
                        break;
                }
                while (Interlocked.CompareExchange(ref _peakMemoryUsed, peak, currentPeak) != currentPeak);

                if (bytes > 100 * 1024 * 1024)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastMemoryWarning).TotalSeconds > 10)
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
            if (bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(bytes), "Tracked release size cannot be negative.");
            if (bytes == 0)
                return;

            var stageResources = _stageResources[(int)stage];

            lock (stageResources.SyncRoot)
            {
                // Never subtract more from the process-wide total than this stage
                // actually owns. The old code clamped the stage to zero but subtracted
                // the caller-supplied amount globally, which could make total memory
                // negative and disable later budget checks.
                var releasedBytes = Math.Min(bytes, stageResources.CurrentMemory);
                if (releasedBytes == 0)
                    return;

                stageResources.CurrentMemory -= releasedBytes;
                stageResources.TotalDeallocated += releasedBytes;
                stageResources.DeallocationCount++;
                SubtractTrackedMemory(releasedBytes);
            }
        }

        private void SubtractTrackedMemory(long bytes)
        {
            long current;
            long next;
            do
            {
                current = Interlocked.Read(ref _totalMemoryUsed);
                next = Math.Max(0, current - bytes);
            }
            while (Interlocked.CompareExchange(ref _totalMemoryUsed, next, current) != current);
        }

        /// <summary>
        /// Track object counts for a specific stage (e.g., DOM nodes, layout boxes).
        /// </summary>
        public void TrackObjectCount(PipelineStage stage, string objectType, int count, int limit)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Object count cannot be negative.");
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), "Object limit cannot be negative.");

            if (count > limit)
            {
                throw new PipelineResourceException(
                    stage,
                    $"{objectType} count {count:N0} exceeds limit {limit:N0} for {stage}");
            }

            var stageResources = _stageResources[(int)stage];
            stageResources.SetObjectCount(objectType, count);

            if (limit > 0 && count > limit * 0.9)
            {
                EngineLogCompat.Warn(
                    $"[RESOURCE WARNING] {objectType} count {count:N0} approaching limit {limit:N0} in {stage}",
                    LogCategory.Performance);
            }
        }

        private void MonitorMemoryPressure(object state)
        {
            using var currentProcess = Process.GetCurrentProcess();
            var workingSet = currentProcess.WorkingSet64;
            var managedMemory = GC.GetTotalMemory(false);

            // WorkingSet64 already measures resident process pages, including managed
            // heap pages. Adding GC.GetTotalMemory() to it double-counts managed memory
            // and can manufacture false pressure. Keep managed memory as a diagnostic
            // only and base process pressure on the working set.
            var pressure = MaxPeakMemory > 0
                ? Math.Min(1.0, (double)workingSet / MaxPeakMemory)
                : 1.0;

            var wasHighPressure = _isHighMemoryPressure;
            _isHighMemoryPressure = pressure > 0.85;

            if (_isHighMemoryPressure)
            {
                var pressureCount = Interlocked.Increment(ref _highPressureCount);

                if ((DateTime.UtcNow - _lastMemoryWarning).TotalSeconds > 30)
                {
                    _lastMemoryWarning = DateTime.UtcNow;
                    EngineLogCompat.Error(
                        $"[RESOURCE PRESSURE] Memory pressure high: {pressure:P1} " +
                        $"(workingSet={workingSet:N0}, managed={managedMemory:N0}, tracked={Interlocked.Read(ref _totalMemoryUsed):N0}, peakTracked={Interlocked.Read(ref _peakMemoryUsed):N0})",
                        LogCategory.Performance);
                }

                // A timer must not stop every renderer/engine thread by forcing full,
                // blocking CLR collections and waiting for finalizers. Severe pressure
                // is surfaced to diagnostics; allocation/document/process budgets must
                // shed work at their actual ownership boundaries.
                if (pressure > 0.95 || pressureCount > 10)
                {
                    EngineLogCompat.Error(
                        $"[RESOURCE CRITICAL] Severe process memory pressure {pressure:P1}; " +
                        "resource owners must shed or reject work",
                        LogCategory.Critical);
                }
            }
            else if (wasHighPressure)
            {
                Interlocked.Exchange(ref _highPressureCount, 0);
                EngineLogCompat.Info(
                    $"[RESOURCE RECOVERY] Memory pressure normalized: {pressure:P1}",
                    LogCategory.Performance);
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            _memoryPressureTimer.Dispose();
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
            builder.Append(
                $"PeakTrackedMemory={Interlocked.Read(ref _peakMemoryUsed):N0}, " +
                $"CurrentTrackedMemory={Interlocked.Read(ref _totalMemoryUsed):N0}");

            for (int i = 0; i < _stageResources.Length; i++)
            {
                var stageResources = _stageResources[i];
                if (stageResources.AllocationCount > 0)
                {
                    builder.Append(
                        $", {stageResources.Stage}:{{alloc={stageResources.AllocationCount:N0}," +
                        $"dealloc={stageResources.DeallocationCount:N0},mem={stageResources.CurrentMemory:N0}}}");
                }
            }

            return builder.ToString();
        }

        #endregion

        #region Diagnostics

        public override string ToString()
        {
            var pressure = _isHighMemoryPressure ? " [HIGH PRESSURE]" : "";
            return $"PipelineResourceGuard: Peak={Interlocked.Read(ref _peakMemoryUsed):N0}, Current={Interlocked.Read(ref _totalMemoryUsed):N0}{pressure}";
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
            if (duration > PeakDuration)
                PeakDuration = duration;
        }

        public double AverageDurationMs =>
            ExecutionCount > 0 ? TotalDuration.TotalMilliseconds / ExecutionCount : 0;
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
