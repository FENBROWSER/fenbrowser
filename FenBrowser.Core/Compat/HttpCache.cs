using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

#pragma warning disable CS8632

namespace FenBrowser.Core.Compat
{
    /// <summary>
    /// Conservative in-memory HTTP cache with Cache-Control, ETag, and
    /// Last-Modified revalidation. This compatibility cache intentionally
    /// refuses request variants it cannot key correctly (Range/Vary/HEAD).
    /// </summary>
    public sealed class HttpCache
    {
        private static readonly HttpCache _instance = new HttpCache();
        public static HttpCache Instance => _instance;

        // URL path/query components are case-sensitive. Never use an
        // OrdinalIgnoreCase comparer for a complete serialized URL.
        private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);

        private const int MaxBodyBytes = 4 * 1024 * 1024;
        private const int MaxEntries = 512;

        public async Task<string> GetStringAsync(HttpClient client, HttpRequestMessage req)
        {
            var key = CacheKey(req);
            if (key == null) return null;
            if (BypassesCache(req)) return null;

            if (!_cache.TryGetValue(key, out var entry)) return null;

            if (!entry.IsFresh())
            {
                if (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue)
                {
                    return await RevalidateStringAsync(client, req, entry);
                }

                _cache.TryRemove(key, out _);
                return null;
            }

            entry.LastAccess = DateTimeOffset.UtcNow;
            return entry.BodyString;
        }

        public async Task<byte[]> GetBufferAsync(HttpClient? client, HttpRequestMessage req)
        {
            var key = CacheKey(req);
            if (key == null) return null;
            if (BypassesCache(req)) return null;

            if (!_cache.TryGetValue(key, out var entry)) return null;

            if (!entry.IsFresh())
            {
                if (client != null && (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue))
                {
                    return await RevalidateBufferAsync(client, req, entry);
                }

                _cache.TryRemove(key, out _);
                return null;
            }

            entry.LastAccess = DateTimeOffset.UtcNow;
            return entry.BodyBytes;
        }

        public void StoreString(HttpRequestMessage req, HttpResponseMessage resp, string body)
        {
            if (body == null || body.Length > MaxBodyBytes) return;
            var entry = TryBuildEntry(req, resp);
            if (entry == null) return;
            entry.BodyString = body;
            Store(CacheKey(req), entry);
        }

        public void StoreBytes(HttpRequestMessage req, HttpResponseMessage resp, byte[] body)
        {
            if (body == null || body.Length > MaxBodyBytes) return;
            var entry = TryBuildEntry(req, resp);
            if (entry == null) return;
            entry.BodyBytes = body;
            Store(CacheKey(req), entry);
        }

        private static string? CacheKey(HttpRequestMessage req)
        {
            if (req?.RequestUri == null) return null;

            // GET and HEAD are different cache methods. This compatibility cache has
            // no method dimension, so cache GET only rather than aliasing the two.
            if (req.Method != HttpMethod.Get) return null;

            // A range response requires the Range request state and Content-Range to
            // participate in cache selection. Refuse it until that model exists.
            if (req.Headers.Range != null) return null;

            return req.RequestUri.AbsoluteUri;
        }

        private static CachedEntry? TryBuildEntry(HttpRequestMessage req, HttpResponseMessage resp)
        {
            if (resp == null || req?.RequestUri == null) return null;
            if (req.Method != HttpMethod.Get || req.Headers.Range != null) return null;

            var status = (int)resp.StatusCode;
            if (!IsSupportedFullResponseStatus(status)) return null;

            var cc = resp.Headers.CacheControl;
            if (cc?.NoStore == true) return null;

            // This cache does not maintain per-entry Vary request values. Any Vary
            // therefore makes the response unsafe to reuse, not just Vary: *.
            if (HasAnyVary(resp)) return null;

            DateTimeOffset? expires;
            if (cc?.MaxAge != null)
            {
                expires = SafeAdd(DateTimeOffset.UtcNow, cc.MaxAge.Value);
            }
            else if (resp.Content?.Headers?.Expires != null)
            {
                expires = resp.Content.Headers.Expires;
            }
            else
            {
                expires = DateTimeOffset.UtcNow.AddSeconds(60);
            }

            var entry = new CachedEntry
            {
                Url = req.RequestUri.AbsoluteUri,
                StatusCode = status,
                Expires = expires,
                CachedAt = DateTimeOffset.UtcNow,
                LastAccess = DateTimeOffset.UtcNow,
                MustRevalidate = cc?.MustRevalidate ?? false
            };

            if (resp.Headers.ETag != null)
                entry.ETag = resp.Headers.ETag.Tag;
            if (resp.Content?.Headers?.LastModified != null)
                entry.LastModified = resp.Content.Headers.LastModified;

            return entry;
        }

        private void Store(string? key, CachedEntry entry)
        {
            if (key == null || entry == null) return;

            if (_cache.Count >= MaxEntries)
                EvictOldest();

            _cache[key] = entry;
        }

        private void EvictOldest()
        {
            var removeCount = Math.Max(1, MaxEntries / 10);
            var oldest = new List<KeyValuePair<string, CachedEntry>>(_cache);
            oldest.Sort((a, b) => a.Value.LastAccess.CompareTo(b.Value.LastAccess));

            var capped = Math.Min(removeCount, oldest.Count);
            for (var i = 0; i < capped; i++)
            {
                _cache.TryRemove(oldest[i].Key, out _);
            }
        }

        private async Task<string?> RevalidateStringAsync(HttpClient client, HttpRequestMessage original, CachedEntry entry)
        {
            try
            {
                using var req = CreateRevalidationRequest(original, entry);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    RefreshEntry(entry, resp);
                    return entry.BodyString;
                }

                if (IsSupportedFullResponseStatus((int)resp.StatusCode))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    StoreString(original, resp, body);
                    return body;
                }

                RemoveOriginal(original);
                return null;
            }
            catch
            {
                if (!entry.MustRevalidate) return entry.BodyString;
                return null;
            }
        }

        private async Task<byte[]?> RevalidateBufferAsync(HttpClient client, HttpRequestMessage original, CachedEntry entry)
        {
            try
            {
                using var req = CreateRevalidationRequest(original, entry);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    RefreshEntry(entry, resp);
                    return entry.BodyBytes;
                }

                if (IsSupportedFullResponseStatus((int)resp.StatusCode))
                {
                    var body = await resp.Content.ReadAsByteArrayAsync();
                    StoreBytes(original, resp, body);
                    return body;
                }

                RemoveOriginal(original);
                return null;
            }
            catch
            {
                if (!entry.MustRevalidate) return entry.BodyBytes;
                return null;
            }
        }

        private static bool IsSupportedFullResponseStatus(int status)
            => status is 200 or 203 or 204;

        private static HttpRequestMessage CreateRevalidationRequest(
            HttpRequestMessage original,
            CachedEntry entry)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, original.RequestUri)
            {
                Version = original.Version,
                VersionPolicy = original.VersionPolicy
            };

            // Preserve request context such as Accept, Accept-Language,
            // Authorization, Cookie and Fetch metadata. A stripped revalidation
            // request can validate a representation for a different request context.
            foreach (var header in original.Headers)
            {
                req.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            req.Headers.IfNoneMatch.Clear();
            req.Headers.IfModifiedSince = null;

            if (!string.IsNullOrEmpty(entry.ETag))
                req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(entry.ETag));
            if (entry.LastModified.HasValue)
                req.Headers.IfModifiedSince = entry.LastModified.Value;

            return req;
        }

        private void RemoveOriginal(HttpRequestMessage original)
        {
            var key = CacheKey(original);
            if (key != null)
            {
                _cache.TryRemove(key, out _);
            }
        }

        private static void RefreshEntry(CachedEntry entry, HttpResponseMessage resp)
        {
            var cc = resp.Headers.CacheControl;
            if (cc?.MaxAge != null)
                entry.Expires = SafeAdd(DateTimeOffset.UtcNow, cc.MaxAge.Value);
            else if (resp.Content?.Headers?.Expires != null)
                entry.Expires = resp.Content.Headers.Expires;
            else
                entry.Expires = DateTimeOffset.UtcNow.AddSeconds(60);

            entry.CachedAt = DateTimeOffset.UtcNow;
            entry.LastAccess = DateTimeOffset.UtcNow;

            if (resp.Headers.ETag != null)
                entry.ETag = resp.Headers.ETag.Tag;
        }

        private static DateTimeOffset SafeAdd(DateTimeOffset now, TimeSpan delta)
        {
            if (delta <= TimeSpan.Zero)
            {
                return now;
            }

            var maxDelta = DateTimeOffset.MaxValue - now;
            return delta >= maxDelta ? DateTimeOffset.MaxValue : now + delta;
        }

        private static bool BypassesCache(HttpRequestMessage req)
        {
            if (req?.Headers.Range != null)
            {
                return true;
            }

            var cc = req?.Headers?.CacheControl;
            if (cc == null)
            {
                return false;
            }

            if (cc.NoStore || cc.NoCache)
            {
                return true;
            }

            if (cc.MaxAge.HasValue && cc.MaxAge.Value <= TimeSpan.Zero)
            {
                return true;
            }

            return false;
        }

        private static bool HasAnyVary(HttpResponseMessage resp)
        {
            return resp?.Headers?.Vary is { Count: > 0 };
        }

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
