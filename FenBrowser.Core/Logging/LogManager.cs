using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Central logging manager with category-based filtering and multiple output sinks.
    /// Thread-safe singleton implementation.
    /// </summary>
    public sealed class LogManager
    {
        private static readonly Lazy<LogManager> _instance = new Lazy<LogManager>(() => new LogManager());
        private static LogCategory _enabledCategories = LogCategory.Errors;
        private static LogLevel _minimumLevel = LogLevel.Info;
        private static bool _isEnabled = false;
        
        /// <summary>
        /// Enable JSON structured logging output (writes to .jsonl file)
        /// </summary>
        public static bool UseJsonFormat { get; set; } = false;
        
        private readonly ConcurrentQueue<LogEntry> _memoryBuffer = new ConcurrentQueue<LogEntry>();
        private readonly object _fileLock = new object();
        private readonly int _maxMemoryEntries = 1000;
        private string _logFilePath;

        public static LogManager Instance => _instance.Value;

        private LogManager()
        {
            try 
            {
                // Hardcode path for reliability in this environment
                var fenBrowserPath = @"C:\Users\udayk\Videos\FENBROWSER\logs";
                if (!Directory.Exists(fenBrowserPath)) Directory.CreateDirectory(fenBrowserPath);
                
                _logFilePath = Path.Combine(fenBrowserPath, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                
                // Write Header
                File.WriteAllText(_logFilePath, $"[LogManager] Initialized at {DateTime.Now:O}\n[LogManager] System: {Environment.OSVersion}\n--------------------------------------------------\n");
            }
            catch (Exception ex)
            {
                // Fallback if logging initialization fails
                System.Diagnostics.Debug.WriteLine($"ERROR: LogManager failed to initialize file sink: {ex.Message}");
                _logFilePath = null; // Prevent further file operations if initialization failed
            }
        }

        /// <summary>
        /// Initialize logging with settings.
        /// </summary>
        public static void Initialize(bool enabled, LogCategory categories, LogLevel minLevel)
        {
            _isEnabled = enabled;
            _enabledCategories = categories;
            _minimumLevel = minLevel;
        }

        public void SetLogFilePath(string path)
        {
            lock (_fileLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                        
                    _logFilePath = path;
                    File.AppendAllText(_logFilePath, $"\n[LogManager] Log path switched to: {path}\n");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to set log path: {ex}");
                }
            }
        }

        /// <summary>
        /// Check if a category and level combination is enabled.
        /// </summary>
        public static bool IsEnabled(LogCategory category, LogLevel level)
        {
            if (!_isEnabled) return false;
            if (!_enabledCategories.HasFlag(category)) return false;
            return level <= _minimumLevel;
        }

        /// <summary>
        /// Main logging method - all other methods funnel through this.
        /// </summary>
        public static void Log(LogCategory category, LogLevel level, string message, Exception exception = null)
        {
            if (!IsEnabled(category, level)) return;

            try
            {
                var entry = new LogEntry
                {
                    Category = category,
                    Level = level,
                    Message = message ?? string.Empty,
                    Exception = exception
                };

                Instance.WriteToSinks(entry);
            }
            catch
            {
                // Never throw from logging
            }
        }

        /// <summary>
        /// Log an error with exception details.
        /// </summary>
        public static void LogError(LogCategory category, string message, Exception exception = null)
        {
            Log(category, LogLevel.Error, message, exception);
        }

        /// <summary>
        /// Log a performance measurement.
        /// </summary>
        public static void LogPerf(LogCategory category, string operation, long milliseconds)
        {
            if (IsEnabled(category, LogLevel.Debug))
            {
                Log(category, LogLevel.Debug, $"[PERF] {operation}: {milliseconds}ms");
            }
        }

        #region Feature Failure Tracking

        /// <summary>
        /// Log an unsupported HTML element and track it.
        /// </summary>
        public static void LogHtmlFailure(string tagName, string reason = null, string suggestion = null)
        {
            EngineCapabilities.LogUnsupportedHtml(tagName, reason, suggestion);
        }

        /// <summary>
        /// Log an unsupported CSS property and track it.
        /// </summary>
        public static void LogCssFailure(string property, string value = null, string reason = null)
        {
            EngineCapabilities.LogUnsupportedCss(property, value, reason);
        }

        /// <summary>
        /// Log an unsupported JavaScript API and track it.
        /// </summary>
        public static void LogJsFailure(string api, string method = null, string reason = null)
        {
            EngineCapabilities.LogUnsupportedJs(api, method, reason);
        }

        /// <summary>
        /// Get a summary of all feature failures.
        /// </summary>
        public static string GetFailureSummary()
        {
            return EngineCapabilities.GetFailureSummary();
        }

        /// <summary>
        /// Log an informational message about a feature.
        /// </summary>
        public static void LogFeature(LogCategory category, string message)
        {
            Log(category, LogLevel.Info, message);
        }

        #endregion

        /// <summary>
        /// Get recent log entries from memory buffer.
        /// </summary>
        public static List<LogEntry> GetRecentLogs(int count = 1000)
        {
            return Instance._memoryBuffer.Reverse().Take(count).Reverse().ToList();
        }

        /// <summary>
        /// Clear all logs from memory buffer and optionally delete log file.
        /// </summary>
        public static void ClearLogs(bool deleteFile = false)
        {
            Instance._memoryBuffer.Clear();
            
            if (deleteFile)
            {
                try
                {
                    lock (Instance._fileLock)
                    {
                        if (File.Exists(Instance._logFilePath))
                        {
                            File.Delete(Instance._logFilePath);
                        }
                    }
                }
                catch { }
            }
        }

        private void WriteToSinks(LogEntry entry)
        {
            // Memory buffer (for log viewer)
            _memoryBuffer.Enqueue(entry);
            while (_memoryBuffer.Count > _maxMemoryEntries)
            {
                _memoryBuffer.TryDequeue(out _);
            }

            // File sink (JSON or text format)
            if (UseJsonFormat)
                WriteJsonToFile(entry);
            else
                WriteToFile(entry);

            // Log shipping to external service
            if (LogShippingService.Instance.IsEnabled)
            {
                LogShippingService.Instance.Enqueue(entry);
            }

            // Debug output
            System.Diagnostics.Debug.WriteLine(entry.ToString());
        }

        private void WriteJsonToFile(LogEntry entry)
        {
            try
            {
                lock (_fileLock)
                {
                    var jsonPath = _logFilePath?.Replace(".log", ".jsonl") ?? _logFilePath;
                    File.AppendAllText(jsonPath, entry.ToJson() + Environment.NewLine);
                    
                    // Rotate if file gets too large (default 10MB)
                    var fileInfo = new FileInfo(jsonPath);
                    if (fileInfo.Exists && fileInfo.Length > 10 * 1024 * 1024)
                    {
                        var archivePath = jsonPath.Replace(".jsonl", $"_{DateTime.Now:HHmmss}.jsonl");
                        File.Move(jsonPath, archivePath);
                    }
                }
            }
            catch
            {
                // Never throw from logging
            }
        }

        private void WriteToFile(LogEntry entry)
        {
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_logFilePath, entry.ToString() + Environment.NewLine);
                    
                    // Rotate if file gets too large (default 10MB)
                    var fileInfo = new FileInfo(_logFilePath);
                    if (fileInfo.Exists && fileInfo.Length > 10 * 1024 * 1024)
                    {
                        var archivePath = _logFilePath.Replace(".log", $"_{DateTime.Now:HHmmss}.log");
                        File.Move(_logFilePath, archivePath);
                    }
                }
            }
            catch
            {
                // Never throw from logging
            }
        }

        /// <summary>
        /// Get the current log file path.
        /// </summary>
        public static string GetLogFilePath()
        {
            return Instance._logFilePath;
        }
    }
}
