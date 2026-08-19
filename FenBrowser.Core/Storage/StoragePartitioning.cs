using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FenBrowser.Core.Network;
using FenBrowser.Core.Security;

namespace FenBrowser.Core.Storage
{
    /// <summary>Immutable storage partition key.</summary>
    public readonly struct StoragePartitionKey : IEquatable<StoragePartitionKey>
    {
        private readonly string _storageKey;

        public string TopLevelSite { get; }
        public string FrameSite { get; }
        public bool IsThirdParty => !string.Equals(TopLevelSite, FrameSite, StringComparison.Ordinal);

        public StoragePartitionKey(string topLevelSite, string frameSite)
        {
            TopLevelSite = NormalizeSite(topLevelSite);
            FrameSite = NormalizeSite(frameSite);
            _storageKey = ComputeStorageKey(TopLevelSite, FrameSite);
        }

        public static StoragePartitionKey FirstParty(string site) => new(site, site);
        public static StoragePartitionKey Opaque => new("null", "null");

        public bool Equals(StoragePartitionKey other) =>
            string.Equals(TopLevelSite, other.TopLevelSite, StringComparison.Ordinal) &&
            string.Equals(FrameSite, other.FrameSite, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is StoragePartitionKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(TopLevelSite, FrameSite);

        public override string ToString() => $"({TopLevelSite}, {FrameSite})";

        /// <summary>
        /// Stable opaque representation for persistent storage keys. Keep the full
        /// SHA-256 digest: storage partition identity is a security boundary and must
        /// not rely on the collision resistance of a truncated 64-bit prefix.
        /// </summary>
        public string ToStorageKey()
        {
            return _storageKey ?? ComputeStorageKey(TopLevelSite, FrameSite);
        }

        private static string ComputeStorageKey(string topLevelSite, string frameSite)
        {
            var combined = (topLevelSite ?? string.Empty) + "\0" + (frameSite ?? string.Empty);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash);
        }

        private static string NormalizeSite(string site)
        {
            return string.IsNullOrWhiteSpace(site)
                ? "null"
                : site.Trim().ToLowerInvariant();
        }
    }

    /// <summary>Computes partition keys from navigation context.</summary>
    public static class StoragePartitionKeyFactory
    {
        public static StoragePartitionKey Compute(string topLevelUrl, string frameUrl)
        {
            var topSite = GetSite(topLevelUrl);
            var frameSite = GetSite(frameUrl);
            return new StoragePartitionKey(topSite, frameSite);
        }

        private static string GetSite(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "null";
            var site = SiteIdentityService.Default.CreateSchemefulSite(url);
            return site.IsOpaque ? "null" : site.SerializedPartitionKey;
        }
    }

    public enum CookieSameSite { Unspecified, Strict, Lax, None }

    public sealed record Cookie
    {
        public string Name { get; init; }
        public string Value { get; init; }
        public string Domain { get; init; }
        public string Path { get; init; } = "/";
        public bool HostOnly { get; init; } = true;
        public bool Secure { get; init; }
        public bool HttpOnly { get; init; }
        public CookieSameSite SameSite { get; init; } = CookieSameSite.Unspecified;
        public DateTimeOffset? Expires { get; init; }
        public StoragePartitionKey? PartitionKey { get; init; }
        public DateTimeOffset CreationTime { get; init; } = DateTimeOffset.UtcNow;

        public bool IsPartitioned => PartitionKey.HasValue;
        public bool IsExpired => Expires.HasValue && Expires.Value <= DateTimeOffset.UtcNow;
        public bool IsSession => !Expires.HasValue;
    }

    public sealed class PartitionedCookieStore
    {
        private const int MaxCookiesPerDomain = 180;
        private const int MaxCookiesTotal = 6000;
        private const int CandidateDomainCacheLimit = 4096;

        private sealed class CookieEntry
        {
            public CookieEntry(Cookie cookie) => Cookie = cookie;
            public Cookie Cookie { get; set; }
        }

        private sealed record EvictionCandidate(
            string Domain,
            string StoreKey,
            CookieBucket Bucket,
            CookieEntry Entry);

        private sealed class CookieBucket
        {
            public object SyncRoot { get; } = new();
            public Dictionary<string, CookieEntry> Entries { get; } = new(StringComparer.Ordinal);
            public PriorityQueue<EvictionCandidate, (long CreatedTicks, long Sequence)> EvictionQueue { get; }
                = new();
        }

        private readonly ConcurrentDictionary<string, CookieBucket> _cookiesByDomain
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string[]> _candidateDomains
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _candidateDomainOrder = new();
        private readonly PriorityQueue<EvictionCandidate, (long CreatedTicks, long Sequence)> _globalEvictionQueue
            = new();
        private readonly object _capacityLock = new();
        private int _cookieCount;
        private long _evictionSequence;

        public void Set(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            var domain = NormalizeDomain(cookie.Domain);
            if (domain.Length == 0) throw new ArgumentException("Cookie domain cannot be empty.", nameof(cookie));

            var actualKey = partitionKey ?? (cookie.IsPartitioned ? cookie.PartitionKey : null);
            var storeKey = MakeKey(actualKey, cookie.Name, cookie.Path);

            if (cookie.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
            {
                DeleteByKey(domain, storeKey);
                return;
            }

            var normalizedCookie = cookie with { Domain = domain, PartitionKey = actualKey };
            var bucket = _cookiesByDomain.GetOrAdd(domain, static _ => new CookieBucket());
            lock (bucket.SyncRoot)
            {
                if (bucket.Entries.TryGetValue(storeKey, out var existing) &&
                    (existing.Cookie.Expires is not { } existingExpires || existingExpires > DateTimeOffset.UtcNow))
                {
                    existing.Cookie = normalizedCookie with { CreationTime = existing.Cookie.CreationTime };
                    return;
                }

                if (bucket.Entries.Remove(storeKey))
                    Interlocked.Decrement(ref _cookieCount);
            }

            lock (_capacityLock)
            {
                bucket = _cookiesByDomain.GetOrAdd(domain, static _ => new CookieBucket());
                lock (bucket.SyncRoot)
                {
                    if (bucket.Entries.TryGetValue(storeKey, out var racedEntry))
                    {
                        racedEntry.Cookie = normalizedCookie with { CreationTime = racedEntry.Cookie.CreationTime };
                        return;
                    }

                    while (bucket.Entries.Count >= MaxCookiesPerDomain)
                    {
                        if (!EvictOldestFromBucketLocked(domain, bucket))
                            return;
                    }

                    while (Volatile.Read(ref _cookieCount) >= MaxCookiesTotal)
                    {
                        if (!EvictOldestGlobalLocked())
                            return;
                    }

                    var entry = new CookieEntry(normalizedCookie);
                    bucket.Entries.Add(storeKey, entry);
                    Interlocked.Increment(ref _cookieCount);
                    EnqueueEvictionCandidateLocked(domain, storeKey, bucket, entry);
                }
            }
        }

        public IReadOnlyList<Cookie> GetForUrl(
            string requestUrl,
            StoragePartitionKey partitionKey,
            bool includeHttpOnly = false)
        {
            var parsed = WhatwgUrl.Parse(requestUrl);
            if (parsed == null) return Array.Empty<Cookie>();

            var host = NormalizeDomain(parsed.Hostname);
            if (host.Length == 0) return Array.Empty<Cookie>();

            var path = parsed.Pathname;
            bool isSecure = parsed.Scheme == "https" || parsed.Scheme == "wss";
            var result = new List<Cookie>();

            var now = DateTimeOffset.UtcNow;
            foreach (var candidateDomain in GetCandidateDomains(host))
            {
                if (!_cookiesByDomain.TryGetValue(candidateDomain, out var bucket))
                    continue;

                lock (bucket.SyncRoot)
                {
                    List<string> expiredKeys = null;
                    foreach (var pair in bucket.Entries)
                    {
                        var cookie = pair.Value.Cookie;
                        if (cookie.Expires is { } expires && expires <= now)
                        {
                            (expiredKeys ??= new List<string>()).Add(pair.Key);
                            continue;
                        }
                        if (cookie.Secure && !isSecure) continue;
                        if (cookie.HttpOnly && !includeHttpOnly) continue;
                        if (!DomainMatches(host, cookie.Domain, cookie.HostOnly)) continue;
                        if (!PathMatches(path, cookie.Path)) continue;
                        if (cookie.IsPartitioned &&
                            (!cookie.PartitionKey.HasValue || !cookie.PartitionKey.Value.Equals(partitionKey)))
                            continue;
                        result.Add(cookie);
                    }

                    if (expiredKeys != null)
                        RemoveKeysLocked(bucket, expiredKeys);
                }
            }

            result.Sort(static (a, b) =>
            {
                var pathOrder = b.Path.Length.CompareTo(a.Path.Length);
                if (pathOrder != 0) return pathOrder;
                var creationOrder = a.CreationTime.CompareTo(b.CreationTime);
                if (creationOrder != 0) return creationOrder;
                var domainOrder = string.CompareOrdinal(a.Domain, b.Domain);
                if (domainOrder != 0) return domainOrder;
                return string.CompareOrdinal(a.Name, b.Name);
            });
            return result;
        }

        public void Delete(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            if (cookie == null) return;
            var domain = NormalizeDomain(cookie.Domain);
            if (domain.Length == 0) return;
            var actualKey = partitionKey ?? (cookie.IsPartitioned ? cookie.PartitionKey : null);

            DeleteByKey(domain, MakeKey(actualKey, cookie.Name, cookie.Path));
        }

        public void DeleteByName(string domain, string name, StoragePartitionKey? partitionKey = null)
        {
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name)) return;
            var normalizedDomain = NormalizeDomain(domain);
            var partitionKeyText = partitionKey?.ToStorageKey() ?? "unpartitioned";
            var prefix = $"pk:{partitionKeyText}:{name}:";

            if (!_cookiesByDomain.TryGetValue(normalizedDomain, out var bucket)) return;
            lock (bucket.SyncRoot)
            {
                var keys = new List<string>();
                foreach (var key in bucket.Entries.Keys)
                    if (key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(key);
                RemoveKeysLocked(bucket, keys);
            }
        }

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var prefix = $"pk:{partitionKey.ToStorageKey()}:";
            foreach (var domainPair in _cookiesByDomain)
            {
                var bucket = domainPair.Value;
                lock (bucket.SyncRoot)
                {
                    var keys = new List<string>();
                    foreach (var key in bucket.Entries.Keys)
                        if (key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(key);
                    RemoveKeysLocked(bucket, keys);
                }
            }
        }

        public void ClearAll()
        {
            lock (_capacityLock)
            {
                foreach (var bucket in _cookiesByDomain.Values)
                {
                    lock (bucket.SyncRoot)
                    {
                        bucket.Entries.Clear();
                        bucket.EvictionQueue.Clear();
                    }
                }
                _cookiesByDomain.Clear();
                _globalEvictionQueue.Clear();
                Interlocked.Exchange(ref _cookieCount, 0);
            }
        }

        private void EnqueueEvictionCandidateLocked(
            string domain,
            string storeKey,
            CookieBucket bucket,
            CookieEntry entry)
        {
            var candidate = new EvictionCandidate(domain, storeKey, bucket, entry);
            var priority = (entry.Cookie.CreationTime.UtcTicks, Interlocked.Increment(ref _evictionSequence));
            bucket.EvictionQueue.Enqueue(candidate, priority);
            _globalEvictionQueue.Enqueue(candidate, priority);
        }

        private bool EvictOldestGlobalLocked()
        {
            CompactGlobalEvictionQueueIfNeededLocked();
            while (_globalEvictionQueue.TryDequeue(out var candidate, out _))
            {
                var bucket = candidate.Bucket;
                lock (bucket.SyncRoot)
                {
                    if (!IsCurrent(candidate)) continue;
                    bucket.Entries.Remove(candidate.StoreKey);
                    Interlocked.Decrement(ref _cookieCount);
                    return true;
                }
            }
            return false;
        }

        private bool EvictOldestFromBucketLocked(string domain, CookieBucket bucket)
        {
            CompactBucketEvictionQueueIfNeededLocked(domain, bucket);
            while (bucket.EvictionQueue.TryDequeue(out var candidate, out _))
            {
                if (!IsCurrent(candidate)) continue;
                bucket.Entries.Remove(candidate.StoreKey);
                Interlocked.Decrement(ref _cookieCount);
                return true;
            }
            return false;
        }

        private static bool IsCurrent(EvictionCandidate candidate) =>
            candidate.Bucket.Entries.TryGetValue(candidate.StoreKey, out var current) &&
            ReferenceEquals(current, candidate.Entry);

        private void CompactBucketEvictionQueueIfNeededLocked(string domain, CookieBucket bucket)
        {
            if (bucket.EvictionQueue.Count <= Math.Max(256, bucket.Entries.Count * 4)) return;
            bucket.EvictionQueue.Clear();
            foreach (var pair in bucket.Entries)
            {
                var candidate = new EvictionCandidate(domain, pair.Key, bucket, pair.Value);
                var priority = (pair.Value.Cookie.CreationTime.UtcTicks, Interlocked.Increment(ref _evictionSequence));
                bucket.EvictionQueue.Enqueue(candidate, priority);
            }
        }

        private void CompactGlobalEvictionQueueIfNeededLocked()
        {
            var count = Volatile.Read(ref _cookieCount);
            if (_globalEvictionQueue.Count <= Math.Max(1024, count * 4)) return;
            _globalEvictionQueue.Clear();
            foreach (var domainPair in _cookiesByDomain)
            {
                var bucket = domainPair.Value;
                lock (bucket.SyncRoot)
                {
                    foreach (var pair in bucket.Entries)
                    {
                        var candidate = new EvictionCandidate(domainPair.Key, pair.Key, bucket, pair.Value);
                        var priority = (pair.Value.Cookie.CreationTime.UtcTicks, Interlocked.Increment(ref _evictionSequence));
                        _globalEvictionQueue.Enqueue(candidate, priority);
                    }
                }
            }
        }

        private void DeleteByKey(string domain, string storeKey)
        {
            if (!_cookiesByDomain.TryGetValue(domain, out var bucket)) return;
            lock (bucket.SyncRoot)
            {
                if (bucket.Entries.Remove(storeKey))
                    Interlocked.Decrement(ref _cookieCount);
            }
        }

        private void RemoveKeysLocked(CookieBucket bucket, IEnumerable<string> keys)
        {
            foreach (var key in keys)
                if (bucket.Entries.Remove(key)) Interlocked.Decrement(ref _cookieCount);
        }

        private static string MakeKey(StoragePartitionKey? pk, string name, string path) =>
            $"pk:{pk?.ToStorageKey() ?? "unpartitioned"}:{name}:{path}";

        private static string NormalizeDomain(string domain) =>
            string.IsNullOrWhiteSpace(domain)
                ? string.Empty
                : domain.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();

        private string[] GetCandidateDomains(string host)
        {
            if (_candidateDomains.TryGetValue(host, out var cached)) return cached;

            var candidates = CreateCandidateDomains(host);
            if (_candidateDomains.TryAdd(host, candidates))
            {
                _candidateDomainOrder.Enqueue(host);
                while (_candidateDomains.Count > CandidateDomainCacheLimit &&
                       _candidateDomainOrder.TryDequeue(out var oldest))
                    _candidateDomains.TryRemove(oldest, out _);
            }

            return candidates;
        }

        private static string[] CreateCandidateDomains(string host)
        {
            if (IPAddress.TryParse(host, out _)) return new[] { host };

            var candidates = new List<string> { host };
            var offset = 0;
            while (true)
            {
                var dot = host.IndexOf('.', offset);
                if (dot < 0 || dot + 1 >= host.Length) break;
                offset = dot + 1;
                candidates.Add(host.Substring(offset));
            }
            return candidates.ToArray();
        }

        private static bool DomainMatches(string host, string cookieDomain, bool hostOnly)
        {
            if (string.IsNullOrEmpty(cookieDomain)) return false;
            if (string.Equals(host, cookieDomain, StringComparison.OrdinalIgnoreCase))
                return true;
            if (hostOnly)
                return false;
            if (IPAddress.TryParse(host, out _) || IPAddress.TryParse(cookieDomain, out _))
                return false;
            return host.Length > cookieDomain.Length &&
                   host.EndsWith(cookieDomain, StringComparison.OrdinalIgnoreCase) &&
                   host[host.Length - cookieDomain.Length - 1] == '.';
        }

        private static bool PathMatches(string requestPath, string cookiePath)
        {
            if (string.IsNullOrEmpty(cookiePath) || cookiePath == "/") return true;
            if (string.IsNullOrEmpty(requestPath)) requestPath = "/";
            if (string.Equals(requestPath, cookiePath, StringComparison.Ordinal)) return true;
            if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal)) return false;
            return cookiePath[^1] == '/' ||
                   (requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/');
        }
    }
    public sealed class PartitionedKeyValueStorage
    {
        private sealed class StorageBucket
        {
            public object SyncRoot { get; } = new();
            public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);
            public long Bytes { get; set; }
        }

        private readonly ConcurrentDictionary<string, StorageBucket> _buckets = new(StringComparer.Ordinal);
        private readonly long _quotaBytesPerBucket;

        public PartitionedKeyValueStorage(long quotaBytesPerBucket = 5 * 1024 * 1024)
        {
            if (quotaBytesPerBucket <= 0) throw new ArgumentOutOfRangeException(nameof(quotaBytesPerBucket));
            _quotaBytesPerBucket = quotaBytesPerBucket;
        }

        private static string MakeBucketKey(string origin, StoragePartitionKey key) =>
            $"{origin}\0{key.ToStorageKey()}";

        private StorageBucket GetOrCreateBucket(string origin, StoragePartitionKey key) =>
            _buckets.GetOrAdd(MakeBucketKey(origin, key), _ => new StorageBucket());

        private bool TryGetBucket(string origin, StoragePartitionKey key, out StorageBucket bucket) =>
            _buckets.TryGetValue(MakeBucketKey(origin, key), out bucket);

        public string GetItem(string origin, StoragePartitionKey partitionKey, string itemKey)
        {
            if (itemKey == null) throw new ArgumentNullException(nameof(itemKey));
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return null;
            lock (bucket.SyncRoot)
            {
                return bucket.Items.TryGetValue(itemKey, out var value) ? value : null;
            }
        }

        public bool SetItem(string origin, StoragePartitionKey partitionKey, string itemKey, string value)
        {
            if (itemKey == null) throw new ArgumentNullException(nameof(itemKey));
            var bucket = GetOrCreateBucket(origin, partitionKey);
            var normalizedValue = value ?? string.Empty;
            var keyBytes = Encoding.UTF8.GetByteCount(itemKey);
            var newEntryBytes = keyBytes + Encoding.UTF8.GetByteCount(normalizedValue);

            lock (bucket.SyncRoot)
            {
                long existingEntryBytes = 0;
                if (bucket.Items.TryGetValue(itemKey, out var existingValue))
                {
                    existingEntryBytes = keyBytes + Encoding.UTF8.GetByteCount(existingValue ?? string.Empty);
                }

                var projectedBytes = bucket.Bytes - existingEntryBytes + newEntryBytes;
                if (projectedBytes > _quotaBytesPerBucket) return false;

                bucket.Items[itemKey] = normalizedValue;
                bucket.Bytes = projectedBytes;
                return true;
            }
        }

        public void RemoveItem(string origin, StoragePartitionKey partitionKey, string itemKey)
        {
            if (itemKey == null) throw new ArgumentNullException(nameof(itemKey));
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return;
            lock (bucket.SyncRoot)
            {
                if (bucket.Items.Remove(itemKey, out var removedValue))
                {
                    bucket.Bytes -= Encoding.UTF8.GetByteCount(itemKey) +
                                    Encoding.UTF8.GetByteCount(removedValue ?? string.Empty);
                }
            }
        }

        public void Clear(string origin, StoragePartitionKey partitionKey)
        {
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return;
            lock (bucket.SyncRoot)
            {
                bucket.Items.Clear();
                bucket.Bytes = 0;
            }
        }

        public IReadOnlyList<string> GetKeys(string origin, StoragePartitionKey partitionKey)
        {
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return Array.Empty<string>();
            lock (bucket.SyncRoot)
            {
                return new List<string>(bucket.Items.Keys);
            }
        }

        public int Length(string origin, StoragePartitionKey partitionKey)
        {
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return 0;
            lock (bucket.SyncRoot)
            {
                return bucket.Items.Count;
            }
        }

        public void ClearAll() => _buckets.Clear();

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var suffix = $"\0{partitionKey.ToStorageKey()}";
            foreach (var key in _buckets.Keys)
            {
                if (key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    _buckets.TryRemove(key, out _);
                }
            }
        }
    }

    public sealed record HttpCacheEntry
    {
        public string Url { get; init; }
        public int StatusCode { get; init; }
        public Dictionary<string, string> ResponseHeaders { get; init; }
        public byte[] Body { get; init; }
        public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? Expires { get; init; }
        public string ETag { get; init; }
        public string LastModified { get; init; }
        public StoragePartitionKey PartitionKey { get; init; }

        public bool IsStale(DateTimeOffset now) => Expires.HasValue && Expires.Value <= now;
    }

    public sealed class PartitionedHttpCache
    {
        private const int MaxEntries = 8192;
        private const int MaxCacheKeyChars = 32 * 1024;
        private const long MaxMetadataBytesPerEntry = 64 * 1024;

        private readonly ConcurrentDictionary<string, HttpCacheEntry> _cache = new(StringComparer.Ordinal);
        private readonly object _mutationLock = new();
        private readonly long _maxBytes;
        private long _currentBytes;

        public PartitionedHttpCache(long maxBytes = 256 * 1024 * 1024)
        {
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxBytes = maxBytes;
        }

        public HttpCacheEntry Get(StoragePartitionKey partitionKey, string url, string varyKey = null)
        {
            if (!TryMakeKey(partitionKey, url, varyKey, out var key)) return null;
            if (!_cache.TryGetValue(key, out var entry)) return null;

            var now = DateTimeOffset.UtcNow;
            if (!entry.IsStale(now)) return CloneForCaller(entry);

            lock (_mutationLock)
            {
                if (!_cache.TryGetValue(key, out var current)) return null;
                if (!current.IsStale(now)) return CloneForCaller(current);

                RemoveLocked(key, current);
                return null;
            }
        }

        public bool Put(StoragePartitionKey partitionKey, string url, HttpCacheEntry entry, string varyKey = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!TryMakeKey(partitionKey, url, varyKey, out var key)) return false;
            if (!TryCloneForStorage(entry, partitionKey, out var ownedEntry, out var entryBytes)) return false;
            if (entryBytes > _maxBytes / 4) return false;

            lock (_mutationLock)
            {
                EvictStaleLocked(DateTimeOffset.UtcNow);

                var replacing = _cache.TryGetValue(key, out var existing);
                var existingBytes = replacing ? GetAccountedBytes(existing) : 0L;

                if (!replacing)
                {
                    while (_cache.Count >= MaxEntries)
                    {
                        if (!EvictOldestLocked()) return false;
                    }
                }

                while (_currentBytes - existingBytes + entryBytes > _maxBytes)
                {
                    if (!EvictOldestLocked(key)) return false;
                    replacing = _cache.TryGetValue(key, out existing);
                    existingBytes = replacing ? GetAccountedBytes(existing) : 0L;
                }

                _cache[key] = ownedEntry;
                _currentBytes = Math.Max(0L, _currentBytes - existingBytes + entryBytes);
                return true;
            }
        }

        public void Invalidate(StoragePartitionKey partitionKey, string url)
        {
            if (url == null || url.Length > MaxCacheKeyChars) return;
            var prefix = MakeUrlPrefix(partitionKey, url);
            lock (_mutationLock)
            {
                foreach (var key in _cache.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                        _cache.TryGetValue(key, out var entry))
                    {
                        RemoveLocked(key, entry);
                    }
                }
            }
        }

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var prefix = $"pk:{partitionKey.ToStorageKey()}:";
            lock (_mutationLock)
            {
                foreach (var key in _cache.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                        _cache.TryGetValue(key, out var entry))
                    {
                        RemoveLocked(key, entry);
                    }
                }
            }
        }

        public void ClearAll()
        {
            lock (_mutationLock)
            {
                _cache.Clear();
                _currentBytes = 0;
            }
        }

        private void EvictStaleLocked(DateTimeOffset now)
        {
            foreach (var pair in _cache)
            {
                if (pair.Value.IsStale(now))
                    RemoveLocked(pair.Key, pair.Value);
            }
        }

        private bool EvictOldestLocked(string protectedKey = null)
        {
            string oldestKey = null;
            HttpCacheEntry oldestEntry = null;
            foreach (var pair in _cache)
            {
                if (protectedKey != null && string.Equals(pair.Key, protectedKey, StringComparison.Ordinal))
                    continue;

                if (oldestEntry == null ||
                    pair.Value.CachedAt < oldestEntry.CachedAt ||
                    (pair.Value.CachedAt == oldestEntry.CachedAt &&
                     string.CompareOrdinal(pair.Key, oldestKey) < 0))
                {
                    oldestKey = pair.Key;
                    oldestEntry = pair.Value;
                }
            }

            if (oldestKey == null || oldestEntry == null) return false;
            return RemoveLocked(oldestKey, oldestEntry);
        }

        private bool RemoveLocked(string key, HttpCacheEntry expected)
        {
            if (!_cache.TryGetValue(key, out var current) || !ReferenceEquals(current, expected))
                return false;
            if (!_cache.TryRemove(key, out var removed))
                return false;

            _currentBytes = Math.Max(0L, _currentBytes - GetAccountedBytes(removed));
            return true;
        }

        private static bool TryCloneForStorage(
            HttpCacheEntry source,
            StoragePartitionKey partitionKey,
            out HttpCacheEntry owned,
            out long accountedBytes)
        {
            owned = null;
            accountedBytes = 0;

            var body = source.Body?.ToArray() ?? Array.Empty<byte>();
            var headers = source.ResponseHeaders != null
                ? new Dictionary<string, string>(source.ResponseHeaders, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            long metadataBytes = EstimateMetadataBytes(source, headers);
            if (metadataBytes > MaxMetadataBytesPerEntry) return false;

            accountedBytes = body.LongLength + metadataBytes;
            owned = source with
            {
                Body = body,
                ResponseHeaders = headers,
                PartitionKey = partitionKey
            };
            return true;
        }

        private static HttpCacheEntry CloneForCaller(HttpCacheEntry source)
        {
            return source with
            {
                Body = source.Body?.ToArray() ?? Array.Empty<byte>(),
                ResponseHeaders = source.ResponseHeaders != null
                    ? new Dictionary<string, string>(source.ResponseHeaders, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static long EstimateMetadataBytes(
            HttpCacheEntry entry,
            IReadOnlyDictionary<string, string> headers)
        {
            long bytes = Encoding.UTF8.GetByteCount(entry.Url ?? string.Empty) +
                         Encoding.UTF8.GetByteCount(entry.ETag ?? string.Empty) +
                         Encoding.UTF8.GetByteCount(entry.LastModified ?? string.Empty);
            foreach (var pair in headers)
            {
                bytes += Encoding.UTF8.GetByteCount(pair.Key ?? string.Empty);
                bytes += Encoding.UTF8.GetByteCount(pair.Value ?? string.Empty);
                if (bytes > MaxMetadataBytesPerEntry) return bytes;
            }
            return bytes;
        }

        private static long GetAccountedBytes(HttpCacheEntry entry)
        {
            if (entry == null) return 0L;
            var headers = entry.ResponseHeaders ?? new Dictionary<string, string>();
            return (entry.Body?.LongLength ?? 0L) + EstimateMetadataBytes(entry, headers);
        }

        private static bool TryMakeKey(StoragePartitionKey pk, string url, string varyKey, out string key)
        {
            key = null;
            var normalizedUrl = url ?? string.Empty;
            var normalizedVary = varyKey ?? string.Empty;
            if (normalizedUrl.Length > MaxCacheKeyChars || normalizedVary.Length > MaxCacheKeyChars)
                return false;

            var prefix = MakeUrlPrefix(pk, normalizedUrl);
            if (prefix.Length + normalizedVary.Length > MaxCacheKeyChars * 2)
                return false;

            key = $"{prefix}{normalizedVary.Length}:{normalizedVary}";
            return true;
        }

        private static string MakeUrlPrefix(StoragePartitionKey pk, string url)
        {
            var normalizedUrl = url ?? string.Empty;
            return $"pk:{pk.ToStorageKey()}:url:{normalizedUrl.Length}:{normalizedUrl}:vary:";
        }
    }
    public sealed class StorageService
    {
        public PartitionedCookieStore Cookies { get; } = new();
        public PartitionedKeyValueStorage LocalStorage { get; } = new();
        public PartitionedKeyValueStorage SessionStorage { get; } = new(1024 * 1024);
        public PartitionedHttpCache HttpCache { get; } = new();

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            Cookies.ClearPartition(partitionKey);
            LocalStorage.ClearPartition(partitionKey);
            SessionStorage.ClearPartition(partitionKey);
            HttpCache.ClearPartition(partitionKey);
        }

        public void ClearAll()
        {
            Cookies.ClearAll();
            LocalStorage.ClearAll();
            SessionStorage.ClearAll();
            HttpCache.ClearAll();
        }
    }
}
