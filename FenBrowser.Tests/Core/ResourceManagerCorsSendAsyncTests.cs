using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class ResourceManagerCorsSendAsyncTests
    {
        [Fact]
        public async Task SendAsync_CorsModeWithoutOriginContext_IsBlockedFailClosed()
        {
            ConfigureEngineLogForTest();
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => manager.SendAsync(request, policy: null));

            Assert.Contains("missing origin context", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CallCount);
            AssertFailClosedEntry(
                reasonCode: FailClosedReasonCodes.CorsOriginContextMissing,
                capabilityId: "FETCH-CORS-POLICY-01",
                stage: "fetch.cors");
        }

        [Fact]
        public async Task SendAsync_CorsModeCrossOriginWithoutAcao_IsBlockedFailClosed()
        {
            ConfigureEngineLogForTest();
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.Referrer = new Uri("https://app.example.test/page");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => manager.SendAsync(request, policy: null));

            Assert.Contains("response validation failed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, handler.CallCount);
            AssertFailClosedEntry(
                reasonCode: FailClosedReasonCodes.CorsResponseDisallowed,
                capabilityId: "FETCH-CORS-POLICY-01",
                stage: "fetch.cors-response");
        }

        [Fact]
        public async Task SendAsync_CorsModeCrossOriginWithMatchingAcao_IsAllowed()
        {
            ConfigureEngineLogForTest();
            string secFetchSite = null;
            using var handler = new RecordingHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://app.example.test");
                return response;
            });
            handler.OnRequest = request => secFetchSite = GetHeader(request, "Sec-Fetch-Site");
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.Referrer = new Uri("https://app.example.test/page");

            using var response = await manager.SendAsync(request, policy: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, handler.CallCount);
            Assert.Equal("same-site", secFetchSite);
        }

        [Fact]
        public async Task SendAsync_CorsModeSameOrigin_SetsSecFetchSiteSameOrigin()
        {
            ConfigureEngineLogForTest();
            string secFetchSite = null;
            using var handler = new RecordingHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://api.example.test");
                return response;
            });
            handler.OnRequest = request => secFetchSite = GetHeader(request, "Sec-Fetch-Site");
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.Referrer = new Uri("https://api.example.test/page");

            using var response = await manager.SendAsync(request, policy: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("same-origin", secFetchSite);
        }

        [Fact]
        public async Task SendAsync_CorsModeNoReferrer_SetsSecFetchSiteNoneAndBlocksBeforeSend()
        {
            ConfigureEngineLogForTest();
            string secFetchSite = null;
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            handler.OnRequest = request => secFetchSite = GetHeader(request, "Sec-Fetch-Site");
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => manager.SendAsync(request, policy: null));

            Assert.Contains("missing origin context", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("none", GetHeader(request, "Sec-Fetch-Site"));
            Assert.Null(secFetchSite);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task SendAsync_CspBlock_EmitsFailClosedStructuredReasonCode()
        {
            ConfigureEngineLogForTest();
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            var blockingPolicy = CspPolicy.Parse("connect-src 'none'");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => manager.SendAsync(request, blockingPolicy));

            Assert.Contains("Content Security Policy", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CallCount);
            AssertFailClosedEntry(
                reasonCode: FailClosedReasonCodes.CspConnectSrcBlocked,
                capabilityId: "SECURITY-CSP-ENFORCEMENT-01",
                stage: "fetch.csp");
        }

        [Fact]
        public async Task SendAsync_PreflightSameSite_UsesSameSiteFetchMetadata()
        {
            ConfigureEngineLogForTest();
            string preflightSite = null;
            string preflightMode = null;
            string preflightDest = null;
            var optionsCount = 0;
            var putCount = 0;

            using var handler = new RecordingHandler(request =>
            {
                if (request.Method == HttpMethod.Options)
                {
                    optionsCount++;
                    preflightSite = GetHeader(request, "Sec-Fetch-Site");
                    preflightMode = GetHeader(request, "Sec-Fetch-Mode");
                    preflightDest = GetHeader(request, "Sec-Fetch-Dest");

                    var preflightResponse = new HttpResponseMessage(HttpStatusCode.NoContent);
                    preflightResponse.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://app.example.test");
                    preflightResponse.Headers.TryAddWithoutValidation("Access-Control-Allow-Methods", "PUT");
                    preflightResponse.Headers.TryAddWithoutValidation("Access-Control-Allow-Headers", "x-test");
                    return preflightResponse;
                }

                putCount++;
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://app.example.test");
                return response;
            });
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Put, "https://api.example.test/data");
            request.Headers.Referrer = new Uri("https://app.example.test/page");
            request.Headers.TryAddWithoutValidation("X-Test", "1");
            CorsHandler.SetAuthorRequestHeaders(request, new[] { "X-Test" });

            using var response = await manager.SendAsync(request, policy: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, optionsCount);
            Assert.Equal(1, putCount);
            Assert.Equal("same-site", preflightSite);
            Assert.Equal("cors", preflightMode);
            Assert.Equal("empty", preflightDest);
        }

        [Fact]
        public async Task SendAsync_ExplicitContext_UsesTopLevelSiteForPartitionedCookies()
        {
            var requestUri = new Uri("https://assets.challenge.test/data");
            var frameUri = new Uri("https://challenge.test/widget");
            var firstPartyTop = new Uri("https://parent.test/page");
            var otherTop = new Uri("https://other.test/page");
            var cookieJar = new FenBrowser.Core.Storage.BrowserCookieJar();
            cookieJar.SetDocumentCookie(
                requestUri,
                "challenge_partition=opaque; Secure; SameSite=None; Partitioned",
                firstPartyTop,
                blockThirdPartyCookies: false);
            var observedCookies = new System.Collections.Generic.List<string>();
            using var handler = new RecordingHandler(request =>
            {
                observedCookies.Add(GetHeader(request, "Cookie") ?? string.Empty);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true, cookieJar);

            using var firstRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var firstResponse = await manager.SendAsync(firstRequest, policy: null, Context(firstPartyTop));
            using var secondRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var secondResponse = await manager.SendAsync(secondRequest, policy: null, Context(otherTop));

            Assert.Contains("challenge_partition=opaque", observedCookies[0]);
            Assert.DoesNotContain("challenge_partition=opaque", observedCookies[1]);

            FetchContext Context(Uri topLevel) => new()
            {
                RequestUri = requestUri,
                InitiatorUri = frameUri,
                FrameDocumentUri = frameUri,
                TopLevelDocumentUri = topLevel,
                Destination = "empty",
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            };
        }

        [Fact]
        public async Task SendAsync_ExplicitContext_CentralizesHeadersWithoutDuplicatingCallerValues()
        {
            string accept = null;
            string acceptEncoding = null;
            string fetchSite = null;
            using var handler = new RecordingHandler(request =>
            {
                accept = GetHeader(request, "Accept");
                acceptEncoding = GetHeader(request, "Accept-Encoding");
                fetchSite = GetHeader(request, "Sec-Fetch-Site");
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.test/module.js");
            request.Headers.TryAddWithoutValidation("Accept", "application/javascript");
            var context = new FetchContext
            {
                RequestUri = request.RequestUri,
                InitiatorUri = new Uri("https://app.example.test/page"),
                FrameDocumentUri = new Uri("https://app.example.test/page"),
                TopLevelDocumentUri = new Uri("https://top.example.test/page"),
                Destination = "script",
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            };

            using var response = await manager.SendAsync(request, policy: null, context);

            Assert.Equal("application/javascript", accept);
            Assert.Equal(
                string.Join(" ", BrowserNetworkCapabilities.AcceptEncodingHeader.Split(',', StringSplitOptions.TrimEntries)),
                acceptEncoding);
            Assert.Equal("same-site", fetchSite);
        }

        [Fact]
        public async Task SendAsync_SameOriginCredentials_DoesNotLeakCookiesCrossOrigin()
        {
            var requestUri = new Uri("https://api.example.test/data");
            var initiatorUri = new Uri("https://app.example.test/page");
            var cookieJar = new FenBrowser.Core.Storage.BrowserCookieJar();
            cookieJar.SetDocumentCookie(
                requestUri,
                "api_session=opaque; Secure; SameSite=None",
                initiatorUri,
                blockThirdPartyCookies: false);
            string observedCookie = null;
            using var handler = new RecordingHandler(request =>
            {
                observedCookie = GetHeader(request, "Cookie");
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://app.example.test");
                return response;
            });
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true, cookieJar);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var context = new FetchContext
            {
                RequestUri = requestUri,
                InitiatorUri = initiatorUri,
                FrameDocumentUri = initiatorUri,
                TopLevelDocumentUri = initiatorUri,
                Destination = "empty",
                Mode = "cors",
                CredentialsMode = "same-origin",
                Method = "GET"
            };

            using var response = await manager.SendAsync(request, policy: null, context);

            Assert.Null(observedCookie);
        }

        private static void ConfigureEngineLogForTest()
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Debug,
                EnableConsoleSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = true,
                EnableTraceSink = false,
                RingBufferCapacity = 2000
            });
            EngineLog.ClearCompatibilityBuffer();
        }

        private static void AssertFailClosedEntry(string reasonCode, string capabilityId, string stage)
        {
            LogEntry matching = null;
            for (var i = 0; i < 20 && matching == null; i++)
            {
                var entries = EngineLog.GetCompatibilityRecentEntries(200);
                matching = entries.Find(entry =>
                    entry.Data != null &&
                    entry.Data.TryGetValue("reasonCode", out var reason) &&
                    string.Equals(reason?.ToString(), reasonCode, StringComparison.Ordinal));
                if (matching == null)
                {
                    Thread.Sleep(25);
                }
            }

            Assert.NotNull(matching);
            Assert.Equal("deny", matching!.Data["decision"]?.ToString());
            Assert.Equal("fail-closed", matching.Data["policy"]?.ToString());
            Assert.Equal(capabilityId, matching.Data["capabilityId"]?.ToString());
            Assert.Equal(stage, matching.Data["stage"]?.ToString());
            Assert.True(matching.Data.ContainsKey("schemaVersion"));
        }

        private static string GetHeader(HttpRequestMessage request, string name)
        {
            if (request?.Headers != null && request.Headers.TryGetValues(name, out var values))
            {
                return string.Join(" ", values);
            }

            if (request?.Content != null && request.Content.Headers.TryGetValues(name, out values))
            {
                return string.Join(" ", values);
            }

            return null;
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
            {
                _factory = factory;
            }

            public int CallCount { get; private set; }
            public Action<HttpRequestMessage> OnRequest { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                OnRequest?.Invoke(request);
                return Task.FromResult(_factory(request));
            }
        }
    }
}
