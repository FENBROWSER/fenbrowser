using FenBrowser.DevTools.Core;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class DevToolsServerTests
{
    [Fact]
    public void RemoveJsonOutput_UnsubscribesListenerFromFutureBroadcasts()
    {
        var server = new DevToolsServer();
        var payloads = new List<string>();

        void Listener(string json) => payloads.Add(json);

        server.OnJsonOutput(Listener);
        server.BroadcastEvent("Runtime.consoleAPICalled", new { type = "log" });
        Assert.Single(payloads);

        server.RemoveJsonOutput(Listener);
        server.BroadcastEvent("Runtime.consoleAPICalled", new { type = "warn" });

        Assert.Single(payloads);
    }
}
