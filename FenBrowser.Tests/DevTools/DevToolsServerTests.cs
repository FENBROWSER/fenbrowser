using FenBrowser.DevTools.Core;
using FenBrowser.Core.Dom.V2;
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

    [Fact]
    public void Reset_ClearsRegisteredDomains_SoTheyCanBeReinitializedForAnotherTab()
    {
        var server = new DevToolsServer();
        var host = new StubDevToolsHost();

        server.InitializeDom(() => null);
        server.InitializeCss(_ => null);
        server.InitializeRuntime(host);
        server.InitializeNetwork(host);
        server.InitializeDebugger(host);
        server.InitializeLog();

        server.Reset();

        var ex = Record.Exception(() =>
        {
            server.InitializeDom(() => Document.CreateHtmlDocument());
            server.InitializeCss(_ => null);
            server.InitializeRuntime(host);
            server.InitializeNetwork(host);
            server.InitializeDebugger(host);
            server.InitializeLog();
        });

        Assert.Null(ex);
    }

    private sealed class StubDevToolsHost : IDevToolsHost
    {
        public Task<string> SendProtocolCommandAsync(string json) => Task.FromResult(json);
        public event Action<string>? ProtocolEventReceived;
        public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => Array.Empty<NetworkRequestInfo>();
        public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => Array.Empty<ConsoleMessageInfo>();
        public Task<object?> EvaluateScriptAsync(string script) => Task.FromResult<object?>(null);
        public void HighlightElement(Element? element) { }
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => Array.Empty<ScriptSourceInfo>();
        public string? CurrentUrl => "about:blank";
        public event Action? DomChanged;
        public event Action<ConsoleMessageInfo>? ConsoleMessageAdded;
        public event Action<NetworkRequestInfo>? NetworkRequestUpdated;
        public void RequestCursorChange(CursorType cursor) { }
        public void CopyToClipboard(string text) { }
        public void SetCapture() { }
        public void ReleaseCapture() { }
    }
}
