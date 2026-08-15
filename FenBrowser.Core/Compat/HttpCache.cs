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
    /// refuses request variants it cannot key correctly (credentials/Range/Vary/HEAD).
    /// </summary>
    public sealed class HttpCache
    {
        private static readonly HttpCache _instance = new HttpCache();
        public static HttpCache Instance => _instance;

        private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);

        private const int MaxBodyBytes = 4 * 1024 * 1024;
        private const int MaxManagedStringChars = MaxBodyBytes / sizeof(char);
        private const int MaxEntries = 512;

        public async Task<string> GetStringAsync(HttpClient client, HttpRequestMessage req)
        {
            var key = CacheKey(req);
            if (key == null || BypassesCache(req)) return null;

            if (!_cache.TryGetValue(key, out var entry)) return null;

            if (!entry.IsFresh())
            {
                if (client != null && (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue))
                {
                    return await RevalidateStringAsync(client, req, entry).ConfigureAwait(false);
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
            if (key == null || BypassesCache(req)) return null;

            if (!_cache.TryGetValue(key, out var entry)) return null;

            if (!entry.IsFresh())
            {
                if (client != null && (!string.IsNullOrEmpty(entry.ETag) || entry.LastModified.HasValue))
                {
                    return await RevalidateBufferAsync(client, req, entry).ConfigureAwait(false);
                }

                _cache.TryRemove(key, out _);
                return null;
            }

            entry.LastAccess = DateTimeOffset.UtcNow;
            return CloneBytes(entry.BodyBytes);
        }

        public void StoreString(HttpRequestMessage req, HttpResponseMessage resp, string body)
        {
            // Strings consume two bytes per UTF-16 code unit in managed memory before
            // object/header overhead. Bound the representation we actually retain.
            if (body == null || body.Length > MaxManagedStringChars) return;
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

            // The cache owns its representation. Retaining the caller's mutable array
            // let later caller writes silently corrupt every future cache hit.
            entry.BodyBytes = (byte[])body.Clone();
            Store(CacheKey(req), entry);
        }

        private static string? CacheKey(HttpRequestMessage req)
        {
            if (req?.RequestUri == null) return null;
            if (req.Method != HttpMethod.Get) return null;
            if (req.Headers.Range != null) return null;
            if (HasCredentialContext(req)) return null;
            return req.RequestUri.AbsoluteUri;
        }

        private static CachedEntry? TryBuildEntry(HttpRequestMessage req, HttpResponseMessage resp)
        {
            if (resp == null || req?.RequestUri == null) return null;
            if (req.Method != HttpMethod.Get || req.Headers.Range != null || HasCredentialContext(req)) return null;

            var status = (int)resp.StatusCode;
            if (!IsSupportedFullResponseStatus(status)) return null;

            var cc = resp.Headers.CacheControl;
            if (cc?.NoStore == true) return null;
            if (HasAnyVary(resp)) return null;

            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? expires;
            if (cc?.NoCache == true)
            {
                // Stored no-cache responses may only be reused after successful
                // validation; representing them as immediately stale guarantees that.
                expires = now;
            }
            else if (cc?.MaxAge != null)
            {
                expires = SafeAdd(now, cc.MaxAge.Value);
            }
            else if (resp.Content?.Headers?.Expires != null)
            {
                expires = resp.Content.Headers.Expires;
            }
            else
            {
                expires = now.AddSeconds(60);
            }

            var entry = new CachedEntry
            {
                Url = req.RequestUri.AbsoluteUri,
                StatusCode = status,
                Expires = expires,
                CachedAt = now,
                LastAccess = now,
                MustRevalidate = (cc?.MustRevalidate ?? false) || (cc?.NoCache ?? false)
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
            oldest.Sort(static (a, b) => a.Value.LastAccess.CompareTo(b.Value.LastAccess));

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
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    RefreshEntry(entry, resp);
                    return entry.BodyString;
                }

                if (IsSupportedFullResponseStatus((int)resp.StatusCode))
                {
                    if (!await BufferWithinLimitAsync(resp.Content).ConfigureAwait(false))
                    {
                        RemoveOriginal(original);
                        return null;
                    }

                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > MaxManagedStringChars)
                    {
                        RemoveOriginal(original);
                        return null;
                    }

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
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    RefreshEntry(entry, resp);
                    return CloneBytes(entry.BodyBytes);
                }

                if (IsSupportedFullResponseStatus((int)resp.StatusCode))
                {
                    if (!await BufferWithinLimitAsync(resp.Content).ConfigureAwait(false))
                    {
                        RemoveOriginal(original);
                        return null;
                    }

                    var body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (body.Length > MaxBodyBytes)
                    {
                        RemoveOriginal(original);
                        return null;
                    }

                    StoreBytes(original, resp, body);
                    return body;
                }

                RemoveOriginal(original);
                return null;
            }
            catch
            {
                if (!entry.MustRevalidate) return CloneBytes(entry.BodyBytes);
                return null;
            }
        }

        private static async Task<bool> BufferWithinLimitAsync(HttpContent content)
        {
            if (content == null)
                return true;

            if (content.Headers.ContentLength is long declaredLength && declaredLength > MaxBodyBytes)
                return false;

            try
            {
                await content.LoadIntoBufferAsync(MaxBodyBytes).ConfigureAwait(false);
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        private static bool IsSupportedFullResponseStatus(int status)
            => status is 200 or 203 or 204;

        private static HttpRequestMessage CreateRevalidationRequest(HttpRequestMessage original, CachedEntry entry)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, original.RequestUri)
            {
                Version = original.Version,
                VersionPolicy = original.VersionPolicy
            };

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
            var now = DateTimeOffset.UtcNow;
            entry.MustRevalidate = (cc?.MustRevalidate ?? entry.MustRevalidate) || (cc?.NoCache ?? false);

            if (cc?.NoCache == true)
                entry.Expires = now;
            else if (cc?.MaxAge != null)
                entry.Expires = SafeAdd(now, cc.MaxAge.Value);
            else if (resp.Content?.Headers?.Expires != null)
                entry.Expires = resp.Content.Headers.Expires;
            else
                entry.Expires = now.AddSeconds(60);

            entry.CachedAt = now;
            entry.LastAccess = now;

            if (resp.Headers.ETag != null)
                entry.ETag = resp.Headers.ETag.Tag;
            if (resp.Content?.Headers?.LastModified != null)
                entry.LastModified = resp.Content.Headers.LastModified;
        }

        private static DateTimeOffset SafeAdd(DateTimeOffset now, TimeSpan delta)
        {
            if (delta <= TimeSpan.Zero)
                return now;

            var maxDelta = DateTimeOffset.MaxValue - now;
            return delta >= maxDelta ? DateTimeOffset.MaxValue : now + delta;
        }

        private static bool BypassesCache(HttpRequestMessage req)
        {
            if (req == null || req.Headers.Range != null || HasCredentialContext(req))
                return true;

            var cc = req.Headers.CacheControl;
            if (cc == null)
                return false;

            if (cc.NoStore || cc.NoCache)
                return true;

            return cc.MaxAge.HasValue && cc.MaxAge.Value <= TimeSpan.Zero;
        }

        private static bool HasCredentialContext(HttpRequestMessage req)
        {
            if (req?.Headers == null)
                return false;

            return req.Headers.Authorization != null || req.Headers.Contains("Cookie");
        }

        private static bool HasAnyVary(HttpResponseMessage resp)
            => resp?.Headers?.Vary is { Count: > 0 };

        private static byte[]? CloneBytes(byte[]? value)
            => value == null ? null : (byte[])value.Clone();

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
