using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class NetworkResponseStreamTests
{
    [Fact]
    public async Task ResponseStream_RequiresAsyncReads()
    {
        using var http = new HttpClient(new StubHandler());
        var client = new NetworkClient(new[] { new HttpHandler(http) });
        await using var stream = await client.GetStreamAsync("https://stream.example.test/data");
        var buffer = new byte[4];

        Assert.Throws<NotSupportedException>(() => stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(4, await stream.ReadAsync(buffer));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false))
            });
        }
    }
}
