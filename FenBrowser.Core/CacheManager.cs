using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core
{
    /// <summary>
    /// Cache statistics for monitoring
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
            new Lazy<CacheManager>(() => new CacheManager());
        
        public static CacheManager Instance => _instance.Value;

        // Tab partitioned caches
        private readonly ConcurrentDictionary<int, TabCachePartition> _tabPartitions = 
            new ConcurrentDictionary<int, TabCachePartition>();
        
        // Global tracking
        private long _totalMemoryBytes;
        private long _evictedBytesTotal;
        private DateTime _lastEviction = DateTime.MinValue;
        private bool _disposed;

        // Eviction timer
        private Timer _evictionTimer;
        private readonly object _evictionLock = new object();

        private CacheManager()
        {
            // Run eviction check every 30 seconds
            _evictionTimer = new Timer(EvictionTimerCallback, null, 
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        // ========== Public API ==========

        /// <summary>
        /// Current total memory usage by all caches
        /// </summary>
        public long CurrentMemoryUsage => _totalMemoryBytes;

        /// <summary>
        /// Create or get a cache partition for a tab
        /// </summary>
        public TabCachePartition GetOrCreateTabPartition(int tabId)
        {
            return _tabPartitions.GetOrAdd(tabId, id => new TabCachePartition(id, this));
        }

        /// <summary>
        /// Remove and dispose a tab's cache partition
        /// </summary>
        public void DestroyTabPartition(int tabId)
        {
            if (_tabPartitions.TryRemove(tabId, out var partition))
            {
                var bytes = partition.Clear();
                Interlocked.Add(ref _totalMemoryBytes, -bytes);
                EngineLogCompat.Debug($"[CacheManager] Destroyed partition for tab {tabId}, freed {bytes / 1024}KB", 
                               LogCategory.General);
            }
        }

        /// <summary>
        /// Suspend a tab's cache (compact to essential state only)
        /// </summary>
        public void SuspendTabPartition(int tabId)
        {
            if (_tabPartitions.TryGetValue(tabId, out var partition))
            {
                var freedBytes = partition.Suspend();
                Interlocked.Add(ref _totalMemoryBytes, -freedBytes);
                EngineLogCompat.Info($"[CacheManager] Suspended tab {tabId}, freed {freedBytes / 1024}KB", 
                              LogCategory.General);
            }
        }

        /// <summary>
        /// Resume a suspended tab's cache
        /// </summary>
        public void ResumeTabPartition(int tabId)
        {
            if (_tabPartitions.TryGetValue(tabId, out var partition))
            {
                partition.Resume();
                EngineLogCompat.Debug($"[CacheManager] Resumed tab {tabId}", LogCategory.General);
            }
        }

        /// <summary>
        /// Add bytes to memory tracking
        /// </summary>
        public void TrackMemoryAllocation(long bytes)
        {
            Interlocked.Add(ref _totalMemoryBytes, bytes);
            
            // Check if eviction is needed
            var config = NetworkConfiguration.Instance;
            var total = config.MaxImageCacheBytes + config.MaxTextCacheBytes;
            if (_totalMemoryBytes > total * 0.9) // 90% threshold
            {
                EnqueueEviction();
            }
        }

        /// <summary>
        /// Remove bytes from memory tracking
        /// </summary>
        public void TrackMemoryDeallocation(long bytes)
        {
            Interlocked.Add(ref _totalMemoryBytes, -bytes);
        }

        /// <summary>
        /// Clear all caches
        /// </summary>
        public void ClearAll()
        {
            foreach (var partition in _tabPartitions.Values)
            {
                partition.Clear();
            }
            _tabPartitions.Clear();
            Interlocked.Exchange(ref _totalMemoryBytes, 0);
            
            EngineLogCompat.Info("[CacheManager] All caches cleared", LogCategory.General);
        }

        /// <summary>
        /// Trim caches to target memory limit
        /// </summary>
        public void Trim(long targetBytes)
        {
            if (_totalMemoryBytes <= targetBytes) return;

            var bytesToFree = _totalMemoryBytes - targetBytes;
            var freedTotal = 0L;

            // Evict from partitions starting with oldest
            var partitions = _tabPartitions.Values
                .OrderBy(p => p.LastAccessTime)
                .ToList();

            foreach (var partition in partitions)
            {
                if (freedTotal >= bytesToFree) break;
                
                var freed = partition.EvictOldest(bytesToFree - freedTotal);
                freedTotal += freed;
            }

            Interlocked.Add(ref _totalMemoryBytes, -freedTotal);
            Interlocked.Add(ref _evictedBytesTotal, freedTotal);
            _lastEviction = DateTime.UtcNow;

            EngineLogCompat.Info($"[CacheManager] Trimmed {freedTotal / 1024}KB", LogCategory.General);
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            var stats = new CacheStatistics
            {
                TotalMemoryBytes = _totalMemoryBytes,
                TabPartitions = _tabPartitions.Count,
                SuspendedTabs = _tabPartitions.Count(p => p.Value.IsSuspended),
                LastEviction = _lastEviction,
                EvictedBytesTotal = _evictedBytesTotal
            };

            foreach (var partition in _tabPartitions.Values)
            {
                var partitionStats = partition.GetStats();
                stats.TextCacheCount += partitionStats.textCount;
                stats.ImageCacheCount += partitionStats.imageCount;
            }

            return stats;
        }

        // ========== Private Methods ==========

        private void EnqueueEviction()
        {
            if (Monitor.TryEnter(_evictionLock))
            {
                try
                {
                    var config = NetworkConfiguration.Instance;
                    var target = (long)((config.MaxImageCacheBytes + config.MaxTextCacheBytes) * 0.75);
                    Trim(target);
                }
                finally
                {
                    Monitor.Exit(_evictionLock);
                }
            }
        }

        private void EvictionTimerCallback(object state)
        {
            if (_disposed) return;
            
            var config = NetworkConfiguration.Instance;
            var maxMemory = config.MaxImageCacheBytes + config.MaxTextCacheBytes;
            
            if (_totalMemoryBytes > maxMemory * 0.8) // 80% threshold
            {
                EnqueueEviction();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _evictionTimer?.Dispose();
            ClearAll();
        }
    }

    /// <summary>
    /// Per-tab cache partition for isolation and suspension support
    /// </summary>
    public class TabCachePartition
    {
        private readonly int _tabId;
        private readonly CacheManager _manager;
        
        // Text cache (HTML, CSS, JS)
        private readonly ConcurrentDictionary<string, TextCacheEntry> _textCache = 
            new ConcurrentDictionary<string, TextCacheEntry>();
        
        // Reference counts for cleanup
        private int _imageCount;
        private long _memoryBytes;
        
        public DateTime LastAccessTime { get; private set; } = DateTime.UtcNow;
        public bool IsSuspended { get; private set; }
        public string SuspendedUrl { get; private set; }
        public double SuspendedScrollY { get; private set; }

        public TabCachePartition(int tabId, CacheManager manager)
        {
            _tabId = tabId;
            _manager = manager;
        }

        /// <summary>
        /// Cache text content with memory tracking
        /// </summary>
        public void CacheText(string url, string content)
        {
            LastAccessTime = DateTime.UtcNow;
            
            var bytes = (long)content.Length * sizeof(char);
            var entry = new TextCacheEntry
            {
                Content = content,
                ByteSize = bytes,
                CachedAt = DateTime.UtcNow
            };

            if (_textCache.TryAdd(url, entry))
            {
                Interlocked.Add(ref _memoryBytes, bytes);
                _manager.TrackMemoryAllocation(bytes);
            }
        }

        /// <summary>
        /// Get cached text content
        /// </summary>
        public string GetText(string url)
        {
            LastAccessTime = DateTime.UtcNow;
            
            if (_textCache.TryGetValue(url, out var entry))
            {
                entry.CachedAt = DateTime.UtcNow;
                return entry.Content;
            }
            return null;
        }

        /// <summary>
        /// Track an image allocation
        /// </summary>
        public void TrackImage(long bytes)
        {
            LastAccessTime = DateTime.UtcNow;
            Interlocked.Increment(ref _imageCount);
            Interlocked.Add(ref _memoryBytes, bytes);
        }

        /// <summary>
        /// Suspend this partition (clear caches, keep minimal state)
        /// </summary>
        public long Suspend()
        {
            IsSuspended = true;
            var freed = Clear();
            return freed;
        }

        /// <summary>
        /// Resume this partition
        /// </summary>
        public void Resume()
        {
            IsSuspended = false;
            LastAccessTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Store suspension state
        /// </summary>
        public void StoreSuspensionState(string url, double scrollY)
        {
            SuspendedUrl = url;
            SuspendedScrollY = scrollY;
        }

        /// <summary>
        /// Clear all cached content
        /// </summary>
        public long Clear()
        {
            var freed = _memoryBytes;
            _textCache.Clear();
            Interlocked.Exchange(ref _memoryBytes, 0);
            Interlocked.Exchange(ref _imageCount, 0);
            return freed;
        }

        /// <summary>
        /// Evict oldest entries
        /// </summary>
        public long EvictOldest(long targetBytes)
        {
            var freed = 0L;
            
            var oldest = _textCache
                .OrderBy(x => x.Value.CachedAt)
                .ToList();

            foreach (var item in oldest)
            {
                if (freed >= targetBytes) break;
                
                if (_textCache.TryRemove(item.Key, out var entry))
                {
                    freed += entry.ByteSize;
                    Interlocked.Add(ref _memoryBytes, -entry.ByteSize);
                }
            }

            return freed;
        }

        /// <summary>
        /// Get partition statistics
        /// </summary>
        public (int textCount, int imageCount, long memoryBytes) GetStats()
        {
            return (_textCache.Count, _imageCount, _memoryBytes);
        }

        private class TextCacheEntry
        {
            public string Content { get; set; }
            public long ByteSize { get; set; }
            public DateTime CachedAt { get; set; }
        }
    }
}
