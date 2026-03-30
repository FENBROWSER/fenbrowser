using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Cache
{
    public readonly record struct BoundedLruCacheSnapshot(
        string Name,
        int EntryCount,
        long ApproximateBytes,
        int EntryCapacity,
        long ByteCapacity,
        long HitCount,
        long MissCount,
        long EvictionCount,
        long ClearCount);

    /// <summary>
    /// Small, allocation-aware LRU cache with explicit entry and byte ceilings.
    /// </summary>
    public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
    {
        private sealed class CacheEntry
        {
            public CacheEntry(TKey key, TValue value, long approximateBytes)
            {
                Key = key;
                Value = value;
                ApproximateBytes = approximateBytes;
            }

            public TKey Key { get; }
            public TValue Value { get; set; }
            public long ApproximateBytes { get; set; }
        }

        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _entries = new();
        private readonly LinkedList<CacheEntry> _lru = new();
        private readonly object _lock = new();
        private readonly Func<TKey, TValue, long> _sizeEstimator;
        private readonly int _entryCapacity;
        private readonly long _byteCapacity;
        private long _currentBytes;
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;
        private long _clearCount;

        public BoundedLruCache(int entryCapacity, long byteCapacity, Func<TKey, TValue, long> sizeEstimator)
        {
            if (sizeEstimator == null)
            {
                throw new ArgumentNullException(nameof(sizeEstimator));
            }

            _entryCapacity = Math.Max(1, entryCapacity);
            _byteCapacity = Math.Max(1024, byteCapacity);
            _sizeEstimator = sizeEstimator;
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        public long ApproximateBytes
        {
            get
            {
                lock (_lock)
                {
                    return _currentBytes;
                }
            }
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    _hitCount++;
                    value = node.Value.Value;
                    return true;
                }

                _missCount++;
                value = default;
                return false;
            }
        }

        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                var approximateBytes = Math.Max(1L, _sizeEstimator(key, value));
                if (_entries.TryGetValue(key, out var existingNode))
                {
                    _currentBytes -= existingNode.Value.ApproximateBytes;
                    existingNode.Value.Value = value;
                    existingNode.Value.ApproximateBytes = approximateBytes;
                    _currentBytes += approximateBytes;
                    _lru.Remove(existingNode);
                    _lru.AddFirst(existingNode);
                }
                else
                {
                    var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, value, approximateBytes));
                    _entries[key] = node;
                    _lru.AddFirst(node);
                    _currentBytes += approximateBytes;
                }

                TrimToBudget();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _lru.Clear();
                _currentBytes = 0;
                _clearCount++;
            }
        }

        public BoundedLruCacheSnapshot GetSnapshot(string name)
        {
            lock (_lock)
            {
                return new BoundedLruCacheSnapshot(
                    name,
                    _entries.Count,
                    _currentBytes,
                    _entryCapacity,
                    _byteCapacity,
                    _hitCount,
                    _missCount,
                    _evictionCount,
                    _clearCount);
            }
        }

        private void TrimToBudget()
        {
            while (_entries.Count > _entryCapacity || _currentBytes > _byteCapacity)
            {
                var last = _lru.Last;
                if (last == null)
                {
                    _entries.Clear();
                    _currentBytes = 0;
                    return;
                }

                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
                _currentBytes -= last.Value.ApproximateBytes;
                _evictionCount++;
            }
        }
    }
}
