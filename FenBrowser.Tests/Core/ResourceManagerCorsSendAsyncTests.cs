using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
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

        [Fact]
        public async Task SendAsync_CspBlock_EmitsFailClosedStructuredReasonCode()
        {
            ConfigureEngineLogForTest();
            using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            var blockingPolicy = CspPolicy.Parse("connect-src 'none'");

            var ex = await Assert.ThrowsAsync<Exception>(() => manager.SendAsync(request, blockingPolicy));

            Assert.Contains("Content Security Policy", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CallCount);
            AssertFailClosedEntry(
                reasonCode: FailClosedReasonCodes.CspConnectSrcBlocked,
                capabilityId: "SECURITY-CSP-ENFORCEMENT-01",
                stage: "fetch.csp");
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
