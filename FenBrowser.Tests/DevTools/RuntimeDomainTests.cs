using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class RuntimeDomainTests
{
    [Fact]
    public async Task EvaluateAsync_AwaitsHostEvaluationAndReturnsResult()
    {
        var host = new StubDevToolsHost
        {
            EvaluateScriptAsyncHandler = async script =>
            {
                await Task.Delay(25);
                return $"eval:{script}";
            }
        };
        var domain = new RuntimeDomain(host);
        var request = CreateEvaluateRequest("2 + 2");

        var responseTask = domain.HandleAsync("evaluate", request);

        Assert.False(responseTask.IsCompleted, "RuntimeDomain should await asynchronous host evaluation.");

        var response = await responseTask;

        Assert.True(response.IsSuccess);
        var result = Assert.IsType<EvaluateResult>(response.Result);
        Assert.Equal("string", result.Result.Type);
        Assert.Equal("eval:2 + 2", result.Result.Value);
        Assert.Equal("eval:2 + 2", result.Result.Description);
        Assert.Equal(new[] { "2 + 2" }, host.EvaluatedScripts);
    }

    [Fact]
    public async Task EvaluateAsync_HostFailure_ReturnsProtocolFailure()
    {
        var host = new StubDevToolsHost
        {
            EvaluateScriptAsyncHandler = _ => Task.FromException<object?>(new InvalidOperationException("boom"))
        };
        var domain = new RuntimeDomain(host);

        var response = await domain.HandleAsync("evaluate", CreateEvaluateRequest("throw boom"));

        Assert.False(response.IsSuccess);
        Assert.NotNull(response.Error);
        Assert.Contains("Eval error: boom", response.Error!.Message, StringComparison.Ordinal);
    }

    private static ProtocolRequest CreateEvaluateRequest(string expression)
    {
        using var doc = JsonDocument.Parse($"{{\"expression\":{JsonSerializer.Serialize(expression)}}}");
        return new ProtocolRequest
        {
            Id = 1,
            Method = "Runtime.evaluate",
            Params = doc.RootElement.Clone()
        };
    }

    private sealed class StubDevToolsHost : IDevToolsHost
    {
        public Func<string, Task<object?>> EvaluateScriptAsyncHandler { get; set; } = script => Task.FromResult<object?>(script);
        public List<string> EvaluatedScripts { get; } = new();

        public Task<string> SendProtocolCommandAsync(string json) => Task.FromResult(json);
        public event Action<string>? ProtocolEventReceived;
        public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => Array.Empty<NetworkRequestInfo>();
        public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => Array.Empty<ConsoleMessageInfo>();
        public async Task<object?> EvaluateScriptAsync(string script)
        {
            EvaluatedScripts.Add(script);
            return await EvaluateScriptAsyncHandler(script);
        }
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
