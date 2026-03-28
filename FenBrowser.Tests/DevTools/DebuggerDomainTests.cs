using System.Text.Json;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class DebuggerDomainTests
{
    [Fact]
    public async Task EnableAsync_BroadcastsScriptParsedForExistingSources()
    {
        var host = new StubDevToolsHost();
        host.ScriptSources.Add(new ScriptSourceInfo("https://example.com/app.js", "const answer = 42;", false, "script-1"));

        var events = new List<(string Method, object Payload)>();
        var domain = new DebuggerDomain(host, (method, payload) => events.Add((method, payload)));

        var response = await domain.HandleAsync("enable", CreateRequest("Debugger.enable", "{}"));

        Assert.True(response.IsSuccess);
        var evt = Assert.Single(events);
        Assert.Equal("Debugger.scriptParsed", evt.Method);
        var payload = Assert.IsType<ScriptParsedEvent>(evt.Payload);
        Assert.Equal("script-1", payload.ScriptId);
        Assert.Equal("https://example.com/app.js", payload.Url);
        Assert.Equal(host.ScriptSources[0].Content.Length, payload.Length);
    }

    [Fact]
    public async Task GetScriptSourceAsync_ReturnsRegisteredSourceContent()
    {
        var host = new StubDevToolsHost();
        host.ScriptSources.Add(new ScriptSourceInfo("https://example.com/app.js", "console.log('fen');", false, "script-2"));

        var domain = new DebuggerDomain(host);

        var response = await domain.HandleAsync("getScriptSource", CreateRequest("Debugger.getScriptSource", "{\"scriptId\":\"script-2\"}"));

        Assert.True(response.IsSuccess);
        var result = Assert.IsType<GetScriptSourceResult>(response.Result);
        Assert.Equal("console.log('fen');", result.ScriptSource);
    }

    private static ProtocolRequest CreateRequest(string method, string jsonParams)
    {
        using var doc = JsonDocument.Parse(jsonParams);
        return new ProtocolRequest
        {
            Id = 1,
            Method = method,
            Params = doc.RootElement.Clone()
        };
    }

    private sealed class StubDevToolsHost : IDevToolsHost
    {
        public List<ScriptSourceInfo> ScriptSources { get; } = new();

        public Task<string> SendProtocolCommandAsync(string json) => Task.FromResult(json);
        public event Action<string>? ProtocolEventReceived;
        public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => Array.Empty<NetworkRequestInfo>();
        public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => Array.Empty<ConsoleMessageInfo>();
        public Task<object?> EvaluateScriptAsync(string script) => Task.FromResult<object?>(null);
        public void HighlightElement(Element? element) { }
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => ScriptSources;
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
