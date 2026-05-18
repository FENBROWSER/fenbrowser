using Xunit;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core;
using System.Net.Http;
using System.Threading.Tasks;
using System;

namespace FenBrowser.Tests.Privacy
{
    public class CookieIsolationTests
    {
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

        [Fact]
        public async Task PrivacyHandler_TrimsReferer_WhenCrossOriginByScheme()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = false;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
            request.Headers.Referrer = new Uri("http://example.com/path?q=1");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.NotNull(request.Headers.Referrer);
            Assert.Equal("http://example.com/", request.Headers.Referrer!.AbsoluteUri);
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

        [Fact]
        public async Task PrivacyHandler_BlocksCookie_WhenSecFetchSiteIsCrossOrigin()
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();

            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-origin");

            var context = new NetworkContext(request);
            await handler.HandleAsync(context, () => Task.CompletedTask, default);

            Assert.False(request.Headers.Contains("Cookie"));
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
    }
}
