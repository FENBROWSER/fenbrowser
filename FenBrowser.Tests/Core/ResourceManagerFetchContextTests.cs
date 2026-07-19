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
