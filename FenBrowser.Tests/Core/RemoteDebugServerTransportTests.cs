using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FenBrowser.DevTools.Core;
using Xunit;

namespace FenBrowser.Tests.Core;

public class RemoteDebugServerTransportTests
{
    [Fact]
    public async Task WebSocketRequest_ReturnsProtocolResponse()
    {
        using var remote = new RemoteDebugServer(new DevToolsServer(), port: 0, authToken: "test-token");
        remote.Start();

        using var http = new HttpClient();
        var discoveryJson = await http.GetStringAsync(
            $"http://127.0.0.1:{remote.Port}/json/list?token=test-token");
        Assert.Contains($":{remote.Port}/devtools/page/1?token=test-token", discoveryJson);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{remote.Port}/devtools/page/1?token=test-token"),
            CancellationToken.None);

        var request = Encoding.UTF8.GetBytes("""{"id":1,"method":"Missing.domain"}""");
        await socket.SendAsync(request, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var response = JsonDocument.Parse(responseJson);
        Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(WebSocketState.Open, socket.State);
    }
}
