using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core
{
    /// <summary>
    /// Centralized logging facade with structured output and metrics support.
    /// 10/10 Spec: JSON output, performance metrics, and backward compatibility.
    /// </summary>
    public static class FenLogger
    {
        // Wrapper for LogManager to maintain existing API
        
        /// <summary>
        /// Enable structured JSON output for log entries.
        /// </summary>
        public static bool StructuredOutputEnabled { get; set; } = false;
        
        /// <summary>
        /// Event fired when a structured log entry is emitted (for external consumers).
        /// </summary>
        public static event Action<StructuredLogEntry> OnStructuredLog;
        
        public static bool IsEnabled 
        { 
            get => true; // Managed by LogManager
            set => LogManager.Initialize(value, LogCategory.All, LogLevel.Info); // Basic forward
        }

        public static void Initialize(string logFilePath)
        {
            // Forward path to LogManager
            LogManager.Instance.SetLogFilePath(logFilePath);
            // Enable logging: LogManager.Initialize(enabled: true, categories: All, minLevel: Info)
            LogManager.Initialize(true, LogCategory.All, LogLevel.Debug);
            LogManager.Log(LogCategory.General, LogLevel.Info, $"FenLogger initialized with path: {logFilePath}");
        }

        public static void Log(string message, LogCategory category = LogCategory.General, LogLevel level = LogLevel.Info)
        {
            LogManager.Log(category, level, message);
            EmitStructured(category, level, message);
        }

        public static void Debug(string message, LogCategory category = LogCategory.General) 
        {
            LogManager.Log(category, LogLevel.Debug, message);
            EmitStructured(category, LogLevel.Debug, message);
        }
            
        public static void Info(string message, LogCategory category = LogCategory.General) 
        {
            LogManager.Log(category, LogLevel.Info, message);
            EmitStructured(category, LogLevel.Info, message);
        }
            
        public static void Warn(string message, LogCategory category = LogCategory.General) 
        {
            LogManager.Log(category, LogLevel.Warn, message);
            EmitStructured(category, LogLevel.Warn, message);
        }
            
        public static void Error(string message, LogCategory category = LogCategory.General, Exception ex = null) 
        {
            LogManager.LogError(category, message, ex);
            EmitStructured(category, LogLevel.Error, message, ex);
        }
        
        // --- NEW: Structured Logging ---
        
        private static void EmitStructured(LogCategory category, LogLevel level, string message, Exception ex = null)
        {
            if (!StructuredOutputEnabled && OnStructuredLog == null) return;
            
            var entry = new StructuredLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Category = category.ToString(),
                Message = message,
                Exception = ex?.ToString()
            };
            
            OnStructuredLog?.Invoke(entry);
            
            if (StructuredOutputEnabled)
            {
                try
                {
                    var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
                    Console.WriteLine(json);
                }
                catch { /* Avoid infinite loops if JSON serialization fails */ }
            }
        }
        
        // --- NEW: Performance Metrics ---
        
        /// <summary>
        /// Log a performance metric with name, value, and unit.
        /// </summary>
        public static void LogMetric(string name, double value, string unit = "ms", LogCategory category = LogCategory.Performance)
        {
            var message = $"[METRIC] {name}: {value:F2} {unit}";
            LogManager.Log(category, LogLevel.Info, message);
            
            if (StructuredOutputEnabled || OnStructuredLog != null)
            {
                var entry = new StructuredLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Metric",
                    Category = category.ToString(),
                    Message = message,
                    MetricName = name,
                    MetricValue = value,
                    MetricUnit = unit
                };
                OnStructuredLog?.Invoke(entry);
            }
        }
        
        /// <summary>
        /// Start a timing scope that logs duration on dispose.
        /// Usage: using (FenLogger.TimeScope("LayoutPass")) { ... }
        /// </summary>
        public static TimingScope TimeScope(string operationName, LogCategory category = LogCategory.Performance)
        {
            return new TimingScope(operationName, category);
        }
    }
    
    /// <summary>
    /// Structured log entry for JSON output and external consumers.
    /// </summary>
    public class StructuredLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public string Exception { get; set; }
        public string MetricName { get; set; }
        public double? MetricValue { get; set; }
        public string MetricUnit { get; set; }
    }
    
    /// <summary>
    /// Disposable timing scope for performance measurement.
    /// </summary>
    public class TimingScope : IDisposable
    {
        private readonly string _operationName;
        private readonly LogCategory _category;
        private readonly System.Diagnostics.Stopwatch _sw;
        
        public TimingScope(string operationName, LogCategory category)
        {
            _operationName = operationName;
            _category = category;
            _sw = System.Diagnostics.Stopwatch.StartNew();
        }
        
        public void Dispose()
        {
            _sw.Stop();
            FenLogger.LogMetric(_operationName, _sw.Elapsed.TotalMilliseconds, "ms", _category);
        }
    }
}
