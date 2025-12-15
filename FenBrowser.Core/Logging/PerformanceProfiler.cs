using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Performance profiler for timing operations and tracking resource usage.
    /// Thread-safe and designed for use across async boundaries.
    /// </summary>
    public sealed class PerformanceProfiler
    {
        private static readonly Lazy<PerformanceProfiler> _instance = 
            new Lazy<PerformanceProfiler>(() => new PerformanceProfiler());
        
        public static PerformanceProfiler Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, OperationStats> _stats;
        private readonly ConcurrentDictionary<string, Stopwatch> _activeScopes;
        private readonly object _lock = new object();
        
        public bool IsEnabled { get; set; } = true;
        public bool LogToDebug { get; set; } = false;
        public int MaxStatsEntries { get; set; } = 1000;

        private PerformanceProfiler()
        {
            _stats = new ConcurrentDictionary<string, OperationStats>();
            _activeScopes = new ConcurrentDictionary<string, Stopwatch>();
        }

        /// <summary>
        /// Start a profiling scope. Returns IDisposable for automatic timing.
        /// Usage: using (PerformanceProfiler.Instance.BeginScope("MyOperation")) { ... }
        /// </summary>
        public ProfileScope BeginScope(
            string operationName,
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
            if (!IsEnabled) return new ProfileScope(null, null, 0, false);
            
            var scopeId = $"{operationName}_{Guid.NewGuid():N}";
            var sw = Stopwatch.StartNew();
            _activeScopes[scopeId] = sw;
            
            var initialMemory = GC.GetTotalMemory(false);
            
            return new ProfileScope(this, operationName, initialMemory, true, scopeId, sourceFile, sourceLineNumber, memberName);
        }

        /// <summary>
        /// Manually record a timing measurement
        /// </summary>
        public void RecordTiming(string operationName, long milliseconds, long? memoryDelta = null)
        {
            if (!IsEnabled) return;

            var stats = _stats.GetOrAdd(operationName, _ => new OperationStats { Name = operationName });
            stats.RecordCall(milliseconds, memoryDelta ?? 0);

            if (LogToDebug)
            {
                LogManager.Log(LogCategory.Performance, LogLevel.Debug, 
                    $"[PERF] {operationName}: {milliseconds}ms" + 
                    (memoryDelta.HasValue ? $" (mem: {FormatBytes(memoryDelta.Value)})" : ""));
            }

            // Cleanup if too many entries
            if (_stats.Count > MaxStatsEntries)
            {
                CleanupOldStats();
            }
        }

        /// <summary>
        /// Get statistics for an operation
        /// </summary>
        public OperationStats GetStats(string operationName)
        {
            _stats.TryGetValue(operationName, out var stats);
            return stats;
        }

        /// <summary>
        /// Get all recorded statistics
        /// </summary>
        public IReadOnlyDictionary<string, OperationStats> GetAllStats()
        {
            return new Dictionary<string, OperationStats>(_stats);
        }

        /// <summary>
        /// Get a summary report of all profiled operations
        /// </summary>
        public string GetSummaryReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Performance Summary ===");
            sb.AppendLine($"{"Operation",-40} {"Calls",8} {"Min",8} {"Max",8} {"Avg",8} {"Total",10}");
            sb.AppendLine(new string('-', 90));

            foreach (var stat in _stats.Values.OrderByDescending(s => s.TotalMs))
            {
                sb.AppendLine($"{stat.Name,-40} {stat.CallCount,8} {stat.MinMs,7}ms {stat.MaxMs,7}ms {stat.AverageMs,7:F1}ms {stat.TotalMs,9}ms");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Reset all statistics
        /// </summary>
        public void Reset()
        {
            _stats.Clear();
            _activeScopes.Clear();
        }

        /// <summary>
        /// Get current memory usage
        /// </summary>
        public static long GetCurrentMemory() => GC.GetTotalMemory(false);

        /// <summary>
        /// Force garbage collection and get memory (useful for accurate measurements)
        /// </summary>
        public static long GetMemoryAfterGC()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }

        private void CleanupOldStats()
        {
            // Remove least-used entries
            var toRemove = _stats
                .OrderBy(kvp => kvp.Value.LastAccess)
                .Take(_stats.Count / 4)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _stats.TryRemove(key, out _);
            }
        }

        internal void EndScope(string scopeId, string operationName, long initialMemory, 
            string sourceFile, int sourceLineNumber, string memberName)
        {
            if (_activeScopes.TryRemove(scopeId, out var sw))
            {
                sw.Stop();
                var memoryDelta = GC.GetTotalMemory(false) - initialMemory;
                RecordTiming(operationName, sw.ElapsedMilliseconds, memoryDelta);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return $"-{FormatBytes(-bytes)}";
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
            return $"{bytes / (1024.0 * 1024.0):F2}MB";
        }
    }

    /// <summary>
    /// Disposable scope for automatic timing measurement
    /// </summary>
    public readonly struct ProfileScope : IDisposable
    {
        private readonly PerformanceProfiler _profiler;
        private readonly string _operationName;
        private readonly long _initialMemory;
        private readonly bool _isEnabled;
        private readonly string _scopeId;
        private readonly string _sourceFile;
        private readonly int _sourceLineNumber;
        private readonly string _memberName;

        internal ProfileScope(PerformanceProfiler profiler, string operationName, 
            long initialMemory, bool isEnabled, string scopeId = null,
            string sourceFile = "", int sourceLineNumber = 0, string memberName = "")
        {
            _profiler = profiler;
            _operationName = operationName;
            _initialMemory = initialMemory;
            _isEnabled = isEnabled;
            _scopeId = scopeId;
            _sourceFile = sourceFile;
            _sourceLineNumber = sourceLineNumber;
            _memberName = memberName;
        }

        public void Dispose()
        {
            if (_isEnabled && _profiler != null && _scopeId != null)
            {
                _profiler.EndScope(_scopeId, _operationName, _initialMemory, 
                    _sourceFile, _sourceLineNumber, _memberName);
            }
        }
    }

    /// <summary>
    /// Statistics for a profiled operation
    /// </summary>
    public class OperationStats
    {
        public string Name { get; set; }
        public int CallCount { get; private set; }
        public long TotalMs { get; private set; }
        public long MinMs { get; private set; } = long.MaxValue;
        public long MaxMs { get; private set; }
        public long TotalMemoryDelta { get; private set; }
        public DateTime LastAccess { get; private set; }

        private readonly object _lock = new object();

        public double AverageMs => CallCount > 0 ? (double)TotalMs / CallCount : 0;

        public void RecordCall(long milliseconds, long memoryDelta)
        {
            lock (_lock)
            {
                CallCount++;
                TotalMs += milliseconds;
                TotalMemoryDelta += memoryDelta;
                
                if (milliseconds < MinMs) MinMs = milliseconds;
                if (milliseconds > MaxMs) MaxMs = milliseconds;
                
                LastAccess = DateTime.UtcNow;
            }
        }
    }
}
