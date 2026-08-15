// SpecRef: FenBrowser pipeline telemetry and health monitoring contract
// CapabilityId: PIPELINE-TELEMETRY-01
// Determinism: strict
// FallbackPolicy: keep-metrics
// =============================================================================
// PipelineTelemetry.cs
// Comprehensive pipeline health monitoring and metrics collection
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using FenBrowser.Core.Logging;

#pragma warning disable CS8632

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Production telemetry for the rendering pipeline.
    /// Provides metrics, health checks, and performance monitoring.
    /// </summary>
    public static class PipelineTelemetry
    {
        private static readonly Meter _pipelineMeter = new Meter("FenBrowser.Pipeline", "1.0.0");
        private static readonly Counter<long> _framesRendered = _pipelineMeter.CreateCounter<long>("pipeline.frames.rendered");
        private static readonly Counter<long> _stageSuccesses = _pipelineMeter.CreateCounter<long>("pipeline.stage.successes");
        private static readonly Counter<long> _stageFailures = _pipelineMeter.CreateCounter<long>("pipeline.stage.failures");
        private static readonly Histogram<double> _stageDuration = _pipelineMeter.CreateHistogram<double>("pipeline.stage.duration_ms");
        private static readonly Histogram<double> _frameDuration = _pipelineMeter.CreateHistogram<double>("pipeline.frame.duration_ms");
        private static readonly Histogram<long> _memoryBytes = _pipelineMeter.CreateHistogram<long>("pipeline.memory.bytes");
        private static readonly Histogram<int> _domNodeCount = _pipelineMeter.CreateHistogram<int>("pipeline.dom.nodes");
        private static readonly Histogram<int> _layoutBoxCount = _pipelineMeter.CreateHistogram<int>("pipeline.layout.boxes");
        private static readonly Counter<long> _criticalFailures = _pipelineMeter.CreateCounter<long>("pipeline.failures.critical");

        // MeterListener must be strongly rooted for as long as measurements are wanted.
        // Keeping it only in the static-constructor local allowed GC to silently stop
        // callbacks after startup.
        private static readonly MeterListener? _meterListener;

        private static long _totalFrames;
        private static long _successfulFrames;
        private static long _failedFrames;
        private static readonly Stopwatch _uptimeStopwatch = Stopwatch.StartNew();
        private static readonly Queue<(DateTime Timestamp, double Value)> _recentFrameTimes = new();
        private static readonly Queue<(DateTime Timestamp, bool Success)> _recentFrameSuccess = new();
        private static readonly object _telemetryLock = new();

        public static bool IsTracingEnabled { get; set; } = Debugger.IsAttached;
        public static bool IsMetricsEnabled { get; set; } = true;
        public static bool EnableDetailedLogging { get; set; }

        private static long _totalErrors;
        private static readonly Queue<(DateTime Time, string Message, Exception Exception)> _recentErrors = new(capacity: 100);

        static PipelineTelemetry()
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "FenBrowser.Pipeline")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);
            listener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
            listener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
            listener.Start();
            _meterListener = listener;
        }

        private static void OnMeasurementRecorded<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            if (!EnableDetailedLogging || measurement == null)
            {
                return;
            }

            var tagString = FormatTags(tags);
            EngineLogCompat.Trace(
                $"[TELEMETRY] {instrument.Name}: {measurement} {instrument.Unit} {tagString}",
                LogCategory.Telemetry);
        }

        private static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (tags.Length == 0)
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (var i = 0; i < tags.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(tags[i].Key).Append('=').Append(tags[i].Value);
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static void RecordFrameRendered(TimeSpan frameDuration, bool success)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            lock (_telemetryLock)
            {
                _totalFrames++;
                if (success)
                {
                    _successfulFrames++;
                }
                else
                {
                    _failedFrames++;
                }

                _framesRendered.Add(1);
                _frameDuration.Record(
                    frameDuration.TotalMilliseconds,
                    new KeyValuePair<string, object?>("success", success));

                var now = DateTime.UtcNow;
                _recentFrameTimes.Enqueue((now, frameDuration.TotalMilliseconds));
                _recentFrameSuccess.Enqueue((now, success));

                var cutoff = now.AddMinutes(-5);
                while (_recentFrameTimes.Count > 0 && _recentFrameTimes.Peek().Timestamp < cutoff)
                {
                    _recentFrameTimes.Dequeue();
                    _recentFrameSuccess.Dequeue();
                }

                if (IsTracingEnabled)
                {
                    EngineLogCompat.Info(
                        $"[TELEMETRY] Frame {_totalFrames}: duration={frameDuration.TotalMilliseconds:F2}ms, success={success}",
                        LogCategory.Telemetry);
                }
            }
        }

        public static void RecordStageSuccess(PipelineStage stage, string operationName, TimeSpan duration)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            lock (_telemetryLock)
            {
                _stageSuccesses.Add(
                    1,
                    new KeyValuePair<string, object?>("stage", stage.ToString()),
                    new KeyValuePair<string, object?>("operation", operationName));

                RecordStageDuration(stage, operationName, duration, success: true);

                if (EnableDetailedLogging)
                {
                    EngineLogCompat.Debug(
                        $"[TELEMETRY] Stage {stage}.{operationName}: {duration.TotalMilliseconds:F2}ms",
                        LogCategory.Telemetry);
                }
            }
        }

        public static void RecordStageFailure(PipelineStage stage, string operationName, Exception exception, TimeSpan duration)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            lock (_telemetryLock)
            {
                _stageFailures.Add(
                    1,
                    new KeyValuePair<string, object?>("stage", stage.ToString()),
                    new KeyValuePair<string, object?>("operation", operationName),
                    new KeyValuePair<string, object?>("exception_type", exception?.GetType().Name ?? "Unknown"));

                // Failure latency is often the most valuable latency sample. The old
                // path accepted duration but dropped it, biasing histograms toward fast
                // successful work and hiding timeout/exception cost.
                RecordStageDuration(stage, operationName, duration, success: false);
                RecordError($"{stage}.{operationName}", exception);

                if (EnableDetailedLogging)
                {
                    EngineLogCompat.Error(
                        $"[TELEMETRY] Stage {stage}.{operationName} failed after {duration.TotalMilliseconds:F2}ms: {exception?.Message ?? "Unknown error"}",
                        LogCategory.Telemetry,
                        exception);
                }
            }
        }

        public static void RecordCriticalFailure(PipelineStage stage, string operationName, Exception exception, TimeSpan duration)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            _criticalFailures.Add(
                1,
                new KeyValuePair<string, object?>("stage", stage.ToString()),
                new KeyValuePair<string, object?>("operation", operationName));
            _stageFailures.Add(
                1,
                new KeyValuePair<string, object?>("stage", stage.ToString()),
                new KeyValuePair<string, object?>("operation", operationName),
                new KeyValuePair<string, object?>("exception_type", exception?.GetType().Name ?? "Unknown"),
                new KeyValuePair<string, object?>("critical", true));
            RecordStageDuration(stage, operationName, duration, success: false);

            RecordError($"CRITICAL: {stage}.{operationName}", exception);

            EngineLogCompat.Error(
                $"[TELEMETRY CRITICAL] {stage}.{operationName} after {duration.TotalMilliseconds:F2}ms: {exception?.Message ?? "Unknown error"}",
                LogCategory.Critical,
                exception);
        }

        private static void RecordStageDuration(PipelineStage stage, string operationName, TimeSpan duration, bool success)
        {
            _stageDuration.Record(
                Math.Max(0, duration.TotalMilliseconds),
                new KeyValuePair<string, object?>("stage", stage.ToString()),
                new KeyValuePair<string, object?>("operation", operationName),
                new KeyValuePair<string, object?>("success", success));
        }

        public static void RecordMemoryUsage(long bytes, PipelineStage? stage = null)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            if (stage.HasValue)
            {
                _memoryBytes.Record(bytes, new KeyValuePair<string, object?>("stage", stage.Value.ToString()));
            }
            else
            {
                _memoryBytes.Record(bytes);
            }
        }

        public static void RecordDomNodeCount(int count)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            lock (_telemetryLock)
            {
                _domNodeCount.Record(count);
                if (EnableDetailedLogging)
                {
                    EngineLogCompat.Debug($"[TELEMETRY] DOM nodes: {count:N0}", LogCategory.Telemetry);
                }
            }
        }

        public static void RecordLayoutBoxCount(int count)
        {
            if (!IsMetricsEnabled)
            {
                return;
            }

            lock (_telemetryLock)
            {
                _layoutBoxCount.Record(count);
                if (EnableDetailedLogging)
                {
                    EngineLogCompat.Debug($"[TELEMETRY] Layout boxes: {count:N0}", LogCategory.Telemetry);
                }
            }
        }

        public static PipelineHealthStatus GetHealthStatus()
        {
            lock (_telemetryLock)
            {
                var uptime = _uptimeStopwatch.Elapsed;
                double avgFrameTime = 0;
                var frameCount = 0;
                var recentSuccessRate = 1.0;

                if (_recentFrameTimes.Count > 0)
                {
                    double totalTime = 0;
                    foreach (var entry in _recentFrameTimes)
                    {
                        totalTime += entry.Value;
                        frameCount++;
                    }
                    avgFrameTime = totalTime / Math.Max(1, frameCount);

                    var successfulFrames = 0;
                    foreach (var entry in _recentFrameSuccess)
                    {
                        if (entry.Success)
                        {
                            successfulFrames++;
                        }
                    }
                    recentSuccessRate = (double)successfulFrames / Math.Max(1, _recentFrameSuccess.Count);
                }

                // No frame samples is an unknown/idle state, not evidence of a broken
                // pipeline. The previous zero default marked a freshly-started or reset
                // browser unhealthy before it rendered anything.
                var hasRecentFrames = frameCount > 0;
                var isHealthy = !hasRecentFrames ||
                    (recentSuccessRate >= 0.95 && avgFrameTime < 250);

                return new PipelineHealthStatus
                {
                    Uptime = uptime,
                    TotalFrames = _totalFrames,
                    SuccessfulFrames = _successfulFrames,
                    FailedFrames = _failedFrames,
                    SuccessRate = _totalFrames > 0 ? (double)_successfulFrames / _totalFrames : 1.0,
                    RecentSuccessRate = recentSuccessRate,
                    AverageFrameTimeMs = avgFrameTime,
                    TotalErrors = _totalErrors,
                    IsHealthy = isHealthy,
                    ErrorRatePerMinute = GetErrorRatePerMinute(),
                    Timestamp = DateTime.UtcNow
                };
            }
        }

        public static bool IsHealthy()
        {
            return GetHealthStatus().IsHealthy;
        }

        private static double GetErrorRatePerMinute()
        {
            if (_totalErrors == 0)
            {
                return 0;
            }

            var uptimeMinutes = _uptimeStopwatch.Elapsed.TotalMinutes;
            return _totalErrors / Math.Max(1.0, uptimeMinutes);
        }

        private static void RecordError(string context, Exception exception)
        {
            Interlocked.Increment(ref _totalErrors);

            lock (_telemetryLock)
            {
                _recentErrors.Enqueue((DateTime.UtcNow, context, exception));
                while (_recentErrors.Count > 100)
                {
                    _recentErrors.Dequeue();
                }
            }
        }

        public static IReadOnlyList<(DateTime Time, string Message, Exception Exception)> GetRecentErrors()
        {
            lock (_telemetryLock)
            {
                return new List<(DateTime, string, Exception)>(_recentErrors);
            }
        }

        public static void Reset()
        {
            lock (_telemetryLock)
            {
                _totalFrames = 0;
                _successfulFrames = 0;
                _failedFrames = 0;
                _totalErrors = 0;
                _recentFrameTimes.Clear();
                _recentFrameSuccess.Clear();
                _recentErrors.Clear();
            }
        }

        public static void LogPeriodicDiagnostics()
        {
            var health = GetHealthStatus();
            var resourceGuard = PipelineResourceGuard.Current;
            var logLevel = health.IsHealthy
                ? LogLevel.Info
                : health.SuccessRate > 0.8 ? LogLevel.Warning : LogLevel.Error;

            EngineLogCompat.Log(
                $"[PIPELINE HEALTH] " +
                $"Uptime: {health.Uptime:hh\\:mm\\:ss}, " +
                $"Frames: {health.TotalFrames:N0} ({health.SuccessRate:P1} success, {health.RecentSuccessRate:P1} recent), " +
                $"AvgFrame: {health.AverageFrameTimeMs:F1}ms, " +
                $"Errors: {health.TotalErrors:N0} ({health.ErrorRatePerMinute:F2}/min), " +
                $"Resources: {resourceGuard}",
                LogCategory.Telemetry,
                logLevel);
        }
    }

    /// <summary>
    /// Current health status of the pipeline.
    /// </summary>
    public readonly struct PipelineHealthStatus
    {
        public TimeSpan Uptime { get; init; }
        public long TotalFrames { get; init; }
        public long SuccessfulFrames { get; init; }
        public long FailedFrames { get; init; }
        public double SuccessRate { get; init; }
        public double RecentSuccessRate { get; init; }
        public double AverageFrameTimeMs { get; init; }
        public long TotalErrors { get; init; }
        public double ErrorRatePerMinute { get; init; }
        public bool IsHealthy { get; init; }
        public DateTime Timestamp { get; init; }

        public override string ToString()
        {
            var status = IsHealthy ? "HEALTHY" : "UNHEALTHY";
            return $"[PIPELINE {status}] Up={Uptime:hh\\:mm\\:ss}, " +
                   $"Frames={TotalFrames:N0} ({SuccessRate:P1}), " +
                   $"Avg={AverageFrameTimeMs:F1}ms, " +
                   $"Errors={TotalErrors:N0} ({ErrorRatePerMinute:F2}/min)";
        }
    }
}
