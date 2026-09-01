using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core
{
    // A request with no explicit Accept used the document Accept whatever its
    // destination was. That is wrong on the wire — a stylesheet asking for
    // text/html — and Accept is part of the response-cache key, so a preload issued
    // that way could never satisfy the real load that asked for text/css. On
    // github.com that showed up as stylesheets fetched by the preload scanner and
    // then fetched again for real.
    public sealed class ResourceManagerAcceptDefaultTests
    {
        [Theory]
        [InlineData("style", "text/css")]
        [InlineData("script", "*/*")]
        [InlineData("empty", "*/*")]
        public async Task DefaultAccept_FollowsRequestDestination(string destination, string expectedPrefix)
        {
            var seen = new List<string>();
            using var client = new HttpClient(new RecordingHandler(seen));
            var manager = new ResourceManager(client, isPrivate: false);

            await manager.FetchTextDetailedAsync(new FetchContext
            {
                RequestUri = new Uri($"https://accept.test/{destination}-asset"),
                Destination = destination,
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            });

            Assert.Single(seen);
            Assert.StartsWith(expectedPrefix, seen[0], StringComparison.Ordinal);
        }

        // The point of the fix: a preload that passes no Accept must produce the same
        // cache entry the real stylesheet load looks for.
        [Fact]
        public async Task PreloadWithoutAccept_IsReusedByTheRealStylesheetLoad()
        {
            var requests = 0;
            using var client = new HttpClient(new CountingCssHandler(() => Interlocked.Increment(ref requests)));
            var manager = new ResourceManager(client, isPrivate: false);
            var url = new Uri("https://accept.test/theme.css");

            // Preload: destination style, no explicit Accept.
            await manager.FetchTextDetailedAsync(new FetchContext
            {
                RequestUri = url,
                Destination = "style",
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            });

            // Real load: FetchCssAsync passes "text/css,*/*;q=0.1" explicitly.
            var css = await manager.FetchCssAsync(url);

            Assert.Equal(".a{color:red}", css);
            Assert.Equal(1, Volatile.Read(ref requests));
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly List<string> _accepts;

            public RecordingHandler(List<string> accepts) => _accepts = accepts;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _accepts.Add(request.Headers.Accept.ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("body{}")
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/css");
                return Task.FromResult(response);
            }
        }

        private sealed class CountingCssHandler : HttpMessageHandler
        {
            private readonly Action _onRequest;

            public CountingCssHandler(Action onRequest) => _onRequest = onRequest;

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
                response.Headers.Vary.Add("Accept-Encoding");
                return Task.FromResult(response);
            }
        }
    }
}
