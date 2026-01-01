using FenBrowser.Core.Dom;
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
    /// Execute JavaScript in the page context (Legacy, will be moved to Runtime domain).
    /// </summary>
    object? EvaluateScript(string script);
    
    /// <summary>
    /// Highlight an element on the page (still useful internally, but we might prefer protocol).
    /// </summary>
    void HighlightElement(Element? element);
    
    /// <summary>
    /// Navigate to element's location in DOM.
    /// </summary>
    void ScrollToElement(Element element);
    
    /// <summary>
    /// Get source scripts loaded by the page.
    /// </summary>
    IEnumerable<ScriptSourceInfo> GetScriptSources();
    
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
    bool IsInline
);
