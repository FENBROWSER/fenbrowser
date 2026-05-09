// SpecRef: FenBrowser pipeline stage authority contract (parse -> DOM -> style -> layout -> paint -> raster)
// CapabilityId: PIPELINE-EXCEPTION-BOUNDARY-01
// Determinism: strict
// FallbackPolicy: degrade-gracefully
// =============================================================================
// PipelineExceptionBoundary.cs
// Production-grade exception handling for pipeline stages
//
// SPEC REFERENCE: Custom (internal architecture)
// PURPOSE: Guarantees pipeline stability and observability under failure conditions
//
// DESIGN PRINCIPLES:
// 1. Isolate stage failures - one stage failing doesn't crash the entire pipeline
// 2. Structured error reporting - detailed failure context for debugging
// 3. Graceful degradation - failed stages produce fallback data, not crashes
// 4. Comprehensive telemetry - all failures are logged with production context
// 5. Resource cleanup - guarantee cleanup on exceptional paths
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Production-grade exception boundary for pipeline stages.
    /// Wraps stage execution with comprehensive error handling and telemetry.
    /// </summary>
    public sealed class PipelineExceptionBoundary
    {
        private static readonly ThreadLocal<PipelineExceptionBoundary> _instance = new(() => new());
        
        public static PipelineExceptionBoundary Current => _instance.Value;

        private readonly List<PipelineStageFailure> _recentFailures = new(capacity: 10);
        private readonly object _syncRoot = new();
        private int _totalFailureCount = 0;
        private DateTime _lastFailureTime = DateTime.MinValue;
        private PipelineStageFailure _lastFailure;

        private PipelineExceptionBoundary() { }

        #region Properties

        /// <summary>
        /// Total number of pipeline failures across all stages since process start.
        /// </summary>
        public int TotalFailureCount => _totalFailureCount;

        /// <summary>
        /// Timestamp of most recent pipeline failure.
        /// </summary>
        public DateTime LastFailureTime => _lastFailureTime;

        /// <summary>
        /// Most recent failure details.
        /// </summary>
        public PipelineStageFailure LastFailure => _lastFailure;

        /// <summary>
        /// Recent failures (last 10) for diagnostic purposes.
        /// </summary>
        public IReadOnlyList<PipelineStageFailure> RecentFailures
        {
            get
            {
                lock (_syncRoot)
                {
                    return _recentFailures.ToArray();
                }
            }
        }

        /// <summary>
        /// Failure rate (failures per minute) over recent period.
        /// </summary>
        public double FailureRatePerMinute
        {
            get
            {
                var recentCutoff = DateTime.UtcNow.AddMinutes(-1);
                int recentCount = 0;
                
                lock (_syncRoot)
                {
                    for (int i = _recentFailures.Count - 1; i >= 0; i--)
                    {
                        if (_recentFailures[i].Timestamp >= recentCutoff)
                            recentCount++;
                        else
                            break;
                    }
                }
                
                return recentCount; // Per minute by definition
            }
        }

        /// <summary>
        /// True if pipeline is in a degraded state (high failure rate).
        /// </summary>
        public bool IsDegraded => FailureRatePerMinute > 5.0;

        #endregion

        #region Exception Boundaries

        /// <summary>
        /// Executes a pipeline stage with comprehensive exception handling.
        /// Guarantees cleanup and detailed telemetry on failure.
        /// </summary>
        /// <typeparam name="TResult">Result type of the stage</typeparam>
        /// <param name="stage">Stage being executed</param>
        /// <param name="stageAction">Stage implementation</param>
        /// <param name="fallbackFactory">Factory for fallback result on failure</param>
        /// <param name="operationName">Name for telemetry</param>
        /// <returns>Stage result or fallback if stage failed</returns>
        public TResult ExecuteWithBoundary<TResult>(
            PipelineStage stage,
            Func<TResult> stageAction,
            Func<PipelineStageException, TResult> fallbackFactory,
            [CallerMemberName] string operationName = null)
        {
            var context = PipelineContext.Current;
            var stopwatch = Stopwatch.StartNew();
            Exception capturedException = null;
            string additionalContext = null;

            try
            {
                // Set up exception context
                BeginStageExceptionContext(stage, operationName);
                
                // Execute stage with narrow exception windows
                var result = stageAction();
                
                stopwatch.Stop();
                
                // Record success telemetry
                RecordStageSuccess(stage, operationName, stopwatch.Elapsed);
                
                return result;
            }
            catch (PipelineStageException pse)
            {
                capturedException = pse;
                additionalContext = pse.AdditionalContext;
                
                stopwatch.Stop();
                HandlePipelineException(context, stage, pse, additionalContext, stopwatch.Elapsed, operationName);
                
                // Use fallback factory if provided
                if (fallbackFactory != null)
                {
                    try
                    {
                        return fallbackFactory(pse);
                    }
                    catch (Exception fbEx)
                    {
                        // Fallback factory itself failed - critical error
                        EngineLogCompat.Error(
                            $"[PIPELINE CRITICAL] Fallback factory failed for {stage}.{operationName}: {fbEx.Message}",
                            LogCategory.Rendering,
                            fbEx);
                        
                        // Re-throw original or fallback error, whichever is more severe
                        throw new PipelineStageException(
                            $"Stage '{stage}.{operationName}' failed and fallback also failed: {fbEx.Message}",
                            context?.CurrentStage ?? stage,
                            stage).WithAdditionalContext(additionalContext ?? "No context");
                    }
                }
                
                // No fallback provided, so re-throw
                throw;
            }
            catch (OutOfMemoryException oom)
            {
                capturedException = oom;
                stopwatch.Stop();
                
                HandleCriticalException(stage, oom, stopwatch.Elapsed, operationName);
                
                // OOM is not recoverable at pipeline level - re-throw
                throw;
            }
            catch (StackOverflowException so)
            {
                capturedException = so;
                stopwatch.Stop();
                
                HandleCriticalException(stage, so, stopwatch.Elapsed, operationName);
                
                // Stack overflow is not recoverable - re-throw
                throw;
            }
            catch (ThreadAbortException tae)
            {
                capturedException = tae;
                stopwatch.Stop();
                
                // Thread abort is external - log and re-throw without fallback
                EngineLogCompat.Warn(
                    $"[PIPELINE] Thread abort during {stage}.{operationName} after {stopwatch.Elapsed.TotalMilliseconds:F2}ms",
                    LogCategory.Rendering);
                
                throw;
            }
            catch (Exception ex)
            {
                capturedException = ex;
                
                stopwatch.Stop();
                
                // Wrap and handle as pipeline exception
                var pipelineEx = new PipelineStageException(
                    $"Unexpected error in {stage}.{operationName}: {ex.Message}",
                    context?.CurrentStage ?? stage,
                    stage,
                    ex).WithAdditionalContext(additionalContext);
                
                HandlePipelineException(context, stage, pipelineEx, additionalContext, stopwatch.Elapsed, operationName);
                
                // Use fallback if available
                if (fallbackFactory != null)
                {
                    try
                    {
                        return fallbackFactory(pipelineEx);
                    }
                    catch (Exception fbEx)
                    {
                        EngineLogCompat.Error(
                            $"[PIPELINE CRITICAL] Fallback factory failed for {stage}.{operationName}: {fbEx.Message}",
                            LogCategory.Rendering,
                            fbEx);
                        
                        throw new PipelineStageException(
                            $"Stage '{stage}.{operationName}' failed and fallback also failed: {fbEx.Message}",
                            context?.CurrentStage ?? stage,
                            stage,
                            ex).WithAdditionalContext(additionalContext ?? "No context");
                    }
                }
                
                throw pipelineEx;
            }
            finally
            {
                ClearExceptionContext();

                // Always record telemetry
                if (capturedException != null)
                {
                    RecordStageException(context, stage, capturedException, stopwatch.Elapsed, operationName);
                }
            }
        }

        /// <summary>
        /// Executes an async pipeline stage with comprehensive exception handling.
        /// </summary>
        public async System.Threading.Tasks.Task<TResult> ExecuteWithBoundaryAsync<TResult>(
            PipelineStage stage,
            Func<System.Threading.Tasks.Task<TResult>> stageAction,
            Func<PipelineStageException, TResult> fallbackFactory,
            [CallerMemberName] string operationName = null)
        {
            var context = PipelineContext.Current;
            var stopwatch = Stopwatch.StartNew();
            Exception capturedException = null;
            string additionalContext = null;

            try
            {
                BeginStageExceptionContext(stage, operationName);
                
                var result = await stageAction().ConfigureAwait(false);
                
                stopwatch.Stop();
                RecordStageSuccess(stage, operationName, stopwatch.Elapsed);
                
                return result;
            }
            catch (PipelineStageException pse)
            {
                capturedException = pse;
                additionalContext = pse.AdditionalContext;
                
                stopwatch.Stop();
                HandlePipelineException(context, stage, pse, additionalContext, stopwatch.Elapsed, operationName);
                
                if (fallbackFactory != null)
                {
                    try
                    {
                        return fallbackFactory(pse);
                    }
                    catch (Exception fbEx)
                    {
                        EngineLogCompat.Error(
                            $"[PIPELINE CRITICAL] Async fallback factory failed for {stage}.{operationName}: {fbEx.Message}",
                            LogCategory.Rendering,
                            fbEx);
                        
                        throw new PipelineStageException(
                            $"Async stage '{stage}.{operationName}' failed and fallback also failed: {fbEx.Message}",
                            context?.CurrentStage ?? stage,
                            stage).WithAdditionalContext(additionalContext ?? "No context");
                    }
                }
                
                throw;
            }
            catch (OutOfMemoryException oom)
            {
                capturedException = oom;
                stopwatch.Stop();
                HandleCriticalException(stage, oom, stopwatch.Elapsed, operationName);
                throw;
            }
            catch (StackOverflowException so)
            {
                capturedException = so;
                stopwatch.Stop();
                HandleCriticalException(stage, so, stopwatch.Elapsed, operationName);
                throw;
            }
            catch (ThreadAbortException tae)
            {
                capturedException = tae;
                stopwatch.Stop();
                
                EngineLogCompat.Warn(
                    $"[PIPELINE] Async thread abort during {stage}.{operationName} after {stopwatch.Elapsed.TotalMilliseconds:F2}ms",
                    LogCategory.Rendering);
                
                throw;
            }
            catch (Exception ex)
            {
                capturedException = ex;
                stopwatch.Stop();
                
                var pipelineEx = new PipelineStageException(
                    $"Unexpected async error in {stage}.{operationName}: {ex.Message}",
                    context?.CurrentStage ?? stage,
                    stage,
                    ex).WithAdditionalContext(additionalContext);
                
                HandlePipelineException(context, stage, pipelineEx, additionalContext, stopwatch.Elapsed, operationName);
                
                if (fallbackFactory != null)
                {
                    try
                    {
                        return fallbackFactory(pipelineEx);
                    }
                    catch (Exception fbEx)
                    {
                        EngineLogCompat.Error(
                            $"[PIPELINE CRITICAL] Async fallback factory failed for {stage}.{operationName}: {fbEx.Message}",
                            LogCategory.Rendering,
                            fbEx);
                        
                        throw new PipelineStageException(
                            $"Async stage '{stage}.{operationName}' failed and fallback also failed: {fbEx.Message}",
                            context?.CurrentStage ?? stage,
                            stage,
                            ex).WithAdditionalContext(additionalContext ?? "No context");
                    }
                }
                
                throw pipelineEx;
            }
            finally
            {
                ClearExceptionContext();

                if (capturedException != null)
                {
                    RecordStageException(context, stage, capturedException, stopwatch.Elapsed, operationName);
                }
            }
        }

        #endregion

        #region Exception Context Management

        private readonly ThreadLocal<ExceptionContext> _threadExceptionContext = new();

        private ExceptionContext BeginStageExceptionContext(PipelineStage stage, string operationName)
        {
            var context = new ExceptionContext
            {
                Stage = stage,
                OperationName = operationName,
                StartTime = DateTime.UtcNow,
                ThreadId = Environment.CurrentManagedThreadId
            };
            
            _threadExceptionContext.Value = context;
            return context;
        }

        private void ClearExceptionContext()
        {
            _threadExceptionContext.Value = null;
        }

        #endregion

        #region Telemetry Recording

        private void RecordStageSuccess(PipelineStage stage, string operationName, TimeSpan duration)
        {
            var context = _threadExceptionContext.Value;
            
            if (duration > TimeSpan.FromMilliseconds(100))
            {
                EngineLogCompat.Warn(
                    $"[PIPELINE SLOW] {stage}.{operationName} took {duration.TotalMilliseconds:F2}ms (threshold: 100ms)",
                    LogCategory.Performance);
            }
            else if (PipelineTelemetry.IsTracingEnabled)
            {
                PipelineTelemetry.RecordStageSuccess(stage, operationName, duration);
            }
        }

        private void RecordStageException(PipelineContext context, PipelineStage stage, Exception ex, TimeSpan duration, string operationName)
        {
            var failure = new PipelineStageFailure
            {
                Stage = stage,
                OperationName = operationName,
                Exception = ex,
                Duration = duration,
                Timestamp = DateTime.UtcNow,
                FrameNumber = context?.FrameNumber ?? 0,
                CurrentStage = context?.CurrentStage ?? PipelineStage.Idle,
                ThreadId = Environment.CurrentManagedThreadId
            };

            lock (_syncRoot)
            {
                Interlocked.Increment(ref _totalFailureCount);
                _lastFailureTime = failure.Timestamp;
                _lastFailure = failure;
                
                _recentFailures.Add(failure);
                if (_recentFailures.Count > 10)
                {
                    _recentFailures.RemoveAt(0);
                }
            }

            ClearExceptionContext();
        }

        private void HandlePipelineException(PipelineContext context, PipelineStage stage, PipelineStageException pse, string additionalContext, TimeSpan duration, string operationName)
        {
            var logMessage = $"[PIPELINE FAILURE] {stage}.{operationName} failed after {duration.TotalMilliseconds:F2}ms: {pse.Message}";
            
            // Add context if available
            if (!string.IsNullOrEmpty(additionalContext))
                logMessage += $" | Context: {additionalContext}";
            
            // Log at appropriate level based on exception type
            LogCategory category = pse switch
            {
                _ when pse.IsRecoverable() => LogCategory.Warning,
                _ when duration > TimeSpan.FromSeconds(1) => LogCategory.Performance,
                _ => LogCategory.Error
            };
            
            EngineLogCompat.Error(logMessage, category, pse);

            // Check for degradation patterns
            CheckDegradationPatterns();
        }

        private void HandleCriticalException(PipelineStage stage, Exception criticalEx, TimeSpan duration, string operationName)
        {
            var logMessage = $"[PIPELINE CRITICAL] Unrecoverable exception in {stage}.{operationName} after {duration.TotalMilliseconds:F2}ms: {criticalEx.GetType().Name}";
            
            EngineLogCompat.Error(logMessage, LogCategory.Critical, criticalEx);
            
            // Critical exceptions are logged to separate channel for alerting
            PipelineTelemetry.RecordCriticalFailure(stage, operationName, criticalEx, duration);
        }

        private void CheckDegradationPatterns()
        {
            var failureRate = FailureRatePerMinute;
            
            // Alert on high failure rates
            if (failureRate > 10.0)
            {
                EngineLogCompat.Error(
                    $"[PIPELINE DEGRADED] High failure rate detected: {failureRate:F2} failures/minute",
                    LogCategory.Critical);
            }
            else if (failureRate > 5.0)
            {
                EngineLogCompat.Warn(
                    $"[PIPELINE WARNING] Elevated failure rate: {failureRate:F2} failures/minute",
                    LogCategory.Performance);
            }
        }

        #endregion

        #region Diagnostics

        public override string ToString()
        {
            var rate = FailureRatePerMinute;
            var degraded = IsDegraded ? " [DEGRADED]" : "";
            
            return $"PipelineExceptionBoundary: Failures={_totalFailureCount}, Rate={rate:F2}/min{degraded}";
        }

        #endregion
    }

    /// <summary>
    /// Records a pipeline stage failure for diagnostic purposes.
    /// </summary>
    public sealed class PipelineStageFailure
    {
        public PipelineStage Stage { get; init; }
        public string OperationName { get; init; }
        public Exception Exception { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTime Timestamp { get; init; }
        public long FrameNumber { get; init; }
        public PipelineStage CurrentStage { get; init; }
        public int ThreadId { get; init; }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff} Frame:{FrameNumber} Thread:{ThreadId}] {Stage}.{OperationName} failed after {Duration.TotalMilliseconds:F2}ms: {Exception.Message}";
        }
    }

    /// <summary>
    /// Thread-local context for exception tracking.
    /// </summary>
    internal sealed class ExceptionContext
    {
        public PipelineStage Stage { get; set; }
        public string OperationName { get; set; }
        public DateTime StartTime { get; set; }
        public int ThreadId { get; set; }
    }
}
