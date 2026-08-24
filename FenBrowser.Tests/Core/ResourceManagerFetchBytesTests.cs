using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Security;
using SkiaSharp;
using Xunit;
using System.IO;

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
        public async Task FetchTextDetailedAsync_CorbStopsAfterSniffPrefix()
        {
            var payload = Encoding.UTF8.GetBytes("<html>" + new string('x', 2 * 1024 * 1024));
            using var source = new CountingReadStream(payload);
            using var client = new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(source)
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
                return response;
            }));

            var manager = new ResourceManager(client, isPrivate: true);
            var result = await manager.FetchTextDetailedAsync(new FetchContext
            {
                RequestUri = new Uri("https://cross-origin.test/data"),
                InitiatorUri = new Uri("https://example.test/page"),
                FrameDocumentUri = new Uri("https://example.test/page"),
                TopLevelDocumentUri = new Uri("https://example.test/page"),
                Destination = "script",
                Mode = "no-cors",
                CredentialsMode = "same-origin",
                Method = "GET"
            });

            Assert.Equal(FetchFailureReasonCode.CorbBlocked, result.FailureReason);
            Assert.InRange(source.BytesRead, 1, 512);
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

        [Fact]
        public async Task FetchTextDetailedAsync_MisleadingLatin1Header_PreservesUtf8Text()
        {
            const string html =
                "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" +
                "<p>Beyonc\u00E9</p><p>World War\u00A0II</p><p>\u0939\u093F\u0928\u094D\u0926\u0940</p><p>\u0420\u0443\u0441\u0441\u043A\u0438\u0439</p>" +
                "</body></html>";
            byte[] payload = Encoding.UTF8.GetBytes(html);

            using var client = new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
                response.Content.Headers.ContentType.CharSet = "iso-8859-1";
                return response;
            }));

            var manager = new ResourceManager(client, isPrivate: false);
            var result = await manager.FetchTextDetailedAsync(
                new Uri("https://example.test/utf8"),
                referer: new Uri("https://example.test/page"),
                secFetchDest: "document");

            Assert.Equal(FetchStatus.Success, result.Status);
            Assert.Contains("Beyonc\u00E9", result.Content);
            Assert.Contains("World War\u00A0II", result.Content);
            Assert.Contains("\u0939\u093F\u0928\u094D\u0926\u0940", result.Content);
            Assert.Contains("\u0420\u0443\u0441\u0441\u043A\u0438\u0439", result.Content);
            Assert.DoesNotContain("Beyonc\u00C3\u00A9", result.Content);
            Assert.DoesNotContain("\u00E0\u00A4", result.Content);
            Assert.DoesNotContain("\u00D0\u00A0\u00D1", result.Content);
        }

        [Fact]
        public async Task FetchTextDetailedAsync_FileSchemeBlockedWhenDisabled()
        {
            var originalAllowFile = BrowserSettings.Instance.AllowFileSchemeNavigation;
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path, "<html>local</html>");
                BrowserSettings.Instance.AllowFileSchemeNavigation = false;
                using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
                var manager = new ResourceManager(client, isPrivate: true);

                var result = await manager.FetchTextDetailedAsync(new Uri(path), secFetchDest: "document");

                Assert.Equal(FetchStatus.UnknownError, result.Status);
                Assert.Contains("Blocked by file scheme navigation policy", result.ErrorDetail);
            }
            finally
            {
                BrowserSettings.Instance.AllowFileSchemeNavigation = originalAllowFile;
                TryDelete(path);
            }
        }

        [Fact]
        public async Task FetchImageAsync_FileSchemeBlockedWhenDisabled()
        {
            var originalAllowFile = BrowserSettings.Instance.AllowFileSchemeNavigation;
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path, "bytes");
                BrowserSettings.Instance.AllowFileSchemeNavigation = false;
                using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
                var manager = new ResourceManager(client, isPrivate: true);

                using var stream = await manager.FetchImageAsync(new Uri(path));
                Assert.Null(stream);
            }
            finally
            {
                BrowserSettings.Instance.AllowFileSchemeNavigation = originalAllowFile;
                TryDelete(path);
            }
        }

        [Fact]
        public void TopLevelFileNavigation_RemoteProgrammaticInitiator_IsBlocked()
        {
            var decision = BrowserSecurityPolicy.EvaluateTopLevelNavigation(
                new Uri("file:///C:/Users/example/private.html"),
                new Uri("https://attacker.test/page"),
                isUserInput: false,
                automationContext: false,
                allowFileSchemeNavigation: true,
                allowAutomationFileNavigation: false);

            Assert.False(decision.IsAllowed);
            Assert.Equal("remote-initiated-file-navigation", decision.Code);
        }

        [Fact]
        public void TopLevelFileNavigation_UserInputOrLocalInitiator_IsAllowed()
        {
            var target = new Uri("file:///C:/Users/example/local.html");

            var userDecision = BrowserSecurityPolicy.EvaluateTopLevelNavigation(
                target,
                initiatorUri: null,
                isUserInput: true,
                automationContext: false,
                allowFileSchemeNavigation: true,
                allowAutomationFileNavigation: false);
            var localDecision = BrowserSecurityPolicy.EvaluateTopLevelNavigation(
                target,
                new Uri("file:///C:/Users/example/index.html"),
                isUserInput: false,
                automationContext: false,
                allowFileSchemeNavigation: true,
                allowAutomationFileNavigation: false);

            Assert.True(userDecision.IsAllowed);
            Assert.True(localDecision.IsAllowed);
        }

        [Fact]
        public async Task FileSubresource_RemoteInitiator_IsBlockedButLocalInitiatorIsAllowed()
        {
            var originalAllowFile = BrowserSettings.Instance.AllowFileSchemeNavigation;
            var path = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(path, "window.localFixture = true;");
                BrowserSettings.Instance.AllowFileSchemeNavigation = true;
                using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
                var manager = new ResourceManager(client, isPrivate: true);
                var fileUri = new Uri(path);

                var remoteResult = await manager.FetchTextDetailedAsync(
                    fileUri,
                    referer: new Uri("https://attacker.test/page"),
                    secFetchDest: "script",
                    isUserInitiatedNavigation: false);
                var localResult = await manager.FetchTextDetailedAsync(
                    fileUri,
                    referer: new Uri("file:///C:/fixtures/page.html"),
                    secFetchDest: "script",
                    isUserInitiatedNavigation: false);

                Assert.Equal(FetchStatus.UnknownError, remoteResult.Status);
                Assert.Contains("Blocked by file scheme navigation policy", remoteResult.ErrorDetail);
                Assert.Equal(FetchStatus.Success, localResult.Status);
            }
            finally
            {
                BrowserSettings.Instance.AllowFileSchemeNavigation = originalAllowFile;
                TryDelete(path);
            }
        }

        [Fact]
        public async Task ScriptMixedContent_WithoutCsp_IsBlockedBeforeNetworkDispatch()
        {
            var dispatchCount = 0;
            using var client = new HttpClient(new StubHandler(_ =>
            {
                dispatchCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("window.compromised = true;")
                };
            }));
            var manager = new ResourceManager(client, isPrivate: true);

            var result = await manager.FetchTextDetailedAsync(new FetchContext
            {
                RequestUri = new Uri("http://cdn.attacker.test/app.js"),
                InitiatorUri = new Uri("https://secure.example.test/page"),
                FrameDocumentUri = new Uri("https://secure.example.test/page"),
                TopLevelDocumentUri = new Uri("https://secure.example.test/page"),
                Destination = "script",
                Mode = "no-cors",
                CredentialsMode = "include"
            });

            Assert.Equal(FetchStatus.UnknownError, result.Status);
            Assert.Equal(FetchFailureReasonCode.MixedContentBlocked, result.FailureReason);
            Assert.Equal(0, dispatchCount);
        }

        [Fact]
        public async Task ScriptRedirect_HttpsToHttp_IsRecheckedAndBlocked()
        {
            var dispatchCount = 0;
            using var client = new HttpClient(new StubHandler(request =>
            {
                dispatchCount++;
                Assert.Equal("https", request.RequestUri.Scheme);
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("http://cdn.attacker.test/downgraded.js");
                return response;
            }));
            var manager = new ResourceManager(client, isPrivate: true);

            var result = await manager.FetchTextDetailedAsync(new FetchContext
            {
                RequestUri = new Uri("https://cdn.example.test/app.js"),
                InitiatorUri = new Uri("https://secure.example.test/page"),
                FrameDocumentUri = new Uri("https://secure.example.test/page"),
                TopLevelDocumentUri = new Uri("https://secure.example.test/page"),
                Destination = "script",
                Mode = "no-cors",
                CredentialsMode = "include"
            });

            Assert.Equal(FetchStatus.UnknownError, result.Status);
            Assert.Equal(FetchFailureReasonCode.MixedContentBlocked, result.FailureReason);
            Assert.Equal(1, dispatchCount);
        }

        [Fact]
        public async Task GenericFetchMixedContent_WithoutCsp_IsBlockedBeforeNetworkDispatch()
        {
            var dispatchCount = 0;
            using var client = new HttpClient(new StubHandler(_ =>
            {
                dispatchCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            var manager = new ResourceManager(client, isPrivate: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://api.attacker.test/data");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "fetch");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
            var securePage = new Uri("https://secure.example.test/page");

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                manager.SendAsync(request, policy: null, context: new FetchContext
                {
                    RequestUri = request.RequestUri,
                    InitiatorUri = securePage,
                    FrameDocumentUri = securePage,
                    TopLevelDocumentUri = securePage,
                    Destination = "fetch",
                    Mode = "no-cors",
                    CredentialsMode = "omit"
                }));

            Assert.Contains("Blocked by Mixed Content Policy", exception.Message);
            Assert.Equal(0, dispatchCount);
        }

        [Fact]
        public async Task FetchTextDetailedAsync_UnsupportedScheme_IsRejected()
        {
            using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var manager = new ResourceManager(client, isPrivate: true);
            var uri = new Uri("ftp://example.test/file.txt");

            var result = await manager.FetchTextDetailedAsync(uri, secFetchDest: "document");

            Assert.Equal(FetchStatus.UnknownError, result.Status);
            Assert.Equal(FetchFailureReasonCode.MalformedInput, result.FailureReason);
            Assert.Contains("Unsupported URL scheme", result.ErrorDetail);
        }

        [Fact]
        public async Task FetchBytesAsync_UnsupportedScheme_ReturnsNull()
        {
            using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var manager = new ResourceManager(client, isPrivate: true);
            var uri = new Uri("ftp://example.test/image.png");

            var bytes = await manager.FetchBytesAsync(uri, secFetchDest: "image");

            Assert.Null(bytes);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
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

        private sealed class CountingReadStream : Stream
        {
            private readonly MemoryStream _inner;

            public CountingReadStream(byte[] buffer)
            {
                _inner = new MemoryStream(buffer, writable: false);
            }

            public long BytesRead { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                var read = _inner.Read(buffer);
                BytesRead += read;
                return read;
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer, cancellationToken);
                BytesRead += read;
                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
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
