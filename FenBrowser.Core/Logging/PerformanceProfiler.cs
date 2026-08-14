using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Lightweight performance profiler for timing operations. BeginScope uses only
    /// Stopwatch timestamps on the hot path; managed-heap sampling is opt-in because
    /// GC.GetTotalMemory itself is too expensive/noisy for every render or JS scope.
    /// </summary>
    public sealed class PerformanceProfiler
    {
        private static readonly Lazy<PerformanceProfiler> _instance =
            new(() => new PerformanceProfiler());

        public static PerformanceProfiler Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, OperationStats> _stats = new(StringComparer.Ordinal);

        public bool IsEnabled { get; set; } = true;
        public bool LogToDebug { get; set; }
        public bool TrackMemoryDeltas { get; set; }
        public int MaxStatsEntries { get; set; } = 1000;

        private PerformanceProfiler()
        {
        }

        /// <summary>
        /// Start a profiling scope without allocating a Stopwatch, GUID, or active-scope
        /// dictionary entry. The returned value type is intended for normal using-scope
        /// ownership and should not be copied.
        /// </summary>
        public ProfileScope BeginScope(
            string operationName,
            [CallerFilePath] string sourceFile = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
            if (!IsEnabled)
                return default;
            if (string.IsNullOrWhiteSpace(operationName))
                throw new ArgumentException("Operation name is required.", nameof(operationName));

            var initialMemory = TrackMemoryDeltas ? GC.GetTotalMemory(false) : 0L;
            return new ProfileScope(
                this,
                operationName,
                Stopwatch.GetTimestamp(),
                initialMemory,
                TrackMemoryDeltas);
        }

        public void RecordTiming(string operationName, long milliseconds, long? memoryDelta = null)
        {
            if (!IsEnabled)
                return;
            if (string.IsNullOrWhiteSpace(operationName))
                throw new ArgumentException("Operation name is required.", nameof(operationName));

            var stats = _stats.GetOrAdd(operationName, static name => new OperationStats { Name = name });
            stats.RecordCall(milliseconds, memoryDelta ?? 0);

            if (LogToDebug)
            {
                LogManager.Log(
                    LogCategory.Performance,
                    LogLevel.Debug,
                    $"[PERF] {operationName}: {milliseconds}ms" +
                    (memoryDelta.HasValue ? $" (mem: {FormatBytes(memoryDelta.Value)})" : string.Empty));
            }

            var maxEntries = MaxStatsEntries;
            if (maxEntries > 0 && _stats.Count > maxEntries)
                CleanupOldStats(maxEntries);
        }

        public OperationStats GetStats(string operationName)
        {
            if (operationName == null)
                return null;
            _stats.TryGetValue(operationName, out var stats);
            return stats;
        }

        public IReadOnlyDictionary<string, OperationStats> GetAllStats()
        {
            return new Dictionary<string, OperationStats>(_stats);
        }

        public string GetSummaryReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Performance Summary ===");
            sb.AppendLine($"{"Operation",-40} {"Calls",8} {"Min",8} {"Max",8} {"Avg",8} {"Total",10}");
            sb.AppendLine(new string('-', 90));

            foreach (var stat in _stats.Values.OrderByDescending(s => s.TotalMs))
            {
                sb.AppendLine(
                    $"{stat.Name,-40} {stat.CallCount,8} {stat.MinMs,7}ms {stat.MaxMs,7}ms " +
                    $"{stat.AverageMs,7:F1}ms {stat.TotalMs,9}ms");
            }

            return sb.ToString();
        }

        public void Reset()
        {
            _stats.Clear();
        }

        public static long GetCurrentMemory() => GC.GetTotalMemory(false);

        /// <summary>
        /// Explicit diagnostic helper. Never call this from a browser hot path.
        /// </summary>
        public static long GetMemoryAfterGC()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }

        private void CleanupOldStats(int maxEntries)
        {
            var currentCount = _stats.Count;
            if (currentCount <= maxEntries)
                return;

            var removeCount = Math.Max(1, currentCount - maxEntries);
            var toRemove = _stats
                .OrderBy(kvp => kvp.Value.LastAccess)
                .Take(removeCount)
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var key in toRemove)
                _stats.TryRemove(key, out _);
        }

        internal void EndScope(
            string operationName,
            long startTimestamp,
            long initialMemory,
            bool trackMemory)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var elapsedMilliseconds = (long)elapsed.TotalMilliseconds;
            long? memoryDelta = trackMemory
                ? GC.GetTotalMemory(false) - initialMemory
                : null;

            RecordTiming(operationName, elapsedMilliseconds, memoryDelta);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0)
            {
                // Avoid overflowing on long.MinValue.
                if (bytes == long.MinValue)
                    return "-8.0EB";
                return $"-{FormatBytes(-bytes)}";
            }
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
            return $"{bytes / (1024.0 * 1024.0):F2}MB";
        }
    }

    /// <summary>
    /// Allocation-free timing scope. Do not copy an active scope; Dispose is idempotent
    /// for the original value used by a normal C# using statement.
    /// </summary>
    public struct ProfileScope : IDisposable
    {
        private PerformanceProfiler _profiler;
        private string _operationName;
        private long _startTimestamp;
        private long _initialMemory;
        private bool _trackMemory;

        internal ProfileScope(
            PerformanceProfiler profiler,
            string operationName,
            long startTimestamp,
            long initialMemory,
            bool trackMemory)
        {
            _profiler = profiler;
            _operationName = operationName;
            _startTimestamp = startTimestamp;
            _initialMemory = initialMemory;
            _trackMemory = trackMemory;
        }

        public void Dispose()
        {
            var profiler = _profiler;
            if (profiler == null)
                return;

            // Mark the owned value disposed before recording so the same local cannot
            // double-record if Dispose is invoked explicitly and again by a using block.
            _profiler = null;
            profiler.EndScope(_operationName, _startTimestamp, _initialMemory, _trackMemory);
        }
    }

    public class OperationStats
    {
        public string Name { get; set; }
        public int CallCount { get; private set; }
        public long TotalMs { get; private set; }
        public long MinMs { get; private set; } = long.MaxValue;
        public long MaxMs { get; private set; }
        public long TotalMemoryDelta { get; private set; }
        public DateTime LastAccess { get; private set; }

        private readonly object _lock = new();

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
