using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Cache
{
    /// <summary>
    /// A generic cache that partitions entries by a CacheKey (Partition + URL).
    /// Implements a global LRU policy across all partitions to respect memory limits.
    /// </summary>
    /// <typeparam name="T">The type of content to cache</typeparam>
    public class ShardedCache<T>
    {
        private readonly int _capacity;
        private readonly Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, T>>> _map;
        private readonly LinkedList<KeyValuePair<CacheKey, T>> _lru;
        private readonly object _lock = new object();
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;

        public ShardedCache(int capacity)
        {
            _capacity = capacity > 0 ? capacity : 128;
            _map = new Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, T>>>();
            _lru = new LinkedList<KeyValuePair<CacheKey, T>>();
        }

        public int Capacity => _capacity;

        public long HitCount => System.Threading.Interlocked.Read(ref _hitCount);

        public long MissCount => System.Threading.Interlocked.Read(ref _missCount);

        public long EvictionCount => System.Threading.Interlocked.Read(ref _evictionCount);

        public void Put(string partition, string url, T value)
        {
            var key = new CacheKey(partition, url);
            lock (_lock)
            {
                // Remove existing if present (to update val/position)
                if (_map.TryGetValue(key, out var existingNode))
                {
                    _lru.Remove(existingNode);
                    _map.Remove(key);
                }

                // Add new
                var node = new LinkedListNode<KeyValuePair<CacheKey, T>>(new KeyValuePair<CacheKey, T>(key, value));
                _lru.AddFirst(node);
                _map[key] = node;

                // Evict if over capacity
                while (_lru.Count > _capacity)
                {
                    var last = _lru.Last;
                    if (last == null) break;
                    _map.Remove(last.Value.Key);
                    _lru.RemoveLast();
                    System.Threading.Interlocked.Increment(ref _evictionCount);
                }
            }
        }

        public bool TryGet(string partition, string url, out T value)
        {
            var key = new CacheKey(partition, url);
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // Move to front (LRU)
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    System.Threading.Interlocked.Increment(ref _hitCount);
                    value = node.Value.Value;
                    return true;
                }
            }
            System.Threading.Interlocked.Increment(ref _missCount);
            value = default;
            return false;
        }

        public bool Contains(string partition, string url)
        {
            var key = new CacheKey(partition, url);
            lock (_lock)
            {
                return _map.ContainsKey(key);
            }
        }

        public bool TryRemove(string partition, string url, out T value)
        {
            var key = new CacheKey(partition, url);
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _map.Remove(key);
                    value = node.Value.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
            }
        }

        public int Count
        {
            get
            {
                lock (_lock) return _map.Count;
            }
        }
    }
}
