using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FenBrowser.WebDriver;
using FenBrowser.WebDriver.BiDi;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.Tests.Core;

public sealed class BiDiProtocolTests
{
    [Fact]
    public async Task SessionSubscriptionAndContextTree_AreAvailable()
    {
        using var sessions = new SessionManager();
        var session = sessions.CreateSession(new Capabilities());
        var port = ReservePort();
        await using var server = new BiDiWebSocketServer(sessions, port);
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/session/{session.Id}/bidi"),
            CancellationToken.None);

        await SendAsync(socket, """{"id":1,"method":"session.subscribe","params":{"events":["browsingContext.load"]}}""");
        using var subscribe = JsonDocument.Parse(await ReceiveAsync(socket));
        Assert.Equal("success", subscribe.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            subscribe.RootElement.GetProperty("result").GetProperty("subscription").GetString()));

        await SendAsync(socket, """{"id":2,"method":"browsingContext.getTree","params":{}}""");
        using var tree = JsonDocument.Parse(await ReceiveAsync(socket));
        var contexts = tree.RootElement.GetProperty("result").GetProperty("contexts");
        Assert.Single(contexts.EnumerateArray());
        Assert.Equal(session.CurrentWindowHandle, contexts[0].GetProperty("context").GetString());
    }

    private static async Task SendAsync(ClientWebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(result.EndOfMessage);
        return buffer[..result.Count];
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
