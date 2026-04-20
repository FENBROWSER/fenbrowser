using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class ResourceManagerFetchBytesTests
    {
        [Fact]
        public async Task FetchBytesAsync_Image404WithPngBody_ReturnsBodyBytes()
        {
            byte[] png = CreatePngBytes(SKColors.Red);
            using var client = new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new ByteArrayContent(png)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return response;
            }));

            var manager = new ResourceManager(client, isPrivate: false);
            var bytes = await manager.FetchBytesAsync(
                new Uri("https://example.test/missing.png"),
                referer: new Uri("https://example.test/page"),
                accept: "image/png,*/*;q=0.8",
                secFetchDest: "image");

            Assert.NotNull(bytes);
            Assert.Equal(png.Length, bytes.Length);
        }

        [Fact]
        public async Task FetchBytesAsync_Document404WithPngBody_RemainsBlocked()
        {
            byte[] png = CreatePngBytes(SKColors.Red);
            using var client = new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new ByteArrayContent(png)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return response;
            }));

            var manager = new ResourceManager(client, isPrivate: false);
            var bytes = await manager.FetchBytesAsync(
                new Uri("https://example.test/missing.png"),
                referer: new Uri("https://example.test/page"),
                accept: "*/*",
                secFetchDest: "document");

            Assert.Null(bytes);
        }

        [Fact]
        public async Task FetchCssAsync_HtmlMime_IsBlocked()
        {
            using var client = new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body>not css</body></html>")
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
                return response;
            }));

            var manager = new ResourceManager(client, isPrivate: false);
            var css = await manager.FetchCssAsync(new Uri("https://example.test/empty.css"));

            Assert.Null(css);
        }

        [Fact]
        public async Task FetchCssAsync_CssMime_IsReturned()
        {
            const string cssText = "h1 { color: black; }";
            using var client = new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(cssText)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/css");
                return response;
            }));

            var manager = new ResourceManager(client, isPrivate: false);
            var css = await manager.FetchCssAsync(new Uri("https://example.test/site.css"));

            Assert.Equal(cssText, css);
        }

        [Fact]
        public async Task FetchTextDetailedAsync_BodyLimitExceeded_ReturnsLimitExceeded()
        {
            var original = SnapshotResilience();
            try
            {
                BrowserSettings.Instance.Resilience.MaxTextBodyBytes = 1024;

                using var client = new HttpClient(new StubHandler(_ =>
                {
                    var payload = new string('a', 200_000);
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload)
                    };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
                    return response;
                }));

                var manager = new ResourceManager(client, isPrivate: false);
                var result = await manager.FetchTextDetailedAsync(
                    new Uri("https://example.test/large"),
                    referer: new Uri("https://example.test/page"),
                    secFetchDest: "document");

                Assert.Equal(FetchStatus.LimitExceeded, result.Status);
                Assert.Equal(FetchFailureReasonCode.LimitExceeded, result.FailureReason);
                Assert.Equal("text_body_bytes", result.LimitType);
                Assert.NotNull(result.InputSizeBytes);
                Assert.True(result.InputSizeBytes > 64 * 1024);
            }
            finally
            {
                RestoreResilience(original);
            }
        }

        [Fact]
        public async Task FetchTextDetailedAsync_RedirectHopLimitExceeded_ReturnsLimitExceeded()
        {
            var original = SnapshotResilience();
            try
            {
                BrowserSettings.Instance.Resilience.MaxRedirectHops = 2;

                using var client = new HttpClient(new StubHandler(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri(request.RequestUri, "/loop");
                    return response;
                }));

                var manager = new ResourceManager(client, isPrivate: false);
                var result = await manager.FetchTextDetailedAsync(
                    new Uri("https://example.test/start"),
                    referer: new Uri("https://example.test/page"),
                    secFetchDest: "document");

                Assert.Equal(FetchStatus.LimitExceeded, result.Status);
                Assert.Equal(FetchFailureReasonCode.RedirectLimitExceeded, result.FailureReason);
                Assert.Equal("redirect_hops", result.LimitType);
                Assert.True(result.IsRetryable);
            }
            finally
            {
                RestoreResilience(original);
            }
        }

        private static byte[] CreatePngBytes(SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(2, 2));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
            {
                _factory = factory;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_factory(request));
            }
        }

        private static ResilienceSettings SnapshotResilience()
        {
            var source = BrowserSettings.Instance.Resilience;
            return new ResilienceSettings
            {
                MaxHtmlInputChars = source.MaxHtmlInputChars,
                MaxHtmlTokenEmissions = source.MaxHtmlTokenEmissions,
                MaxOpenElementsDepth = source.MaxOpenElementsDepth,
                MaxRedirectHops = source.MaxRedirectHops,
                MaxTextBodyBytes = source.MaxTextBodyBytes,
                MaxImageBodyBytes = source.MaxImageBodyBytes,
                RequestTimeoutSeconds = source.RequestTimeoutSeconds,
                NavigationTimeoutSeconds = source.NavigationTimeoutSeconds
            };
        }

        private static void RestoreResilience(ResilienceSettings snapshot)
        {
            BrowserSettings.Instance.Resilience.MaxHtmlInputChars = snapshot.MaxHtmlInputChars;
            BrowserSettings.Instance.Resilience.MaxHtmlTokenEmissions = snapshot.MaxHtmlTokenEmissions;
            BrowserSettings.Instance.Resilience.MaxOpenElementsDepth = snapshot.MaxOpenElementsDepth;
            BrowserSettings.Instance.Resilience.MaxRedirectHops = snapshot.MaxRedirectHops;
            BrowserSettings.Instance.Resilience.MaxTextBodyBytes = snapshot.MaxTextBodyBytes;
            BrowserSettings.Instance.Resilience.MaxImageBodyBytes = snapshot.MaxImageBodyBytes;
            BrowserSettings.Instance.Resilience.RequestTimeoutSeconds = snapshot.RequestTimeoutSeconds;
            BrowserSettings.Instance.Resilience.NavigationTimeoutSeconds = snapshot.NavigationTimeoutSeconds;
        }
    }
}
