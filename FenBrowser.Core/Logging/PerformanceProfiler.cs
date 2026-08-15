using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Lightweight performance profiler for timing operations. BeginScope uses only
    /// Stopwatch timestamps on the hot path; managed-heap sampling is opt-in because
    /// GC.GetTotalMemory itself is too expensive/noisy for every render or JS scope.
    /// </summary>
    public sealed class PerformanceProfiler
    {
        private const int DefaultMaxStatsEntries = 1000;
        private const int AbsoluteMaxStatsEntries = 10_000;
        private const int MaxOperationNameChars = 256;
        private const int LongNamePrefixChars = 192;

        private static readonly Lazy<PerformanceProfiler> _instance =
            new(() => new PerformanceProfiler());

        public static PerformanceProfiler Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, OperationStats> _stats = new(StringComparer.Ordinal);
        private int _maxStatsEntries = DefaultMaxStatsEntries;
        private int _cleanupInProgress;

        public bool IsEnabled { get; set; } = true;
        public bool LogToDebug { get; set; }
        public bool TrackMemoryDeltas { get; set; }

        public int MaxStatsEntries
        {
            get => Volatile.Read(ref _maxStatsEntries);
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Profiler cardinality limit must be positive.");

                Volatile.Write(ref _maxStatsEntries, Math.Min(value, AbsoluteMaxStatsEntries));
                CleanupOldStats(Volatile.Read(ref _maxStatsEntries));
            }
        }

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

            var normalizedName = NormalizeOperationName(operationName);
            var trackMemory = TrackMemoryDeltas;
            var initialMemory = trackMemory ? GC.GetTotalMemory(false) : 0L;
            return new ProfileScope(
                this,
                normalizedName,
                Stopwatch.GetTimestamp(),
                initialMemory,
                trackMemory);
        }

        public void RecordTiming(string operationName, long milliseconds, long? memoryDelta = null)
        {
            if (!IsEnabled)
                return;
            if (milliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(milliseconds), "Timing duration cannot be negative.");

            var normalizedName = NormalizeOperationName(operationName);
            var stats = _stats.GetOrAdd(normalizedName, static name => new OperationStats(name));
            stats.RecordCall(milliseconds, memoryDelta ?? 0);

            if (LogToDebug)
            {
                LogManager.Log(
                    LogCategory.Performance,
                    LogLevel.Debug,
                    $"[PERF] {NormalizeLogField(normalizedName)}: {milliseconds}ms" +
                    (memoryDelta.HasValue ? $" (mem: {FormatBytes(memoryDelta.Value)})" : string.Empty));
            }

            var maxEntries = MaxStatsEntries;
            if (_stats.Count > maxEntries)
                CleanupOldStats(maxEntries);
        }

        /// <summary>
        /// Returns an immutable-by-convention point-in-time copy rather than the live
        /// mutable counter object that RecordTiming is updating on other threads.
        /// </summary>
        public OperationStats GetStats(string operationName)
        {
            if (string.IsNullOrWhiteSpace(operationName))
                return null;

            var normalizedName = NormalizeOperationName(operationName);
            return _stats.TryGetValue(normalizedName, out var stats)
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

            var sb = new StringBuilder();
            sb.AppendLine("=== Performance Summary ===");
            sb.AppendLine($"{"Operation",-40} {"Calls",8} {"Min",8} {"Max",8} {"Avg",8} {"Total",10}");
            sb.AppendLine(new string('-', 90));

            foreach (var stat in snapshots)
            {
                var displayName = stat.Name.Length <= 40 ? stat.Name : stat.Name[..37] + "...";
                sb.AppendLine(
                    $"{displayName,-40} {stat.CallCount,8} {stat.MinMs,7}ms {stat.MaxMs,7}ms " +
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
            if (_stats.Count <= maxEntries || Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) != 0)
                return;

            try
            {
                var currentCount = _stats.Count;
                if (currentCount <= maxEntries)
                    return;

                // Remove a little extra headroom so high-cardinality concurrent callers
                // do not immediately force another O(n log n) cleanup pass.
                var targetCount = Math.Max(1, (int)(maxEntries * 0.9));
                var removeCount = Math.Max(1, currentCount - targetCount);
                var toRemove = _stats
                    .Select(static kvp => (kvp.Key, Snapshot: kvp.Value.GetSnapshot()))
                    .OrderBy(static item => item.Snapshot.LastAccess)
                    .Take(removeCount)
                    .Select(static item => item.Key)
                    .ToArray();

                foreach (var key in toRemove)
                    _stats.TryRemove(key, out _);
            }
            finally
            {
                Volatile.Write(ref _cleanupInProgress, 0);
            }
        }

        internal void EndScope(
            string operationName,
            long startTimestamp,
            long initialMemory,
            bool trackMemory)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var elapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds);
            long? memoryDelta = trackMemory
                ? GC.GetTotalMemory(false) - initialMemory
                : null;

            RecordTiming(operationName, elapsedMilliseconds, memoryDelta);
        }

        private static string NormalizeOperationName(string operationName)
        {
            if (string.IsNullOrWhiteSpace(operationName))
                throw new ArgumentException("Operation name is required.", nameof(operationName));

            var trimmed = operationName.Trim();
            if (trimmed.Length <= MaxOperationNameChars)
                return trimmed;

            // Preserve human-readable context while ensuring two long names with the
            // same prefix do not collapse into one metric series.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).AsSpan(0, 16).ToString();
            return trimmed[..LongNamePrefixChars] + "#" + hash;
        }

        private static string NormalizeLogField(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\0", "\\0", StringComparison.Ordinal);
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
        private long _callCount;
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
        public long CallCount { get { lock (_lock) return _callCount; } }
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
            if (milliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(milliseconds));

            lock (_lock)
            {
                _callCount = SaturatingIncrement(_callCount);
                _totalMs = SaturatingAdd(_totalMs, milliseconds);
                _totalMemoryDelta = SaturatingAddSigned(_totalMemoryDelta, memoryDelta);

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

        private static long SaturatingIncrement(long value) =>
            value == long.MaxValue ? long.MaxValue : value + 1;

        private static long SaturatingAdd(long left, long right)
        {
            if (right <= 0)
                return left;
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private static long SaturatingAddSigned(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right)
                return long.MaxValue;
            if (right < 0 && left < long.MinValue - right)
                return long.MinValue;
            return left + right;
        }
    }

    internal readonly record struct OperationStatsSnapshot(
        string Name,
        long CallCount,
        long TotalMs,
        long MinMs,
        long MaxMs,
        long TotalMemoryDelta,
        DateTime LastAccess)
    {
        public double AverageMs => CallCount > 0 ? (double)TotalMs / CallCount : 0;
    }
}
