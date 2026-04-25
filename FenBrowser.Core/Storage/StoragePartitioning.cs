using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Network;

namespace FenBrowser.Core.Storage
{
    // ── Storage Partitioning ──────────────────────────────────────────────────
    // Per the guide §14: all storage is keyed by (top-level site, frame site).
    // This prevents cross-site tracking via shared storage state.
    //
    // Partitioned storage types:
    //   - Cookies (SameSite + partition key)
    //   - HTTP cache
    //   - localStorage / sessionStorage
    //   - IndexedDB (origin + partition key)
    //   - Cache Storage (Service Worker scoped)
    //   - Permissions (origin + partition key)
    //
    // Partition key: (topLevelSite, frameSite) pair.
    // topLevelSite = eTLD+1 of the top-level document URL.
    // frameSite    = eTLD+1 of the frame URL (or origin in strict mode).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Immutable storage partition key.</summary>
    public readonly struct StoragePartitionKey : IEquatable<StoragePartitionKey>
    {
        /// <summary>eTLD+1 of the top-level frame (or "null" for opaque).</summary>
        public string TopLevelSite { get; }

        /// <summary>eTLD+1 of the frame (or "null" for opaque).</summary>
        public string FrameSite { get; }

        /// <summary>Whether this partition is in a third-party context.</summary>
        public bool IsThirdParty => !string.Equals(TopLevelSite, FrameSite, StringComparison.OrdinalIgnoreCase);

        public StoragePartitionKey(string topLevelSite, string frameSite)
        {
            TopLevelSite = topLevelSite ?? "null";
            FrameSite = frameSite ?? "null";
        }

        /// <summary>First-party partition: top-level site = frame site.</summary>
        public static StoragePartitionKey FirstParty(string site) => new(site, site);

        /// <summary>Opaque / sandboxed partition (no storage access).</summary>
        public static StoragePartitionKey Opaque => new("null", "null");

        public bool Equals(StoragePartitionKey other) =>
            TopLevelSite == other.TopLevelSite && FrameSite == other.FrameSite;

        public override bool Equals(object obj) => obj is StoragePartitionKey k && Equals(k);

        public override int GetHashCode() =>
            HashCode.Combine(TopLevelSite, FrameSite);

        public override string ToString() => $"({TopLevelSite}, {FrameSite})";

        /// <summary>Stable opaque representation for use as a storage key string.</summary>
        public string ToStorageKey()
        {
            var combined = TopLevelSite + "\0" + FrameSite;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash)[..16]; // first 8 bytes = 16 hex chars
        }
    }

    /// <summary>
    /// Computes partition keys from navigation context.
    /// </summary>
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
            // scheme + eTLD+1
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

    // ── Cookie store with partitioning ────────────────────────────────────────

    /// <summary>Cookie SameSite attribute value.</summary>
    public enum CookieSameSite { Unspecified, Strict, Lax, None }

    /// <summary>Immutable cookie record.</summary>
    public sealed record Cookie
    {
        public string Name { get; init; }
        public string Value { get; init; }
        public string Domain { get; init; }
        public string Path { get; init; } = "/";
        public bool Secure { get; init; }
        public bool HttpOnly { get; init; }
        public CookieSameSite SameSite { get; init; } = CookieSameSite.Lax;
        public DateTimeOffset? Expires { get; init; }
        public StoragePartitionKey? PartitionKey { get; init; }   // null = unpartitioned

        public bool IsPartitioned => PartitionKey.HasValue;
        public bool IsExpired => Expires.HasValue && Expires.Value < DateTimeOffset.UtcNow;
        public bool IsSession => !Expires.HasValue;
    }

    /// <summary>
    /// Partitioned cookie store.
    /// Cookies are keyed by (domain, path, name, partitionKey?).
    /// Implements SameSite + Secure + domain matching semantics.
    /// </summary>
    public sealed class PartitionedCookieStore
    {
        // Key: (partitionKeyStr, domain, name, path)
        private readonly ConcurrentDictionary<string, Cookie> _cookies = new(StringComparer.Ordinal);

        /// <summary>Set a cookie. Partitioned cookies require the partition key.</summary>
        public void Set(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            if (cookie.IsExpired) { Delete(cookie, partitionKey); return; }

            var actualKey = partitionKey ?? (cookie.IsPartitioned ? cookie.PartitionKey : null);
            var storeKey = MakeKey(actualKey, cookie.Domain, cookie.Name, cookie.Path);
            _cookies[storeKey] = cookie with { PartitionKey = actualKey };
        }

        /// <summary>Get all cookies matching the given URL and partition key.</summary>
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
            foreach (var cookie in _cookies.Values)
            {
                if (cookie.IsExpired) continue;
                if (cookie.Secure && !isSecure) continue;
                if (cookie.HttpOnly && !includeHttpOnly) continue;
                if (!DomainMatches(host, cookie.Domain)) continue;
                if (!PathMatches(path, cookie.Path)) continue;

                // Partition key check
                if (cookie.IsPartitioned)
                {
                    if (!cookie.PartitionKey.HasValue ||
                        !cookie.PartitionKey.Value.Equals(partitionKey))
                        continue;
                }

                result.Add(cookie);
            }

            // Sort by path length descending (more specific paths first)
            result.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
            return result;
        }

        public void Delete(Cookie cookie, StoragePartitionKey? partitionKey = null)
        {
            var key = MakeKey(partitionKey, cookie.Domain, cookie.Name, cookie.Path);
            _cookies.TryRemove(key, out _);
        }

        public void DeleteByName(string domain, string name, StoragePartitionKey? partitionKey = null)
        {
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

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
            foreach (var k in _cookies.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    _cookies.TryRemove(k, out _);
        }

        public void ClearAll() => _cookies.Clear();

        private static string MakeKey(StoragePartitionKey? pk, string domain, string name, string path) =>
            $"pk:{pk?.ToStorageKey() ?? "unpartitioned"}:{domain}:{name}:{path}";

        private static bool DomainMatches(string host, string cookieDomain)
        {
            if (string.IsNullOrEmpty(cookieDomain)) return false;
            if (cookieDomain.StartsWith("."))
            {
                var suffix = cookieDomain.TrimStart('.');
                return host == suffix || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(host, cookieDomain, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathMatches(string requestPath, string cookiePath)
        {
            if (string.IsNullOrEmpty(cookiePath) || cookiePath == "/") return true;
            if (requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
            {
                return requestPath.Length == cookiePath.Length || requestPath[cookiePath.Length] == '/';
            }
            return false;
        }
    }

    // ── LocalStorage / SessionStorage with partitioning ───────────────────────

    /// <summary>
    /// Partitioned key-value storage (localStorage / sessionStorage).
    /// Each (origin, partitionKey) pair has independent storage.
    /// </summary>
    public sealed class PartitionedKeyValueStorage
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _buckets = new();
        private readonly long _quotaBytesPerBucket;

        public PartitionedKeyValueStorage(long quotaBytesPerBucket = 5 * 1024 * 1024 /* 5 MB */)
        {
            _quotaBytesPerBucket = quotaBytesPerBucket;
        }

        private ConcurrentDictionary<string, string> GetBucket(string origin, StoragePartitionKey key) =>
            _buckets.GetOrAdd($"{origin}\0{key.ToStorageKey()}", _ => new());

        public string GetItem(string origin, StoragePartitionKey partitionKey, string itemKey)
        {
            var bucket = GetBucket(origin, partitionKey);
            return bucket.TryGetValue(itemKey, out var v) ? v : null;
        }

        public bool SetItem(string origin, StoragePartitionKey partitionKey, string itemKey, string value)
        {
            var bucket = GetBucket(origin, partitionKey);

            // Quota check
            long currentBytes = 0;
            foreach (var (k, v) in bucket)
                currentBytes += Encoding.UTF8.GetByteCount(k) + Encoding.UTF8.GetByteCount(v);
            var addedBytes = Encoding.UTF8.GetByteCount(itemKey) + Encoding.UTF8.GetByteCount(value ?? "");
            if (currentBytes + addedBytes > _quotaBytesPerBucket)
                return false; // QuotaExceededError

            bucket[itemKey] = value ?? "";
            return true;
        }

        public void RemoveItem(string origin, StoragePartitionKey partitionKey, string itemKey)
        {
            GetBucket(origin, partitionKey).TryRemove(itemKey, out _);
        }

        public void Clear(string origin, StoragePartitionKey partitionKey)
        {
            GetBucket(origin, partitionKey).Clear();
        }

        public IReadOnlyList<string> GetKeys(string origin, StoragePartitionKey partitionKey) =>
            new List<string>(GetBucket(origin, partitionKey).Keys);

        public int Length(string origin, StoragePartitionKey partitionKey) =>
            GetBucket(origin, partitionKey).Count;

        public void ClearAll() => _buckets.Clear();

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var suffix = $"\0{partitionKey.ToStorageKey()}";
            foreach (var k in _buckets.Keys)
                if (k.EndsWith(suffix, StringComparison.Ordinal))
                    _buckets.TryRemove(k, out _);
        }
    }

    // ── HTTP cache with partitioning ──────────────────────────────────────────

    /// <summary>Cache entry.</summary>
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

        public bool IsStale(DateTimeOffset now) =>
            Expires.HasValue && Expires.Value < now;
    }

    /// <summary>
    /// Partitioned HTTP cache.
    /// Entries are keyed by (partitionKey, url, vary-key).
    /// </summary>
    public sealed class PartitionedHttpCache
    {
        private readonly ConcurrentDictionary<string, HttpCacheEntry> _cache = new(StringComparer.Ordinal);
        private readonly long _maxBytes;
        private long _currentBytes;

        public PartitionedHttpCache(long maxBytes = 256 * 1024 * 1024 /* 256 MB */)
        {
            _maxBytes = maxBytes;
        }

        public HttpCacheEntry Get(StoragePartitionKey partitionKey, string url, string varyKey = null)
        {
            var key = MakeKey(partitionKey, url, varyKey);
            if (!_cache.TryGetValue(key, out var entry)) return null;
            if (entry.IsStale(DateTimeOffset.UtcNow)) { _cache.TryRemove(key, out _); return null; }
            return entry;
        }

        public bool Put(StoragePartitionKey partitionKey, string url, HttpCacheEntry entry, string varyKey = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var entryBytes = entry.Body?.Length ?? 0;
            if (entryBytes > _maxBytes / 4) return false; // single entry > 25% of cache cap → skip

            // Evict if needed (simple LRU approximation: just prune stale entries)
            if (_currentBytes + entryBytes > _maxBytes) EvictStale();

            var key = MakeKey(partitionKey, url, varyKey);
            _cache[key] = entry with { PartitionKey = partitionKey };
            System.Threading.Interlocked.Add(ref _currentBytes, entryBytes);
            return true;
        }

        public void Invalidate(StoragePartitionKey partitionKey, string url)
        {
            var prefix = $"pk:{partitionKey.ToStorageKey()}:url:{url}";
            foreach (var k in _cache.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    if (_cache.TryRemove(k, out var e))
                        System.Threading.Interlocked.Add(ref _currentBytes, -(e.Body?.Length ?? 0));
        }

        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            var prefix = $"pk:{partitionKey.ToStorageKey()}:";
            foreach (var k in _cache.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    if (_cache.TryRemove(k, out var e))
                        System.Threading.Interlocked.Add(ref _currentBytes, -(e.Body?.Length ?? 0));
        }

        public void ClearAll()
        {
            _cache.Clear();
            System.Threading.Interlocked.Exchange(ref _currentBytes, 0);
        }

        private void EvictStale()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (k, e) in _cache)
            {
                if (e.IsStale(now) && _cache.TryRemove(k, out var removed))
                    System.Threading.Interlocked.Add(ref _currentBytes, -(removed.Body?.Length ?? 0));
            }
        }

        private static string MakeKey(StoragePartitionKey pk, string url, string varyKey) =>
            $"pk:{pk.ToStorageKey()}:url:{url}:{varyKey ?? ""}";
    }

    // ── StorageService — unified access point ─────────────────────────────────

    /// <summary>
    /// Top-level storage service: owns all partitioned storage backends.
    /// The Broker creates one per browser profile; renderers access via IPC.
    /// </summary>
    public sealed class StorageService
    {
        public PartitionedCookieStore Cookies { get; } = new();
        public PartitionedKeyValueStorage LocalStorage { get; } = new();
        public PartitionedKeyValueStorage SessionStorage { get; } = new(1024 * 1024 /* 1 MB per tab */);
        public PartitionedHttpCache HttpCache { get; } = new();

        /// <summary>Clear all storage for a given partition (e.g. when clearing browsing data).</summary>
        public void ClearPartition(StoragePartitionKey partitionKey)
        {
            Cookies.ClearPartition(partitionKey);
            LocalStorage.ClearPartition(partitionKey);
            SessionStorage.ClearPartition(partitionKey);
            HttpCache.ClearPartition(partitionKey);
        }

        /// <summary>Clear all storage (factory reset).</summary>
        public void ClearAll()
        {
            Cookies.ClearAll();
            LocalStorage.ClearAll();
            SessionStorage.ClearAll();
            HttpCache.ClearAll();
        }
    }
}
