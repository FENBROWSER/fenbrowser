using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Network;

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
            if (string.IsNullOrEmpty(url)) return "null";
            var parsed = WhatwgUrl.Parse(url);
            if (parsed == null) return "null";
            var origin = parsed.ComputeOrigin();
            if (origin.Kind == Network.UrlOriginKind.Opaque) return "null";

            var host = parsed.Hostname;
            var domain = GetApproximateRegistrableDomain(host);
            return $"{parsed.Scheme}://{domain}";
        }

        private static string GetApproximateRegistrableDomain(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return "null";

            var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (IPAddress.TryParse(normalized, out _))
                return normalized;

            var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? parts[^2] + "." + parts[^1]
                : normalized;
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
        public CookieSameSite SameSite { get; init; } = CookieSameSite.Lax;
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

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Cookie>> _cookiesByDomain
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _mutationLock = new();
        private int _cookieCount;

        public void Set(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            var domain = NormalizeDomain(cookie.Domain);
            if (domain.Length == 0) throw new ArgumentException("Cookie domain cannot be empty.", nameof(cookie));

            var actualKey = partitionKey ?? (cookie.IsPartitioned ? cookie.PartitionKey : null);
            var storeKey = MakeKey(actualKey, cookie.Name, cookie.Path);

            lock (_mutationLock)
            {
                if (cookie.IsExpired)
                {
                    DeleteLocked(domain, storeKey);
                    return;
                }

                var bucket = _cookiesByDomain.GetOrAdd(
                    domain,
                    static _ => new ConcurrentDictionary<string, Cookie>(StringComparer.Ordinal));

                if (bucket.TryGetValue(storeKey, out var existing))
                {
                    if (!existing.IsExpired)
                    {
                        cookie = cookie with { CreationTime = existing.CreationTime };
                        bucket[storeKey] = cookie with { Domain = domain, PartitionKey = actualKey };
                        return;
                    }

                    if (bucket.TryRemove(storeKey, out _))
                        _cookieCount = Math.Max(0, _cookieCount - 1);
                }

                if (!EnsureCapacityForNewCookieLocked(domain, bucket))
                    return;

                _cookiesByDomain[domain] = bucket;
                bucket[storeKey] = cookie with { Domain = domain, PartitionKey = actualKey };
                _cookieCount++;
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

            foreach (var candidateDomain in EnumerateCandidateDomains(host))
            {
                if (!_cookiesByDomain.TryGetValue(candidateDomain, out var bucket))
                    continue;

                foreach (var pair in bucket)
                {
                    var cookie = pair.Value;
                    if (cookie.IsExpired)
                    {
                        RemoveExpiredCookie(candidateDomain, bucket, pair.Key);
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

            lock (_mutationLock)
            {
                DeleteLocked(domain, MakeKey(actualKey, cookie.Name, cookie.Path));
            }
        }

        public void DeleteByName(string domain, string name, StoragePartitionKey? partitionKey = null)
        {
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name)) return;
            var normalizedDomain = NormalizeDomain(domain);
            var partitionKeyText = partitionKey?.ToStorageKey() ?? "unpartitioned";
            var prefix = $"pk:{partitionKeyText}:{name}:";

            lock (_mutationLock)
            {
                if (!_cookiesByDomain.TryGetValue(normalizedDomain, out var bucket)) return;
                foreach (var key in bucket.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal) && bucket.TryRemove(key, out _))
                        _cookieCount = Math.Max(0, _cookieCount - 1);
                }
                RemoveBucketIfEmptyLocked(normalizedDomain, bucket);
            }
        }

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var prefix = $"pk:{partitionKey.ToStorageKey()}:";
            lock (_mutationLock)
            {
                foreach (var domainPair in _cookiesByDomain)
                {
                    var bucket = domainPair.Value;
                    foreach (var key in bucket.Keys)
                    {
                        if (key.StartsWith(prefix, StringComparison.Ordinal) && bucket.TryRemove(key, out _))
                            _cookieCount = Math.Max(0, _cookieCount - 1);
                    }
                    RemoveBucketIfEmptyLocked(domainPair.Key, bucket);
                }
            }
        }

        public void ClearAll()
        {
            lock (_mutationLock)
            {
                _cookiesByDomain.Clear();
                _cookieCount = 0;
            }
        }

        private bool EnsureCapacityForNewCookieLocked(
            string targetDomain,
            ConcurrentDictionary<string, Cookie> targetBucket)
        {
            while (targetBucket.Count >= MaxCookiesPerDomain)
            {
                if (!EvictOldestFromBucketLocked(targetDomain, targetBucket, removeEmptyBucket: false))
                    return false;
            }

            while (_cookieCount >= MaxCookiesTotal)
            {
                if (!EvictOldestGlobalLocked())
                    return false;
            }

            return true;
        }

        private bool EvictOldestGlobalLocked()
        {
            string oldestDomain = null;
            string oldestKey = null;
            Cookie oldestCookie = null;
            ConcurrentDictionary<string, Cookie> oldestBucket = null;

            foreach (var domainPair in _cookiesByDomain)
            {
                foreach (var pair in domainPair.Value)
                {
                    if (oldestCookie == null ||
                        pair.Value.CreationTime < oldestCookie.CreationTime ||
                        (pair.Value.CreationTime == oldestCookie.CreationTime &&
                         string.CompareOrdinal(pair.Key, oldestKey) < 0))
                    {
                        oldestDomain = domainPair.Key;
                        oldestKey = pair.Key;
                        oldestCookie = pair.Value;
                        oldestBucket = domainPair.Value;
                    }
                }
            }

            if (oldestBucket == null || oldestKey == null || !oldestBucket.TryRemove(oldestKey, out _))
                return false;

            _cookieCount = Math.Max(0, _cookieCount - 1);
            RemoveBucketIfEmptyLocked(oldestDomain, oldestBucket);
            return true;
        }

        private bool EvictOldestFromBucketLocked(
            string domain,
            ConcurrentDictionary<string, Cookie> bucket,
            bool removeEmptyBucket)
        {
            string oldestKey = null;
            Cookie oldestCookie = null;
            foreach (var pair in bucket)
            {
                if (oldestCookie == null ||
                    pair.Value.CreationTime < oldestCookie.CreationTime ||
                    (pair.Value.CreationTime == oldestCookie.CreationTime &&
                     string.CompareOrdinal(pair.Key, oldestKey) < 0))
                {
                    oldestKey = pair.Key;
                    oldestCookie = pair.Value;
                }
            }

            if (oldestKey == null || !bucket.TryRemove(oldestKey, out _))
                return false;

            _cookieCount = Math.Max(0, _cookieCount - 1);
            if (removeEmptyBucket)
                RemoveBucketIfEmptyLocked(domain, bucket);
            return true;
        }

        private void RemoveExpiredCookie(
            string domain,
            ConcurrentDictionary<string, Cookie> bucket,
            string key)
        {
            lock (_mutationLock)
            {
                if (!_cookiesByDomain.TryGetValue(domain, out var currentBucket) ||
                    !ReferenceEquals(currentBucket, bucket) ||
                    !bucket.TryGetValue(key, out var current) ||
                    !current.IsExpired)
                {
                    return;
                }

                if (bucket.TryRemove(key, out _))
                    _cookieCount = Math.Max(0, _cookieCount - 1);
                RemoveBucketIfEmptyLocked(domain, bucket);
            }
        }

        private void DeleteLocked(string domain, string storeKey)
        {
            if (!_cookiesByDomain.TryGetValue(domain, out var bucket)) return;
            if (bucket.TryRemove(storeKey, out _))
                _cookieCount = Math.Max(0, _cookieCount - 1);
            RemoveBucketIfEmptyLocked(domain, bucket);
        }

        private void RemoveBucketIfEmptyLocked(
            string domain,
            ConcurrentDictionary<string, Cookie> bucket)
        {
            if (!bucket.IsEmpty) return;
            if (_cookiesByDomain.TryGetValue(domain, out var current) && ReferenceEquals(current, bucket))
                _cookiesByDomain.TryRemove(domain, out _);
        }

        private static string MakeKey(StoragePartitionKey? pk, string name, string path) =>
            $"pk:{pk?.ToStorageKey() ?? "unpartitioned"}:{name}:{path}";

        private static string NormalizeDomain(string domain) =>
            string.IsNullOrWhiteSpace(domain)
                ? string.Empty
                : domain.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();

        private static IEnumerable<string> EnumerateCandidateDomains(string host)
        {
            yield return host;
            if (IPAddress.TryParse(host, out _)) yield break;

            var offset = 0;
            while (true)
            {
                var dot = host.IndexOf('.', offset);
                if (dot < 0 || dot + 1 >= host.Length) yield break;
                offset = dot + 1;
                yield return host.Substring(offset);
            }
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
            return host.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
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
            var key = MakeKey(partitionKey, url, varyKey);
            if (!_cache.TryGetValue(key, out var entry)) return null;

            var now = DateTimeOffset.UtcNow;
            if (!entry.IsStale(now)) return entry;

            lock (_mutationLock)
            {
                if (!_cache.TryGetValue(key, out var current)) return null;
                if (!current.IsStale(now)) return current;

                if (_cache.TryRemove(key, out var removed))
                {
                    _currentBytes -= GetBodyBytes(removed);
                }
                return null;
            }
        }

        public bool Put(StoragePartitionKey partitionKey, string url, HttpCacheEntry entry, string varyKey = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var entryBytes = GetBodyBytes(entry);
            if (entryBytes > _maxBytes / 4) return false;

            var key = MakeKey(partitionKey, url, varyKey);
            lock (_mutationLock)
            {
                EvictStaleLocked(DateTimeOffset.UtcNow);

                var existingBytes = _cache.TryGetValue(key, out var existing)
                    ? GetBodyBytes(existing)
                    : 0L;
                var projectedBytes = _currentBytes - existingBytes + entryBytes;
                if (projectedBytes > _maxBytes) return false;

                _cache[key] = entry with { PartitionKey = partitionKey };
                _currentBytes = projectedBytes;
                return true;
            }
        }

        public void Invalidate(StoragePartitionKey partitionKey, string url)
        {
            var prefix = MakeUrlPrefix(partitionKey, url);
            lock (_mutationLock)
            {
                foreach (var key in _cache.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                        _cache.TryRemove(key, out var entry))
                    {
                        _currentBytes -= GetBodyBytes(entry);
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
                        _cache.TryRemove(key, out var entry))
                    {
                        _currentBytes -= GetBodyBytes(entry);
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
                if (pair.Value.IsStale(now) && _cache.TryRemove(pair.Key, out var removed))
                {
                    _currentBytes -= GetBodyBytes(removed);
                }
            }
        }

        private static long GetBodyBytes(HttpCacheEntry entry) => entry?.Body?.LongLength ?? 0L;

        private static string MakeKey(StoragePartitionKey pk, string url, string varyKey)
        {
            var normalizedUrl = url ?? string.Empty;
            var normalizedVary = varyKey ?? string.Empty;
            return $"{MakeUrlPrefix(pk, normalizedUrl)}{normalizedVary.Length}:{normalizedVary}";
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