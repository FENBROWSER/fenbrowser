// SpecRef: FenBrowser pipeline integration and safety enhancement layer
// CapabilityId: PIPELINE-INTEGRATION-01
// Determinism: strict
// FallbackPolicy: degrade-gracefully
// =============================================================================
// PipelineIntegrationLayer.cs
// Production-ready integration layer for all pipeline hardening components
//
// SPEC REFERENCE: Custom (internal architecture)
// PURPOSE: Seamlessly integrate exception boundaries, resource guards, and telemetry
//          with existing pipeline infrastructure without breaking changes
//
// DESIGN PRINCIPLES:
// 1. Zero-breaking-changes - works with existing PipelineContext and RenderPipeline
// 2. Opt-in safety - can be enabled/disabled per stage
// 3. Minimal overhead - no performance impact when disabled
// 4. Comprehensive protection - all failure modes are caught and handled
// 5. Observable - all decisions are logged and tracked
// =============================================================================

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Production integration layer for enhanced pipeline safety.
    /// Wraps existing pipeline calls with exception boundaries, resource guards, and telemetry.
    /// </summary>
    public static class PipelineIntegrationLayer
    {
        #region Configuration

        public static bool EnableExceptionBoundaries { get; set; } = true;
        public static bool EnableResourceGuards { get; set; } = true;
        public static bool EnableTelemetry { get; set; } = true;
        public static bool EnableStrictMode { get; set; } = false;

        /// <summary>
        /// Global failure threshold - if exceeded, pipeline degrades to fail-safe mode
        /// </summary>
        public static int MaxConsecutiveFailures { get; set; } = 3;

        private static int _consecutiveFailureCount = 0;
        private static bool _isInFailSafeMode = false;

        #endregion

        #region Protected Stage Execution

        /// <summary>
        /// Execute a pipeline stage with full production hardening.
        /// Integrates exception boundary, resource guard, and telemetry.
        /// </summary>
        public static TResult ExecuteStage<TResult>(
            PipelineStage stage,
            string operationName,
            Func<TResult> stageAction,
            Func<TResult> fallbackAction = null,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string filePath = null,
            [CallerLineNumber] int lineNumber = 0)
        {
            if (!EnableExceptionBoundaries)
            {
                // Direct execution without hardening
                return stageAction();
            }

            var resourceGuard = PipelineResourceGuard.Current;
            var exceptionBoundary = PipelineExceptionBoundary.Current;
            TResult result = default;
            Exception capturedException = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Set up context
                var context = PipelineContext.Current;
                context.BeginStage(stage);

                // Create fallback if not provided
                if (fallbackAction == null)
                {
                    fallbackAction = CreateFallbackAction<TResult>(stage, operationName);
                }

                // Execute with exception boundary
                result = exceptionBoundary.ExecuteWithBoundary(
                    stage,
                    () =>
                    {
                        // Track resource usage for the stage
                        if (EnableResourceGuards)
                        {
                            // This could be extended to track actual allocations
                            // For now, we just validate against hard limits
                            resourceGuard.ValidateStageBudget(stage, operationName);
                        }

                        // Execute the actual stage logic
                        return stageAction();
                    },
                    pse => fallbackAction(),
                    operationName);

                stopwatch.Stop();

                // Track success
                RecordStageResult(stage, operationName, stopwatch.Elapsed, null, caller, filePath, lineNumber);
                
                // Update health metrics
                UpdateHealthCounters(true);

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                capturedException = ex;

                // Track failure
                RecordStageResult(stage, operationName, stopwatch.Elapsed, ex, caller, filePath, lineNumber);
                
                // Update health counters
                UpdateHealthCounters(false);

                // Health check - are we degrading?
                CheckDegradation();

                // Fail-safe in strict mode
                if (EnableStrictMode)
                {
                    throw;
                }

                // Execute fallback
                if (fallbackAction != null)
                {
                    try
                    {
                        return fallbackAction();
                    }
                    catch (Exception fbEx)
                    {
                        // Even fallback failed - escalate to caller
                        var error = new PipelineStageException(
                            $"Stage '{stage}:{operationName}' failed and fallback also failed: {fbEx.Message}",
                            stage,
                            stage,
                            fbEx);
                        
                        throw error;
                    }
                }

                // Return default if no fallback
                return default;
            }
            finally
            {
                // Always clean up context
                try
                {
                    PipelineContext.Current.EndStage();
                }
                catch (Exception cleanupEx)
                {
                    // Log but don't throw - primary error is more important
                    PipelineTelemetry.RecordStageFailure(
                        stage, 
                        $"{operationName}_cleanup", 
                        cleanupEx, 
                        TimeSpan.Zero);
                }
            }
        }

        /// <summary>
        /// Async version of protected stage execution.
        /// </summary>
        public static System.Threading.Tasks.Task<TResult> ExecuteStageAsync<TResult>(
            PipelineStage stage,
            string operationName,
            Func<System.Threading.Tasks.Task<TResult>> stageAction,
            Func<TResult> fallbackAction = null,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string filePath = null,
            [CallerLineNumber] int lineNumber = 0)
        {
            if (!EnableExceptionBoundaries)
            {
                return stageAction();
            }

            // For async operations, we need to wrap the entire thing in a task
            return System.Threading.Tasks.Task.Run(async () =>
            {
                var resourceGuard = PipelineResourceGuard.Current;
                var exceptionBoundary = PipelineExceptionBoundary.Current;
                TResult result = default;
                Exception capturedException = null;
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    var context = PipelineContext.Current;
                    context.BeginStage(stage);

                    if (fallbackAction == null)
                    {
                        fallbackAction = CreateFallbackAction<TResult>(stage, operationName);
                    }

                    result = await exceptionBoundary.ExecuteWithBoundaryAsync(
                        stage,
                        () =>
                        {
                            if (EnableResourceGuards)
                            {
                                resourceGuard.ValidateStageBudget(stage, operationName);
                            }
                            
                            return stageAction();
                        },
                        pse => fallbackAction(),
                        operationName).ConfigureAwait(false);

                    stopwatch.Stop();
                    RecordStageResult(stage, operationName, stopwatch.Elapsed, null, caller, filePath, lineNumber);
                    UpdateHealthCounters(true);
                    return result;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    capturedException = ex;
                    RecordStageResult(stage, operationName, stopwatch.Elapsed, ex, caller, filePath, lineNumber);
                    UpdateHealthCounters(false);
                    CheckDegradation();

                    if (EnableStrictMode)
                    {
                        throw;
                    }

                    if (fallbackAction != null)
                    {
                        try
                        {
                            return fallbackAction();
                        }
                        catch (Exception fbEx)
                        {
                            throw new PipelineStageException(
                                $"Stage '{stage}:{operationName}' failed and fallback also failed: {fbEx.Message}",
                                stage,
                                stage,
                                fbEx);
                        }
                    }

                    return default;
                }
                finally
                {
                    try
                    {
                        PipelineContext.Current.EndStage();
                    }
                    catch (Exception cleanupEx)
                    {
                        PipelineTelemetry.RecordStageFailure(
                            stage, 
                            $"{operationName}_cleanup", 
                            cleanupEx, 
                            TimeSpan.Zero);
                    }
                }
            });
        }

        private static void RecordStageResult(
            PipelineStage stage,
            string operationName,
            TimeSpan duration,
            Exception exception,
            string caller,
            string filePath,
            int lineNumber)
        {
            if (!EnableTelemetry) return;

            if (exception == null)
            {
                PipelineTelemetry.RecordStageSuccess(stage, operationName, duration);
            }
            else
            {
                PipelineTelemetry.RecordStageFailure(stage, operationName, exception, duration);
            }

            // Log to general log as well
            var location = FormatLocation(caller, filePath, lineNumber);
            if (exception != null)
            {
                EngineLogCompat.Warn(
                    $"[PIPELINE EXCEPTION] {stage}:{operationName} at {location} - {exception.Message}",
                    LogCategory.Rendering,
                    exception);
            }
            else if (EnableDetailedLogging)
            {
                EngineLogCompat.Debug(
                    $"[PIPELINE] {stage}:{operationName} at {location} completed in {duration.TotalMilliseconds:F1}ms",
                    LogCategory.Rendering);
            }
        }

        private static string FormatLocation(string caller, string filePath, int lineNumber)
        {
            var simpleName = filePath?.Split('\', '/')[^1] ?? "<unknown>";
            return $"{simpleName}:{lineNumber}:{caller}";
        }

        #endregion

        #region Fallback Factory

        private static Func<TResult> CreateFallbackAction<TResult>(
            PipelineStage stage,
            string operationName)
        {
            return () =>
            {
                var fallbackType = DetermineFallbackType(stage);

                EngineLogCompat.Warn(
                    $"[PIPELINE FALLBACK] Executing fallback for {stage}:{operationName} (type: {fallbackType})",
                    LogCategory.Rendering);

                try
                {
                    switch (stage)
                    {
                        case PipelineStage.Tokenizing:
                            throw new PipelineStageException(
                                "Tokenizing failures are not recoverable - falling back is not safe",
                                stage, stage);

                        case PipelineStage.Parsing:
                            throw new PipelineStageException(
                                "Parsing failures are not recoverable - falling back is not safe",
                                stage, stage);

                        case PipelineStage.Styling:
                            return (TResult)(object)CreateFallbackStyles();

                        case PipelineStage.Layout:
                            return (TResult)(object)CreateFallbackLayout();

                        case PipelineStage.Painting:
                            return (TResult)(object)CreateFallbackPaint();

                        case PipelineStage.Rasterizing:
                            return (TResult)(object)CreateFallbackRaster();

                        case PipelineStage.Presenting:
                            return (TResult)(object)CreateFallbackPresent();

                        default:
                            throw new PipelineStageException(
                                $"No fallback available for stage {stage}",
                                stage, stage);
                    }
                }
                catch (Exception ex)
                {
                    throw new PipelineStageException(
                        $"Fallback for {stage}:{operationName} failed: {ex.Message}",
                        stage, stage, ex);
                }
            };
        }

        private static string DetermineFallbackType(PipelineStage stage)
        {
            return stage switch
            {
                PipelineStage.Styling => "empty styles",
                PipelineStage.Layout => "default layout",
                PipelineStage.Painting => "empty display list",
                PipelineStage.Rasterizing => "blank raster",
                PipelineStage.Presenting => "skip present",
                _ => "none"
            };
        }

        private static object CreateFallbackStyles()
        {
            // Return empty style collection
            return new System.Collections.Generic.Dictionary<Core.Dom.V2.Node, Core.Css.CssComputed>();
        }

        private static object CreateFallbackLayout()
        {
            // Return empty layout result
            return new FenBrowser.FenEngine.Layout.LayoutResult(
                Array.Empty<FenBrowser.FenEngine.Layout.LayoutBox>(),
                new System.Collections.Generic.Dictionary<Core.Dom.V2.Node, FenBrowser.FenEngine.Layout.BoxModel>(),
                0, 0
            );
        }

        private static object CreateFallbackPaint()
        {
            // Return empty paint tree
            return new FenBrowser.FenEngine.Rendering.PaintTree.ImmutablePaintTree(
                Array.Empty<FenBrowser.FenEngine.Rendering.PaintTree.PaintNode>()
            );
        }

        private static object CreateFallbackRaster()
        {
            // Return blank or cached raster
            return null; // Let caller handle
        }

        private static object CreateFallbackPresent()
        {
            return true; // Present skipped
        }

        #endregion

        #region Health Management

        private static void UpdateHealthCounters(bool success)
        {
            if (!EnableTelemetry) return;

            lock (typeof(PipelineIntegrationLayer))
            {
                if (success)
                {
                    _consecutiveFailureCount = 0;
                }
                else
                {
                    Interlocked.Increment(ref _consecutiveFailureCount);
                }
            }
        }

        private static void CheckDegradation()
        {
            var consecutiveFailures = _consecutiveFailureCount;
            var maxFailures = MaxConsecutiveFailures;

            if (consecutiveFailures >= maxFailures && !_isInFailSafeMode)
            {
                _isInFailSafeMode = true;
                
                EngineLogCompat.Error(
                    $"[PIPELINE DEGRADATION] Entering fail-safe mode after {consecutiveFailures} consecutive failures",
                    LogCategory.Critical);

                PipelineTelemetry.RecordCriticalFailure(
                    PipelineStage.Idle,
                    "FailSafeActivation",
                    new Exception($"Pipeline degradation: {consecutiveFailures} consecutive failures"),
                    TimeSpan.Zero);
            }
            else if (consecutiveFailures < maxFailures && _isInFailSafeMode)
            {
                _isInFailSafeMode = false;
                
                EngineLogCompat.Info(
                    $"[PIPELINE RECOVERY] Exiting fail-safe mode",
                    LogCategory.Rendering);
            }
        }

        /// <summary>
        /// True if pipeline is in fail-safe mode (degraded performance).
        /// </summary>
        public static bool IsInFailSafeMode => _isInFailSafeMode;

        /// <summary>
        /// Current consecutive failure count.
        /// </summary>
        public static int ConsecutiveFailureCount => _consecutiveFailureCount;

        #endregion

        #region Control

        public static void Reset()
        {
            lock (typeof(PipelineIntegrationLayer))
            {
                _consecutiveFailureCount = 0;
                _isInFailSafeMode = false;
            }
        }

        #endregion

        #region Diagnostics

        public static string GetStatusReport()
        {
            var health = PipelineTelemetry.GetHealthStatus();
            var exceptionBoundary = PipelineExceptionBoundary.Current;
            var guard = PipelineResourceGuard.Current;
            var failSafe = IsInFailSafeMode ? " [FAIL-SAFE MODE]" : "";
            
            return $"[PIPELINE STATUS]{failSafe}\n" +
                   $"  {health}\n" +
                   $"  {exceptionBoundary}\n" +
                   $"  {guard}\n" +
                   $"  Consecutive Failures: {ConsecutiveFailureCount}/{MaxConsecutiveFailures}\n" +
                   $"  Safety Features: Exceptions={EnableExceptionBoundaries}, " +
                   $"Resources={EnableResourceGuards}, Telemetry={EnableTelemetry}";
        }

        #endregion
    }
}
