using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Runtime.CompilerServices;
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

        public static IDisposable BeginScope(
            string component = null,
            string correlationId = null,
            IReadOnlyDictionary<string, object> data = null)
        {
            return LogContext.Push(component, correlationId, data);
        }

        public static IDisposable BeginCorrelationScope(
            string correlationId = null,
            string component = null,
            IReadOnlyDictionary<string, object> data = null)
        {
            return LogContext.Push(component, correlationId, data);
        }

        public static void Log(
            string message,
            LogCategory category = LogCategory.General,
            LogLevel level = LogLevel.Info,
            Exception ex = null,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0)
        {
            var entry = CreateEntry(category, level, message, ex, memberName, sourceFile, sourceLine);
            LogManager.Log(entry);
            EmitStructured(entry);
        }

        public static void Debug(
            string message,
            LogCategory category = LogCategory.General,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0) 
        {
            Log(message, category, LogLevel.Debug, null, memberName, sourceFile, sourceLine);
        }
            
        public static void Info(
            string message,
            LogCategory category = LogCategory.General,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0) 
        {
            Log(message, category, LogLevel.Info, null, memberName, sourceFile, sourceLine);
        }
            
        public static void Warn(
            string message,
            LogCategory category = LogCategory.General,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0) 
        {
            Log(message, category, LogLevel.Warn, null, memberName, sourceFile, sourceLine);
        }
            
        public static void Error(
            string message,
            LogCategory category = LogCategory.General,
            Exception ex = null,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLine = 0) 
        {
            Log(message, category, LogLevel.Error, ex, memberName, sourceFile, sourceLine);
        }
        
        // --- NEW: Structured Logging ---
        
        private static LogEntry CreateEntry(
            LogCategory category,
            LogLevel level,
            string message,
            Exception ex,
            string memberName,
            string sourceFile,
            int sourceLine)
        {
            return new LogEntry
            {
                Category = category,
                Level = level,
                Message = message ?? string.Empty,
                Exception = ex,
                MethodName = memberName,
                SourceFile = string.IsNullOrWhiteSpace(sourceFile) ? null : System.IO.Path.GetFileName(sourceFile),
                SourceLine = sourceLine
            };
        }

        private static void EmitStructured(LogEntry logEntry)
        {
            if (!StructuredOutputEnabled && OnStructuredLog == null) return;
            
            var structuredEntry = new StructuredLogEntry
            {
                Timestamp = logEntry.Timestamp,
                Level = logEntry.Level.ToString(),
                Category = logEntry.Category.ToString(),
                Message = logEntry.Message,
                Exception = logEntry.Exception?.ToString()
            };
            
            OnStructuredLog?.Invoke(structuredEntry);
            
            if (StructuredOutputEnabled)
            {
                try
                {
                    var json = JsonSerializer.Serialize(structuredEntry, new JsonSerializerOptions { WriteIndented = false });
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
            var entry = new LogEntry
            {
                Category = category,
                Level = LogLevel.Info,
                Message = message
            };
            entry.WithData("metricName", name);
            entry.WithData("metricValue", value);
            entry.WithData("metricUnit", unit);
            LogManager.Log(entry);
            
            if (StructuredOutputEnabled || OnStructuredLog != null)
            {
                var structuredEntry = new StructuredLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Metric",
                    Category = category.ToString(),
                    Message = message,
                    MetricName = name,
                    MetricValue = value,
                    MetricUnit = unit
                };
                OnStructuredLog?.Invoke(structuredEntry);
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
