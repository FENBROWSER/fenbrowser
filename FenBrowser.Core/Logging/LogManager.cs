using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
        
        /// <summary>
        /// Event fired when a log entry is added. Can be used by DevTools to show logs.
        /// </summary>
        public static event Action<LogEntry> LogEntryAdded;
        
        private readonly ConcurrentQueue<LogEntry> _memoryBuffer = new ConcurrentQueue<LogEntry>();
        private readonly object _fileLock = new object();
        private int _maxMemoryEntries = 1000;
        private long _maxLogFileBytes = 10L * 1024L * 1024L;
        private int _maxArchivedFiles = 10;
        private string _logFilePath;
        private bool _mirrorStructuredLogs = true;

        public static LogManager Instance => _instance.Value;

        private LogManager()
        {
            // Lazy initialization - log path is set later via InitializeFromSettings()
            _logFilePath = null;
        }
        
        /// <summary>
        /// Initialize logging from BrowserSettings.
        /// Should be called at app startup after settings are loaded.
        /// </summary>
        public static void InitializeFromSettings()
        {
            try
            {
                var settings = BrowserSettings.Instance.Logging;
                var logPath = settings.LogPath;
                
                if (!Directory.Exists(logPath)) 
                    Directory.CreateDirectory(logPath);
                
                Instance._logFilePath = Path.Combine(logPath, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                
                // Write header
                File.WriteAllText(Instance._logFilePath, 
                    $"╔══════════════════════════════════════════════════════════════════╗\n" +
                    $"║  FenBrowser Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}                          ║\n" +
                    $"║  System: {Environment.OSVersion}                                  \n" +
                    $"╚══════════════════════════════════════════════════════════════════╝\n\n");
                
                // Apply settings
                _isEnabled = settings.EnableLogging;
                _enabledCategories = (LogCategory)settings.EnabledCategories;
                _minimumLevel = (LogLevel)settings.MinimumLevel;
                Instance._maxMemoryEntries = Math.Max(100, settings.MemoryBufferSize);
                Instance._maxLogFileBytes = Math.Max(1, settings.MaxLogFileSizeMB) * 1024L * 1024L;
                Instance._maxArchivedFiles = Math.Max(1, settings.MaxArchivedFiles);
                Instance._mirrorStructuredLogs = settings.MirrorStructuredLogs;
                
                System.Diagnostics.Debug.WriteLine($"[LogManager] Initialized at: {Instance._logFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogManager] Failed to initialize: {ex.Message}");
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
        /// LogLevel: Error=0 (most critical) to Trace=4 (most verbose)
        /// We log if: level <= minimumLevel (e.g., Error always logs, Debug only if min is Debug or Trace)
        /// </summary>
        public static bool IsEnabled(LogCategory category, LogLevel level)
        {
            if (!_isEnabled) return false;
            if (!_enabledCategories.HasFlag(category) && category != LogCategory.All) return false;
            return (int)level <= (int)_minimumLevel;
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
                
                // Fire event for DevTools/UI subscribers
                LogEntryAdded?.Invoke(entry);
            }
            catch
            {
                // Never throw from logging
            }
        }

        /// <summary>
        /// Main structured logging entry point for callers that already have a populated LogEntry.
        /// </summary>
        public static void Log(LogEntry entry)
        {
            if (entry == null || !IsEnabled(entry.Category, entry.Level))
            {
                return;
            }

            try
            {
                Instance.WriteToSinks(entry);
                LogEntryAdded?.Invoke(entry);
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

        #region Performance Timing Aggregation
        
        private static readonly ConcurrentDictionary<string, PerfStats> _perfStats = new();
        
        /// <summary>
        /// Log a performance measurement and aggregate stats.
        /// </summary>
        public static void LogPerf(LogCategory category, string operation, long milliseconds)
        {
            // Update aggregated stats
            _perfStats.AddOrUpdate(operation, 
                new PerfStats { Count = 1, TotalMs = milliseconds, MinMs = milliseconds, MaxMs = milliseconds },
                (key, existing) =>
                {
                    existing.Count++;
                    existing.TotalMs += milliseconds;
                    if (milliseconds < existing.MinMs) existing.MinMs = milliseconds;
                    if (milliseconds > existing.MaxMs) existing.MaxMs = milliseconds;
                    return existing;
                });
            
            if (IsEnabled(category, LogLevel.Debug))
            {
                Log(category, LogLevel.Debug, $"[PERF] {operation}: {milliseconds}ms");
            }
        }
        
        /// <summary>
        /// Get aggregated performance statistics.
        /// </summary>
        public static string GetPerfSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Performance Summary ===");
            foreach (var kvp in _perfStats.OrderByDescending(x => x.Value.TotalMs))
            {
                var s = kvp.Value;
                var avg = s.Count > 0 ? s.TotalMs / s.Count : 0;
                sb.AppendLine($"{kvp.Key}: Count={s.Count}, Total={s.TotalMs}ms, Avg={avg}ms, Min={s.MinMs}ms, Max={s.MaxMs}ms");
            }
            return sb.ToString();
        }
        
        /// <summary>
        /// Clear performance statistics.
        /// </summary>
        public static void ClearPerfStats() => _perfStats.Clear();
        
        private class PerfStats
        {
            public int Count;
            public long TotalMs;
            public long MinMs;
            public long MaxMs;
        }
        
        #endregion

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
            ApplyAmbientContext(entry);

            // Memory buffer (for log viewer)
            _memoryBuffer.Enqueue(entry);
            while (_memoryBuffer.Count > _maxMemoryEntries)
            {
                _memoryBuffer.TryDequeue(out _);
            }

            // File sink (JSON or text format)
            if (!UseJsonFormat)
            {
                WriteToFile(entry);
            }

            if (UseJsonFormat || _mirrorStructuredLogs)
            {
                WriteJsonToFile(entry);
            }

            // Log shipping to external service
            if (LogShippingService.Instance.IsEnabled)
            {
                LogShippingService.Instance.Enqueue(entry);
            }

            // Debug output
            System.Diagnostics.Debug.WriteLine(entry.ToString());
            Console.WriteLine(entry.ToString()); // Added for stdout capture
        }

        private static void ApplyAmbientContext(LogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.CorrelationId))
            {
                entry.CorrelationId = LogContext.CurrentCorrelationId;
            }

            if (string.IsNullOrWhiteSpace(entry.Component))
            {
                entry.Component = LogContext.CurrentComponent;
            }

            var scopeData = LogContext.CaptureData();
            if (scopeData == null || scopeData.Count == 0)
            {
                return;
            }

            entry.Data ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in scopeData)
            {
                if (!entry.Data.ContainsKey(pair.Key))
                {
                    entry.Data[pair.Key] = pair.Value;
                }
            }
        }

        private void WriteJsonToFile(LogEntry entry)
        {
            try
            {
                lock (_fileLock)
                {
                    if (string.IsNullOrWhiteSpace(_logFilePath))
                    {
                        return;
                    }

                    var jsonPath = _logFilePath.Replace(".log", ".jsonl");
                    File.AppendAllText(jsonPath, entry.ToJson() + Environment.NewLine);
                    RotateFileIfNeeded(jsonPath, ".jsonl");
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
                    if (string.IsNullOrWhiteSpace(_logFilePath))
                    {
                        return;
                    }

                    File.AppendAllText(_logFilePath, entry.ToString() + Environment.NewLine);
                    RotateFileIfNeeded(_logFilePath, ".log");
                }
            }
            catch
            {
                // Never throw from logging
            }
        }

        private void RotateFileIfNeeded(string path, string extension)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length < _maxLogFileBytes)
            {
                return;
            }

            var archivePath = Path.Combine(
                fileInfo.DirectoryName ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(path)}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}{extension}");
            File.Move(path, archivePath, overwrite: false);
            TrimArchives(fileInfo.DirectoryName ?? string.Empty, fileInfo.Name, extension);
        }

        private void TrimArchives(string directory, string activeFileName, string extension)
        {
            if (string.IsNullOrWhiteSpace(directory) || _maxArchivedFiles <= 0)
            {
                return;
            }

            var stem = Path.GetFileNameWithoutExtension(activeFileName);
            var archivedFiles = Directory.EnumerateFiles(directory, $"{stem}_*{extension}")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ToList();

            foreach (var archivedFile in archivedFiles.Skip(_maxArchivedFiles))
            {
                try
                {
                    archivedFile.Delete();
                }
                catch
                {
                    // Ignore cleanup failures.
                }
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
