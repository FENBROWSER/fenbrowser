using System;
using System.Collections.Generic;
using System.Threading;

namespace FenBrowser.Core.Cache
{
    /// <summary>
    /// A partition-aware, lock-sharded LRU cache keyed by CacheKey (partition + URL).
    ///
    /// The previous implementation used one dictionary, one LRU list, and one global
    /// lock, so every resource-cache hit serialized all tabs and all partitions. This
    /// implementation stripes both storage and recency tracking across independent
    /// shards. Shard capacities sum exactly to the configured total capacity.
    /// </summary>
    /// <typeparam name="T">The type of content to cache.</typeparam>
    public class ShardedCache<T>
    {
        private const int DefaultShardCount = 16;

        private sealed class Shard
        {
            public Shard(int capacity)
            {
                Capacity = capacity;
                Map = new Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, T>>>(capacity);
                Lru = new LinkedList<KeyValuePair<CacheKey, T>>();
            }

            public int Capacity { get; }
            public object SyncRoot { get; } = new();
            public Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, T>>> Map { get; }
            public LinkedList<KeyValuePair<CacheKey, T>> Lru { get; }
        }

        private readonly int _capacity;
        private readonly Shard[] _shards;
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;

        public ShardedCache(int capacity)
        {
            _capacity = capacity > 0 ? capacity : 128;

            var shardCount = Math.Min(DefaultShardCount, _capacity);
            _shards = new Shard[shardCount];

            var baseCapacity = _capacity / shardCount;
            var remainder = _capacity % shardCount;
            for (var i = 0; i < shardCount; i++)
            {
                // Distribute the remainder across the first shards so all shard
                // capacities together equal the caller-visible total capacity.
                var shardCapacity = baseCapacity + (i < remainder ? 1 : 0);
                _shards[i] = new Shard(shardCapacity);
            }
        }

        public int Capacity => _capacity;

        public long HitCount => Interlocked.Read(ref _hitCount);

        public long MissCount => Interlocked.Read(ref _missCount);

        public long EvictionCount => Interlocked.Read(ref _evictionCount);

        public void Put(string partition, string url, T value)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);

            lock (shard.SyncRoot)
            {
                if (shard.Map.TryGetValue(key, out var existingNode))
                {
                    // Updating an existing entry must not temporarily count as a
                    // second entry or trigger an unnecessary eviction.
                    existingNode.Value = new KeyValuePair<CacheKey, T>(key, value);
                    shard.Lru.Remove(existingNode);
                    shard.Lru.AddFirst(existingNode);
                    return;
                }

                var node = new LinkedListNode<KeyValuePair<CacheKey, T>>(
                    new KeyValuePair<CacheKey, T>(key, value));
                shard.Lru.AddFirst(node);
                shard.Map.Add(key, node);

                while (shard.Map.Count > shard.Capacity)
                {
                    var last = shard.Lru.Last;
                    if (last == null)
                    {
                        // Map/list divergence would indicate an internal cache bug.
                        // Clear the shard rather than leave it permanently over limit.
                        shard.Map.Clear();
                        shard.Lru.Clear();
                        break;
                    }

                    shard.Map.Remove(last.Value.Key);
                    shard.Lru.RemoveLast();
                    Interlocked.Increment(ref _evictionCount);
                }
            }
        }

        public bool TryGet(string partition, string url, out T value)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);

            lock (shard.SyncRoot)
            {
                if (shard.Map.TryGetValue(key, out var node))
                {
                    shard.Lru.Remove(node);
                    shard.Lru.AddFirst(node);
                    Interlocked.Increment(ref _hitCount);
                    value = node.Value.Value;
                    return true;
                }
            }

            Interlocked.Increment(ref _missCount);
            value = default;
            return false;
        }

        public bool Contains(string partition, string url)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);
            lock (shard.SyncRoot)
            {
                return shard.Map.ContainsKey(key);
            }
        }

        public bool TryRemove(string partition, string url, out T value)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);

            lock (shard.SyncRoot)
            {
                if (shard.Map.TryGetValue(key, out var node))
                {
                    shard.Map.Remove(key);
                    shard.Lru.Remove(node);
                    value = node.Value.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Clear()
        {
            // Shards are independent; never hold more than one shard lock at a time.
            // That keeps Clear from introducing a lock-order dependency with hot gets.
            foreach (var shard in _shards)
            {
                lock (shard.SyncRoot)
                {
                    shard.Map.Clear();
                    shard.Lru.Clear();
                }
            }
        }

        public int Count
        {
            get
            {
                var count = 0;
                foreach (var shard in _shards)
                {
                    lock (shard.SyncRoot)
                    {
                        count += shard.Map.Count;
                    }
                }

                return count;
            }
        }

        private Shard GetShard(CacheKey key)
        {
            // Convert to uint before modulo so int.MinValue is handled without
            // Math.Abs overflow and every hash maps deterministically to a shard.
            var index = (int)((uint)key.GetHashCode() % (uint)_shards.Length);
            return _shards[index];
        }
    }
}
