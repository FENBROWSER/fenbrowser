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

            var stats = _stats.GetOrAdd(operationName, static name => new OperationStats(name));
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

        /// <summary>
        /// Returns an immutable-by-convention point-in-time copy rather than the live
        /// mutable counter object that RecordTiming is updating on other threads.
        /// </summary>
        public OperationStats GetStats(string operationName)
        {
            if (operationName == null)
                return null;
            return _stats.TryGetValue(operationName, out var stats)
                ? stats.SnapshotCopy()
                : null;
        }

        public IReadOnlyDictionary<string, OperationStats> GetAllStats()
        {
            var snapshot = new Dictionary<string, OperationStats>(_stats.Count, StringComparer.Ordinal);
            foreach (var pair in _stats)
            {
                snapshot[pair.Key] = pair.Value.SnapshotCopy();
            }
            return snapshot;
        }

        public string GetSummaryReport()
        {
            var snapshots = _stats.Values
                .Select(static stats => stats.GetSnapshot())
                .OrderByDescending(static stats => stats.TotalMs)
                .ToArray();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Performance Summary ===");
            sb.AppendLine($"{"Operation",-40} {"Calls",8} {"Min",8} {"Max",8} {"Avg",8} {"Total",10}");
            sb.AppendLine(new string('-', 90));

            foreach (var stat in snapshots)
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
                .Select(static kvp => (kvp.Key, Snapshot: kvp.Value.GetSnapshot()))
                .OrderBy(static item => item.Snapshot.LastAccess)
                .Take(removeCount)
                .Select(static item => item.Key)
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
                if (bytes == long.MinValue)
                    return "-8.0EB";
                return $"-{FormatBytes(-bytes)}";
            }
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
            return $"{bytes / (1024.0 * 1024.0):F2}MB";
        }
    }

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

            _profiler = null;
            profiler.EndScope(_operationName, _startTimestamp, _initialMemory, _trackMemory);
        }
    }

    public sealed class OperationStats
    {
        private readonly object _lock = new();
        private int _callCount;
        private long _totalMs;
        private long _minMs = long.MaxValue;
        private long _maxMs;
        private long _totalMemoryDelta;
        private DateTime _lastAccess;

        public OperationStats(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        private OperationStats(OperationStatsSnapshot snapshot)
        {
            Name = snapshot.Name;
            _callCount = snapshot.CallCount;
            _totalMs = snapshot.TotalMs;
            _minMs = snapshot.MinMs;
            _maxMs = snapshot.MaxMs;
            _totalMemoryDelta = snapshot.TotalMemoryDelta;
            _lastAccess = snapshot.LastAccess;
        }

        public string Name { get; }
        public int CallCount { get { lock (_lock) return _callCount; } }
        public long TotalMs { get { lock (_lock) return _totalMs; } }
        public long MinMs { get { lock (_lock) return _callCount == 0 ? 0 : _minMs; } }
        public long MaxMs { get { lock (_lock) return _maxMs; } }
        public long TotalMemoryDelta { get { lock (_lock) return _totalMemoryDelta; } }
        public DateTime LastAccess { get { lock (_lock) return _lastAccess; } }
        public double AverageMs
        {
            get
            {
                lock (_lock)
                {
                    return _callCount > 0 ? (double)_totalMs / _callCount : 0;
                }
            }
        }

        public void RecordCall(long milliseconds, long memoryDelta)
        {
            lock (_lock)
            {
                _callCount++;
                _totalMs += milliseconds;
                _totalMemoryDelta += memoryDelta;

                if (milliseconds < _minMs) _minMs = milliseconds;
                if (milliseconds > _maxMs) _maxMs = milliseconds;

                _lastAccess = DateTime.UtcNow;
            }
        }

        internal OperationStatsSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                var minMs = _callCount == 0 ? 0 : _minMs;
                return new OperationStatsSnapshot(
                    Name,
                    _callCount,
                    _totalMs,
                    minMs,
                    _maxMs,
                    _totalMemoryDelta,
                    _lastAccess);
            }
        }

        internal OperationStats SnapshotCopy() => new(GetSnapshot());
    }

    internal readonly record struct OperationStatsSnapshot(
        string Name,
        int CallCount,
        long TotalMs,
        long MinMs,
        long MaxMs,
        long TotalMemoryDelta,
        DateTime LastAccess)
    {
        public double AverageMs => CallCount > 0 ? (double)TotalMs / CallCount : 0;
    }
}
