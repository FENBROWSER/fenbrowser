using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Interface for browser integration with DevTools.
/// The browser implements this to provide DOM, styles, and network data.
/// </summary>
public interface IDevToolsHost
{
    /// <summary>
    /// Send a raw protocol command.
    /// </summary>
    Task<string> SendProtocolCommandAsync(string json);
    
    /// <summary>
    /// Event fired when a protocol event is broadcast.
    /// </summary>
    event Action<string>? ProtocolEventReceived;

    /// <summary>
    /// Get all network requests.
    /// </summary>
    IEnumerable<NetworkRequestInfo> GetNetworkRequests();
    
    /// <summary>
    /// Get console messages.
    /// </summary>
    IEnumerable<ConsoleMessageInfo> GetConsoleMessages();
    
    /// <summary>
    /// Execute JavaScript in the page context.
    /// </summary>
    Task<object?> EvaluateScriptAsync(string script);
    
    /// <summary>
    /// Highlight an element on the page (still useful internally, but we might prefer protocol).
    /// </summary>
    void HighlightElement(Element? element);

    /// <summary>
    /// Resolve a live DOM node to its DevTools protocol node ID.
    /// </summary>
    int GetNodeId(Node node);
    
    /// <summary>
    /// Navigate to element's location in DOM.
    /// </summary>
    void ScrollToElement(Element element);
    
    /// <summary>
    /// Get source scripts loaded by the page.
    /// </summary>
    IEnumerable<ScriptSourceInfo> GetScriptSources();

    /// <summary>
    /// Get FenBrowser-native layout/paint diagnostics for a protocol node ID.
    /// </summary>
    NodeDiagnosticsInfo? GetNodeDiagnostics(int nodeId);
    
    /// <summary>
    /// Current page URL.
    /// </summary>
    string? CurrentUrl { get; }
    
    /// <summary>
    /// Event when DOM changes.
    /// </summary>
    event Action? DomChanged;
    
    /// <summary>
    /// Event when new console message arrives.
    /// </summary>
    event Action<ConsoleMessageInfo>? ConsoleMessageAdded;
    
    /// <summary>
    /// Event when network request is made or updates.
    /// </summary>
    event Action<NetworkRequestInfo>? NetworkRequestUpdated;
    
    /// <summary>
    /// Request the browser to change the mouse cursor.
    /// </summary>
    void RequestCursorChange(CursorType cursor);
    
    /// <summary>
    /// Copy text to system clipboard. (10/10)
    /// </summary>
    void CopyToClipboard(string text);
    
    /// <summary>
    /// Set mouse capture to receive all mouse events during drag operations.
    /// </summary>
    void SetCapture();
    
    /// <summary>
    /// Release mouse capture after drag operations.
    /// </summary>
    void ReleaseCapture();
}

/// <summary>
/// Mouse cursor types.
/// </summary>
public enum CursorType
{
    Default,
    Pointer,
    Text,
    HorizontalResize,
    VerticalResize,
    Crosshair
}

/// <summary>
/// CSS rule that matched an element.
/// </summary>
public record MatchedCssRule(
    string Selector,
    string SourceFile,
    int LineNumber,
    Dictionary<string, string> Properties,
    int Specificity
);

/// <summary>
/// Network request information.
/// </summary>
public record NetworkRequestInfo(
    string Id,
    string Url,
    string Method,
    int StatusCode,
    string StatusText,
    string ContentType,
    long Size,
    double DurationMs,
    DateTime StartTime,
    Dictionary<string, string> RequestHeaders,
    Dictionary<string, string> ResponseHeaders,
    string? RequestBody,
    string? ResponseBody,
    bool IsComplete
);

/// <summary>
/// Console message information.
/// </summary>
public record ConsoleMessageInfo(
    string Message,
    ConsoleLevel Level,
    DateTime Timestamp,
    string? SourceFile,
    int? LineNumber,
    string? StackTrace
);

/// <summary>
/// Console message level.
/// </summary>
public enum ConsoleLevel
{
    Log,
    Info,
    Warn,
    Error,
    Debug
}

/// <summary>
/// Script source information.
/// </summary>
public record ScriptSourceInfo(
    string Url,
    string Content,
    bool IsInline,
    string ScriptId = "",
    int StartLine = 0,
    int StartColumn = 0
);

/// <summary>
/// FenBrowser-native diagnostics for a selected DOM node.
/// </summary>
public record NodeDiagnosticsInfo(
    int NodeId,
    string NodeName,
    NodeBoxModelInfo? BoxModel,
    ComputedLayoutSummaryInfo? ComputedStyle,
    IReadOnlyList<NodePaintNodeInfo> PaintNodes,
    FrameTelemetryInfo FrameTelemetry,
    bool HasLayoutBox,
    bool HasPaintNodes,
    bool IsVisible,
    IReadOnlyList<string> MissingReasons
);

public record NodeBoxModelInfo(
    RectInfo Margin,
    RectInfo Border,
    RectInfo Padding,
    RectInfo Content,
    EdgeSizesInfo MarginEdges,
    EdgeSizesInfo BorderEdges,
    EdgeSizesInfo PaddingEdges
);

public record RectInfo(float Left, float Top, float Right, float Bottom, float Width, float Height);

public record EdgeSizesInfo(double Top, double Right, double Bottom, double Left);

public record ComputedLayoutSummaryInfo(
    string? Display,
    string? Position,
    string? Visibility,
    string? Overflow,
    string? OverflowX,
    string? OverflowY,
    string? BoxSizing,
    string? Width,
    string? Height,
    int? ZIndex
);

public record NodePaintNodeInfo(
    string Type,
    RectInfo Bounds,
    float Opacity,
    bool IsFocused,
    bool IsHovered,
    bool HasClip,
    bool HasTransform
);

public record FrameTelemetryInfo(
    long FrameSequence,
    string? RequestedBy,
    string? InvalidationReason,
    string? RasterMode,
    double LayoutDurationMs,
    double PaintDurationMs,
    double RasterDurationMs,
    double TotalDurationMs,
    bool WatchdogTriggered,
    string? WatchdogReason,
    int DomNodeCount,
    int BoxCount,
    int PaintNodeCount
);
