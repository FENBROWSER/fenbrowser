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
    }
}
