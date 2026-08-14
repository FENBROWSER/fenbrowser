using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Network;

namespace FenBrowser.Core.Storage
{
    /// <summary>Immutable storage partition key.</summary>
    public readonly struct StoragePartitionKey : IEquatable<StoragePartitionKey>
    {
        public string TopLevelSite { get; }
        public string FrameSite { get; }
        public bool IsThirdParty => !string.Equals(TopLevelSite, FrameSite, StringComparison.OrdinalIgnoreCase);

        public StoragePartitionKey(string topLevelSite, string frameSite)
        {
            TopLevelSite = topLevelSite ?? "null";
            FrameSite = frameSite ?? "null";
        }

        public static StoragePartitionKey FirstParty(string site) => new(site, site);
        public static StoragePartitionKey Opaque => new("null", "null");

        public bool Equals(StoragePartitionKey other) =>
            TopLevelSite == other.TopLevelSite && FrameSite == other.FrameSite;

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
            var combined = TopLevelSite + "\0" + FrameSite;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash);
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
            var domain = GetEtldPlusOne(host);
            return $"{parsed.Scheme}://{domain}";
        }

        private static string GetEtldPlusOne(string host)
        {
            var parts = host.Split('.');
            return parts.Length >= 2
                ? parts[parts.Length - 2] + "." + parts[parts.Length - 1]
                : host;
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

        public bool IsPartitioned => PartitionKey.HasValue;
        public bool IsExpired => Expires.HasValue && Expires.Value < DateTimeOffset.UtcNow;
        public bool IsSession => !Expires.HasValue;
    }

    public sealed class PartitionedCookieStore
    {
        private readonly ConcurrentDictionary<string, Cookie> _cookies = new(StringComparer.Ordinal);

        public void Set(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            var actualKey = partitionKey ?? (cookie.IsPartitioned ? cookie.PartitionKey : null);

            if (cookie.IsExpired)
            {
                Delete(cookie, actualKey);
                return;
            }

            var storeKey = MakeKey(actualKey, cookie.Domain, cookie.Name, cookie.Path);
            _cookies[storeKey] = cookie with { PartitionKey = actualKey };
        }

        public IReadOnlyList<Cookie> GetForUrl(
            string requestUrl,
            StoragePartitionKey partitionKey,
            bool includeHttpOnly = false)
        {
            var parsed = WhatwgUrl.Parse(requestUrl);
            if (parsed == null) return Array.Empty<Cookie>();

            var host = parsed.Hostname;
            var path = parsed.Pathname;
            bool isSecure = parsed.Scheme == "https" || parsed.Scheme == "wss";
            var result = new List<Cookie>();

            foreach (var pair in _cookies)
            {
                var cookie = pair.Value;
                if (cookie.IsExpired)
                {
                    _cookies.TryRemove(pair.Key, out _);
                    continue;
                }
                if (cookie.Secure && !isSecure) continue;
                if (cookie.HttpOnly && !includeHttpOnly) continue;
                if (!DomainMatches(host, cookie.Domain, cookie.HostOnly)) continue;
                if (!PathMatches(path, cookie.Path)) continue;

                if (cookie.IsPartitioned &&
                    (!cookie.PartitionKey.HasValue || !cookie.PartitionKey.Value.Equals(partitionKey)))
                {
                    continue;
                }

                result.Add(cookie);
            }

            result.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
            return result;
        }

        public void Delete(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            if (cookie == null) return;
            var actualKey = partitionKey ?? (cookie.IsPartitioned ? cookie.PartitionKey : null);
            var key = MakeKey(actualKey, cookie.Domain, cookie.Name, cookie.Path);
            _cookies.TryRemove(key, out _);
        }

        public void DeleteByName(string domain, string name, StoragePartitionKey? partitionKey = null)
        {
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name)) return;

            var partitionKeyText = partitionKey?.ToStorageKey() ?? "unpartitioned";
            var prefix = $"pk:{partitionKeyText}:{domain}:{name}:";
            foreach (var key in _cookies.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _cookies.TryRemove(key, out _);
                }
            }
        }

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var prefix = $"pk:{partitionKey.ToStorageKey()}:";
            foreach (var key in _cookies.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _cookies.TryRemove(key, out _);
                }
            }
        }

        public void ClearAll() => _cookies.Clear();

        private static string MakeKey(StoragePartitionKey? pk, string domain, string name, string path) =>
            $"pk:{pk?.ToStorageKey() ?? "unpartitioned"}:{domain}:{name}:{path}";

        private static bool DomainMatches(string host, string cookieDomain, bool hostOnly)
        {
            if (string.IsNullOrEmpty(cookieDomain)) return false;

            if (hostOnly)
            {
                return string.Equals(host, cookieDomain, StringComparison.OrdinalIgnoreCase);
            }

            var suffix = cookieDomain.TrimStart('.');
            if (string.IsNullOrEmpty(suffix)) return false;

            return string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathMatches(string requestPath, string cookiePath)
        {
            if (string.IsNullOrEmpty(cookiePath) || cookiePath == "/") return true;
            if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal)) return false;

            return requestPath.Length == cookiePath.Length || requestPath[cookiePath.Length] == '/';
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
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return null;
            lock (bucket.SyncRoot)
            {
                return bucket.Items.TryGetValue(itemKey, out var value) ? value : null;
            }
        }

        public bool SetItem(string origin, StoragePartitionKey partitionKey, string itemKey, string value)
        {
            var bucket = GetOrCreateBucket(origin, partitionKey);
            var normalizedValue = value ?? string.Empty;
            var keyBytes = Encoding.UTF8.GetByteCount(itemKey ?? string.Empty);
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
            if (!TryGetBucket(origin, partitionKey, out var bucket)) return;
            lock (bucket.SyncRoot)
            {
                if (bucket.Items.Remove(itemKey, out var removedValue))
                {
                    bucket.Bytes -= Encoding.UTF8.GetByteCount(itemKey ?? string.Empty) +
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

        public bool IsStale(DateTimeOffset now) => Expires.HasValue && Expires.Value < now;
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
            var prefix = $"pk:{partitionKey.ToStorageKey()}:url:{url}";
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

        private static string MakeKey(StoragePartitionKey pk, string url, string varyKey) =>
            $"pk:{pk.ToStorageKey()}:url:{url}:{varyKey ?? ""}";
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
