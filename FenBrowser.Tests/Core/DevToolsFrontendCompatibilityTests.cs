using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using Xunit;

namespace FenBrowser.Tests.Core;

public class DevToolsFrontendCompatibilityTests
{
    [Fact]
    public async Task BrowserFrontendSetupDomains_ReturnSuccess()
    {
        var server = new DevToolsServer();
        var host = new StubDevToolsHost();
        server.InitializeBrowserFrontendCompatibility(host);

        var pageResponse = await Send(server, 1, "Page.getFrameTree");
        var overlayResponse = await Send(server, 2, "Overlay.setInspectMode", new { mode = "none" });

        Assert.True(pageResponse.IsSuccess);
        Assert.True(overlayResponse.IsSuccess);
    }

    [Fact]
    public async Task CssInlineStylesForNode_ReturnsInlineStyle()
    {
        var document = Document.CreateHtmlDocument();
        var div = document.CreateElement("div");
        div.SetAttribute("style", "display: flex; height: 20px");
        document.Body.AppendChild(div);

        var server = new DevToolsServer();
        server.InitializeDom(() => document);
        server.InitializeCss(_ => null);

        var docResponse = await Send(server, 1, "DOM.getDocument");
        Assert.True(docResponse.IsSuccess);
        var nodeId = server.Registry.GetId(div);

        var cssResponse = await Send(server, 2, "CSS.getInlineStylesForNode", new { nodeId });

        Assert.True(cssResponse.IsSuccess);
        var json = ProtocolJson.Serialize(cssResponse);
        Assert.Contains("\"inlineStyle\"", json);
        Assert.Contains("\"display\"", json);
        Assert.Contains("\"flex\"", json);
    }

    private static async Task<ProtocolResponse> Send(DevToolsServer server, int id, string method, object? parameters = null)
    {
        var json = ProtocolJson.Serialize(new
        {
            id,
            method,
            @params = parameters
        });

        return ProtocolJson.Deserialize<ProtocolResponse>(await server.ProcessRequestAsync(json))!;
    }

    private sealed class StubDevToolsHost : IDevToolsHost
    {
        public Task<string> SendProtocolCommandAsync(string json) => Task.FromResult(json);
#pragma warning disable CS0067
        public event Action<string>? ProtocolEventReceived;
        public event Action? DomChanged;
        public event Action<ConsoleMessageInfo>? ConsoleMessageAdded;
        public event Action<NetworkRequestInfo>? NetworkRequestUpdated;
#pragma warning restore CS0067
        public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => Array.Empty<NetworkRequestInfo>();
        public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => Array.Empty<ConsoleMessageInfo>();
        public Task<object?> EvaluateScriptAsync(string script) => Task.FromResult<object?>(null);
        public void HighlightElement(Element? element) { }
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => Array.Empty<ScriptSourceInfo>();
        public string? CurrentUrl => "https://example.test/";
        public void RequestCursorChange(CursorType cursor) { }
        public void CopyToClipboard(string text) { }
        public void SetCapture() { }
        public void ReleaseCapture() { }
    }
}
