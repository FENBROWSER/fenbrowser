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

        public ShardedCache(int capacity)
        {
            _capacity = capacity > 0 ? capacity : 128;
            _map = new Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, T>>>();
            _lru = new LinkedList<KeyValuePair<CacheKey, T>>();
        }

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
