using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core
{
    /// <summary>
    /// Cache statistics for monitoring.
    /// </summary>
    public class CacheStatistics
    {
        public long TotalMemoryBytes { get; set; }
        public int TextCacheCount { get; set; }
        public int ImageCacheCount { get; set; }
        public int DiskCacheCount { get; set; }
        public int TabPartitions { get; set; }
        public int SuspendedTabs { get; set; }
        public DateTime LastEviction { get; set; }
        public long EvictedBytesTotal { get; set; }
    }

    /// <summary>
    /// Centralized cache management for FenBrowser with memory limits,
    /// LRU eviction, and per-tab partitioning for suspension support.
    /// </summary>
    public sealed class CacheManager : IDisposable
    {
        private static readonly Lazy<CacheManager> _instance =
            new(() => new CacheManager());

        public static CacheManager Instance => _instance.Value;

        private readonly ConcurrentDictionary<int, TabCachePartition> _tabPartitions = new();
        private long _totalMemoryBytes;
        private long _evictedBytesTotal;
        private long _lastEvictionUtcTicks = DateTime.MinValue.Ticks;
        private int _disposed;

        private readonly Timer _evictionTimer;
        private readonly object _evictionLock = new();

        private CacheManager()
        {
            _evictionTimer = new Timer(
                EvictionTimerCallback,
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
        }

        public long CurrentMemoryUsage => Interlocked.Read(ref _totalMemoryBytes);

        public TabCachePartition GetOrCreateTabPartition(int tabId)
        {
            ThrowIfDisposed();
            return _tabPartitions.GetOrAdd(tabId, id => new TabCachePartition(id, this));
        }

        public void DestroyTabPartition(int tabId)
        {
            if (_tabPartitions.TryRemove(tabId, out var partition))
            {
                var bytes = partition.Clear();
                ReleaseTrackedMemory(bytes, "destroy partition");
                EngineLogCompat.Debug(
                    $"[CacheManager] Destroyed partition for tab {tabId}, freed {bytes / 1024}KB",
                    LogCategory.General);
            }
        }

        public void SuspendTabPartition(int tabId)
        {
            if (_tabPartitions.TryGetValue(tabId, out var partition))
            {
                var freedBytes = partition.Suspend();
                ReleaseTrackedMemory(freedBytes, "suspend partition");
                EngineLogCompat.Info(
                    $"[CacheManager] Suspended tab {tabId}, freed {freedBytes / 1024}KB",
                    LogCategory.General);
            }
        }

        public void ResumeTabPartition(int tabId)
        {
            if (_tabPartitions.TryGetValue(tabId, out var partition))
            {
                partition.Resume();
                EngineLogCompat.Debug($"[CacheManager] Resumed tab {tabId}", LogCategory.General);
            }
        }

        public void TrackMemoryAllocation(long bytes)
        {
            if (bytes <= 0 || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var current = Interlocked.Add(ref _totalMemoryBytes, bytes);
            var config = NetworkConfiguration.Instance;
            var total = SaturatingAdd(config.MaxImageCacheBytes, config.MaxTextCacheBytes);
            if (total > 0 && current > total * 0.9)
            {
                EnqueueEviction();
            }
        }

        public void TrackMemoryDeallocation(long bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            ReleaseTrackedMemory(bytes, "explicit deallocation");
        }

        public void ClearAll()
        {
            foreach (var pair in _tabPartitions)
            {
                if (_tabPartitions.TryRemove(pair.Key, out var partition))
                {
                    partition.Clear();
                }
            }

            Interlocked.Exchange(ref _totalMemoryBytes, 0);
            EngineLogCompat.Info("[CacheManager] All caches cleared", LogCategory.General);
        }

        public void Trim(long targetBytes)
        {
            targetBytes = Math.Max(0, targetBytes);
            var current = CurrentMemoryUsage;
            if (current <= targetBytes)
            {
                return;
            }

            var bytesToFree = current - targetBytes;
            var freedTotal = 0L;
            var partitions = _tabPartitions.Values
                .OrderBy(p => p.LastAccessTime)
                .ToList();

            foreach (var partition in partitions)
            {
                if (freedTotal >= bytesToFree)
                {
                    break;
                }

                var freed = partition.EvictOldest(bytesToFree - freedTotal);
                freedTotal = SaturatingAdd(freedTotal, freed);
            }

            ReleaseTrackedMemory(freedTotal, "trim");
            Interlocked.Add(ref _evictedBytesTotal, freedTotal);
            Interlocked.Exchange(ref _lastEvictionUtcTicks, DateTime.UtcNow.Ticks);

            EngineLogCompat.Info($"[CacheManager] Trimmed {freedTotal / 1024}KB", LogCategory.General);
        }

        public CacheStatistics GetStatistics()
        {
            var lastEvictionTicks = Interlocked.Read(ref _lastEvictionUtcTicks);
            var stats = new CacheStatistics
            {
                TotalMemoryBytes = CurrentMemoryUsage,
                TabPartitions = _tabPartitions.Count,
                SuspendedTabs = _tabPartitions.Count(p => p.Value.IsSuspended),
                LastEviction = new DateTime(lastEvictionTicks, DateTimeKind.Utc),
                EvictedBytesTotal = Interlocked.Read(ref _evictedBytesTotal)
            };

            foreach (var partition in _tabPartitions.Values)
            {
                var partitionStats = partition.GetStats();
                stats.TextCacheCount += partitionStats.textCount;
                stats.ImageCacheCount += partitionStats.imageCount;
            }

            return stats;
        }

        private void EnqueueEviction()
        {
            if (Volatile.Read(ref _disposed) != 0 || !Monitor.TryEnter(_evictionLock))
            {
                return;
            }

            try
            {
                var config = NetworkConfiguration.Instance;
                var configuredMaximum = SaturatingAdd(config.MaxImageCacheBytes, config.MaxTextCacheBytes);
                if (configuredMaximum <= 0)
                {
                    return;
                }

                var target = (long)(configuredMaximum * 0.75);
                Trim(target);
            }
            finally
            {
                Monitor.Exit(_evictionLock);
            }
        }

        private void EvictionTimerCallback(object state)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var config = NetworkConfiguration.Instance;
            var maxMemory = SaturatingAdd(config.MaxImageCacheBytes, config.MaxTextCacheBytes);
            if (maxMemory > 0 && CurrentMemoryUsage > maxMemory * 0.8)
            {
                EnqueueEviction();
            }
        }

        private void ReleaseTrackedMemory(long bytes, string reason)
        {
            if (bytes <= 0)
            {
                return;
            }

            while (true)
            {
                var observed = Interlocked.Read(ref _totalMemoryBytes);
                if (observed <= 0)
                {
                    if (observed < 0)
                    {
                        Interlocked.CompareExchange(ref _totalMemoryBytes, 0, observed);
                    }
                    return;
                }

                var next = bytes >= observed ? 0 : observed - bytes;
                if (Interlocked.CompareExchange(ref _totalMemoryBytes, next, observed) == observed)
                {
                    if (bytes > observed)
                    {
                        EngineLogCompat.Warn(
                            $"[CacheManager] Accounting over-release prevented during {reason}: requested={bytes}, tracked={observed}",
                            LogCategory.General);
                    }
                    return;
                }
            }
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (left <= 0)
            {
                return Math.Max(0, right);
            }
            if (right <= 0)
            {
                return left;
            }
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(CacheManager));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _evictionTimer.Dispose();
            ClearAll();
        }
    }

    /// <summary>
    /// Per-tab cache partition for isolation and suspension support.
    /// All accounting-changing operations are serialized by the partition gate so
    /// clear/suspend cannot race an allocation and corrupt global memory totals.
    /// </summary>
    public class TabCachePartition
    {
        private readonly int _tabId;
        private readonly CacheManager _manager;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, TextCacheEntry> _textCache = new();

        private int _imageCount;
        private long _memoryBytes;
        private long _lastAccessUtcTicks = DateTime.UtcNow.Ticks;
        private int _isSuspended;
        private string _suspendedUrl;
        private double _suspendedScrollY;

        public DateTime LastAccessTime => new(
            Interlocked.Read(ref _lastAccessUtcTicks),
            DateTimeKind.Utc);

        public bool IsSuspended => Volatile.Read(ref _isSuspended) != 0;

        public string SuspendedUrl
        {
            get
            {
                lock (_gate)
                {
                    return _suspendedUrl;
                }
            }
        }

        public double SuspendedScrollY
        {
            get
            {
                lock (_gate)
                {
                    return _suspendedScrollY;
                }
            }
        }

        public TabCachePartition(int tabId, CacheManager manager)
        {
            _tabId = tabId;
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        public void CacheText(string url, string content)
        {
            if (string.IsNullOrWhiteSpace(url) || content == null)
            {
                return;
            }

            var bytes = (long)content.Length * sizeof(char);
            var nowTicks = DateTime.UtcNow.Ticks;
            long delta;

            lock (_gate)
            {
                if (_isSuspended != 0)
                {
                    return;
                }

                delta = bytes;
                if (_textCache.TryGetValue(url, out var previous))
                {
                    delta -= previous.ByteSize;
                }

                _textCache[url] = new TextCacheEntry
                {
                    Content = content,
                    ByteSize = bytes,
                    LastAccessUtcTicks = nowTicks
                };
                _memoryBytes = Math.Max(0, SaturatingAddSigned(_memoryBytes, delta));
                _lastAccessUtcTicks = nowTicks;
            }

            if (delta > 0)
            {
                _manager.TrackMemoryAllocation(delta);
            }
            else if (delta < 0)
            {
                _manager.TrackMemoryDeallocation(-delta);
            }
        }

        public string GetText(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            lock (_gate)
            {
                if (_isSuspended != 0 || !_textCache.TryGetValue(url, out var entry))
                {
                    return null;
                }

                var nowTicks = DateTime.UtcNow.Ticks;
                entry.LastAccessUtcTicks = nowTicks;
                _lastAccessUtcTicks = nowTicks;
                return entry.Content;
            }
        }

        public void TrackImage(long bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            var accepted = false;
            lock (_gate)
            {
                if (_isSuspended == 0)
                {
                    _imageCount++;
                    _memoryBytes = SaturatingAddPositive(_memoryBytes, bytes);
                    _lastAccessUtcTicks = DateTime.UtcNow.Ticks;
                    accepted = true;
                }
            }

            // Keep the manager callback outside the partition lock. Eviction takes
            // manager -> partition locks; doing this callback under _gate would create
            // the reverse order and a cross-thread deadlock opportunity.
            if (accepted)
            {
                _manager.TrackMemoryAllocation(bytes);
            }
        }

        public long Suspend()
        {
            lock (_gate)
            {
                if (_isSuspended != 0)
                {
                    return 0;
                }

                _isSuspended = 1;
                return ClearLocked();
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                _isSuspended = 0;
                _lastAccessUtcTicks = DateTime.UtcNow.Ticks;
            }
        }

        public void StoreSuspensionState(string url, double scrollY)
        {
            lock (_gate)
            {
                _suspendedUrl = url;
                _suspendedScrollY = scrollY;
            }
        }

        public long Clear()
        {
            lock (_gate)
            {
                return ClearLocked();
            }
        }

        public long EvictOldest(long targetBytes)
        {
            if (targetBytes <= 0)
            {
                return 0;
            }

            lock (_gate)
            {
                var freed = 0L;
                var oldest = _textCache
                    .OrderBy(x => x.Value.LastAccessUtcTicks)
                    .ToList();

                foreach (var item in oldest)
                {
                    if (freed >= targetBytes)
                    {
                        break;
                    }

                    if (_textCache.TryRemove(item.Key, out var entry))
                    {
                        freed = SaturatingAddPositive(freed, entry.ByteSize);
                        _memoryBytes = Math.Max(0, _memoryBytes - entry.ByteSize);
                    }
                }

                return freed;
            }
        }

        public (int textCount, int imageCount, long memoryBytes) GetStats()
        {
            lock (_gate)
            {
                return (_textCache.Count, _imageCount, _memoryBytes);
            }
        }

        private long ClearLocked()
        {
            var freed = _memoryBytes;
            _textCache.Clear();
            _memoryBytes = 0;
            _imageCount = 0;
            return freed;
        }

        private static long SaturatingAddPositive(long left, long right)
        {
            if (right <= 0)
            {
                return left;
            }
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private static long SaturatingAddSigned(long value, long delta)
        {
            if (delta > 0 && value > long.MaxValue - delta)
            {
                return long.MaxValue;
            }
            if (delta < 0 && value < -delta)
            {
                return 0;
            }
            return value + delta;
        }

        private sealed class TextCacheEntry
        {
            public string Content { get; set; }
            public long ByteSize { get; set; }
            public long LastAccessUtcTicks { get; set; }
        }
    }
}
