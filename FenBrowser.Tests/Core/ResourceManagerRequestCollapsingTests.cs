using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using Xunit;

namespace FenBrowser.Tests.Core
{
    // A page that references the same asset from several places issues those loads
    // together. The response cache cannot help until the first response lands, so
    // without collapsing every one of them goes to the network — github.com fetched
    // one stylesheet eleven times in a single load.
    public sealed class ResourceManagerRequestCollapsingTests
    {
        [Fact]
        public async Task ConcurrentIdenticalGets_ShareOneRoundTrip()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            using var client = new HttpClient(new GatedHandler(
                () => Interlocked.Increment(ref requests),
                release.Task));
            var manager = new ResourceManager(client, isPrivate: false);
            var url = new Uri("https://collapse.test/app.css");

            // Start several before any can complete.
            var inFlight = new Task<string>[6];
            for (var i = 0; i < inFlight.Length; i++)
            {
                inFlight[i] = manager.FetchCssAsync(url);
            }

            release.SetResult(true);
            var results = await Task.WhenAll(inFlight);

            Assert.All(results, r => Assert.Equal(".a{color:red}", r));
            Assert.Equal(1, Volatile.Read(ref requests));
        }

        // Collapsing must not outlive the request: once it has completed, a later
        // fetch is free to go to the network (or the response cache) again.
        [Fact]
        public async Task SequentialGets_AreNotCollapsedAfterCompletion()
        {
            var requests = 0;
            using var client = new HttpClient(new GatedHandler(
                () => Interlocked.Increment(ref requests),
                Task.FromResult(true),
                cacheable: false));
            var manager = new ResourceManager(client, isPrivate: false);
            var url = new Uri("https://collapse.test/no-store.css");

            await manager.FetchCssAsync(url);
            await manager.FetchCssAsync(url);

            Assert.Equal(2, Volatile.Read(ref requests));
        }

        // Different URLs must never share a fetch.
        [Fact]
        public async Task ConcurrentDifferentUrls_AreNotCollapsed()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            using var client = new HttpClient(new GatedHandler(
                () => Interlocked.Increment(ref requests),
                release.Task));
            var manager = new ResourceManager(client, isPrivate: false);

            var first = manager.FetchCssAsync(new Uri("https://collapse.test/one.css"));
            var second = manager.FetchCssAsync(new Uri("https://collapse.test/two.css"));

            release.SetResult(true);
            await Task.WhenAll(first, second);

            Assert.Equal(2, Volatile.Read(ref requests));
        }

        private sealed class GatedHandler : HttpMessageHandler
        {
            private readonly Action _onRequest;
            private readonly Task _gate;
            private readonly bool _cacheable;

            public GatedHandler(Action onRequest, Task gate, bool cacheable = true)
            {
                _onRequest = onRequest;
                _gate = gate;
                _cacheable = cacheable;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _onRequest();
                await _gate.ConfigureAwait(false);

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(".a{color:red}")
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/css");
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoStore = !_cacheable,
                    Public = _cacheable,
                    MaxAge = _cacheable ? TimeSpan.FromDays(365) : null
                };
                return response;
            }
        }
    }
}
