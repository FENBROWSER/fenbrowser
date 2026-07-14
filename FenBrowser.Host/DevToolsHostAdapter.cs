using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;
using FenBrowser.FenEngine.DevTools;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using System.Security.Cryptography;
using System.Text;

namespace FenBrowser.Host;

/// <summary>
/// DevTools host adapter for FenBrowser.Host.
/// Bridges the browser engine with DevTools.
/// </summary>
public class DevToolsHostAdapter : IDevToolsHost, IDisposable
{
    private readonly BrowserIntegration _browser;
    private readonly DevToolsServer _server;
    private readonly List<ConsoleMessageInfo> _consoleMessages = new();
    private readonly List<NetworkRequestInfo> _networkRequests = new();
    private readonly Action _needsRepaintHandler;
    private readonly Action<string> _jsonOutputHandler;
    private readonly Action<FenBrowser.FenEngine.DevTools.NetworkRequest> _networkRequestHandler;
    private readonly Action<string> _consoleMessageHandler;
    private bool _disposed;

    private static readonly Dictionary<int, string> HttpStatusTextMap = new()
    {
        [200] = "OK",
        [201] = "Created",
        [202] = "Accepted",
        [204] = "No Content",
        [206] = "Partial Content",
        [301] = "Moved Permanently",
        [302] = "Found",
        [304] = "Not Modified",
        [307] = "Temporary Redirect",
        [308] = "Permanent Redirect",
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [403] = "Forbidden",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [408] = "Request Timeout",
        [409] = "Conflict",
        [410] = "Gone",
        [413] = "Payload Too Large",
        [415] = "Unsupported Media Type",
        [429] = "Too Many Requests",
        [500] = "Internal Server Error",
        [501] = "Not Implemented",
        [502] = "Bad Gateway",
        [503] = "Service Unavailable",
        [504] = "Gateway Timeout"
    };
    
    public event Action DomChanged;
    public event Action<ConsoleMessageInfo> ConsoleMessageAdded;
    public event Action<NetworkRequestInfo> NetworkRequestUpdated;
    public event Action<string>? ProtocolEventReceived;
    public event Action<CursorType>? CursorChanged;
    
    public string CurrentUrl => _browser.CurrentUrl;
    
    public DevToolsHostAdapter(BrowserIntegration browser, DevToolsServer server)
    {
        _browser = browser;
        _server = server;

        _needsRepaintHandler = OnBrowserNeedsRepaint;
        _jsonOutputHandler = OnProtocolJsonOutput;
        _networkRequestHandler = OnEngineNetworkRequest;
        _consoleMessageHandler = OnBrowserConsoleMessage;
        
        // Wire up events
        _browser.NeedsRepaint += _needsRepaintHandler;
        
        // Wire up protocol events - Assuming OnJsonOutput can be handled on any thread or is already safe?
        // RemoteDebugServer handles it. Internal DevTools might need safe dispatch. 
        // Let's safe dispatch it.
        _server.OnJsonOutput(_jsonOutputHandler);
        
        // Initialize domains
        _server.InitializeRuntime(this);
        _server.InitializeNetwork(this);
        _server.InitializeDebugger(this);
        _server.InitializeLog();
        _server.InitializeBrowserFrontendCompatibility(this);
        _server.InitializeFenBrowser(this);
        
        // Wire up network events from legacy DevToolsCore (correlate with protocol)
        DevToolsCore.Instance.OnNetworkRequest += _networkRequestHandler;
        
        // Wire up console messages from engine
        _browser.ConsoleMessage += _consoleMessageHandler;
    }
    
    public async Task<string> SendProtocolCommandAsync(string json)
    {
        return await _server.ProcessRequestAsync(json);
    }
    
    public Task<object?> EvaluateScriptAsync(string script)
    {
        return _browser.EvaluateScriptAsync(script);
    }
    
    public void HighlightElement(Element? element)
    {
        Program.RunOnMainThread(() => _browser.HighlightElement(element));
    }

    public int GetNodeId(Node node) => _server.Registry.GetId(node);

    public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => _networkRequests;
    public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => _consoleMessages;

    public NodeDiagnosticsInfo? GetNodeDiagnostics(int nodeId)
    {
        var node = _server.Registry.GetNode(nodeId);
        if (node == null)
        {
            return null;
        }

        var missingReasons = new List<string>();
        var context = _browser.TryCreateRenderContextSnapshot();
        if (context == null)
        {
            missingReasons.Add("Renderer context is busy or unavailable.");
        }

        NodeBoxModelInfo? boxModel = null;
        var hasLayoutBox = false;
        var isVisible = false;
        if (context?.TryGetBox(node, out var box) == true && box != null)
        {
            hasLayoutBox = true;
            boxModel = ToBoxModelInfo(box);
            isVisible = IsVisible(box.BorderBox, context.Viewport);
        }
        else
        {
            missingReasons.Add("Node has no layout box in the current frame.");
        }

        var paintNodes = context != null
            ? FindPaintNodesForNode(context.PaintTreeRoots, node, context.Viewport).ToArray()
            : Array.Empty<NodePaintNodeInfo>();
        if (paintNodes.Length == 0)
        {
            missingReasons.Add("Node has no paint nodes in the current frame.");
        }
        else
        {
            isVisible = isVisible || paintNodes.Any(p => p.Bounds.Width > 0 && p.Bounds.Height > 0);
        }

        var style = context?.GetStyle(node);
        var telemetry = ToFrameTelemetryInfo(_browser.LastFrameTelemetry, context);
        return new NodeDiagnosticsInfo(
            nodeId,
            node.NodeName ?? node.GetType().Name,
            boxModel,
            style != null ? ToComputedLayoutSummary(style) : null,
            paintNodes,
            telemetry,
            hasLayoutBox,
            paintNodes.Length > 0,
            isVisible,
            missingReasons);
    }
    
    public void ScrollToElement(Element element)
    {
        Program.RunOnMainThread(() => _browser.ScrollToElement(element));
    }

    public void RequestCursorChange(CursorType cursor)
    {
        Program.RunOnMainThread(() => CursorChanged?.Invoke(cursor));
    }
    
    public IEnumerable<ScriptSourceInfo> GetScriptSources()
    {
        var engineSources = DevToolsCore.Instance.GetSources().ToList();
        var scripts = BuildScopedScriptSources(engineSources);

        foreach (var evalSource in engineSources.Where(source => string.Equals(source.Url, "eval.js", StringComparison.OrdinalIgnoreCase)))
        {
            if (scripts.Any(existing => string.Equals(existing.ScriptId, evalSource.ScriptId, StringComparison.Ordinal)))
            {
                continue;
            }

            scripts.Add(new ScriptSourceInfo(
                evalSource.Url,
                evalSource.Content,
                true,
                evalSource.ScriptId));
        }

        return scripts;
    }
    
    /// <summary>
    /// Copy text to system clipboard. (10/10)
    /// </summary>
    public void CopyToClipboard(string text)
    {
        Program.RunOnMainThread(() =>
        {
            Program.CopyToClipboard(text);
        });
    }
    
    /// <summary>
    /// Events for capture management - DevToolsWidget listens to these.
    /// </summary>
    public event Action? CaptureRequested;
    public event Action? CaptureReleased;
    
    public void SetCapture()
    {
        Program.RunOnMainThread(() => CaptureRequested?.Invoke());
    }
    
    public void ReleaseCapture()
    {
        Program.RunOnMainThread(() => CaptureReleased?.Invoke());
    }
    
    /// <summary>
    /// Add a console message.
    /// </summary>
    public void AddConsoleMessage(string message, ConsoleLevel level = ConsoleLevel.Log)
    {
        // Assumes called on MainThread now (wrapper above)
        var msg = new ConsoleMessageInfo(
            message,
            level,
            DateTime.Now,
            null,
            null,
            null
        );
        _consoleMessages.Add(msg);
        ConsoleMessageAdded?.Invoke(msg);
        
        // Broadcast protocol event
        var evt = new ConsoleAPICalledEvent
        {
            Type = level.ToString().ToLower(),
            Timestamp = (DateTime.Now - DateTime.UnixEpoch).TotalSeconds,
            Args = new[] { 
                new RemoteObject { 
                    Type = "string", 
                    Value = message, 
                    Description = message 
                } 
            }
        };
        _server.BroadcastEvent("Runtime.consoleAPICalled", evt);
    }
    
    private void BroadcastNetworkEvent(FenBrowser.FenEngine.DevTools.NetworkRequest req)
    {
        // Assumes called on MainThread now
        // Translate legacy NetworkRequest to protocol events
        if (req.Status == "pending")
        {
            var evt = new RequestWillBeSentEvent
            {
                RequestId = req.Id,
                Timestamp = (req.StartTime - DateTime.UnixEpoch).TotalSeconds,
                WallTime = (req.StartTime - DateTime.UnixEpoch).TotalSeconds,
                Dto = new FenBrowser.DevTools.Domains.DTOs.NetworkRequest
                {
                    Url = req.Url,
                    Method = req.Method,
                    Headers = req.RequestHeaders
                }
            };
            _server.BroadcastEvent("Network.requestWillBeSent", evt);
        }
        else if (req.Status == "success" || req.Status == "error" || req.Status == "redirect")
        {
            var respEvt = new ResponseReceivedEvent
            {
                RequestId = req.Id,
                Timestamp = (req.EndTime - DateTime.UnixEpoch).TotalSeconds,
                Dto = new NetworkResponse
                {
                    Url = req.Url,
                    Status = req.StatusCode,
                    StatusText = ResolveStatusText(req.StatusCode, req.Status),
                    Headers = req.ResponseHeaders,
                    MimeType = req.MimeType ?? ""
                }
            };
            _server.BroadcastEvent("Network.responseReceived", respEvt);
            
            var finishEvt = new LoadingFinishedEvent
            {
                RequestId = req.Id,
                Timestamp = (req.EndTime - DateTime.UnixEpoch).TotalSeconds,
                EncodedDataLength = req.Size
            };
            _server.BroadcastEvent("Network.loadingFinished", finishEvt);
        }

        // Update local list for initial state
        var existing = _networkRequests.FirstOrDefault(n => n.Id == req.Id);
        var info = new NetworkRequestInfo(
            req.Id,
            req.Url,
            req.Method,
            req.StatusCode,
            req.Status,
            req.MimeType ?? "",
            req.Size,
            (req.EndTime - req.StartTime).TotalMilliseconds,
            req.StartTime,
            req.RequestHeaders ?? new(),
            req.ResponseHeaders ?? new(),
            req.RequestBody,
            req.ResponseBody,
            req.Status != "pending"
        );

        if (existing != null)
        {
            _networkRequests.Remove(existing);
        }
        _networkRequests.Add(info);
        NetworkRequestUpdated?.Invoke(info);
    }

    private static NodeBoxModelInfo ToBoxModelInfo(BoxModel box)
    {
        return new NodeBoxModelInfo(
            ToRectInfo(box.MarginBox),
            ToRectInfo(box.BorderBox),
            ToRectInfo(box.PaddingBox),
            ToRectInfo(box.ContentBox),
            ToEdges(box.Margin),
            ToEdges(box.Border),
            ToEdges(box.Padding));
    }

    private static ComputedLayoutSummaryInfo ToComputedLayoutSummary(CssComputed style)
    {
        return new ComputedLayoutSummaryInfo(
            style.Display,
            style.Position,
            style.Visibility,
            style.Overflow,
            style.OverflowX,
            style.OverflowY,
            style.BoxSizing,
            FormatCssLength(style.Width, style.WidthExpression),
            FormatCssLength(style.Height, style.HeightExpression),
            style.ZIndex);
    }

    private static string? FormatCssLength(double? value, string? expression)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            return expression;
        }

        return value.HasValue ? $"{value.Value:0.##}px" : null;
    }

    private static IEnumerable<NodePaintNodeInfo> FindPaintNodesForNode(
        IReadOnlyList<PaintNodeBase>? roots,
        Node node,
        SKRect viewport)
    {
        if (roots == null)
        {
            yield break;
        }

        var yielded = 0;
        foreach (var paintNode in EnumeratePaintNodes(roots))
        {
            if (!ReferenceEquals(paintNode.SourceNode, node))
            {
                continue;
            }

            yield return new NodePaintNodeInfo(
                paintNode.GetType().Name,
                ToRectInfo(paintNode.Bounds),
                paintNode.Opacity,
                paintNode.IsFocused,
                paintNode.IsHovered,
                paintNode.ClipRect.HasValue,
                paintNode.Transform.HasValue);

            yielded++;
            if (yielded >= 64)
            {
                yield break;
            }
        }
    }

    private static IEnumerable<PaintNodeBase> EnumeratePaintNodes(IReadOnlyList<PaintNodeBase> roots)
    {
        var stack = new Stack<PaintNodeBase>(roots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            if (node.Children == null)
            {
                continue;
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    private static int CountPaintNodes(IReadOnlyList<PaintNodeBase>? roots)
    {
        return roots == null ? 0 : EnumeratePaintNodes(roots).Count();
    }

    private static FrameTelemetryInfo ToFrameTelemetryInfo(RenderFrameTelemetry? telemetry, RenderContext? context)
    {
        return new FrameTelemetryInfo(
            telemetry?.FrameSequence ?? 0,
            telemetry?.RequestedBy,
            telemetry?.InvalidationReason.ToString(),
            telemetry?.RasterMode.ToString(),
            telemetry?.LayoutDurationMs ?? 0,
            telemetry?.PaintDurationMs ?? 0,
            telemetry?.RasterDurationMs ?? 0,
            telemetry?.TotalDurationMs ?? 0,
            telemetry?.WatchdogTriggered ?? false,
            telemetry?.WatchdogReason,
            telemetry?.DomNodeCount ?? 0,
            telemetry?.BoxCount ?? context?.Boxes.Count ?? 0,
            telemetry?.PaintNodeCount ?? CountPaintNodes(context?.PaintTreeRoots));
    }

    private static RectInfo ToRectInfo(SKRect rect)
    {
        return new RectInfo(
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            rect.Width,
            rect.Height);
    }

    private static EdgeSizesInfo ToEdges(FenBrowser.Core.Thickness thickness)
    {
        return new EdgeSizesInfo(
            thickness.Top,
            thickness.Right,
            thickness.Bottom,
            thickness.Left);
    }

    private static bool IsVisible(SKRect rect, SKRect viewport)
    {
        return rect.Width > 0 &&
               rect.Height > 0 &&
               (!viewport.IsEmpty ? rect.IntersectsWith(viewport) : true);
    }

    private static string ResolveStatusText(int statusCode, string fallbackStatus)
    {
        if (HttpStatusTextMap.TryGetValue(statusCode, out var text))
        {
            return text;
        }

        return fallbackStatus ?? string.Empty;
    }

    private static bool IsInlineSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        return string.Equals(url, "eval.js", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("inline:", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
    }

    private void OnBrowserNeedsRepaint()
    {
        Program.RunOnMainThread(() => DomChanged?.Invoke());
    }

    private void OnProtocolJsonOutput(string json)
    {
        Program.RunOnMainThread(() => ProtocolEventReceived?.Invoke(json));
    }

    private void OnEngineNetworkRequest(FenBrowser.FenEngine.DevTools.NetworkRequest request)
    {
        Program.RunOnMainThread(() => BroadcastNetworkEvent(request));
    }

    private void OnBrowserConsoleMessage(string msg)
    {
        Program.RunOnMainThread(() =>
        {
            var level = ConsoleLevel.Log;
            var cleanMsg = msg;

            if (msg.StartsWith("[Error] ", StringComparison.Ordinal)) { level = ConsoleLevel.Error; cleanMsg = msg.Substring(8); }
            else if (msg.StartsWith("[Warn] ", StringComparison.Ordinal)) { level = ConsoleLevel.Warn; cleanMsg = msg.Substring(7); }
            else if (msg.StartsWith("[Info] ", StringComparison.Ordinal)) { level = ConsoleLevel.Info; cleanMsg = msg.Substring(7); }
            else if (msg.StartsWith("[Debug] ", StringComparison.Ordinal)) { level = ConsoleLevel.Debug; cleanMsg = msg.Substring(8); }
            else if (msg.StartsWith("[Alert] ", StringComparison.Ordinal)) { level = ConsoleLevel.Info; cleanMsg = "alert: " + msg.Substring(8); }

            AddConsoleMessage(cleanMsg, level);
        });
    }

    private List<ScriptSourceInfo> BuildScopedScriptSources(List<SourceFile> engineSources)
    {
        var scripts = new List<ScriptSourceInfo>();
        var documentRoot = _browser.Document;
        if (documentRoot == null)
        {
            return engineSources
                .Select(source => new ScriptSourceInfo(source.Url, source.Content, IsInlineSource(source.Url), source.ScriptId))
                .ToList();
        }

        var currentUrl = CurrentUrl;
        var inlineIndex = 0;
        foreach (var node in EnumerateNodes(documentRoot))
        {
            if (node is not Element element ||
                !string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var src = element.GetAttribute("src");
            if (!string.IsNullOrWhiteSpace(src))
            {
                var resolvedUrl = ResolveScriptUrl(currentUrl, src);
                var matchedSource = engineSources.FirstOrDefault(source => string.Equals(source.Url, resolvedUrl, StringComparison.OrdinalIgnoreCase));
                scripts.Add(new ScriptSourceInfo(
                    resolvedUrl,
                    matchedSource?.Content ?? string.Empty,
                    false,
                    matchedSource?.ScriptId ?? MakeStableScriptId(resolvedUrl)));
                continue;
            }

            var content = element.TextContent ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                inlineIndex++;
                continue;
            }

            var inlineUrl = !string.IsNullOrWhiteSpace(currentUrl)
                ? $"{currentUrl}#inline-{inlineIndex}"
                : $"inline:{inlineIndex}";
            var matchedInlineSource = engineSources.FirstOrDefault(source =>
                string.Equals(source.Content, content, StringComparison.Ordinal) &&
                (IsInlineSource(source.Url) || string.Equals(source.Url, currentUrl, StringComparison.OrdinalIgnoreCase)));

            scripts.Add(new ScriptSourceInfo(
                inlineUrl,
                content,
                true,
                matchedInlineSource?.ScriptId ?? MakeStableScriptId(inlineUrl)));
            inlineIndex++;
        }

        return scripts;
    }

    private static IEnumerable<Node> EnumerateNodes(Node root)
    {
        yield return root;

        foreach (var child in root.ChildNodes)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static string ResolveScriptUrl(string? currentUrl, string scriptUrl)
    {
        if (string.IsNullOrWhiteSpace(scriptUrl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(scriptUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        if (!string.IsNullOrWhiteSpace(currentUrl) &&
            Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri) &&
            Uri.TryCreate(currentUri, scriptUrl, out var resolved))
        {
            return resolved.AbsoluteUri;
        }

        return scriptUrl;
    }

    private static string MakeStableScriptId(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _browser.NeedsRepaint -= _needsRepaintHandler;
        _browser.ConsoleMessage -= _consoleMessageHandler;
        DevToolsCore.Instance.OnNetworkRequest -= _networkRequestHandler;
        _server.RemoveJsonOutput(_jsonOutputHandler);
        CursorChanged = null;
        CaptureRequested = null;
        CaptureReleased = null;
        ProtocolEventReceived = null;
        DomChanged = null;
        ConsoleMessageAdded = null;
        NetworkRequestUpdated = null;
        _consoleMessages.Clear();
        _networkRequests.Clear();
    }
}
