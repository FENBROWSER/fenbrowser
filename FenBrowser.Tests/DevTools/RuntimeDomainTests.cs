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

    [Fact]
    public async Task GetPropertiesAsync_ReturnsInspectableOwnPropertiesForObjectResults()
    {
        var host = new StubDevToolsHost
        {
            EvaluateScriptAsyncHandler = _ => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["alpha"] = 1,
                ["beta"] = "two"
            })
        };
        var domain = new RuntimeDomain(host);

        var evaluateResponse = await domain.HandleAsync("evaluate", CreateEvaluateRequest("({ alpha: 1, beta: 'two' })"));

        Assert.True(evaluateResponse.IsSuccess);
        var evaluateResult = Assert.IsType<EvaluateResult>(evaluateResponse.Result);
        Assert.Equal("object", evaluateResult.Result.Type);
        Assert.False(string.IsNullOrWhiteSpace(evaluateResult.Result.ObjectId));

        var propertiesResponse = await domain.HandleAsync("getProperties", CreateGetPropertiesRequest(evaluateResult.Result.ObjectId!));

        Assert.True(propertiesResponse.IsSuccess);
        var propertiesResult = Assert.IsType<GetPropertiesResult>(propertiesResponse.Result);
        Assert.Collection(
            propertiesResult.Result.OrderBy(property => property.Name),
            property =>
            {
                Assert.Equal("alpha", property.Name);
                Assert.Equal("number", property.Value?.Type);
                Assert.Equal(1, Convert.ToInt32(property.Value?.Value));
            },
            property =>
            {
                Assert.Equal("beta", property.Name);
                Assert.Equal("string", property.Value?.Type);
                Assert.Equal("two", property.Value?.Value);
            });
    }

    [Fact]
    public async Task ReleaseObjectAsync_RemovesStoredRemoteObject()
    {
        var host = new StubDevToolsHost
        {
            EvaluateScriptAsyncHandler = _ => Task.FromResult<object?>(new Dictionary<string, object?> { ["alpha"] = 1 })
        };
        var domain = new RuntimeDomain(host);

        var evaluateResponse = await domain.HandleAsync("evaluate", CreateEvaluateRequest("({ alpha: 1 })"));
        var evaluateResult = Assert.IsType<EvaluateResult>(evaluateResponse.Result);
        Assert.False(string.IsNullOrWhiteSpace(evaluateResult.Result.ObjectId));

        var releaseResponse = await domain.HandleAsync("releaseObject", CreateReleaseObjectRequest(evaluateResult.Result.ObjectId!));
        Assert.True(releaseResponse.IsSuccess);

        var propertiesResponse = await domain.HandleAsync("getProperties", CreateGetPropertiesRequest(evaluateResult.Result.ObjectId!));
        Assert.False(propertiesResponse.IsSuccess);
        Assert.Equal("Remote object not found", propertiesResponse.Error?.Message);
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

    private static ProtocolRequest CreateGetPropertiesRequest(string objectId)
    {
        using var doc = JsonDocument.Parse($"{{\"objectId\":{JsonSerializer.Serialize(objectId)}}}");
        return new ProtocolRequest
        {
            Id = 2,
            Method = "Runtime.getProperties",
            Params = doc.RootElement.Clone()
        };
    }

    private static ProtocolRequest CreateReleaseObjectRequest(string objectId)
    {
        using var doc = JsonDocument.Parse($"{{\"objectId\":{JsonSerializer.Serialize(objectId)}}}");
        return new ProtocolRequest
        {
            Id = 3,
            Method = "Runtime.releaseObject",
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
