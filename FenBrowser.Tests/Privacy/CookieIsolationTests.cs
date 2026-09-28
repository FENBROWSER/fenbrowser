using Xunit;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Net;
using System.Threading;
using FenBrowser.Core.Storage;

namespace FenBrowser.Tests.Privacy
{
    // These tests flip the process-wide BrowserSettings.Instance.BlockThirdPartyCookies.
    // Run them apart from every other class and put the setting back afterwards, or a
    // ResourceManager test running alongside sees third-party blocking switched on.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class BrowserSettingsStateCollection
    {
        public const string Name = "Browser Settings State";
    }

    [Collection(BrowserSettingsStateCollection.Name)]
    public class CookieIsolationTests : IDisposable
    {
        private readonly bool _previousBlockThirdPartyCookies = BrowserSettings.Instance.BlockThirdPartyCookies;

        public void Dispose() => BrowserSettings.Instance.BlockThirdPartyCookies = _previousBlockThirdPartyCookies;

        [Fact]
        public async Task PrivacyHandler_BlocksThirdPartyCookies()
        {
            // Arrange
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();
            
            var request = new HttpRequestMessage(HttpMethod.Get, "https://tracker.com/pixel.png");
            request.Headers.Add("Cookie", "id=123");
            request.Headers.Referrer = new Uri("https://example.com"); // Different host
            
            var context = new NetworkContext(request);
            
            // Act
            await handler.HandleAsync(context, () => Task.CompletedTask, default);
            
            // Assert
            Assert.False(request.Headers.Contains("Cookie"), "Third-party cookie should be removed");
        }

        [Fact]
        public async Task PrivacyHandler_AllowsFirstPartyCookies()
        {
            // Arrange
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();
            
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api/data");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.Referrer = new Uri("https://example.com/page"); // Same host
            
            var context = new NetworkContext(request);
            
            // Act
            await handler.HandleAsync(context, () => Task.CompletedTask, default);
            
            // Assert
            Assert.True(request.Headers.Contains("Cookie"), "First-party cookie should be preserved");
        }

        [Fact]
        public async Task PrivacyHandler_BlocksCookie_ForSuffixConfusionDomains()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/beacon");
            request.Headers.Add("Cookie", "id=abc");
            request.Headers.Referrer = new Uri("https://badexample.com/page");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.False(request.Headers.Contains("Cookie"), "Suffix-confusion domain should be treated as third-party");
        }

        // Referrer Policy 8.3: the default strict-origin-when-cross-origin sends only the
        // origin across origins, and a scheme change is cross-origin. The Referer is set by
        // BrowserRequestHeaderPolicy for the request's policy; PrivacyHandler must not trim
        // it again, or a page's unsafe-url policy would be overridden.
        [Fact]
        public void DefaultReferrerPolicy_TrimsReferer_WhenCrossOriginByScheme()
        {
            var referrer = BrowserRequestHeaderPolicy.ComputeReferrer(
                new Uri("http://example.com/path?q=1"),
                new Uri("https://example.com/api"),
                ReferrerPolicyDirective.StrictOriginWhenCrossOrigin);

            Assert.Equal("http://example.com/", referrer?.AbsoluteUri);
        }

        [Fact]
        public async Task PrivacyHandler_LeavesTheRefererToTheReferrerPolicy()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = false;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
            request.Headers.Referrer = new Uri("http://example.com/path?q=1");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.Equal("http://example.com/path?q=1", request.Headers.Referrer?.AbsoluteUri);
        }

        [Fact]
        public async Task PrivacyHandler_BlocksCookie_WhenSecFetchSiteIsCrossSite_CaseInsensitive()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "Cross-Site");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.False(request.Headers.Contains("Cookie"));
        }

        // Fetch Metadata 2.1: Sec-Fetch-Site is cross-site, same-site, same-origin or none.
        // Third-party cookie blocking is about sites, so a same-site request between two
        // origins keeps its cookies.
        [Fact]
        public async Task PrivacyHandler_KeepsCookie_WhenSecFetchSiteIsSameSite()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.True(request.Headers.Contains("Cookie"));
        }

        [Fact]
        public async Task PrivacyHandler_DoesNotThrow_WhenRequestUriIsNull()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();

            using var request = new HttpRequestMessage();
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.Referrer = new Uri("https://example.com/page");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.True(request.Headers.Contains("Cookie"));
        }

        [Fact]
        public async Task PrivacyHandler_BlocksCookie_WhenSecFetchSiteContainsCrossSiteInCombinedValue()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin, cross-site");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.False(request.Headers.Contains("Cookie"));
        }

        // CHIPS: a Partitioned cookie is keyed to the top-level site it was set under, so
        // third-party cookie blocking still lets an embedded site set and read it; only
        // its unpartitioned cookies are withheld in the cross-site context.
        [Fact]
        public void CookieJar_WithThirdPartyBlocking_KeepsPartitionedCookiesOnly()
        {
            var jar = new BrowserCookieJar();
            var frame = new Uri("https://widget.test/frame");
            var top = new Uri("https://parent.test/page");

            jar.SetDocumentCookie(frame, "chips=1; Secure; SameSite=None; Partitioned", top, blockThirdPartyCookies: true);
            jar.SetDocumentCookie(frame, "tracker=1; Secure; SameSite=None", top, blockThirdPartyCookies: true);

            var header = jar.GetRequestCookieHeader(frame, top, blockThirdPartyCookies: true);
            Assert.Contains("chips=1", header);
            Assert.DoesNotContain("tracker=1", header);

            // An unpartitioned cookie the site set as a first party is not sent to it
            // while it is embedded elsewhere.
            jar.SetDocumentCookie(frame, "session=1; Secure; SameSite=None", frame);
            Assert.DoesNotContain("session=1", jar.GetRequestCookieHeader(frame, top, blockThirdPartyCookies: true));
            Assert.Contains("session=1", jar.GetRequestCookieHeader(frame, top, blockThirdPartyCookies: false));
        }

        [Fact]
        public async Task ResourceManager_WithThirdPartyBlocking_SendsPartitionedCookies()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var requestUri = new Uri("https://assets.widget.test/data");
            var frameUri = new Uri("https://widget.test/frame");
            var top = new Uri("https://parent.test/page");
            var jar = new BrowserCookieJar();
            jar.SetDocumentCookie(requestUri, "chips=1; Secure; SameSite=None; Partitioned", top);
            jar.SetDocumentCookie(requestUri, "tracker=1; Secure; SameSite=None", requestUri);

            string sent = null;
            using var client = new HttpClient(new CapturingHandler(r => sent = r.Headers.TryGetValues("Cookie", out var v) ? string.Join("; ", v) : string.Empty));
            var manager = new ResourceManager(client, isPrivate: true, jar);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await manager.SendAsync(request, policy: null, new FetchContext
            {
                RequestUri = requestUri,
                InitiatorUri = frameUri,
                FrameDocumentUri = frameUri,
                TopLevelDocumentUri = top,
                Destination = "empty",
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            });

            Assert.Contains("chips=1", sent);
            Assert.DoesNotContain("tracker=1", sent);
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly Action<HttpRequestMessage> _capture;

            public CapturingHandler(Action<HttpRequestMessage> capture) => _capture = capture;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _capture(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
