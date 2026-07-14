using System.Text.Json;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;
using FenBrowser.DevTools.Panels;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class DevToolsProtocolDiagnosticsTests
{
    [Fact]
    public async Task DomGetDocument_HonorsRequestedDepthAndKeepsLazyChildIds()
    {
        var document = Document.CreateHtmlDocument("depth");
        var outer = document.CreateElement("section");
        var inner = document.CreateElement("button");
        inner.SetAttribute("id", "deep-target");
        outer.AppendChild(inner);
        document.Body!.AppendChild(outer);

        var server = new DevToolsServer();
        server.InitializeDom(() => document);

        var response = await Send<GetDocumentResult>(server, 1, "DOM.getDocument", new { depth = 1, pierce = true });

        Assert.NotNull(response.Result);
        var root = response.Result!.Root;
        Assert.NotNull(root.Children);
        Assert.DoesNotContain(Flatten(root), node => node.NodeName == "BUTTON");
        Assert.Contains(Flatten(root), node => node.ChildNodeIds is { Length: > 0 });
    }

    [Fact]
    public async Task DomGetDocument_DepthMinusOneHydratesDescendantsForSearch()
    {
        var document = Document.CreateHtmlDocument("full");
        var current = document.Body!;
        for (var i = 0; i < 6; i++)
        {
            var child = document.CreateElement("div");
            child.SetAttribute("data-depth", i.ToString());
            current.AppendChild(child);
            current = child;
        }

        var target = document.CreateElement("span");
        target.SetAttribute("id", "search-target");
        current.AppendChild(target);

        var server = new DevToolsServer();
        server.InitializeDom(() => document);

        var response = await Send<GetDocumentResult>(server, 1, "DOM.getDocument", new { depth = -1, pierce = true });

        Assert.NotNull(response.Result);
        Assert.Contains(Flatten(response.Result!.Root), node =>
            node.NodeName == "SPAN" &&
            node.Attributes?.TryGetValue("id", out var id) == true &&
            id == "search-target");
    }

    [Fact]
    public async Task DomGetDocument_PierceIncludesOpenShadowRoot()
    {
        var document = Document.CreateHtmlDocument("pierce");
        var host = document.CreateElement("section");
        document.Body!.AppendChild(host);
        var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
        var shadowChild = document.CreateElement("span");
        shadowChild.SetAttribute("id", "shadow-target");
        shadowRoot.AppendChild(shadowChild);

        var server = new DevToolsServer();
        server.InitializeDom(() => document);

        var shallow = await Send<GetDocumentResult>(server, 1, "DOM.getDocument", new { depth = -1, pierce = false });
        var pierced = await Send<GetDocumentResult>(server, 2, "DOM.getDocument", new { depth = -1, pierce = true });

        Assert.DoesNotContain(Flatten(shallow.Result!.Root), node =>
            node.Attributes?.TryGetValue("id", out var id) == true && id == "shadow-target");
        Assert.Contains(Flatten(pierced.Result!.Root), node =>
            node.Attributes?.TryGetValue("id", out var id) == true && id == "shadow-target");
    }

    [Fact]
    public async Task FenBrowserGetNodeDiagnostics_ReturnsHostDiagnostics()
    {
        var host = new StubDevToolsHost();
        var server = new DevToolsServer();
        server.InitializeFenBrowser(host);

        var response = await Send<NodeDiagnosticsInfo>(server, 7, "FenBrowser.getNodeDiagnostics", new { nodeId = 42 });

        Assert.True(response.IsSuccess);
        Assert.NotNull(response.Result);
        Assert.Equal(42, response.Result!.NodeId);
        Assert.True(response.Result.HasLayoutBox);
        Assert.Single(response.Result.PaintNodes);
    }

    [Fact]
    public async Task ElementsPanel_ActivationUsesBoundedDomSnapshotAndSearchUsesFullTree()
    {
        var host = new RecordingDevToolsHost();
        var panel = new ElementsPanel();

        panel.SetHost(host);
        panel.OnActivate();
        await host.WaitForCommandCountAsync(2);

        var activationRequests = host.Commands
            .Where(command => command.Method == "DOM.getDocument")
            .Take(2)
            .ToArray();

        Assert.Equal(2, activationRequests.Length);
        Assert.All(activationRequests, command => Assert.Equal(4, command.Depth));

        panel.Search("target");
        await host.WaitForFullTreeRequestAsync();

        Assert.Contains(host.Commands, command => command.Method == "DOM.getDocument" && command.Depth == -1);
    }

    [Fact]
    public async Task ElementsPanel_SelectInspectedElement_HydratesAncestorPathWithoutFullTreeRefresh()
    {
        var document = Document.CreateHtmlDocument("inspect");
        var current = document.Body!;
        for (var i = 0; i < 8; i++)
        {
            var child = document.CreateElement("div");
            child.SetAttribute("data-depth", i.ToString());
            current.AppendChild(child);
            current = child;
        }

        var target = document.CreateElement("button");
        target.SetAttribute("id", "inspect-target");
        current.AppendChild(target);

        var server = new DevToolsServer();
        server.InitializeDom(() => document);
        server.InitializeCss(_ => null);

        var host = new ServerBackedDevToolsHost(server);
        var panel = new ElementsPanel();
        var targetId = server.Registry.GetId(target);

        panel.SetHost(host);
        panel.SelectInspectedElement(target);

        await WaitForConditionAsync(() => panel.SelectedNodeId == targetId, "Expected inspected node to become selected.");
        Assert.DoesNotContain(host.Commands, command => command.Method == "DOM.highlightNode");

        Assert.Contains(host.Commands, command => command.Method == "DOM.requestChildNodes");
        Assert.DoesNotContain(host.Commands, command => command.Method == "DOM.getDocument" && command.Depth == -1);

        panel.SelectInspectedElement(target);
        await Task.Delay(100);

        Assert.DoesNotContain(host.Commands, command => command.Method == "DOM.highlightNode");
    }

    [Fact]
    public void DevToolsController_SelectElement_DelegatesToPanelWithoutDirectHostHighlight()
    {
        var document = Document.CreateHtmlDocument("inspect-controller");
        var target = document.CreateElement("button");
        document.Body!.AppendChild(target);

        var host = new HighlightRecordingHost();
        var panel = new SelectionRecordingPanel();
        var controller = new DevToolsController();

        controller.RegisterPanel(panel);
        controller.Attach(host);

        controller.SelectElement(target);

        Assert.Same(target, panel.SelectedElement);
        Assert.Equal(0, host.HighlightCount);
    }

    [Fact]
    public async Task ElementsPanel_SearchKeyboard_FocusesRunsFullTreeSearchAndNavigatesResults()
    {
        var document = Document.CreateHtmlDocument("search");
        var first = document.CreateElement("button");
        first.SetAttribute("id", "needle-one");
        document.Body!.AppendChild(first);
        var second = document.CreateElement("span");
        second.SetAttribute("class", "needle-two");
        document.Body!.AppendChild(second);

        var server = new DevToolsServer();
        server.InitializeDom(() => document);
        server.InitializeCss(_ => null);

        var host = new ServerBackedDevToolsHost(server);
        var panel = new ElementsPanel();
        panel.SetHost(host);

        Assert.True(panel.OnKeyDown(70, ctrl: true, shift: false, alt: false));
        Assert.True(panel.IsSearchFocused);

        foreach (var c in "needle")
        {
            panel.OnTextInput(c);
        }

        await WaitForConditionAsync(() => panel.SearchResultCount == 2, "Expected keyboard search to find both matching DOM nodes.");

        Assert.Equal("needle", panel.SearchQuery);
        Assert.Contains(host.Commands, command => command.Method == "DOM.getDocument" && command.Depth == -1);
        Assert.Equal(0, panel.SearchCurrentIndex);

        Assert.True(panel.OnKeyDown(13, ctrl: false, shift: false, alt: false));
        Assert.Equal(1, panel.SearchCurrentIndex);
    }

    private static IEnumerable<DomNodeDto> Flatten(DomNodeDto root)
    {
        yield return root;

        if (root.Children == null)
        {
            yield break;
        }

        foreach (var child in root.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task<ProtocolResponse<T>> Send<T>(DevToolsServer server, int id, string method, object? parameters = null)
    {
        var json = ProtocolJson.Serialize(new
        {
            id,
            method,
            @params = parameters
        });

        return ProtocolJson.Deserialize<ProtocolResponse<T>>(await server.ProcessRequestAsync(json))!;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail(failureMessage);
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
        public int GetNodeId(Node node) => 1;
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => Array.Empty<ScriptSourceInfo>();
        public NodeDiagnosticsInfo? GetNodeDiagnostics(int nodeId)
        {
            return new NodeDiagnosticsInfo(
                nodeId,
                "DIV",
                new NodeBoxModelInfo(
                    new RectInfo(0, 0, 20, 20, 20, 20),
                    new RectInfo(1, 1, 19, 19, 18, 18),
                    new RectInfo(2, 2, 18, 18, 16, 16),
                    new RectInfo(3, 3, 17, 17, 14, 14),
                    new EdgeSizesInfo(1, 1, 1, 1),
                    new EdgeSizesInfo(1, 1, 1, 1),
                    new EdgeSizesInfo(1, 1, 1, 1)),
                new ComputedLayoutSummaryInfo("block", "static", "visible", "visible", "visible", "visible", "content-box", "14px", "14px", null),
                new[]
                {
                    new NodePaintNodeInfo("BackgroundPaintNode", new RectInfo(0, 0, 20, 20, 20, 20), 1, false, false, false, false)
                },
                new FrameTelemetryInfo(1, "test", "Diagnostics", "Full", 1, 2, 3, 6, false, null, 3, 2, 1),
                true,
                true,
                true,
                Array.Empty<string>());
        }

        public string? CurrentUrl => "https://example.test/";
        public void RequestCursorChange(CursorType cursor) { }
        public void CopyToClipboard(string text) { }
        public void SetCapture() { }
        public void ReleaseCapture() { }
    }

    private sealed class HighlightRecordingHost : IDevToolsHost
    {
        public int HighlightCount { get; private set; }
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
        public void HighlightElement(Element? element) => HighlightCount++;
        public int GetNodeId(Node node) => 1;
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => Array.Empty<ScriptSourceInfo>();
        public NodeDiagnosticsInfo? GetNodeDiagnostics(int nodeId) => null;
        public string? CurrentUrl => "https://example.test/";
        public void RequestCursorChange(CursorType cursor) { }
        public void CopyToClipboard(string text) { }
        public void SetCapture() { }
        public void ReleaseCapture() { }
    }

    private sealed class SelectionRecordingPanel : IDevToolsPanel, IDevToolsElementSelectionPanel
    {
        public string Title => "Elements";
        public string? Shortcut => null;
        public bool IsDragging => false;
        public Element? SelectedElement { get; private set; }
#pragma warning disable CS0067
        public event Action? Invalidated;
#pragma warning restore CS0067

        public void SelectInspectedElement(Element element) => SelectedElement = element;
        public void OnActivate() { }
        public void OnDeactivate() { }
        public void Paint(SKCanvas canvas, SKRect bounds) { }
        public void OnMouseMove(float x, float y) { }
        public bool OnMouseDown(float x, float y, bool isRightButton) => false;
        public void OnMouseUp(float x, float y) { }
        public void OnMouseWheel(float x, float y, float deltaX, float deltaY) { }
        public bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt) => false;
        public void OnTextInput(char c) { }
        public void SetHost(IDevToolsHost host) { }
    }

    private sealed class RecordingDevToolsHost : IDevToolsHost
    {
        private readonly object _lock = new();
        private readonly List<RecordedProtocolCommand> _commands = new();

        public IReadOnlyList<RecordedProtocolCommand> Commands
        {
            get
            {
                lock (_lock)
                {
                    return _commands.ToArray();
                }
            }
        }

        public Task<string> SendProtocolCommandAsync(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            int? depth = null;
            if (root.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("depth", out var depthElement) &&
                depthElement.TryGetInt32(out var requestedDepth))
            {
                depth = requestedDepth;
            }

            lock (_lock)
            {
                _commands.Add(new RecordedProtocolCommand(method, depth));
            }

            object result = method switch
            {
                "DOM.getDocument" => new GetDocumentResult
                {
                    Root = new DomNodeDto
                    {
                        NodeId = 1,
                        NodeType = 9,
                        NodeName = "#document",
                        Children = Array.Empty<DomNodeDto>(),
                        ChildNodeCount = 0
                    }
                },
                _ => new { }
            };

            return Task.FromResult(ProtocolJson.Serialize(ProtocolResponse.Success(id, result)));
        }

        public async Task WaitForCommandCountAsync(int count)
        {
            for (var i = 0; i < 50; i++)
            {
                if (Commands.Count >= count)
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"Expected at least {count} protocol commands, saw {Commands.Count}.");
        }

        public async Task WaitForFullTreeRequestAsync()
        {
            for (var i = 0; i < 50; i++)
            {
                if (Commands.Any(command => command.Method == "DOM.getDocument" && command.Depth == -1))
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail("Expected a full-tree DOM.getDocument request.");
        }

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
        public int GetNodeId(Node node) => 1;
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => Array.Empty<ScriptSourceInfo>();
        public NodeDiagnosticsInfo? GetNodeDiagnostics(int nodeId) => null;
        public string? CurrentUrl => "https://example.test/";
        public void RequestCursorChange(CursorType cursor) { }
        public void CopyToClipboard(string text) { }
        public void SetCapture() { }
        public void ReleaseCapture() { }
    }

    private sealed class ServerBackedDevToolsHost : IDevToolsHost
    {
        private readonly DevToolsServer _server;
        private readonly object _lock = new();
        private readonly List<RecordedProtocolCommand> _commands = new();

        public ServerBackedDevToolsHost(DevToolsServer server)
        {
            _server = server;
        }

        public IReadOnlyList<RecordedProtocolCommand> Commands
        {
            get
            {
                lock (_lock)
                {
                    return _commands.ToArray();
                }
            }
        }

        public async Task<string> SendProtocolCommandAsync(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            int? depth = null;
            if (root.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("depth", out var depthElement) &&
                depthElement.TryGetInt32(out var requestedDepth))
            {
                depth = requestedDepth;
            }

            lock (_lock)
            {
                _commands.Add(new RecordedProtocolCommand(method, depth));
            }

            return await _server.ProcessRequestAsync(json);
        }

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
        public int GetNodeId(Node node) => _server.Registry.GetId(node);
        public void ScrollToElement(Element element) { }
        public IEnumerable<ScriptSourceInfo> GetScriptSources() => Array.Empty<ScriptSourceInfo>();
        public NodeDiagnosticsInfo? GetNodeDiagnostics(int nodeId) => null;
        public string? CurrentUrl => "https://example.test/";
        public void RequestCursorChange(CursorType cursor) { }
        public void CopyToClipboard(string text) { }
        public void SetCapture() { }
        public void ReleaseCapture() { }
    }

    private sealed record RecordedProtocolCommand(string Method, int? Depth);
}
