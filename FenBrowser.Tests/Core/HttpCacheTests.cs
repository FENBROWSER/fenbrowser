using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core.Compat;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// Regression tests for Net-1 tranche: real HTTP cache implementation.
    /// Verifies that responses are cached, freshness is respected, and no-store is honoured.
    /// </summary>
    public class HttpCacheTests
    {
        private static HttpCache NewCache() => HttpCache.Instance;

        // ------------------------------------------------------------------ store and retrieve

        [Fact]
        public async Task HttpCache_StoreAndRetrieve_ReturnsCachedString()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/test-cache-{Guid.NewGuid():N}");

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

            cache.StoreString(req, resp, "hello cached world");

            using var req2 = new HttpRequestMessage(HttpMethod.Get, uri);
            var result = await cache.GetStringAsync(null, req2);

            Assert.Equal("hello cached world", result);
        }

        [Fact]
        public async Task HttpCache_StoreAndRetrieve_ReturnsCachedBytes()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/test-bytes-{Guid.NewGuid():N}");
            var body = new byte[] { 1, 2, 3, 4, 5 };

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

            cache.StoreBytes(req, resp, body);

            using var req2 = new HttpRequestMessage(HttpMethod.Get, uri);
            var result = await cache.GetBufferAsync(null, req2);

            Assert.Equal(body, result);
        }

        // ------------------------------------------------------------------ no-store

        [Fact]
        public async Task HttpCache_NoStore_IsNotCached()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/no-store-{Guid.NewGuid():N}");

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };

            cache.StoreString(req, resp, "should not be cached");

            using var req2 = new HttpRequestMessage(HttpMethod.Get, uri);
            var result = await cache.GetStringAsync(null, req2);

            Assert.Null(result);
        }

        // ------------------------------------------------------------------ freshness

        [Fact]
        public async Task HttpCache_ExpiredEntry_ReturnsNull()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/expired-{Guid.NewGuid():N}");

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            // Max-Age=0 means stale immediately
            resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.Zero };

            cache.StoreString(req, resp, "stale content");

            await Task.Delay(10); // Let it expire

            using var req2 = new HttpRequestMessage(HttpMethod.Get, uri);
            var result = await cache.GetStringAsync(null, req2);

            // Expired with no validators and no client: null expected
            Assert.Null(result);
        }

        // ------------------------------------------------------------------ only GET cached

        [Fact]
        public async Task HttpCache_PostRequest_IsNotCached()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/post-{Guid.NewGuid():N}");

            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

            cache.StoreString(req, resp, "post response");

            using var req2 = new HttpRequestMessage(HttpMethod.Post, uri);
            var result = await cache.GetStringAsync(null, req2);

            Assert.Null(result);
        }

        // ------------------------------------------------------------------ miss on unknown URL

        [Fact]
        public async Task HttpCache_Miss_ReturnsNullForUnknownUrl()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/unknown-{Guid.NewGuid():N}");

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            var result = await cache.GetStringAsync(null, req);

            Assert.Null(result);
        }

        // ------------------------------------------------------------------ singleton

        [Fact]
        public void HttpCache_Instance_IsSingleton()
        {
            var a = HttpCache.Instance;
            var b = HttpCache.Instance;
            Assert.Same(a, b);
        }

        // ------------------------------------------------------------------ large body not cached

        [Fact]
        public async Task HttpCache_LargeBody_IsNotCached()
        {
            var cache = NewCache();
            var uri = new Uri($"https://example.com/large-{Guid.NewGuid():N}");
            var largeBody = new string('x', 5 * 1024 * 1024); // 5 MB > 4 MB limit

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

            cache.StoreString(req, resp, largeBody);

            using var req2 = new HttpRequestMessage(HttpMethod.Get, uri);
            var result = await cache.GetStringAsync(null, req2);

            Assert.Null(result);
        }
    }
}
