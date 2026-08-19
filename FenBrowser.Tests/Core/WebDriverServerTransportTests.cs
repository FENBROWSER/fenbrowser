using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FenBrowser.WebDriver;

namespace FenBrowser.Tests.Core;

public sealed class WebDriverServerTransportTests
{
    [Fact]
    public async Task StatusResponse_IsStreamedAndStopAsyncCompletes()
    {
        var port = ReservePort();
        await using var server = new WebDriverServer(port);
        server.Start();

        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/status");
        var payload = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        Assert.True(document.RootElement.GetProperty("value").GetProperty("ready").GetBoolean());

        await server.StopAsync();
    }

    [Fact]
    public async Task MalformedRequestBody_ReturnsInvalidArgument()
    {
        var port = ReservePort();
        await using var server = new WebDriverServer(port);
        server.Start();

        using var client = new HttpClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/session", content);
        var payload = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(
            "invalid argument",
            document.RootElement.GetProperty("value").GetProperty("error").GetString());
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
