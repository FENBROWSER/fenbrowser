using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class NetworkDomainTests
{
    [Fact]
    public async Task GetResponseBody_ReturnsBufferedBody()
    {
        var host = new StubDevToolsHost(
            new NetworkRequestInfo(
                "req-1",
                "https://example.test/data.json",
                "POST",
                200,
                "OK",
                "application/json",
                27,
                14,
                DateTime.UtcNow,
                new Dictionary<string, string> { ["content-type"] = "application/json" },
                new Dictionary<string, string> { ["cache-control"] = "no-cache" },
                "{\"query\":\"fen\"}",
                "{\"ok\":true}",
                true));

        var domain = new NetworkDomain(host);
        using var paramsDoc = JsonDocument.Parse("{\"requestId\":\"req-1\"}");

        var response = await domain.HandleAsync(
            "getResponseBody",
            new ProtocolRequest
            {
                Id = 1,
                Method = "Network.getResponseBody",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.True(response.IsSuccess);
        var result = Assert.IsType<GetResponseBodyResult>(response.Result);
        Assert.Equal("{\"ok\":true}", result.Body);
        Assert.False(result.Base64Encoded);
    }

    [Fact]
    public async Task GetRequestPostData_ReturnsBufferedRequestBody()
    {
        var host = new StubDevToolsHost(
            new NetworkRequestInfo(
                "req-2",
                "https://example.test/search",
                "POST",
                201,
                "Created",
                "application/json",
                19,
                8,
                DateTime.UtcNow,
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                "{\"term\":\"devtools\"}",
                "{\"id\":7}",
                true));

        var domain = new NetworkDomain(host);
        using var paramsDoc = JsonDocument.Parse("{\"requestId\":\"req-2\"}");

        var response = await domain.HandleAsync(
            "getRequestPostData",
            new ProtocolRequest
            {
                Id = 2,
                Method = "Network.getRequestPostData",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.True(response.IsSuccess);
        var result = Assert.IsType<GetRequestPostDataResult>(response.Result);
        Assert.Equal("{\"term\":\"devtools\"}", result.PostData);
    }

    [Fact]
    public async Task GetResponseBody_WhenUnavailable_ReturnsFailure()
    {
        var host = new StubDevToolsHost(
            new NetworkRequestInfo(
                "req-3",
                "https://example.test/image.png",
                "GET",
                200,
                "OK",
                "image/png",
                1024,
                5,
                DateTime.UtcNow,
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null,
                null,
                true));

        var domain = new NetworkDomain(host);
        using var paramsDoc = JsonDocument.Parse("{\"requestId\":\"req-3\"}");

        var response = await domain.HandleAsync(
            "getResponseBody",
            new ProtocolRequest
            {
                Id = 3,
                Method = "Network.getResponseBody",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.False(response.IsSuccess);
        Assert.Contains("Response body unavailable", response.Error!.Message, StringComparison.Ordinal);
    }

    private sealed class StubDevToolsHost : IDevToolsHost
    {
        private readonly IReadOnlyList<NetworkRequestInfo> _requests;

        public StubDevToolsHost(params NetworkRequestInfo[] requests)
        {
            _requests = requests;
        }

        public Task<string> SendProtocolCommandAsync(string json) => Task.FromResult(json);
        public event Action<string>? ProtocolEventReceived;
        public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => _requests;
        public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => Array.Empty<ConsoleMessageInfo>();
        public Task<object?> EvaluateScriptAsync(string script) => Task.FromResult<object?>(script);
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
