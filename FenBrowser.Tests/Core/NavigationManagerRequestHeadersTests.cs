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
            using var handler = new StaticHtmlHandler();
            using var httpClient = new HttpClient(handler);
            var resources = new ResourceManager(httpClient, isPrivate: true);

            string? secFetchDest = null;
            string? secFetchMode = null;
            string? secFetchSite = null;
            string? secFetchUser = null;
            string? upgradeInsecureRequests = null;
            string? userAgent = null;

            resources.NetworkRequestStarting += (_, request) =>
            {
                secFetchDest = GetHeader(request, "Sec-Fetch-Dest");
                secFetchMode = GetHeader(request, "Sec-Fetch-Mode");
                secFetchSite = GetHeader(request, "Sec-Fetch-Site");
                secFetchUser = GetHeader(request, "Sec-Fetch-User");
                upgradeInsecureRequests = GetHeader(request, "Upgrade-Insecure-Requests");
                userAgent = GetHeader(request, "User-Agent");
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
            Assert.DoesNotContain("Android", userAgent);
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
