using System;
using System.Collections.Concurrent;
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
                Map = new ConcurrentDictionary<CacheKey, CacheEntry>();
            }

            public int Capacity { get; }
            public object WriteLock { get; } = new();
            public ConcurrentDictionary<CacheKey, CacheEntry> Map { get; }
        }

        private sealed class CacheEntry
        {
            public CacheEntry(T value, long accessSequence)
            {
                Value = value;
                LastAccessSequence = accessSequence;
            }

            public T Value;
            public long LastAccessSequence;
        }

        private readonly int _capacity;
        private readonly Shard[] _shards;
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;
        private long _accessSequence;

        public ShardedCache(int capacity)
        {
            _capacity = capacity > 0 ? capacity : 128;

            var shardCount = Math.Min(DefaultShardCount, Math.Max(1, _capacity / 16));
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

            var accessSequence = Interlocked.Increment(ref _accessSequence);
            lock (shard.WriteLock)
            {
                if (shard.Map.TryGetValue(key, out var existing))
                {
                    existing.Value = value;
                    Volatile.Write(ref existing.LastAccessSequence, accessSequence);
                    return;
                }

                shard.Map[key] = new CacheEntry(value, accessSequence);

                while (shard.Map.Count > shard.Capacity)
                {
                    CacheKey oldestKey = default;
                    var oldestSequence = long.MaxValue;
                    var found = false;
                    foreach (var candidate in shard.Map)
                    {
                        var sequence = Volatile.Read(ref candidate.Value.LastAccessSequence);
                        if (sequence < oldestSequence)
                        {
                            oldestSequence = sequence;
                            oldestKey = candidate.Key;
                            found = true;
                        }
                    }

                    if (!found || !shard.Map.TryRemove(oldestKey, out _))
                    {
                        break;
                    }

                    Interlocked.Increment(ref _evictionCount);
                }
            }
        }

        public bool TryGet(string partition, string url, out T value)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);

            if (shard.Map.TryGetValue(key, out var entry))
            {
                Volatile.Write(
                    ref entry.LastAccessSequence,
                    Interlocked.Increment(ref _accessSequence));
                Interlocked.Increment(ref _hitCount);
                value = entry.Value;
                return true;
            }

            Interlocked.Increment(ref _missCount);
            value = default;
            return false;
        }

        public bool Contains(string partition, string url)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);
            return shard.Map.ContainsKey(key);
        }

        public bool TryRemove(string partition, string url, out T value)
        {
            var key = new CacheKey(partition, url);
            var shard = GetShard(key);

            if (shard.Map.TryRemove(key, out var entry))
            {
                value = entry.Value;
                return true;
            }

            value = default;
            return false;
        }

        public void Clear()
        {
            foreach (var shard in _shards)
            {
                shard.Map.Clear();
            }
        }

        public int Count
        {
            get
            {
                var count = 0;
                foreach (var shard in _shards)
                {
                    count += shard.Map.Count;
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
