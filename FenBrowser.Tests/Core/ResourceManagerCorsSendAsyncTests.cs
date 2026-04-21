using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class ResourceManagerCorsSendAsyncTests
    {
        [Fact]
        public async Task SendAsync_CorsModeWithoutOriginContext_IsBlockedFailClosed()
        {
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => manager.SendAsync(request, policy: null));

            Assert.Contains("missing origin context", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task SendAsync_CorsModeCrossOriginWithoutAcao_IsBlockedFailClosed()
        {
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.Referrer = new Uri("https://app.example.test/page");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => manager.SendAsync(request, policy: null));

            Assert.Contains("response validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task SendAsync_CorsModeCrossOriginWithMatchingAcao_IsAllowed()
        {
            using var handler = new RecordingHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://app.example.test");
                return response;
            });
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.Referrer = new Uri("https://app.example.test/page");

            using var response = await manager.SendAsync(request, policy: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, handler.CallCount);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
            {
                _factory = factory;
            }

            public int CallCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_factory(request));
            }
        }
    }
}
