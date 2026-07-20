using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenWebSocketHostNonBlockingTests
{
    [Fact]
    public async Task Connect_DoesNotWaitForServerHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var holdHandshake = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await Task.Delay(500);
        });

        using var host = new FenWebSocketHost();
        var stopwatch = Stopwatch.StartNew();
        var error = host.Connect($"ws://127.0.0.1:{port}/socket", Array.Empty<string>());
        stopwatch.Stop();

        Assert.Null(error);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"WebSocket constructor blocked for {stopwatch.Elapsed.TotalMilliseconds:F0}ms");

        await holdHandshake;
    }
}
