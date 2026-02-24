using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace FenBrowser.Core.Compat
{
    /// <summary>
    /// In-memory HTTP cache with proper Cache-Control, ETag, and Last-Modified semantics.
    /// Replaces the previous stub that always returned null.
    /// </summary>
    public sealed class HttpCache
    {
        private static readonly HttpCache _instance = new HttpCache();
        public static HttpCache Instance => _instance;

        // Keyed by canonical URL string
        private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Maximum cached response body size (4 MB per entry)
        private const int MaxBodyBytes = 4 * 1024 * 1024;

        // Maximum total number of cached entries before LRU eviction
        private const int MaxEntries = 512;

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Try to serve a response from cache. Returns null when no valid cached entry exists.
        /// Handles conditional revalidation headers automatically.
        /// </summary>
        public async Task<string> GetStringAsync(HttpClient client, HttpRequestMessage req)
        {
            var key = CacheKey(req);
            if (key == null) return null;

            if (!_cache.TryGetValue(key, out var entry)) return null;

            // Check freshness
            if (!entry.IsFresh())
            {
                // Try conditional GET if we have validators
                if (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue)
                {
                    var revalResult = await RevalidateStringAsync(client, req, entry);
                    return revalResult; // null means do a full fetch
                }
                _cache.TryRemove(key, out _);
                return null;
            }

            entry.LastAccess = DateTimeOffset.UtcNow;
            return entry.BodyString;
        }

        /// <summary>
        /// Try to serve a binary response from cache. Returns null when no valid cached entry exists.
        /// </summary>
        public async Task<byte[]> GetBufferAsync(HttpClient? client, HttpRequestMessage req)
        {
            var key = CacheKey(req);
            if (key == null) return null;

            if (!_cache.TryGetValue(key, out var entry)) return null;

            if (!entry.IsFresh())
            {
                if (client != null && (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue))
                {
                    var revalResult = await RevalidateBufferAsync(client, req, entry);
                    return revalResult;
                }
                _cache.TryRemove(key, out _);
                return null;
            }

            entry.LastAccess = DateTimeOffset.UtcNow;
            return entry.BodyBytes;
        }

        /// <summary>
        /// Store a string response in the cache if the response is cacheable.
        /// </summary>
        public void StoreString(HttpRequestMessage req, HttpResponseMessage resp, string body)
        {
            if (body == null || body.Length > MaxBodyBytes) return;
            var entry = TryBuildEntry(req, resp);
            if (entry == null) return;
            entry.BodyString = body;
            Store(CacheKey(req), entry);
        }

        /// <summary>
        /// Store a binary response in the cache if cacheable.
        /// </summary>
        public void StoreBytes(HttpRequestMessage req, HttpResponseMessage resp, byte[] body)
        {
            if (body == null || body.Length > MaxBodyBytes) return;
            var entry = TryBuildEntry(req, resp);
            if (entry == null) return;
            entry.BodyBytes = body;
            Store(CacheKey(req), entry);
        }

        // ------------------------------------------------------------------ private helpers

        private static string? CacheKey(HttpRequestMessage req)
        {
            if (req?.RequestUri == null) return null;
            // Only cache GET/HEAD
            if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head) return null;
            return req.RequestUri.AbsoluteUri;
        }

        private static CachedEntry? TryBuildEntry(HttpRequestMessage req, HttpResponseMessage resp)
        {
            if (resp == null) return null;
            // Only cache 200/203/204/206/300/301/302/304/307/308 where appropriate
            var status = (int)resp.StatusCode;
            if (status != 200 && status != 203 && status != 204 && status != 206) return null;
            if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head) return null;

            var cc = resp.Headers.CacheControl;

            // no-store means do not cache at all
            if (cc?.NoStore == true) return null;

            DateTimeOffset? expires = null;
            int maxAgeSeconds = 0;

            if (cc?.MaxAge != null)
            {
                maxAgeSeconds = (int)cc.MaxAge.Value.TotalSeconds;
                expires = DateTimeOffset.UtcNow.AddSeconds(maxAgeSeconds);
            }
            else if (resp.Content?.Headers?.Expires != null)
            {
                expires = resp.Content.Headers.Expires;
            }
            else
            {
                // Default heuristic: cache for 60 s if no directive
                expires = DateTimeOffset.UtcNow.AddSeconds(60);
            }

            var entry = new CachedEntry
            {
                Url = req.RequestUri?.AbsoluteUri ?? "",
                StatusCode = status,
                Expires = expires,
                CachedAt = DateTimeOffset.UtcNow,
                LastAccess = DateTimeOffset.UtcNow,
                MustRevalidate = cc?.MustRevalidate ?? false
            };

            // Store validators
            if (resp.Headers.ETag != null)
                entry.ETag = resp.Headers.ETag.Tag;
            if (resp.Content?.Headers?.LastModified != null)
                entry.LastModified = resp.Content.Headers.LastModified;

            return entry;
        }

        private void Store(string? key, CachedEntry entry)
        {
            if (key == null || entry == null) return;

            // LRU eviction if we're at capacity
            if (_cache.Count >= MaxEntries)
                EvictOldest();

            _cache[key] = entry;
        }

        private void EvictOldest()
        {
            // Remove the 10% oldest entries by last access time
            var toEvict = new List<string>();
            DateTimeOffset cutoff = DateTimeOffset.UtcNow;
            string? oldest = null;
            DateTimeOffset oldestTime = DateTimeOffset.MaxValue;

            foreach (var kv in _cache)
            {
                if (kv.Value.LastAccess < oldestTime)
                {
                    oldestTime = kv.Value.LastAccess;
                    oldest = kv.Key;
                }
            }

            if (oldest != null)
                _cache.TryRemove(oldest, out _);
        }

        private async Task<string?> RevalidateStringAsync(HttpClient client, HttpRequestMessage original, CachedEntry entry)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, original.RequestUri);
                if (!string.IsNullOrEmpty(entry.ETag))
                    req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(entry.ETag));
                if (entry.LastModified.HasValue)
                    req.Headers.IfModifiedSince = entry.LastModified.Value;

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    // Still fresh — update expiry
                    RefreshEntry(entry, resp);
                    return entry.BodyString;
                }

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    StoreString(original, resp, body);
                    return body;
                }

                _cache.TryRemove(CacheKey(original)!, out _);
                return null;
            }
            catch
            {
                // Network error during revalidation — serve stale if must-revalidate not set
                if (!entry.MustRevalidate) return entry.BodyString;
                return null;
            }
        }

        private async Task<byte[]?> RevalidateBufferAsync(HttpClient client, HttpRequestMessage original, CachedEntry entry)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, original.RequestUri);
                if (!string.IsNullOrEmpty(entry.ETag))
                    req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(entry.ETag));
                if (entry.LastModified.HasValue)
                    req.Headers.IfModifiedSince = entry.LastModified.Value;

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    RefreshEntry(entry, resp);
                    return entry.BodyBytes;
                }

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsByteArrayAsync();
                    StoreBytes(original, resp, body);
                    return body;
                }

                _cache.TryRemove(CacheKey(original)!, out _);
                return null;
            }
            catch
            {
                if (!entry.MustRevalidate) return entry.BodyBytes;
                return null;
            }
        }

        private static void RefreshEntry(CachedEntry entry, HttpResponseMessage resp)
        {
            var cc = resp.Headers.CacheControl;
            if (cc?.MaxAge != null)
                entry.Expires = DateTimeOffset.UtcNow.AddSeconds((int)cc.MaxAge.Value.TotalSeconds);
            else if (resp.Content?.Headers?.Expires != null)
                entry.Expires = resp.Content.Headers.Expires;
            else
                entry.Expires = DateTimeOffset.UtcNow.AddSeconds(60);

            entry.CachedAt = DateTimeOffset.UtcNow;
            entry.LastAccess = DateTimeOffset.UtcNow;

            if (resp.Headers.ETag != null)
                entry.ETag = resp.Headers.ETag.Tag;
        }

        // ------------------------------------------------------------------ entry model

        private sealed class CachedEntry
        {
            public string Url { get; set; } = "";
            public int StatusCode { get; set; }
            public DateTimeOffset? Expires { get; set; }
            public DateTimeOffset CachedAt { get; set; }
            public DateTimeOffset LastAccess { get; set; }
            public bool MustRevalidate { get; set; }
            public string? ETag { get; set; }
            public DateTimeOffset? LastModified { get; set; }
            public string? BodyString { get; set; }
            public byte[]? BodyBytes { get; set; }

            public bool IsFresh()
            {
                if (Expires == null) return false;
                return DateTimeOffset.UtcNow < Expires.Value;
            }
        }
    }
}
