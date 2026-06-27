using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class NavigationManagerRequestHeadersTests
    {
        [Fact]
        public async Task NavigateUserInputAsync_SendsDocumentNavigationHeaders()
        {
            var previousUserAgent = BrowserSettings.Instance.SelectedUserAgent;
            var previousImproveBrowser = BrowserSettings.Instance.ImproveBrowser;

            try
            {
                BrowserSettings.Instance.SelectedUserAgent = UserAgentType.FenBrowser;
                BrowserSettings.Instance.ImproveBrowser = false;

                using var handler = new StaticHtmlHandler();
                using var httpClient = new HttpClient(handler);
                var resources = new ResourceManager(httpClient, isPrivate: true);

                string? secFetchDest = null;
                string? secFetchMode = null;
                string? secFetchSite = null;
                string? secFetchUser = null;
                string? upgradeInsecureRequests = null;
                string? userAgent = null;
                string? secChUa = null;
                string? secChUaPlatform = null;
                string? secChUaPlatformVersion = null;
                string? secChUaFullVersion = null;
                string? secChUaFullVersionList = null;

                resources.NetworkRequestStarting += (_, request) =>
                {
                    secFetchDest = GetHeader(request, "Sec-Fetch-Dest");
                    secFetchMode = GetHeader(request, "Sec-Fetch-Mode");
                    secFetchSite = GetHeader(request, "Sec-Fetch-Site");
                    secFetchUser = GetHeader(request, "Sec-Fetch-User");
                    upgradeInsecureRequests = GetHeader(request, "Upgrade-Insecure-Requests");
                    userAgent = GetHeader(request, "User-Agent");
                    secChUa = GetHeader(request, "Sec-CH-UA");
                    secChUaPlatform = GetHeader(request, "Sec-CH-UA-Platform");
                    secChUaPlatformVersion = GetHeader(request, "Sec-CH-UA-Platform-Version");
                    secChUaFullVersion = GetHeader(request, "Sec-CH-UA-Full-Version");
                    secChUaFullVersionList = GetHeader(request, "Sec-CH-UA-Full-Version-List");
                };

                var navigation = new NavigationManager(resources);
                var result = await navigation.NavigateUserInputAsync("https://example.com/search?q=test");

                Assert.Equal(FetchStatus.Success, result.Status);
                Assert.Equal("document", secFetchDest);
                Assert.Equal("navigate", secFetchMode);
                Assert.Equal("none", secFetchSite);
                Assert.Equal("?1", secFetchUser);
                Assert.Equal("1", upgradeInsecureRequests);
                Assert.Contains("Windows NT 10.0", userAgent);
                Assert.Contains("AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36 FenBrowser/1.0", userAgent);
                Assert.DoesNotContain("Android", userAgent);
                Assert.Contains("\"FenBrowser\";v=\"1\"", secChUa);
                Assert.Contains("\"Chromium\";v=\"146\"", secChUa);
                Assert.Contains(" Not;A Brand", secChUa);
                Assert.Equal("\"Windows\"", secChUaPlatform);
                Assert.Null(secChUaPlatformVersion);
                Assert.Null(secChUaFullVersion);
                Assert.Null(secChUaFullVersionList);
            }
            finally
            {
                BrowserSettings.Instance.SelectedUserAgent = previousUserAgent;
                BrowserSettings.Instance.ImproveBrowser = previousImproveBrowser;
            }
        }

        [Fact]
        public async Task NavigateUserInputAsync_WhenImproveBrowserEnabled_SendsHighEntropyClientHints()
        {
            var previousUserAgent = BrowserSettings.Instance.SelectedUserAgent;
            var previousImproveBrowser = BrowserSettings.Instance.ImproveBrowser;

            try
            {
                BrowserSettings.Instance.SelectedUserAgent = UserAgentType.FenBrowser;
                BrowserSettings.Instance.ImproveBrowser = true;

                using var handler = new StaticHtmlHandler();
                using var httpClient = new HttpClient(handler);
                var resources = new ResourceManager(httpClient, isPrivate: true);

                string? secChUaPlatformVersion = null;
                string? secChUaFullVersion = null;
                string? secChUaFullVersionList = null;
                string? secChUaArch = null;
                string? secChUaBitness = null;
                string? secChUaModel = null;

                resources.NetworkRequestStarting += (_, request) =>
                {
                    secChUaPlatformVersion = GetHeader(request, "Sec-CH-UA-Platform-Version");
                    secChUaFullVersion = GetHeader(request, "Sec-CH-UA-Full-Version");
                    secChUaFullVersionList = GetHeader(request, "Sec-CH-UA-Full-Version-List");
                    secChUaArch = GetHeader(request, "Sec-CH-UA-Arch");
                    secChUaBitness = GetHeader(request, "Sec-CH-UA-Bitness");
                    secChUaModel = GetHeader(request, "Sec-CH-UA-Model");
                };

                var navigation = new NavigationManager(resources);
                var result = await navigation.NavigateUserInputAsync("https://example.com/search?q=test");

                Assert.Equal(FetchStatus.Success, result.Status);
                Assert.Equal(BrowserSettings.GetSecChUaPlatformVersion(UserAgentType.FenBrowser), secChUaPlatformVersion);
                Assert.Equal("\"1.0.0.0\"", secChUaFullVersion);
                Assert.Contains("\"FenBrowser\";v=\"1.0.0.0\"", secChUaFullVersionList);
                Assert.Contains("\"Chromium\";v=\"146.0.7800.12\"", secChUaFullVersionList);
                Assert.Equal(BrowserSettings.GetSecChUaArch(UserAgentType.FenBrowser), secChUaArch);
                Assert.Equal(BrowserSettings.GetSecChUaBitness(UserAgentType.FenBrowser), secChUaBitness);
                Assert.Equal(BrowserSettings.GetSecChUaModel(UserAgentType.FenBrowser), secChUaModel);
            }
            finally
            {
                BrowserSettings.Instance.SelectedUserAgent = previousUserAgent;
                BrowserSettings.Instance.ImproveBrowser = previousImproveBrowser;
            }
        }

        [Fact]
        public async Task NavigateAsync_ProgrammaticDocumentNavigation_SendsReferrerWithoutUserActivationHeader()
        {
            using var handler = new StaticHtmlHandler();
            using var httpClient = new HttpClient(handler);
            var resources = new ResourceManager(httpClient, isPrivate: true);

            string? referer = null;
            string? secFetchDest = null;
            string? secFetchMode = null;
            string? secFetchSite = null;
            string? secFetchUser = null;
            string? upgradeInsecureRequests = null;

            resources.NetworkRequestStarting += (_, request) =>
            {
                referer = request.Headers.Referrer?.AbsoluteUri;
                secFetchDest = GetHeader(request, "Sec-Fetch-Dest");
                secFetchMode = GetHeader(request, "Sec-Fetch-Mode");
                secFetchSite = GetHeader(request, "Sec-Fetch-Site");
                secFetchUser = GetHeader(request, "Sec-Fetch-User");
                upgradeInsecureRequests = GetHeader(request, "Upgrade-Insecure-Requests");
            };

            var navigation = new NavigationManager(resources);
            var result = await navigation.NavigateAsync(
                "https://example.com/search?q=test",
                NavigationRequestKind.Programmatic,
                new Uri("https://example.com/search?q=test"));

            Assert.Equal(FetchStatus.Success, result.Status);
            Assert.Equal("https://example.com/search?q=test", referer);
            Assert.Equal("document", secFetchDest);
            Assert.Equal("navigate", secFetchMode);
            Assert.Equal("same-origin", secFetchSite);
            Assert.Null(secFetchUser);
            Assert.Equal("1", upgradeInsecureRequests);
        }

        private static string? GetHeader(HttpRequestMessage request, string name)
        {
            if (request.Headers.TryGetValues(name, out var values))
            {
                return string.Join(" ", values);
            }

            if (request.Content != null && request.Content.Headers.TryGetValues(name, out values))
            {
                return string.Join(" ", values);
            }

            return null;
        }

        private sealed class StaticHtmlHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<!doctype html><html><head><title>ok</title></head><body>ok</body></html>", Encoding.UTF8, "text/html"),
                    RequestMessage = request
                };

                return Task.FromResult(response);
            }
        }
    }
}
