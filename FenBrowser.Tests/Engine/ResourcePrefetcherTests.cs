using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: HTML preload scanner / W3C Resource Hints — speculatively fetching
    // subresources before the parser reaches them is one of the largest cold-
    // load wins in a real browser. These tests pin the two pieces that have
    // to be true for the optimization to fire:
    //
    //   1. QueueHintAsync actually drains: putting a request on the queue
    //      starts a worker, which calls the ResourceManager.
    //   2. The detailed and simple fetch paths share one text cache, so a
    //      preload via the prefetcher warms cache for the engine's later
    //      FetchCssAsync / ScriptFetcher requests.
    public class ResourcePrefetcherTests
    {
        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Hits;
            private readonly string _body;
            private readonly string _contentType;

            public CountingHandler(string body, string contentType)
            {
                _body = body;
                _contentType = contentType;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Hits);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_body, System.Text.Encoding.UTF8, _contentType),
                    RequestMessage = request
                };
                return Task.FromResult(resp);
            }
        }

        [Fact]
        public async Task FetchTextDetailedAsync_PopulatesAndConsultsSharedCache()
        {
            var handler = new CountingHandler("body{}", "text/css");
            var http = new HttpClient(handler);
            var rm = new ResourceManager(http, isPrivate: true);
            var url = new Uri("https://example.test/site.css");

            var first = await rm.FetchTextDetailedAsync(url, secFetchDest: "style");
            Assert.Equal(FetchStatus.Success, first.Status);
            Assert.Equal(1, handler.Hits);

            // Second detailed fetch must hit cache.
            var second = await rm.FetchTextDetailedAsync(url, secFetchDest: "style");
            Assert.Equal(FetchStatus.Success, second.Status);
            Assert.Equal(1, handler.Hits);

            // A FetchTextAsync against the same URL also hits the same cache.
            var third = await rm.FetchTextAsync(url, secFetchDest: "style");
            Assert.Equal("body{}", third);
            Assert.Equal(1, handler.Hits);
        }

        [Fact]
        public async Task QueueHintAsync_DrainsWithoutExplicitProcessCall()
        {
            var handler = new CountingHandler(".x{}", "text/css");
            var http = new HttpClient(handler);
            var rm = new ResourceManager(http, isPrivate: true);
            using var prefetcher = new ResourcePrefetcher(rm);

            var url = new Uri("https://example.test/preload.css");
            await prefetcher.QueueHintAsync(url, ResourceHint.Preload, PreloadAs.Style);

            // The queue worker is async. Poll until both the network hit AND
            // the subsequent cache population have completed — we know the
            // cache is populated when a follow-up FetchTextAsync on the same
            // URL is served without bumping the hit counter.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (handler.Hits >= 1)
                {
                    var hitsBefore = handler.Hits;
                    var body = await rm.FetchTextAsync(url, secFetchDest: "style");
                    if (body == ".x{}" && handler.Hits == hitsBefore)
                    {
                        return; // ✓ drained and cache warmed
                    }
                }
                await Task.Delay(20);
            }

            Assert.True(false,
                $"Prefetch did not warm cache within 3s. Hits={handler.Hits}");
        }
    }
}
