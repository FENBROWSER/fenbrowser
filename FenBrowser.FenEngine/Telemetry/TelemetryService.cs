using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Telemetry
{
    /// <summary>
    /// Phase 5 Production Hardening: Telemetry and performance metrics system
    /// Collects performance metrics, crash reports, and usage statistics
    /// </summary>
    public sealed class TelemetryService : IDisposable
    {
        private static TelemetryService _instance;
        private static readonly object _lock = new object();
        
        private readonly List<MetricEvent> _metricBuffer = new List<MetricEvent>();
        private readonly List<CrashReport> _crashBuffer = new List<CrashReport>();
        private readonly Timer _flushTimer;
        private readonly string _telemetryDir;
        private readonly Stopwatch _sessionTimer = Stopwatch.StartNew();
        
        private bool _disposed;
        private bool _enabled = true;
        
        // Performance counters
        private long _frameCount;
        private long _layoutTimeTotal;
        private long _paintTimeTotal;


        // Memory tracking
        private long _peakMemoryUsage;
        private readonly Process _currentProcess;
        
        private TelemetryService()
        {
            _telemetryDir = Path.Combine(Path.GetTempPath(), "fenbrowser_telemetry");
            Directory.CreateDirectory(_telemetryDir);
            
            _currentProcess = Process.GetCurrentProcess();
            
            // Flush metrics every 30 seconds
            _flushTimer = new Timer(FlushMetrics, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            
            EngineLogCompat.Debug("[Telemetry] Service initialized", LogCategory.Performance);
        }
        
        public static TelemetryService Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ??= new TelemetryService();
                }
            }
        }
        
        /// <summary>
        /// Record a performance metric
        /// </summary>
        public void RecordMetric(string name, double value, MetricUnit unit = MetricUnit.Milliseconds)
        {
            if (!_enabled || _disposed) return;
            
            lock (_metricBuffer)
            {
                _metricBuffer.Add(new MetricEvent
                {
                    Name = name,
                    Value = value,
                    Unit = unit,
                    Timestamp = DateTime.UtcNow,
                    SessionId = GetSessionId()
                });
            }
        }
        
        /// <summary>
        /// Record frame render time
        /// </summary>
        public void RecordFrameTime(double milliseconds)
        {
            Interlocked.Increment(ref _frameCount);
            Interlocked.Add(ref _layoutTimeTotal, (long)(milliseconds * 1000));
            RecordMetric("frame_time", milliseconds, MetricUnit.Milliseconds);
            RecordMetric("fps", 1000.0 / milliseconds, MetricUnit.Count);
        }
        
        /// <summary>
        /// Record layout compute time
        /// </summary>
        public void RecordLayoutTime(double milliseconds)
        {
            Interlocked.Add(ref _layoutTimeTotal, (long)(milliseconds * 1000));
            RecordMetric("layout_time", milliseconds, MetricUnit.Milliseconds);
        }
        
        /// <summary>
        /// Record paint/raster time
        /// </summary>
        public void RecordPaintTime(double milliseconds)
        {
            Interlocked.Add(ref _paintTimeTotal, (long)(milliseconds * 1000));
            RecordMetric("paint_time", milliseconds, MetricUnit.Milliseconds);
        }
        
        /// <summary>
        /// Record memory usage
        /// </summary>
        public void RecordMemoryUsage(long bytes)
        {
            var current = _currentProcess.WorkingSet64;
            Interlocked.Exchange(ref _peakMemoryUsage, Math.Max(_peakMemoryUsage, current));
            RecordMetric("memory_usage_mb", current / (1024.0 * 1024.0), MetricUnit.Megabytes);
            RecordMetric("gc_pressure", GC.GetTotalMemory(false) / (1024.0 * 1024.0), MetricUnit.Megabytes);
        }
        
        /// <summary>
        /// Record a crash report
        /// </summary>
        public void RecordCrash(Exception exception, string component, bool fatal = false)
        {
            if (!_enabled || _disposed) return;
            
            lock (_crashBuffer)
            {
                _crashBuffer.Add(new CrashReport
                {
                    ExceptionType = exception.GetType().Name,
                    Message = exception.Message,
                    StackTrace = exception.StackTrace,
                    Component = component,
                    IsFatal = fatal,
                    Timestamp = DateTime.UtcNow,
                    SessionId = GetSessionId(),
                    MemoryUsageMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0)
                });
            }
            
            EngineLogCompat.Error($"[Telemetry] Crash recorded in {component}: {exception.Message}", LogCategory.Errors);
            
            // Flush immediately for fatal crashes
            if (fatal)
            {
                FlushMetrics(null);
            }
        }
        
        /// <summary>
        /// Record GPU process crash
        /// </summary>
        public void RecordGpuCrash(string reason, string stackTrace = null)
        {
            RecordMetric("gpu_crash", 1, MetricUnit.Count);
            
            lock (_crashBuffer)
            {
                _crashBuffer.Add(new CrashReport
                {
                    ExceptionType = "GpuProcessCrash",
                    Message = reason,
                    StackTrace = stackTrace ?? "GPU Driver Failure",
                    Component = "GPU",
                    IsFatal = false,
                    Timestamp = DateTime.UtcNow,
                    SessionId = GetSessionId(),
                    MemoryUsageMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0)
                });
            }
            
            EngineLogCompat.Error($"[Telemetry] GPU crash recorded: {reason}", LogCategory.Errors);
        }
        
        /// <summary>
        /// Record OOM (Out of Memory) event
        /// </summary>
        public void RecordOutOfMemory(long attemptedAllocationBytes)
        {
            RecordMetric("oom_crash", 1, MetricUnit.Count);
            RecordMetric("oom_allocation_mb", attemptedAllocationBytes / (1024.0 * 1024.0), MetricUnit.Megabytes);
            
            EngineLogCompat.Error($"[Telemetry] OOM recorded: {attemptedAllocationBytes / (1024 * 1024)}MB allocation failed", LogCategory.Errors);
        }
        
        /// <summary>
        /// Get current performance summary
        /// </summary>
        public PerformanceSummary GetPerformanceSummary()
        {
            return new PerformanceSummary
            {
                SessionDuration = _sessionTimer.Elapsed,
                FrameCount = _frameCount,
                AverageFrameTimeMs = _frameCount > 0 ? (_layoutTimeTotal / 1000.0) / _frameCount : 0,
                AverageLayoutTimeMs = _layoutTimeTotal / 1000.0,
                AveragePaintTimeMs = _paintTimeTotal / 1000.0,
                PeakMemoryUsageMb = _peakMemoryUsage / (1024.0 * 1024.0),
                CurrentMemoryUsageMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0),
                GcPressureMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0)
            };
        }
        
        private void FlushMetrics(object state)
        {
            try
            {
                List<MetricEvent> metrics;
                List<CrashReport> crashes;
                
                lock (_metricBuffer)
                {
                    metrics = new List<MetricEvent>(_metricBuffer);
                    _metricBuffer.Clear();
                }
                
                lock (_crashBuffer)
                {
                    crashes = new List<CrashReport>(_crashBuffer);
                    _crashBuffer.Clear();
                }
                
                if (metrics.Count > 0)
                {
                    var metricsJson = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
                    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    var path = Path.Combine(_telemetryDir, $"metrics_{timestamp}_{Guid.NewGuid()}.json");
                    File.WriteAllText(path, metricsJson);
                    EngineLogCompat.Debug($"[Telemetry] Flushed {metrics.Count} metrics", LogCategory.Performance);
                }
                
                if (crashes.Count > 0)
                {
                    var crashesJson = JsonSerializer.Serialize(crashes, new JsonSerializerOptions { WriteIndented = true });
                    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    var path = Path.Combine(_telemetryDir, $"crashes_{timestamp}_{Guid.NewGuid()}.json");
                    File.WriteAllText(path, crashesJson);
                    EngineLogCompat.Error($"[Telemetry] Flushed {crashes.Count} crashes", LogCategory.Errors);
                }
                
                // Keep only last 10 files to prevent disk overflow
                CleanupOldFiles();
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[Telemetry] Flush failed: {ex.Message}", LogCategory.Errors);
            }
        }
        
        private void CleanupOldFiles()
        {
            var files = Directory.GetFiles(_telemetryDir, "*.json");
            if (files.Length > 100)
            {
                var sorted = files.OrderBy(f => File.GetCreationTime(f)).ToList();
                for (int i = 0; i < sorted.Count - 10; i++)
                {
                    try { File.Delete(sorted[i]); }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }
        
        private string GetSessionId()
        {
            // Simple session tracking
            return $"session_{DateTime.UtcNow:yyyyMMdd}_{Environment.ProcessId}";
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                
                _flushTimer?.Dispose();
                FlushMetrics(null); // Final flush
                
                _currentProcess?.Dispose();
            }
        }
        
        /// <summary>
        /// Emergency flush for crash scenarios
        /// </summary>
        public void EmergencyFlush()
        {
            FlushMetrics(null);
        }
    }
    
    /// <summary>
    /// Telemetry metric types
    /// </summary>
    public enum MetricUnit
    {
        Count,
        Milliseconds,
        Megabytes,
        Percentage
    }
    
    /// <summary>
    /// Individual metric event
    /// </summary>
    public class MetricEvent
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public MetricUnit Unit { get; set; }
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
    }
    
    /// <summary>
    /// Crash report structure
    /// </summary>
    public class CrashReport
    {
        public string ExceptionType { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }
        public string Component { get; set; }
        public bool IsFatal { get; set; }
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
        public double MemoryUsageMb { get; set; }
    }
    
    /// <noinheritable>
    /// Performance summary snapshot
    /// </summary>
    public class PerformanceSummary
    {
        public TimeSpan SessionDuration { get; set; }
        public long FrameCount { get; set; }
        public double AverageFrameTimeMs { get; set; }
        public double AverageLayoutTimeMs { get; set; }
        public double AveragePaintTimeMs { get; set; }
        public double PeakMemoryUsageMb { get; set; }
        public double CurrentMemoryUsageMb { get; set; }
        public double GcPressureMb { get; set; }
    }
}
