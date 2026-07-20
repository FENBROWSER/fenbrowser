using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class ResourceManagerFetchContextTests
{
    [Theory]
    [InlineData("https://assets.example.test/page", "https://assets.example.test/image.png", "same-origin")]
    [InlineData("https://www.example.test/page", "https://static.example.test/image.png", "same-site")]
    [InlineData("https://parent.example.test/page", "https://challenge.other.test/image.png", "cross-site")]
    public void HeaderPolicy_ComputesFetchSiteFromInitiator(
        string initiator,
        string requestUri,
        string expected)
    {
        Assert.Equal(
            expected,
            BrowserRequestHeaderPolicy.DetermineSite(new Uri(initiator), new Uri(requestUri)));
    }

    [Fact]
    public void NetworkCapabilities_AdvertiseOnlyAvailableImageDecoders()
    {
        var accept = BrowserNetworkCapabilities.ImageAcceptHeader;

        Assert.Contains("image/png", accept);
        Assert.Contains("image/jpeg", accept);
        Assert.Contains("image/svg+xml", accept);
        Assert.Equal(BrowserNetworkCapabilities.SupportsWebP, accept.Contains("image/webp", StringComparison.Ordinal));
        Assert.DoesNotContain("image/avif", accept);
    }

    [Fact]
    public void NetworkCapabilities_AdvertiseBrotliOnlyWhenConfiguredForDecompression()
    {
        var config = NetworkConfiguration.Instance;
        var previous = config.EnableBrotli;
        try
        {
            config.EnableBrotli = false;
            Assert.DoesNotContain("br", BrowserNetworkCapabilities.AcceptEncodingHeader.Split(", "));

            config.EnableBrotli = true;
            Assert.Contains("br", BrowserNetworkCapabilities.AcceptEncodingHeader.Split(", "));
        }
        finally
        {
            config.EnableBrotli = previous;
        }
    }

    [Fact]
    public async Task FetchTextDetailedAsync_UsesExplicitInitiatorAndDestination()
    {
        HttpRequestMessage observed = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        }));
        var manager = new ResourceManager(client, isPrivate: true);

        var context = new FetchContext
        {
            RequestUri = new Uri("https://resource.example.test/frame"),
            InitiatorUri = new Uri("https://parent.example.test/page"),
            FrameDocumentUri = new Uri("https://parent.example.test/page"),
            TopLevelDocumentUri = new Uri("https://top.example.test/"),
            Destination = "iframe",
            Mode = "navigate",
            CredentialsMode = "include",
            IsTopLevelNavigation = false,
            IsUserInitiated = false
        };

        var result = await manager.FetchTextDetailedAsync(context, "text/html");

        Assert.Equal(FetchStatus.Success, result.Status);
        Assert.NotNull(observed);
        Assert.Equal(context.RequestUri, observed.RequestUri);
        Assert.Equal(new Uri("https://parent.example.test/"), observed.Headers.Referrer);
        Assert.Equal(new Uri("https://parent.example.test/page"), context.InitiatorUri);
        Assert.Equal("iframe", Assert.Single(observed.Headers.GetValues("Sec-Fetch-Dest")));
        Assert.Equal("navigate", Assert.Single(observed.Headers.GetValues("Sec-Fetch-Mode")));
        Assert.Equal(new Uri("https://top.example.test/"), context.TopLevelDocumentUri);
    }

    [Fact]
    public async Task FrameResponsePolicy_DoesNotReplaceTopLevelPolicy_AndAppliesToFrameSubresources()
    {
        HttpRequestMessage frameScriptRequest = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri.AbsolutePath == "/top")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("top")
                };
                response.Headers.TryAddWithoutValidation("Referrer-Policy", "unsafe-url");
                return response;
            }

            if (request.RequestUri.AbsolutePath == "/frame")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("frame")
                };
                response.Headers.TryAddWithoutValidation("Referrer-Policy", "no-referrer");
                return response;
            }

            frameScriptRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("script")
            };
        }));
        var manager = new ResourceManager(client, isPrivate: true);
        var topUri = new Uri("https://top.example.test/top");
        var frameUri = new Uri("https://frame.example.test/frame");

        await manager.FetchTextDetailedAsync(new FetchContext
        {
            RequestUri = topUri,
            InitiatorUri = topUri,
            FrameDocumentUri = topUri,
            TopLevelDocumentUri = topUri,
            Destination = "document",
            Mode = "navigate",
            CredentialsMode = "include",
            IsTopLevelNavigation = true
        });
        var frameResult = await manager.FetchTextDetailedAsync(new FetchContext
        {
            RequestUri = frameUri,
            InitiatorUri = topUri,
            FrameDocumentUri = topUri,
            TopLevelDocumentUri = topUri,
            Destination = "iframe",
            Mode = "navigate",
            CredentialsMode = "include",
            IsTopLevelNavigation = false
        });
        await manager.FetchTextDetailedAsync(new FetchContext
        {
            RequestUri = new Uri("https://frame.example.test/frame.js"),
            InitiatorUri = frameUri,
            FrameDocumentUri = frameUri,
            TopLevelDocumentUri = topUri,
            Destination = "script",
            Mode = "no-cors",
            CredentialsMode = "include",
            ReferrerPolicy = frameResult.ReferrerPolicy,
            IsTopLevelNavigation = false
        });

        Assert.Equal(ReferrerPolicyDirective.UnsafeUrl, manager.ActiveReferrerPolicy);
        Assert.Equal(ReferrerPolicyDirective.NoReferrer, frameResult.ReferrerPolicy);
        Assert.NotNull(frameScriptRequest);
        Assert.Null(frameScriptRequest.Headers.Referrer);
    }

    [Fact]
    public async Task FetchTextDetailedAsync_UsesTopLevelSiteForPartitionedCookies()
    {
        var requestUri = new Uri("https://assets.challenge.test/frame.js");
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
        using var client = new HttpClient(new StubHandler(request =>
        {
            observedCookies.Add(request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join(";", values)
                : string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("ok")
            };
        }));
        var manager = new ResourceManager(client, isPrivate: true, cookieJar);

        var firstResult = await manager.FetchTextDetailedAsync(Context(firstPartyTop, requestUri), "application/javascript");
        var secondResult = await manager.FetchTextDetailedAsync(
            Context(otherTop, new Uri(requestUri + "?second=1")),
            "application/javascript");

        Assert.Equal(FetchStatus.Success, firstResult.Status);
        Assert.Equal(FetchStatus.Success, secondResult.Status);
        Assert.Contains("challenge_partition=opaque", observedCookies[0]);
        Assert.DoesNotContain("challenge_partition=opaque", observedCookies[1]);

        FetchContext Context(Uri topLevel, Uri resource) => new()
        {
            RequestUri = resource,
            InitiatorUri = frameUri,
            FrameDocumentUri = frameUri,
            TopLevelDocumentUri = topLevel,
            Destination = "script",
            Mode = "no-cors",
            CredentialsMode = "include",
            Method = "GET"
        };
    }

    [Fact]
    public async Task FetchBytesAsync_RedirectKeepsTopLevelCookiePartitionAndImageMetadata()
    {
        var previousThirdPartySetting = BrowserSettings.Instance.BlockThirdPartyCookies;
        BrowserSettings.Instance.BlockThirdPartyCookies = false;
        try
        {
            var requestCount = 0;
            string redirectedCookie = null;
            string redirectedSite = null;
            using var client = new HttpClient(new StubHandler(request =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        RequestMessage = request
                    };
                    redirect.Headers.Location = new Uri("https://cdn.other.test/image.png");
                    redirect.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "challenge_session=opaque; Domain=other.test; Path=/; Secure; SameSite=None");
                    return redirect;
                }

                redirectedCookie = request.Headers.TryGetValues("Cookie", out var cookies)
                    ? string.Join(";", cookies)
                    : null;
                redirectedSite = Assert.Single(request.Headers.GetValues("Sec-Fetch-Site"));
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return response;
            }));
            var manager = new ResourceManager(client, isPrivate: true);
            var context = new FetchContext
            {
                RequestUri = new Uri("https://challenge.other.test/start"),
                InitiatorUri = new Uri("https://frame.google.test/challenge"),
                FrameDocumentUri = new Uri("https://frame.google.test/challenge"),
                TopLevelDocumentUri = new Uri("https://search.example.test/"),
                Destination = "image",
                Mode = "no-cors",
                CredentialsMode = "include"
            };

            var bytes = await manager.FetchBytesAsync(context, "image/png,*/*;q=0.8");

            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            Assert.Equal(2, requestCount);
            Assert.Contains("challenge_session=opaque", redirectedCookie);
            Assert.Equal("cross-site", redirectedSite);
            Assert.Equal(new Uri("https://search.example.test/"), context.TopLevelDocumentUri);
        }
        finally
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = previousThirdPartySetting;
        }
    }

    [Fact]
    public async Task FetchBytesDetailedAsync_HttpFailureReturnsStructuredReason()
    {
        using var client = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
            Content = new StringContent("missing")
        }));
        var manager = new ResourceManager(client, isPrivate: true);

        var result = await manager.FetchBytesDetailedAsync(ImageContext("https://cdn.example.test/missing.png"));

        Assert.False(result.Succeeded);
        Assert.Equal(BinaryFetchFailureReason.HttpError, result.FailureReason);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(new Uri("https://cdn.example.test/missing.png"), result.FinalUri);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task FetchBytesDetailedAsync_TransportFailureReturnsStructuredReason()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var manager = new ResourceManager(client, isPrivate: true);

        var result = await manager.FetchBytesDetailedAsync(ImageContext("https://cdn.example.test/image.png"));

        Assert.False(result.Succeeded);
        Assert.Equal(BinaryFetchFailureReason.TransportFailure, result.FailureReason);
        Assert.Contains("offline", result.FailureDetail);
        Assert.Null(result.Body);
    }

    private static FetchContext ImageContext(string requestUri) => new FetchContext
    {
        RequestUri = new Uri(requestUri),
        InitiatorUri = new Uri("https://page.example.test/"),
        FrameDocumentUri = new Uri("https://page.example.test/"),
        TopLevelDocumentUri = new Uri("https://page.example.test/"),
        Destination = "image",
        Mode = "no-cors",
        CredentialsMode = "include"
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_handler(request));
    }
}
