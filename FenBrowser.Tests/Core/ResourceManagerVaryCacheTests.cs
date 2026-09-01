using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using Xunit;

namespace FenBrowser.Tests.Core
{
    // Nearly every CDN sends "Vary: Accept-Encoding". Treating any Vary as
    // uncacheable disabled the response cache for most of the real web — one
    // github.com load refetched a single 286KB stylesheet eleven times. RFC 9111
    // 4.1 allows reuse as long as the listed request headers still match.
    public sealed class ResourceManagerVaryCacheTests
    {
        [Fact]
        public async Task VaryOnAcceptEncoding_IsCachedAndReused()
        {
            var requests = 0;
            using var client = new HttpClient(new CountingHandler(
                () => Interlocked.Increment(ref requests),
                varyValue: "Accept-Encoding"));
            var manager = new ResourceManager(client, isPrivate: false);
            var url = new Uri("https://cdn.vary.test/app.css");

            var first = await manager.FetchCssAsync(url);
            var second = await manager.FetchCssAsync(url);

            Assert.Equal(".a{color:red}", first);
            Assert.Equal(first, second);
            Assert.Equal(1, Volatile.Read(ref requests));
        }

        // "Vary: *" means the response is not reusable for any other request.
        [Fact]
        public async Task VaryOnEverything_IsNeverReused()
        {
            var requests = 0;
            using var client = new HttpClient(new CountingHandler(
                () => Interlocked.Increment(ref requests),
                varyValue: "*"));
            var manager = new ResourceManager(client, isPrivate: false);
            var url = new Uri("https://cdn.vary.test/star.css");

            await manager.FetchCssAsync(url);
            await manager.FetchCssAsync(url);

            Assert.Equal(2, Volatile.Read(ref requests));
        }

        // A header we cannot predict the outgoing value of must not be reused,
        // or we would serve a response negotiated for a different request.
        [Fact]
        public async Task VaryOnUnpredictableHeader_IsNotReused()
        {
            var requests = 0;
            using var client = new HttpClient(new CountingHandler(
                () => Interlocked.Increment(ref requests),
                varyValue: "X-Custom-Negotiation"));
            var manager = new ResourceManager(client, isPrivate: false);
            var url = new Uri("https://cdn.vary.test/custom.css");

            await manager.FetchCssAsync(url);
            await manager.FetchCssAsync(url);

            Assert.Equal(2, Volatile.Read(ref requests));
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            private readonly Action _onRequest;
            private readonly string _varyValue;

            public CountingHandler(Action onRequest, string varyValue)
            {
                _onRequest = onRequest;
                _varyValue = varyValue;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _onRequest();
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(".a{color:red}")
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/css");
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    Public = true,
                    MaxAge = TimeSpan.FromDays(365)
                };
                response.Headers.Vary.Add(_varyValue);
                return Task.FromResult(response);
            }
        }
    }
}
