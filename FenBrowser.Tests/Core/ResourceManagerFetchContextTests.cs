using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class ResourceManagerFetchContextTests
{
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
