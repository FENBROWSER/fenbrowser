using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Security;
using FenBrowser.Core.Storage;
using FenBrowser.FenEngine.Security; // Added
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.DevTools;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;

using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Rendering.Core;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType; // For IHistoryBridge

namespace FenBrowser.FenEngine.Rendering
{
    public readonly record struct BrowserRenderSnapshot(
        Element Root,
        Dictionary<Node, CssComputed> Styles,
        long Version,
        bool HasStableStyles);

    /// <summary>
    /// High-level facade wrapping existing engine pieces.
    /// C# 5.0 Compatible for Windows Phone 8.1 (No Newtonsoft dependency)
    /// </summary>
    public interface IBrowser
    {
        // Core properties
        Uri CurrentUri { get; }
        ResourceManager ResourceManager { get; }
        bool CanGoBack { get; }
        bool CanGoForward { get; }
        SecurityState SecurityState { get; }
        CertificateInfo CurrentCertificate { get; }
        Dictionary<Node, CssComputed> ComputedStyles { get; }

        // Events
        event EventHandler<Uri> Navigated;
        event EventHandler<string> NavigationFailed;
        event EventHandler<bool> LoadingChanged;
        event EventHandler<string> TitleChanged;
        event EventHandler<object> RepaintReady;
        event Action<string> ConsoleMessage;
        event Action<SKRect?> HighlightRectChanged;
        event Func<string, JsPermissions, Task<bool>> PermissionRequested;

        // Navigation
        Task<bool> NavigateAsync(string url);
        Task<bool> GoBackAsync();
        Task<bool> GoForwardAsync();
        Task<bool> RefreshAsync();
        Task<string> GetCurrentUrlAsync();
        Task<string> GetTitleAsync();

        // Window/Frame management
        WindowRect GetWindowRect();
        WindowRect SetWindowRect(int? x, int? y, int? width, int? height);
        WindowRect MaximizeWindow();
        WindowRect MinimizeWindow();
        WindowRect FullscreenWindow();
        Task CreateNewTabAsync();
        Task SwitchToFrameAsync(object frameId);
        Task SwitchToParentFrameAsync();

        // Element operations
        Task<string> FindElementAsync(string strategy, string value, string parentId = null);
        Task<string[]> FindElementsAsync(string strategy, string value, string parentId = null);
        Task<string> GetActiveElementAsync();
        Task<string> GetShadowRootAsync(string elementId);
        Task<bool> IsElementSelectedAsync(string elementId);
        Task<string> GetElementAttributeAsync(string elementId, string name);
        Task<object> GetElementPropertyAsync(string elementId, string name);
        Task<string> GetElementCssValueAsync(string elementId, string property);
        Task<string> GetElementTextAsync(string elementId);
        Task<string> GetElementTagNameAsync(string elementId);
        Task<ElementRect> GetElementRectAsync(string elementId);
        Task<bool> IsElementEnabledAsync(string elementId);
        Task<string> GetElementComputedRoleAsync(string elementId);
        Task<string> GetElementComputedLabelAsync(string elementId);
        Task ClickElementAsync(string elementId);
        Task ClearElementAsync(string elementId);
        Task SendKeysToElementAsync(string elementId, string text, bool strictFileInteractability = false);

        // Document
        Task<string> GetPageSourceAsync();
        Task<object> ExecuteScriptAsync(string script, object[] args = null);
        Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeoutMs);
        Task<string> CaptureScreenshotAsync();
        Task<string> CaptureElementScreenshotAsync(string elementId);
        Task<string> PrintToPdfAsync(double pageWidth, double pageHeight, bool landscape, double scale);

        // Cookies
        Task<List<WebDriverCookie>> GetAllCookiesAsync();
        Task<WebDriverCookie> GetCookieAsync(string name);
        Task AddCookieAsync(WebDriverCookie cookie);
        Task DeleteCookieAsync(string name);
        Task DeleteAllCookiesAsync();
        void SetCookie(string name, string value);
        void DeleteCookie(string name);
        void ClearBrowsingData();

        // Actions
        Task PerformActionsAsync(List<ActionChain> actions);
        Task ReleaseActionsAsync();

        // Alerts
        Task<bool> HasAlertAsync();
        Task DismissAlertAsync();
        Task AcceptAlertAsync();
        Task<string> GetAlertTextAsync();
        Task SendAlertTextAsync(string text);

        // Legacy/Utility
        IList<string> GetAllLinks();
        string GetTextContent();
        Element GetDomRoot();
        string GetRawHtml();
        void HighlightElement(Element element);
        void RemoveHighlight();

        // Interactive
        Task HandleElementClick(Element element);
        Task HandleKeyPress(string key);
    }

    /// <summary>Represents a window rectangle (position and size)</summary>
    public class WindowRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>Represents an element's bounding rectangle</summary>
    public class ElementRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>Represents a browser cookie for WebDriver</summary>
    public class WebDriverCookie
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Path { get; set; } = "/";
        public string Domain { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
        public long? Expiry { get; set; }
        public string SameSite { get; set; } = "Lax";
    }

    /// <summary>Represents an action chain for complex input</summary>
    public class ActionChain
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public List<InputAction> Actions { get; } = new List<InputAction>();
    }

    /// <summary>Represents a single input action</summary>
    public class InputAction
    {
        public string Type { get; set; }
        public int Duration { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }
        public string Value { get; set; }
        public string Origin { get; set; }
    }

    public enum SecurityState
    {
        None,
        Secure,
        NotSecure,
        Warning
    }

    public sealed class BrowserHost : IBrowser, IDisposable, IHistoryBridge
    {
        private const string WebDriverElementTokenPrefix = "__fen_wd_el__:";
        private const string WebDriverShadowTokenPrefix = "__fen_wd_sr__:";
        private const string WebDriverFrameTokenPrefix = "__fen_wd_fr__:";
        private const string WebDriverWindowTokenPrefix = "__fen_wd_win__:";
        private const string WebDriverArgElementMarker = "__fen_wd_arg_element_id__";
        private const string WebDriverArgShadowRootMarker = "__fen_wd_arg_shadow_root_id__";
        private const string WebDriverArgElementDomIdMarker = "__fen_wd_arg_element_dom_id__";
        private const string WebDriverArgShadowHostDomIdMarker = "__fen_wd_arg_shadow_host_dom_id__";
        private const string WebDriverArgFrameMarker = "__fen_wd_arg_frame_id__";
        private const string WebDriverArgWindowMarker = "__fen_wd_arg_window_id__";
        private const string WebDriverDomIdAttribute = "data-fen-wd-id";
        private const string WebDriverCanonicalProbeAttribute = "data-fen-wd-canon";
        private const string WebDriverShadowHostProbeAttribute = "data-fen-wd-shadow-host-probe";
        private const string WebDriverShadowRootProbeAttribute = "data-fen-wd-shadow-root-probe";
        private const string WebDriverFrameScriptsHydratedAttribute = "data-fen-wd-frame-scripts-hydrated";
        private const string WebDriverFrameScriptsHydratedUrlAttribute = "data-fen-wd-frame-scripts-hydrated-url";
        private const string WebDriverUploadedFilesAttribute = "data-fen-wd-uploaded-files";
        private static readonly bool WebDriverFrameTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("FEN_WEBDRIVER_FRAME_TRACE"), "1", StringComparison.Ordinal);
        private static readonly HashSet<string> WebDriverBooleanAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "allowfullscreen", "allowpaymentrequest", "async", "autofocus", "autoplay", "checked", "controls",
            "default", "defer", "disabled", "formnovalidate", "hidden", "ismap", "itemscope", "loop",
            "multiple", "muted", "nomodule", "novalidate", "open", "playsinline", "readonly", "required",
            "reversed", "selected"
        };
        private static readonly FieldInfo ElementShadowRootField = typeof(Element).GetField(
            "_shadowRoot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Regex InlineWindowOpenCallRegex = new(
            @"window\s*\.\s*open\s*\((?<args>[^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly CustomHtmlEngine _engine = new CustomHtmlEngine();
        private readonly ResourceManager _resources;
        private readonly ConditionalWeakTable<Document, FrameResourceSecurityContext> _frameResourceSecurity = new();
        // Per-host speculative prefetcher fed by the HTML PreloadScanner;
        // populates the ResourceManager text/image caches before the parser
        // reaches the resource references in the stream.
        private FenBrowser.Core.Network.ResourcePrefetcher _prefetcher;
        private readonly NavigationManager _navManager;
        private readonly BrowserHostOptions _options;
        private static readonly BrowserCookieJar SharedProfileCookieJar = new BrowserCookieJar();
        private readonly NavigationLifecycleTracker _navigationLifecycle = new NavigationLifecycleTracker();
        private readonly NavigationSubresourceTracker _navigationSubresources = new NavigationSubresourceTracker();
        private readonly FenBrowser.FenEngine.Core.EngineLoop _engineLoop; // Phase 5: Engine Loop
        private readonly InputManager _inputManager = new InputManager();
        private Uri _current;
        private long _latestNavigationId;
        private long _activeRenderNavigationId;
        private long _engineDiagnosticsCapturedNavigationId;
        private long _renderedDiagnosticsCapturedNavigationId;
        private int _engineDiagnosticsCapturedNodeCount;
        private int _renderedDiagnosticsCapturedTextLength;
        private int _renderedDiagnosticsCapturedContentHash;
        private bool _disposed;
        private readonly Action<Element> _elementStateChangedHandler;
        private readonly Action<Element> _styleAttributeChangedHandler;
        private CancellationTokenSource _interactionRecascadeDebounce;
        
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private int _historyIndex = -1;
        private bool _isNavigatingHistory;
        
        // Map WebDriver element and shadow-root IDs to live DOM nodes.
        private readonly Dictionary<string, Element> _elementMap = new Dictionary<string, Element>();
        private readonly Dictionary<string, string> _elementBrowsingContextMap = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShadowRoot> _shadowRootMap = new Dictionary<string, ShadowRoot>();
        private readonly Stack<Element> _frameContextStack = new Stack<Element>();
        private Element _currentFrameElement;
        private bool _frameContextInvalidated;
        private Action<string> _fontLoadedHandler;
        private readonly ImageLoader.ImageLoaderRequestContext _imageLoaderContext;
        private readonly FontRegistry.FontLoaderRequestContext _fontLoaderContext;
        private readonly string _imageLoaderContextId = Guid.NewGuid().ToString("N");
        private double? _hostViewportHintWidth;
        private double? _hostViewportHintHeight;

        private sealed record FrameResourceSecurityContext(
            Uri DocumentUri,
            CspPolicy Policy,
            ReferrerPolicyDirective ReferrerPolicy);

        private sealed class ResourceLoaderContextScope : IDisposable
        {
            private readonly IDisposable _imageScope;
            private readonly IDisposable _fontScope;
            private bool _disposed;

            public ResourceLoaderContextScope(
                ImageLoader.ImageLoaderRequestContext imageContext,
                FontRegistry.FontLoaderRequestContext fontContext)
            {
                _imageScope = ImageLoader.EnterRequestContext(imageContext);
                _fontScope = FontRegistry.EnterRequestContext(fontContext);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _fontScope.Dispose();
                _imageScope.Dispose();
                _disposed = true;
            }
        }

        // External renderer reference: BrowserIntegration injects the actual renderer used for
        // painting so that hit tests in DispatchInputEvent use the correct (populated) paint tree
        // instead of the stale _engine._cachedRenderer that never has Render() called on it.
        private SkiaDomRenderer _activeRenderer;
        public void SetActiveRenderer(SkiaDomRenderer renderer) { _activeRenderer = renderer; }

        private (double? Width, double? Height) GetRenderViewportHint()
        {
            if (_hostViewportHintWidth > 0 && _hostViewportHintHeight > 0)
            {
                return (_hostViewportHintWidth, _hostViewportHintHeight);
            }

            try
            {
                var context = _activeRenderer?.CreateRenderContext();
                if (context != null && context.ViewportWidth > 0 && context.ViewportHeight > 0)
                {
                    return (context.ViewportWidth, context.ViewportHeight);
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserApi] Failed reading active renderer viewport: {ex.Message}", LogCategory.Rendering);
            }

            return (null, null);
        }

        public void UpdateViewportHint(double width, double height)
        {
            _hostViewportHintWidth = width > 0 ? width : null;
            _hostViewportHintHeight = height > 0 ? height : null;
        }

        // Tracks whether the last dispatched click event allowed default action.
        // BrowserIntegration triggers DOM click first, then calls HandleElementClick for fallback activation.
        private bool _lastClickDefaultAllowed = true;
        private bool _lastClickHadTarget;
        private Element _lastClickTarget;
        private Element _lastDispatchedInputTarget;
        private bool _pendingWebDriverClickPointValid;
        private int _pendingWebDriverClickClientX;
        private int _pendingWebDriverClickClientY;
        private bool _suppressNextDomClickDispatchInHandleElementClick;
        private readonly object _programmaticNavigationLock = new();
        private string _programmaticNavigationInFlightUrl;

        public event EventHandler<Uri> Navigated;
        public event EventHandler<string> NavigationFailed;
        public event EventHandler<NavigationLifecycleTransition> NavigationLifecycleChanged;
        public event EventHandler<bool> LoadingChanged;
        public event EventHandler<string> TitleChanged;
        public event EventHandler<object> RepaintReady;
        public event Action<string> ConsoleMessage;
        public event Action<SKRect?> HighlightRectChanged;
        public event Func<string, JsPermissions, Task<bool>> PermissionRequested;
        private static void TryLogDebug(string message, LogCategory category = LogCategory.General)
        {
            try { EngineLogCompat.Debug(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Debug log failed: {ex.Message}"); }
        }

        private static void TryLogInfo(string message, LogCategory category = LogCategory.General)
        {
            try { EngineLogCompat.Info(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Info log failed: {ex.Message}"); }
        }

        private static void TryLogWarn(string message, LogCategory category = LogCategory.General)
        {
            try { EngineLogCompat.Warn(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Warn log failed: {ex.Message}"); }
        }

        private static void TryLogError(string message, LogCategory category = LogCategory.General)
        {
            try { EngineLogCompat.Error(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
        }

        private static Document ResolveSnapshotDocument(Node activeNode)
        {
            if (activeNode == null)
            {
                return null;
            }

            if (activeNode is Document document)
            {
                return document;
            }

            return activeNode.OwnerDocument;
        }

        private static string BuildEngineSourceSnapshot(Node activeNode, Uri uri)
        {
            if (activeNode == null)
            {
                return "<!-- Fen engine source unavailable: active DOM is null. -->";
            }

            try
            {
                var document = ResolveSnapshotDocument(activeNode);
                if (document != null)
                {
                    var documentElementHtml = document.DocumentElement?.OuterHTML;
                    if (!string.IsNullOrWhiteSpace(documentElementHtml))
                    {
                        var builder = new System.Text.StringBuilder();
                        if (document.Doctype != null)
                        {
                            builder.Append(document.Doctype.ToString());
                        }

                        builder.Append(documentElementHtml);
                        return builder.ToString();
                    }

                    return "<!-- Fen engine source unavailable: document element is null. -->";
                }

                if (activeNode is Element element)
                {
                    var elementHtml = element.OuterHTML;
                    if (!string.IsNullOrWhiteSpace(elementHtml))
                    {
                        return elementHtml;
                    }

                    return $"<!-- Fen engine source unavailable: outerHTML was empty for <{element.TagName}>. -->";
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Engine source fast-path serialization failed for '{uri}': {ex.Message}", LogCategory.General);
            }

            try
            {
                var html = activeNode.ToHtml();
                if (!string.IsNullOrWhiteSpace(html))
                {
                    return html;
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Engine source fallback serialization failed for '{uri}': {ex.Message}", LogCategory.General);
            }

            try
            {
                var serialized = DomSerializer.Serialize(activeNode, prettyPrint: false);
                if (!string.IsNullOrWhiteSpace(serialized))
                {
                    return serialized;
                }

                TryLogWarn($"[BrowserHost] Engine source serializer returned empty output for '{uri}'.", LogCategory.General);
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Engine source serializer failed for '{uri}': {ex.Message}", LogCategory.General);
            }

            return $"<!-- Fen engine source unavailable for '{uri}'. NodeType={activeNode.NodeType}. -->";
        }

        private void TryCaptureNavigationDiagnosticsSnapshot()
        {
            var navigationId = Interlocked.Read(ref _activeRenderNavigationId);
            if (navigationId <= 0)
            {
                navigationId = Interlocked.Read(ref _latestNavigationId);
            }

            TryCaptureNavigationDiagnosticsSnapshot(navigationId, _current, allowIncomplete: false);
        }

        private void TryCaptureNavigationDiagnosticsSnapshot(long navigationId, Uri uri, bool allowIncomplete)
        {
            if (navigationId <= 0 || uri == null)
            {
                return;
            }

            var activeNode = _engine.GetActiveDom();
            if (activeNode == null)
            {
                return;
            }

            string textContent = GetTextContent();
            int domNodeCount = activeNode.SelfAndDescendants()?.Count() ?? 0;
            if (!allowIncomplete && !IsDiagnosticsSnapshotReady(activeNode, domNodeCount, textContent))
            {
                return;
            }

            TryCaptureEngineSourceSnapshot(navigationId, uri, activeNode, domNodeCount);
            TryCaptureRenderedTextSnapshot(navigationId, uri, textContent, domNodeCount);
        }

        private static bool IsDiagnosticsSnapshotReady(Node activeNode, int domNodeCount, string textContent)
        {
            if (activeNode == null)
            {
                return false;
            }

            if (domNodeCount >= 128)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(textContent) && textContent.Length >= 96)
            {
                return true;
            }

            var document = ResolveSnapshotDocument(activeNode);
            if (document?.Body != null)
            {
                var bodyTextLength = document.Body.TextContent?.Trim().Length ?? 0;
                if (bodyTextLength >= 96)
                {
                    return true;
                }

                var bodyNodeCount = document.Body.SelfAndDescendants()?.Count() ?? 0;
                if (bodyNodeCount >= 64)
                {
                    return true;
                }
            }

            return false;
        }

        private void TryCaptureEngineSourceSnapshot(long navigationId, Uri uri, Node activeNode, int domNodeCount)
        {
            var capturedNavigationId = Interlocked.Read(ref _engineDiagnosticsCapturedNavigationId);
            if (capturedNavigationId == navigationId &&
                domNodeCount <= Volatile.Read(ref _engineDiagnosticsCapturedNodeCount))
            {
                return;
            }

            try
            {
                string engineSource = BuildEngineSourceSnapshot(activeNode, uri);
                string enginePath = EngineLogCompat.DumpEngineSource(uri.AbsoluteUri, engineSource);
                if (!string.IsNullOrEmpty(enginePath))
                {
                    FenBrowser.Core.Verification.ContentVerifier.RegisterEngineSourceFile(enginePath);
                    Interlocked.Exchange(ref _engineDiagnosticsCapturedNavigationId, navigationId);
                    Volatile.Write(ref _engineDiagnosticsCapturedNodeCount, domNodeCount);
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Engine source dump failed for '{uri}': {ex.Message}", LogCategory.General);
            }
        }

        private void TryCaptureRenderedTextSnapshot(long navigationId, Uri uri, string textContent, int domNodeCount)
        {
            var renderedTextLength = textContent?.Length ?? 0;
            var renderedTextHash = textContent != null ? StringComparer.Ordinal.GetHashCode(textContent) : 0;
            var capturedNavigationId = Interlocked.Read(ref _renderedDiagnosticsCapturedNavigationId);
            if (capturedNavigationId == navigationId &&
                renderedTextHash == Volatile.Read(ref _renderedDiagnosticsCapturedContentHash) &&
                renderedTextLength <= Volatile.Read(ref _renderedDiagnosticsCapturedTextLength))
            {
                return;
            }

            try
            {
                FenBrowser.Core.Verification.ContentVerifier.RegisterRendered(
                    uri.AbsoluteUri,
                    domNodeCount,
                    renderedTextLength,
                    authoritative: true);

                string renderedPath = EngineLogCompat.DumpRenderedText(uri.AbsoluteUri, textContent);
                if (!string.IsNullOrEmpty(renderedPath))
                {
                    FenBrowser.Core.Verification.ContentVerifier.RegisterRenderedFile(renderedPath);
                    Interlocked.Exchange(ref _renderedDiagnosticsCapturedNavigationId, navigationId);
                    Volatile.Write(ref _renderedDiagnosticsCapturedTextLength, renderedTextLength);
                    Volatile.Write(ref _renderedDiagnosticsCapturedContentHash, renderedTextHash);
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Rendered text dump failed for '{uri}': {ex.Message}", LogCategory.General);
            }
        }

        private void TryInvokeRepaintReady(object payload)
        {
            try { RepaintReady?.Invoke(this, payload); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] RepaintReady handler failed: {ex.Message}", LogCategory.Events); }
        }

        private void TryInvokeNavigated(Uri uri)
        {
            try { Navigated?.Invoke(this, uri); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] Navigated handler failed: {ex.Message}", LogCategory.Navigation); }
        }

        private void TryInvokeLoadingChanged(bool loading)
        {
            try { LoadingChanged?.Invoke(this, loading); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] LoadingChanged handler failed: {ex.Message}", LogCategory.Navigation); }
        }

        private void ClearFaviconForNavigation()
        {
            if (Favicon == null)
            {
                return;
            }

            Favicon = null;
            TryLogDebug("[BrowserHost] Cleared favicon for new navigation", LogCategory.Navigation);

            try { RepaintReady?.Invoke(this, null); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] RepaintReady handler failed while clearing favicon: {ex.Message}", LogCategory.Events); }

            try { FaviconChanged?.Invoke(this, null); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] FaviconChanged handler failed while clearing favicon: {ex.Message}", LogCategory.Events); }
        }

        private void TryInvokeNavigationLifecycleChanged(NavigationLifecycleTransition transition)
        {
            try { NavigationLifecycleChanged?.Invoke(this, transition); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] NavigationLifecycleChanged handler failed: {ex.Message}", LogCategory.Navigation); }
        }

        private void TryInvokeConsoleMessage(string message)
        {
            try { ConsoleMessage?.Invoke(message); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] ConsoleMessage handler failed: {ex.Message}", LogCategory.JavaScript); }
        }

        public Uri CurrentUri => _current;
        public ResourceManager ResourceManager => _resources;
        public bool CanGoBack => _historyIndex > 0;
        public bool CanGoForward => _historyIndex < _history.Count - 1;
        public bool EnableJavaScript
        {
            get => _engine.EnableJavaScript;
            set => _engine.EnableJavaScript = value;
        }

        public SecurityState SecurityState { get; private set; } = SecurityState.None;
        public CspPolicy CurrentPolicy { get; private set; }
        /// <summary>
        /// X-Frame-Options policy returned by the current page's HTTP response.
        /// DENY means this page asked not to be embedded in any frame.
        /// SAMEORIGIN means only same-origin frames may embed it.
        /// </summary>
        public FenBrowser.Core.XFrameOptionsPolicy CurrentXFrameOptions { get; private set; }

        /// <summary>
        /// Permissions-Policy (or legacy Feature-Policy) of the current page's
        /// HTTP response. Deny-by-default: a feature not mentioned in the
        /// header is disabled unless the default allowlist grants it.
        /// </summary>
        public FenBrowser.Core.Security.PermissionsPolicy CurrentPermissionsPolicy { get; private set; } =
            FenBrowser.Core.Security.PermissionsPolicy.None;
        public Dictionary<Node, CssComputed> ComputedStyles => _engine.LastComputedStyles;
        public CustomHtmlEngine Engine => _engine;
        public NavigationLifecycleSnapshot NavigationLifecycleState => _navigationLifecycle.GetSnapshot();

        public SKBitmap Favicon { get; private set; }
        public event EventHandler<SKBitmap> FaviconChanged;

        // ========== INJECTABLE DELEGATES FOR WEBDRIVER ==========
        // These are set by MainWindow/WebDriverIntegration to provide real implementations
        public Func<WindowRect> GetWindowRectDelegate { get; set; }
        public Func<int?, int?, int?, int?, WindowRect> SetWindowRectDelegate { get; set; }
        public Func<WindowRect> MaximizeWindowDelegate { get; set; }
        public Func<WindowRect> MinimizeWindowDelegate { get; set; }
        public Func<WindowRect> FullscreenWindowDelegate { get; set; }
        public Func<Task<string>> CreateNewTabDelegate { get; set; }
        public Func<Task<string>> CaptureScreenshotDelegate { get; set; }
        public Func<string, Task<string>> CaptureElementScreenshotDelegate { get; set; }

        public bool IsPrivate { get; }

        public BrowserHost(bool isPrivate = false, BrowserHostOptions options = null)
        {
            IsPrivate = isPrivate;
            _options = options ?? BrowserHostOptions.Default;
            _engineLoop = new FenBrowser.FenEngine.Core.EngineLoop(); // Phase 5: Initialize Loop
            _engine.InitHistory(this); // Wire up history bridge
            
            // Initialize FontResolver for @font-face support
            // This allows Core.Css.CssComputed to use the Engine's FontRegistry
            FenBrowser.Core.Css.CssComputed.FontResolver = FenBrowser.FenEngine.Rendering.FontRegistry.TryResolve;

            // Wire ElementStateManager to CSS pseudo-class state provider
            // This allows :hover, :focus, :active etc. to query actual element state
            FenBrowser.Core.Dom.V2.Selectors.StatePseudoClassSelector.StateProvider =
                (el, pseudo) => FenBrowser.FenEngine.Rendering.ElementStateManager.Instance.MatchesPseudoClassState(el, pseudo);
            
            // Get HTTP/2 and Brotli enabled handler from factory
            var config = NetworkConfiguration.Instance;
            var handler = FenBrowser.Core.Network.HttpClientFactory.CreateHandler();
            
            // Add certificate callback for security display + optional soft-fail.
            FenBrowser.Core.Network.HttpClientFactory.ConfigureServerCertificateValidation(
                handler,
                (msg, cert, chain, errors) =>
                {
                    // Extract Subject Alternative Names from the certificate extension
                    var sanList = new List<string>();
                    try
                    {
                        var sanExt = cert?.Extensions["2.5.29.17"]; // OID for SAN
                        if (sanExt != null)
                        {
                            // Format: "DNS Name=example.com, DNS Name=www.example.com"
                            foreach (var part in sanExt.Format(false).Split(','))
                            {
                                var trimmed = part.Trim();
                                var eqIdx = trimmed.IndexOf('=');
                                if (eqIdx >= 0) sanList.Add(trimmed.Substring(eqIdx + 1).Trim());
                            }
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }

                    var info = new CertificateInfo
                    {
                        Subject                = cert?.Subject ?? string.Empty,
                        Issuer                 = cert?.Issuer ?? string.Empty,
                        NotBefore              = cert?.NotBefore ?? DateTime.MinValue,
                        NotAfter               = cert?.NotAfter ?? DateTime.MaxValue,
                        IsValid                = errors == System.Net.Security.SslPolicyErrors.None,
                        Thumbprint             = cert?.GetCertHashString() ?? string.Empty,
                        PolicyErrors           = errors,
                        SubjectAlternativeNames = sanList
                    };

                    _lastCertificate = info;
                    _lastSslErrors   = errors;

                    // Enforce strict validation by default; only allow override via settings.
                    if (FenBrowser.Core.NetworkConfiguration.Instance.IgnoreCertificateErrors)
                        return true;
                    return errors == System.Net.Security.SslPolicyErrors.None;
                });
            
            // Create HTTP/2 + Brotli enabled client
            var httpClient = FenBrowser.Core.Network.HttpClientFactory.CreateClient(handler);
            
            // Log HTTP/2 and Brotli configuration
            EngineLogCompat.Info($"[BrowserHost] HTTP/2: {config.EnableHttp2}, Brotli: {config.EnableBrotli}, " +
                          $"Version: {httpClient.DefaultRequestVersion}", LogCategory.Network);
            
            var sessionCookieJar = _options.CookieJar ??
                (isPrivate ? new BrowserCookieJar() : SharedProfileCookieJar);
            _resources = new ResourceManager(httpClient, isPrivate, sessionCookieJar);
            _engine.CookieJar = _resources.CookieJar;
            _engine.FetchExternalCssForRootAsync = FetchFrameAwareCssAsync;
            _prefetcher = new FenBrowser.Core.Network.ResourcePrefetcher(_resources);
            _engine.Prefetcher = _prefetcher;

            // Wire up Fetch API (Phase 8)
            _engine.FetchHandler = (req) => 
            {
                req.RequestUri = MapRuntimeUri(req.RequestUri);
                string Header(string name) => req.Headers.TryGetValues(name, out var values)
                    ? values.FirstOrDefault()
                    : null;
                var frameDocumentUri = req.Headers.Referrer ?? _current;
                return _resources.SendAsync(req, CurrentPolicy, new FetchContext
                {
                    RequestUri = req.RequestUri,
                    InitiatorUri = frameDocumentUri,
                    FrameDocumentUri = frameDocumentUri,
                    TopLevelDocumentUri = _current ?? frameDocumentUri,
                    Destination = Header("Sec-Fetch-Dest") ?? "empty",
                    Mode = Header("Sec-Fetch-Mode") ?? "cors",
                    CredentialsMode = "same-origin",
                    Method = req.Method.Method
                });
            };
            _engine.FrameElementLoader = LoadFrameElementAsync;

            // Wire up DevTools Network Monitoring
            _resources.NetworkRequestStarting += (id, req) =>
            {
                try
                {
                    var headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
                    
                    // [Compliance] Log Compliance Data
                    try 
                    {
                         var dump = new Dictionary<string, string>(headers);
                         if (!dump.ContainsKey("User-Agent") && req.Headers.UserAgent != null) dump["User-Agent"] = req.Headers.UserAgent.ToString();
                         EngineLogCompat.Debug($"[Compliance] HTTP Request: {req.Method} {req.RequestUri} Headers: {JsonSerializer.Serialize(dump)}", LogCategory.Network);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }

                    // Pass the ResourceManager's ID to DevToolsCore so we can correlate completion
                    DevToolsCore.Instance.RecordRequest(req.RequestUri.ToString(), req.Method.ToString(), headers, id);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            _resources.NetworkRequestCompleted += (id, resp) =>
            {
                try
                {
                    if (resp == null) return;
                    if (resp.IsSuccessStatusCode && resp.RequestMessage?.RequestUri != null)
                    {
                        ElementStateManager.Instance.RecordVisitedUrl(resp.RequestMessage.RequestUri);
                    }
                    var headers = resp.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
                    if (resp.Content?.Headers != null)
                    {
                        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
                    }
                    var size = resp.Content?.Headers?.ContentLength ?? 0;
                    var mime = resp.Content?.Headers?.ContentType?.MediaType ?? "";
                    DevToolsCore.Instance.CompleteRequest(id, (int)resp.StatusCode, headers, size, mime);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            _resources.NetworkRequestFailed += (id, failureEx) =>
            {
                try
                {
                    // Use 599 to indicate network failure (treated as error in DevToolsCore)
                    DevToolsCore.Instance.CompleteRequest(id, 599, null, 0, "error");
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            DevToolsCore.Instance.CookieSnapshotProvider = () =>
            {
                var scope = ResolveCookieScope();
                if (scope == null)
                {
                    return Enumerable.Empty<FenBrowser.FenEngine.DevTools.Cookie>();
                }

                return _engine.GetCookieSnapshot(scope)
                    .Select(kv => new FenBrowser.FenEngine.DevTools.Cookie
                    {
                        Name = kv.Key,
                        Value = kv.Value,
                        Domain = scope.Host,
                        Path = "/",
                        Secure = scope.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    })
                    .ToList();
            };
            DevToolsCore.Instance.CookieSetter = cookie =>
            {
                if (cookie == null) return;
                var scope = ResolveCookieScope();
                if (scope != null)
                {
                    _engine.SetCookie(scope, cookie.Name, cookie.Value ?? string.Empty, string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path);
                }
            };
            DevToolsCore.Instance.CookieDeleteHandler = (name, domain) =>
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var scope = ResolveCookieScope();
                if (scope != null && (string.IsNullOrWhiteSpace(domain) || domain.Equals(scope.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    _engine.DeleteCookie(scope, name);
                }
            };
            DevToolsCore.Instance.CookieClearHandler = () =>
            {
                var scope = ResolveCookieScope();
                if (scope == null) return;
                var keys = _engine.GetCookieSnapshot(scope).Keys.ToArray();
                foreach (var key in keys)
                {
                    _engine.DeleteCookie(scope, key);
                }
            };


            _engine.RepaintReady += (elem) =>
            {
                TryCaptureNavigationDiagnosticsSnapshot();
                TryInvokeRepaintReady(elem);
            };

            // Wire ElementStateManager.OnStateChanged â†’ CSS re-cascade.
            // Hover/focus/active state changes require re-running the selector cascade so that
            // rules like  a:hover { color: red }  are applied.  We schedule a single re-cascade
            // per state-change burst; ScheduleRecascade() ignores overlapping calls.
            _elementStateChangedHandler = _ => ScheduleInteractionRecascade();
            ElementStateManager.Instance.OnStateChanged += _elementStateChangedHandler;

            // Wire DOM attribute mutations (class/id/style changes from JS or DOM manipulation)
            // â†’ CSS re-cascade.  e.g. element.classList.add('active') must reflect in selectors.
            _styleAttributeChangedHandler = _ => _engine.ScheduleRecascade();
            FenBrowser.Core.Dom.V2.Element.StyleAttributeChanged += _styleAttributeChangedHandler;

            _engine.DomReady += (s, dom) =>
            {
                try 
                { 
                    _engineLoop.SetRoot(dom); // Phase 5: Connect DOM to Loop
                    RepaintReady?.Invoke(this, dom); 
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            _engine.LoadingChanged += (s, loading) =>
            {
                TryInvokeLoadingChanged(loading);
            };

            _engine.TitleChanged += (s, title) =>
            {
                try { TitleChanged?.Invoke(this, title); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            _engine.AlertTriggered += (msg) =>
            {
                TriggerAlert(msg);
                TryInvokeConsoleMessage($"[Alert] {msg}");
            };
            _engine.ConfirmTriggered += msg =>
            {
                TriggerConfirm(msg);
                TryInvokeConsoleMessage($"[Confirm] {msg}");
                return ShouldAutoAcceptDialogs();
            };
            _engine.PromptTriggered += (msg, defaultValue) =>
            {
                TriggerPrompt(msg, defaultValue);
                TryInvokeConsoleMessage($"[Prompt] {msg}");
                return ShouldAutoAcceptDialogs() ? (defaultValue ?? string.Empty) : null;
            };

            _engine.ConsoleMessage += (msg) =>
            {
                TryInvokeConsoleMessage(msg);
            };

            _engine.HighlightRectChanged += (rect) =>
            {
                try { HighlightRectChanged?.Invoke(rect); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };
            
            _engine.PermissionRequested += async (origin, perm) =>
            {
                if (PermissionRequested != null)
                {
                    return await PermissionRequested(origin, perm);
                }
                return false;
            };

            ResourceManager.LogSink = (msg) =>
            {
                Console.WriteLine(msg);
                TryLogDebug(msg, LogCategory.Network);
                TryInvokeConsoleMessage(msg);
            };
            _engine.ScriptFetcher = async (u) => 
            {
                if (_options.TryGetScriptOverride(u, out var scriptOverride))
                {
                    TryLogInfo($"[BrowserHost] Using tooling script override for '{u}'.", LogCategory.Navigation);
                    return scriptOverride;
                }

                u = MapRuntimeUri(u);
                var trackedNavigationId = Interlocked.Read(ref _activeRenderNavigationId);
                if (trackedNavigationId > 0)
                {
                    _navigationSubresources.MarkLoadStarted(trackedNavigationId);
                }

                try
                {
                    var scriptResult = await _resources.FetchTextDetailedAsync(u, referer: _current, accept: null, secFetchDest: "script").ConfigureAwait(false);
                    if (scriptResult.Status != FenBrowser.Core.FetchStatus.Success) return null;

                    // X-Content-Type-Options: nosniff â€” block script if Content-Type is not a JS MIME type
                    if (scriptResult.Headers != null && scriptResult.Headers.TryGetValues("X-Content-Type-Options", out var xctoVals))
                    {
                        var xcto = string.Join(",", xctoVals).Trim().ToLowerInvariant();
                        if (xcto.Contains("nosniff"))
                        {
                            var scriptCt = scriptResult.ContentType?.ToLowerInvariant() ?? "";
                            bool isJsMime = scriptCt.Contains("javascript") || scriptCt.Contains("ecmascript");
                            if (!isJsMime)
                            {
                                EngineLogCompat.Warn($"[nosniff] Blocked script â€” Content-Type '{scriptResult.ContentType}' is not a JS MIME type: {u}", LogCategory.JavaScript);
                                return null;
                            }
                        }
                    }

                    return scriptResult.Content;
                }
                finally
                {
                    if (trackedNavigationId > 0)
                    {
                        _navigationSubresources.MarkLoadCompleted(trackedNavigationId);
                    }
                }
            };
            _navManager = new NavigationManager(_resources);
            _navigationLifecycle.Transitioned += transition =>
            {
                TryInvokeNavigationLifecycleChanged(transition);
            };
            _imageLoaderContext = CreateImageLoaderContext();
            _fontLoaderContext = CreateFontLoaderContext();

            // Wire static ImageLoader callbacks so image-load notifications routed
            // outside an explicit ambient context (e.g. background prewarms) still
            // reach this host. Last host instantiated wins, mirroring how a single
            // process hosts one active browser at a time.
            ImageLoader.FetchBytesAsync = _imageLoaderContext.FetchBytesAsync;
            ImageLoader.FetchDetailedAsync = _imageLoaderContext.FetchDetailedAsync;
            ImageLoader.RequestRepaint = _imageLoaderContext.RequestRepaint;
            ImageLoader.RequestRelayout = _imageLoaderContext.RequestRelayout;
            FontRegistry.FetchDetailedAsync = _fontLoaderContext.FetchDetailedAsync;
            FontRegistry.FetchDetailedForDocumentAsync = _fontLoaderContext.FetchDetailedForDocumentAsync;

            // Wire up FontRegistry to trigger full relayout/repaint when fonts finish loading
            _fontLoadedHandler = (family) =>
            {
                try
                {
                    EngineLogCompat.Debug($"[FontRegistry-Repaint] Triggering relayout after font load: {family}", LogCategory.Rendering);
                    // Force CSS to re-evaluate so CssComputed drops the cached system fallback fonts
                    _engine.ScheduleRecascade();
                    var dom = _engine.GetActiveDom();
                    if (dom != null) RepaintReady?.Invoke(this, dom);
                    else RepaintReady?.Invoke(this, null);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };
            FontRegistry.FontLoaded += _fontLoadedHandler;
        }

        private ImageLoader.ImageLoaderRequestContext CreateImageLoaderContext()
        {
            return new ImageLoader.ImageLoaderRequestContext
            {
                OwnerId = _imageLoaderContextId,
                FetchDetailedAsync = async uri =>
                {
                    if (uri == null)
                    {
                        return new BinaryFetchResult
                        {
                            FailureReason = BinaryFetchFailureReason.InvalidRequest,
                            FailureDetail = "Image URI is null"
                        };
                    }

                    var fetchUri = MapRuntimeUri(uri);
                    return await _resources.FetchBytesDetailedAsync(
                            new FetchContext
                            {
                                RequestUri = fetchUri,
                                InitiatorUri = _current,
                                FrameDocumentUri = _current,
                                TopLevelDocumentUri = _current,
                                Destination = "image",
                                Mode = "no-cors",
                                CredentialsMode = "include",
                                Method = "GET"
                            },
                            BrowserNetworkCapabilities.ImageAcceptHeader)
                        .ConfigureAwait(false);
                },
                FetchDetailedForDocumentAsync = FetchFrameAwareImageAsync,
                FetchBytesForDocumentAsync = async (uri, document) =>
                    (await FetchFrameAwareImageAsync(uri, document).ConfigureAwait(false))?.Body,
                FetchBytesAsync = async uri =>
                {
                    if (uri == null)
                    {
                        return null;
                    }

                    var fetchUri = MapRuntimeUri(uri);
                    // SkiaSharp 4.x supports PNG, JPEG, WebP, GIF, BMP, ICO — does NOT support AVIF.
                    // Requesting AVIF tells the server we can handle it, but decoding will fail.
                    return await _resources.FetchBytesAsync(
                            fetchUri,
                            referer: _current,
                            accept: BrowserNetworkCapabilities.ImageAcceptHeader,
                            secFetchDest: "image")
                        .ConfigureAwait(false);
                },
                RequestRepaint = () =>
                {
                    try
                    {
                        EngineLogCompat.Debug("[ImageLoader-Repaint] Triggering repaint after image load", LogCategory.Rendering);
                        RepaintReady?.Invoke(this, null);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}");
                    }
                },
                RequestRelayout = () =>
                {
                    try
                    {
                        EngineLogCompat.Debug("[ImageLoader-Relayout] Triggering re-layout after image load", LogCategory.Rendering);
                        var dom = _engine.GetActiveDom();
                        // Always fire RepaintReady even when the active DOM is null.
                        // The BrowserIntegration handler ignores the payload and calls
                        // GetRenderSnapshot() independently — but if we skip the invoke
                        // entirely, image loads that complete during navigation gaps
                        // never trigger a paint-tree rebuild and images stay blank.
                        RepaintReady?.Invoke(this, dom);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}");
                    }
                }
            };
        }

        private FontRegistry.FontLoaderRequestContext CreateFontLoaderContext()
        {
            return new FontRegistry.FontLoaderRequestContext
            {
                OwnerId = _imageLoaderContextId,
                FetchDetailedAsync = uri => FetchFrameAwareFontAsync(uri, ownerDocument: null),
                FetchDetailedForDocumentAsync = FetchFrameAwareFontAsync
            };
        }

        public IDisposable EnterImageLoaderContext()
        {
            return new ResourceLoaderContextScope(_imageLoaderContext, _fontLoaderContext);
        }

        private Uri MapRuntimeUri(Uri uri)
        {
            return _options.MapRequestUri(uri);
        }

        private CertificateInfo _lastCertificate;
        private System.Net.Security.SslPolicyErrors _lastSslErrors;
        public CertificateInfo CurrentCertificate => _lastCertificate;

        public BrowserRenderSnapshot GetRenderSnapshot()
        {
            return _engine.GetRenderSnapshot();
        }


        public Element GetDomRoot()
        {
            return _engine.GetRenderSnapshot().Root;
        }

        public string GetRawHtml()
        {
            return _engine.GetRawHtml();
        }

        public async Task<bool> GoBackAsync()
        {
            if (!CanGoBack) return false;
            _historyIndex--;
            var entry = _history[_historyIndex];

            // If it's a pushState entry, we don't reload, just popstate
            if (entry.IsPushState)
            {
                _current = entry.Url;
                _engine.NotifyPopState(entry.State); // Notify JS
                TryInvokeNavigated(_current);
                return true;
            }

            _isNavigatingHistory = true;
            try { return await NavigateAsync(entry.Url.AbsoluteUri); }
            finally { _isNavigatingHistory = false; }
        }

        public async Task<bool> GoForwardAsync()
        {
            if (!CanGoForward) return false;
            _historyIndex++;
            var entry = _history[_historyIndex];

            if (entry.IsPushState)
            {
                _current = entry.Url;
                _engine.NotifyPopState(entry.State); // Notify JS
                TryInvokeNavigated(_current);
                return true;
            }

            _isNavigatingHistory = true;
            try { return await NavigateAsync(entry.Url.AbsoluteUri); }
            finally { _isNavigatingHistory = false; }
        }

        public Task<bool> NavigateUserInputAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.UserInput);
        }

        public Task<bool> NavigateAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.Programmatic);
        }

        private async Task<bool> NavigateAsync(string url, NavigationRequestKind requestKind)
        {
            long navigationId = 0;
            TryLogDebug($"[BrowserHost] NavigateAsync called for: '{url}'", LogCategory.Navigation);

            if (_disposed) return false;
                if (string.IsNullOrWhiteSpace(url)) return false;

                using var programmaticReservation = ReserveProgrammaticNavigation(url, requestKind);
                if (!programmaticReservation.Accepted) return false;
                if (!string.IsNullOrEmpty(programmaticReservation.NormalizedUrl))
                {
                    url = programmaticReservation.NormalizedUrl;
                }

                _currentFrameElement = null;
                _frameContextInvalidated = false;
                _frameContextStack.Clear();
                _elementMap.Clear();
                _elementBrowsingContextMap.Clear();
                _shadowRootMap.Clear();

                navigationId = _navigationLifecycle.BeginNavigation(url, requestKind == NavigationRequestKind.UserInput);
                var previousNavigationId = Interlocked.Exchange(ref _latestNavigationId, navigationId);
                if (previousNavigationId > 0 && previousNavigationId != navigationId)
                {
                    _navigationSubresources.AbandonNavigation(previousNavigationId);
                }
                ClearFaviconForNavigation();
                _navigationSubresources.ResetNavigation(navigationId);
                _navigationLifecycle.MarkFetching(navigationId, url);

                // Log raw navigation input for diagnostics
                TryLogInfo($"[BrowserHost] Nav raw='{url}'", LogCategory.Navigation);

                // SPECIAL HANDLING: fen://history
                if (url.Equals("fen://history", StringComparison.OrdinalIgnoreCase))
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("<html><head><title>History</title><style>body { font-family: sans-serif; padding: 20px; background: #fff; color: #333; } h1 { border-bottom: 1px solid #ddd; padding-bottom: 10px; } ul { list-style: none; padding: 0; } li { padding: 8px; border-bottom: 1px solid #eee; } a { text-decoration: none; color: #1a73e8; font-size: 16px; display: block; } a:hover { text-decoration: underline; } .meta { color: #5f6368; font-size: 12px; margin-top: 4px; }</style></head><body>");
                    sb.Append("<h1>Browsing History</h1><ul>");
                    
                    // Iterate history (reverse to show newest first) with index
                    for (int i = _history.Count - 1; i >= 0; i--)
                    {
                        var u = _history[i];
                        string currentMarker = (i == _historyIndex) ? " <span style='color:green; font-weight:bold;'>(Current)</span>" : "";
                        string typeMarker = u.IsPushState ? " <span style='color:gray;'>(SPA)</span>" : "";
                        sb.Append($"<li><a href='{u.Url.AbsoluteUri}'>{u.Url.AbsoluteUri}</a><div class='meta'>{u.Title ?? u.Url.Scheme}{currentMarker}{typeMarker}</div></li>");
                    }
                    if (_history.Count == 0) sb.Append("<li><em>No history yet.</em></li>");
                    
                    sb.Append("</ul></body></html>");

                    _current = new Uri("fen://history");
                    _navigationLifecycle.MarkResponseReceived(
                        navigationId,
                        "Synthetic",
                        _current.AbsoluteUri,
                        isRedirect: false,
                        redirectCount: 0,
                        detail: "source=history-synthetic");
                    _navigationLifecycle.MarkCommitting(navigationId, _current.AbsoluteUri, "synthetic-history");
                    
                    // Render the generated HTML
                    var trackedCssFetcher = CreateTrackedCssFetcher(navigationId);
                    var trackedImageFetcher = CreateTrackedImageFetcher(navigationId);
                    SetActiveRenderNavigation(navigationId);
                    object elem = null;
                    try
                    {
                        var viewportHint = GetRenderViewportHint();
                        using (EnterImageLoaderContext())
                        using (EngineLogCompat.BeginCorrelationScope(navigationId.ToString(), "BrowserHost.Render", new Dictionary<string, object>
                        {
                            ["navigationId"] = navigationId.ToString(),
                            ["url"] = _current.AbsoluteUri
                        }))
                        {
                            elem = await _engine.RenderAsync(sb.ToString(), _current, trackedCssFetcher, trackedImageFetcher, u => { _ = NavigateAsync(u.AbsoluteUri); }, viewportHint.Width, viewportHint.Height);
                        }
                    }
                    finally
                    {
                        ClearActiveRenderNavigation(navigationId);
                    }
                    if (!IsLatestNavigation(navigationId))
                    {
                        _navigationSubresources.AbandonNavigation(navigationId);
                        _navigationLifecycle.MarkCancelled(navigationId, "superseded-by-new-navigation");
                        return false;
                    }
                    TryInvokeRepaintReady(elem);
                    _navigationLifecycle.MarkInteractive(navigationId, "history-dom-ready");
                    await MarkNavigationCompleteWhenSettledAsync(navigationId, "history-document-complete").ConfigureAwait(false);

                    // Add to history if not navigating backwards/forwards
                    if (!_isNavigatingHistory)
                    {
                        if (_historyIndex < _history.Count - 1)
                        {
                            _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
                        }
                        _history.Add(new HistoryEntry(_current));
                        _historyIndex = _history.Count - 1;
                    }

                    TryInvokeNavigated(_current);
                    return true;
                }

                try
                {
                    bool isViewSource = false;
                    if (url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
                    {
                        isViewSource = true;
                        url = url.Substring("view-source:".Length);
                    }

                    // Normalize explicit relative URLs against current document first.
                    if (_current != null && IsExplicitRelativeUrl(url) && Uri.TryCreate(_current, url, out var relative))
                    {
                        url = relative.AbsoluteUri;
                        TryLogInfo($"[BrowserHost] Resolved relative URL -> '{url}'", LogCategory.Navigation);
                    }
                    // Normalize if missing scheme
                    else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                    {
                        var candidate = "https://" + url.TrimStart('/');
                        if (Uri.TryCreate(candidate, UriKind.Absolute, out var normalized))
                        {
                            TryLogInfo($"[BrowserHost] Normalized missing-scheme -> '{normalized}'", LogCategory.Navigation);
                            url = normalized.AbsoluteUri;
                        }
                    }
                    else
                    {
                        TryLogInfo($"[BrowserHost] Parsed absolute Uri='{parsed}'", LogCategory.Navigation);
                        url = parsed.AbsoluteUri; // canonicalize
                    }

                _navigationLifecycle.MarkFetching(navigationId, url);
                FenBrowser.Core.Verification.ContentVerifier.ResetForNavigation(url);

                _resources.ResetBlockedCount();
                _resources.ActivePolicy = null; // Reset CSP for new page
                _engine.ActivePolicy = null;
                CurrentPolicy = null;
                CurrentXFrameOptions = FenBrowser.Core.XFrameOptionsPolicy.None;
                CurrentPermissionsPolicy = FenBrowser.Core.Security.PermissionsPolicy.None;

                const int maxTransientNavAttempts = 2;
                FetchResult result = null;
                for (int attempt = 1; attempt <= maxTransientNavAttempts; attempt++)
                {
                    result = await _navManager.NavigateAsync(url, requestKind);
                    if (!IsLatestNavigation(navigationId))
                    {
                        _navigationSubresources.AbandonNavigation(navigationId);
                        _navigationLifecycle.MarkCancelled(navigationId, "superseded-by-new-navigation");
                        return false;
                    }
                    if (!ShouldRetryTopLevelNavigation(result, url, attempt, maxTransientNavAttempts))
                        break;

                    int retryDelayMs = 350 * attempt;
                    EngineLogCompat.Warn(
                        $"[BrowserHost] Transient navigation failure ({result.Status}) for '{url}'. Retrying {attempt}/{maxTransientNavAttempts - 1} after {retryDelayMs}ms.",
                        LogCategory.Network);
                    await Task.Delay(retryDelayMs);
                }

                // Populate certificate info captured by the TLS callback into the result
                // so the error page and UI can display accurate cert details.
                if (result != null && _lastCertificate != null)
                {
                    result.Certificate = _lastCertificate;
                    result.SslErrors   = _lastSslErrors;
                }

                _navigationLifecycle.MarkResponseReceived(
                    navigationId,
                    result?.Status.ToString() ?? "Unknown",
                    result?.FinalUri?.AbsoluteUri ?? url,
                    isRedirect: result?.Redirected == true,
                    redirectCount: result != null ? result.RedirectCount : 0,
                    detail: BuildResponseLifecycleDetail(result));
                
                // Parse CSP
                if (result.Headers != null && result.Headers.TryGetValues("Content-Security-Policy", out var cspValues))
                {
                    // Multiple CSP headers are joined here until multi-policy intersection support is added.
                    CurrentPolicy = CspPolicy.Parse(string.Join(";", cspValues));
                    _resources.ActivePolicy = CurrentPolicy;
                    _engine.ActivePolicy = CurrentPolicy; // Set on engine for inline script/style CSP checks
                    Console.WriteLine($"[CSP] Policy Applied: {string.Join(";", cspValues)}");

                    // SECURITY: Revoke eval() permission if CSP script-src lacks 'unsafe-eval'
                    var jsContext = _engine.Context;
                    if (jsContext != null)
                    {
                        bool evalAllowed = CurrentPolicy.IsAllowed("script-src", url: null, isEval: true);
                        if (!evalAllowed)
                        {
                            jsContext.Permissions.Revoke(FenBrowser.FenEngine.Security.JsPermissions.Eval);
                            Console.WriteLine("[CSP] eval() revoked: 'unsafe-eval' not in script-src");
                        }
                    }
                }

                // Store X-Frame-Options policy for the current page.
                // Frame-document enforcement now happens in ResourceManager when a document is
                // fetched with secFetchDest=iframe; this copy is retained for diagnostics/UI state.
                CurrentXFrameOptions = result.XFrameOptions;
                if (CurrentXFrameOptions != FenBrowser.Core.XFrameOptionsPolicy.None)
                    Console.WriteLine($"[XFO] X-Frame-Options: {CurrentXFrameOptions}{(result.XFrameAllowFromUri != null ? " " + result.XFrameAllowFromUri : "")}");

                // Store the Permissions-Policy of the current page. The scripting
                // layer gates sensitive features (fullscreen, geolocation, etc.)
                // on this allowlist.
                CurrentPermissionsPolicy = result.PermissionsPolicy ?? FenBrowser.Core.Security.PermissionsPolicy.None;
                if (_engine?.ScriptEngine is FenBrowser.FenEngine.Scripting.FenJsBrowserScriptEngine jsEngine)
                {
                    jsEngine.PermissionsPolicyProvider = () => CurrentPermissionsPolicy;
                }

                // Publish COOP/COEP-derived cross-origin isolation state for this document.
                // The scripting layer reads this to expose crossOriginIsolated and to gate
                // SharedArrayBuffer / Atomics.wait availability.
                if (result.CrossOriginIsolation != null)
                {
                    FenBrowser.Core.Security.CrossOriginIsolationState.Set(result.CrossOriginIsolation);
                }
                else
                {
                    FenBrowser.Core.Security.CrossOriginIsolationState.Reset();
                }

                string htmlToRender = result.Content;
                Uri uri = result.FinalUri ?? new Uri("about:blank");

                if (isViewSource && result.Status == FetchStatus.Success)
                {
                    // The parser decodes HTML entities, so we need to double-encode 
                    // so that after parsing, we still have the encoded entities as visible text
                    var singleEncoded = System.Net.WebUtility.HtmlEncode(htmlToRender);
                    var doubleEncoded = System.Net.WebUtility.HtmlEncode(singleEncoded);
                    
                    htmlToRender = $@"<html>
<head><title>Source of {System.Net.WebUtility.HtmlEncode(url)}</title>
<style>
body {{ margin: 0; padding: 0; background-color: #1e1e1e; }}
pre {{ 
    font-family: 'Consolas', 'Monaco', monospace; 
    font-size: 13px; 
    line-height: 1.5;
    color: #d4d4d4; 
    background-color: #1e1e1e; 
    padding: 16px; 
    margin: 0;
    white-space: pre-wrap;
    word-wrap: break-word;
    overflow-x: auto;
}}
</style>
</head>
<body><pre>{doubleEncoded}</pre></body>
</html>";
                }


                if (uri == null) 
                {
                    System.Diagnostics.Debug.WriteLine("[NavigateAsync] CRITICAL: FinalUri is null! Defaulting to about:blank");
                    uri = new Uri("about:blank");
                }
                EngineLogCompat.Debug($"[BrowserHost] Navigation done. FinalUri: {uri.AbsoluteUri} (Status: {result.Status})", LogCategory.General);

                if (result.Status != FetchStatus.Success)
                {
                    // When the server returned a 4xx HTTP error but included an HTML
                    // body (e.g. Google's 429 CAPTCHA challenge, 403 access-denied
                    // pages), render the server's response body instead of a generic
                    // browser error page.  This matches Chrome/Firefox behavior.
                    bool hasRenderableErrorBody =
                        result.FailureReason == FetchFailureReasonCode.HttpError &&
                        result.StatusCode >= 400 && result.StatusCode < 500 &&
                        !string.IsNullOrWhiteSpace(result.Content) &&
                        IsHtmlContentType(result.ContentType);

                    if (hasRenderableErrorBody)
                    {
                        // Use the server's error body as-is (CAPTCHA, challenge, etc.)
                        htmlToRender = result.Content;
                        SecurityState = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                            ? SecurityState.Secure
                            : SecurityState.NotSecure;
                    }
                    else
                    {
                        // Render browser error page
                        switch (result.Status)
                        {
                            case FetchStatus.ConnectionFailed:
                                htmlToRender = ErrorPageRenderer.RenderConnectionFailed(url, result.ErrorDetail);
                                SecurityState = SecurityState.NotSecure;
                                break;
                            case FetchStatus.SslError:
                                htmlToRender = ErrorPageRenderer.RenderSslError(url, result.ErrorDetail, result.Certificate);
                                SecurityState = SecurityState.Warning;
                                break;
                            case FetchStatus.Timeout:
                                htmlToRender = ErrorPageRenderer.RenderGenericError(url, "Connection Timed Out", "The server took too long to respond.", result.ErrorDetail);
                                SecurityState = SecurityState.NotSecure;
                                break;
                            case FetchStatus.NotFound:
                                htmlToRender = ErrorPageRenderer.RenderGenericError(url, "404 Not Found", "The page you requested could not be found.", result.ErrorDetail);
                                SecurityState = SecurityState.NotSecure;
                                break;
                            default:
                                 htmlToRender = ErrorPageRenderer.RenderGenericError(url, "Error", "Something went wrong.", result.ErrorDetail);
                                 SecurityState = SecurityState.NotSecure;
                                 break;
                        }
                    }
                }
                else
                {
                    // Success
                    if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                        SecurityState = SecurityState.Secure;
                    else if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                        SecurityState = SecurityState.NotSecure;
                    else
                        SecurityState = SecurityState.None;
                }

                // Debug: Log navigation with base URL
                // Debug: Log navigation with base URL
                TryLogDebug($"[BrowserApi] Navigating to: {uri}. Previous _current: {_current?.AbsoluteUri ?? "null"}", LogCategory.General);

                // FIX: Set _current BEFORE rendering so UI has access to correct BaseUrl during render events
                _current = uri;
                ElementStateManager.Instance.SetTargetFragment(uri.Fragment?.TrimStart('#') ?? string.Empty);
                var commitSource = result.Status == FetchStatus.Success ? "network-document" : "error-document";
                _navigationLifecycle.MarkCommitting(navigationId, _current.AbsoluteUri, commitSource);
                TryLogDebug($"[BrowserApi] _current updated early to: {_current?.AbsoluteUri}", LogCategory.General);
                
                // Dump raw HTML source for debugging (CURL level)
                try 
                { 
                    string dumpPath = EngineLogCompat.DumpRawSource(uri.AbsoluteUri, htmlToRender); 
                    if (!string.IsNullOrEmpty(dumpPath))
                    {
                        FenBrowser.Core.Verification.ContentVerifier.RegisterSourceFile(dumpPath);
                    }
                } catch (Exception ex) { TryLogWarn($"[BrowserHost] Raw source dump failed for '{uri}': {ex.Message}", LogCategory.General); }

                var trackedCssFetcher = CreateTrackedCssFetcher(navigationId);
                var trackedImageFetcher = CreateTrackedImageFetcher(navigationId);
                SetActiveRenderNavigation(navigationId);
                object elem = null;
                try
                {
                    var viewportHint = GetRenderViewportHint();
                    using (EnterImageLoaderContext())
                    using (EngineLogCompat.BeginCorrelationScope(navigationId.ToString(), "BrowserHost.Render", new Dictionary<string, object>
                    {
                        ["navigationId"] = navigationId.ToString(),
                        ["url"] = uri.AbsoluteUri
                    }))
                    {
                        elem = await _engine.RenderAsync(htmlToRender, uri, trackedCssFetcher, trackedImageFetcher, u => { _ = NavigateAsync(u.AbsoluteUri); }, viewportHint.Width, viewportHint.Height);
                    }
                }
                finally
                {
                    ClearActiveRenderNavigation(navigationId);
                }
                if (!IsLatestNavigation(navigationId))
                {
                    _navigationSubresources.AbandonNavigation(navigationId);
                    _navigationLifecycle.MarkCancelled(navigationId, "superseded-by-new-navigation");
                    return false;
                }

                TryCaptureNavigationDiagnosticsSnapshot(navigationId, uri, allowIncomplete: true);
                
                TryLogDebug($"[BrowserApi] RenderAsync finished for {_current?.AbsoluteUri}. Firing RepaintReady...", LogCategory.General);
                
                TryInvokeRepaintReady(elem);
                SyncDocumentMetadata(uri);
                _navigationLifecycle.MarkInteractive(navigationId, BuildInteractiveLifecycleDetail(result));

                TryCaptureNavigationDiagnosticsSnapshot(navigationId, uri, allowIncomplete: true);

                if (!_isNavigatingHistory)
                {
                    if (_historyIndex < _history.Count - 1)
                    {
                        _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
                    }
                    _history.Add(new HistoryEntry(uri));
                    _historyIndex = _history.Count - 1;
                }

                TryInvokeNavigated(uri);

                // Kick favicon fetch off the moment the DOM is parsed enough to
                // find <link rel="icon">. Do this BEFORE awaiting subresource
                // settle so the tab icon updates while the page is still loading
                // subresources, JS, fonts, etc. Both calls are fire-and-forget.
                _ = FetchFaviconAsync(uri, navigationId);
                _ = FetchFaviconDelayedAsync(uri, navigationId, delayMs: 1200);

                await MarkNavigationCompleteWhenSettledAsync(navigationId, "document-complete").ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[NavigateAsync] Exception: " + ex.ToString());
                var details = ex.ToString();
                if (details != null && details.Length > 2000) details = details.Substring(0, 2000) + "...";
                if (navigationId > 0)
                {
                    _navigationSubresources.AbandonNavigation(navigationId);
                    _navigationLifecycle.MarkFailed(navigationId, details);
                }
                RaiseNavigationFailed(details);
                return false;
            }
        }

        private ProgrammaticNavigationReservation ReserveProgrammaticNavigation(
            string url,
            NavigationRequestKind requestKind)
        {
            if (requestKind != NavigationRequestKind.Programmatic ||
                !TryNormalizeNavigationUrl(url, out var normalizedUrl))
            {
                return ProgrammaticNavigationReservation.AcceptedNoop;
            }

            lock (_programmaticNavigationLock)
            {
                if (string.Equals(_programmaticNavigationInFlightUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    TryLogInfo(
                        $"[BrowserHost] Suppressing duplicate programmatic navigation while target is already loading: '{normalizedUrl}'",
                        LogCategory.Navigation);
                    return ProgrammaticNavigationReservation.Rejected;
                }

                _programmaticNavigationInFlightUrl = normalizedUrl;
                return new ProgrammaticNavigationReservation(this, normalizedUrl, accepted: true);
            }
        }

        private bool TryNormalizeNavigationUrl(string url, out string normalizedUrl)
        {
            normalizedUrl = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (_current != null && IsExplicitRelativeUrl(url) && Uri.TryCreate(_current, url, out var relative))
            {
                normalizedUrl = relative.AbsoluteUri;
                return true;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                normalizedUrl = parsed.AbsoluteUri;
                return true;
            }

            var candidate = "https://" + url.TrimStart('/');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var normalized))
            {
                normalizedUrl = normalized.AbsoluteUri;
                return true;
            }

            return false;
        }

        private void ReleaseProgrammaticNavigation(string normalizedUrl)
        {
            lock (_programmaticNavigationLock)
            {
                if (string.Equals(_programmaticNavigationInFlightUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _programmaticNavigationInFlightUrl = null;
                }
            }
        }

        private sealed class ProgrammaticNavigationReservation : IDisposable
        {
            public static readonly ProgrammaticNavigationReservation AcceptedNoop = new(null, null, accepted: true);
            public static readonly ProgrammaticNavigationReservation Rejected = new(null, null, accepted: false);

            private BrowserHost _owner;
            private readonly string _normalizedUrl;

            public ProgrammaticNavigationReservation(BrowserHost owner, string normalizedUrl, bool accepted)
            {
                _owner = owner;
                _normalizedUrl = normalizedUrl;
                Accepted = accepted;
                NormalizedUrl = normalizedUrl;
            }

            public bool Accepted { get; }

            public string NormalizedUrl { get; }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                if (owner != null && !string.IsNullOrEmpty(_normalizedUrl))
                {
                    owner.ReleaseProgrammaticNavigation(_normalizedUrl);
                }
            }
        }

        public void SetCookie(string name, string value)
        {
            try
            {
                var u = _current ?? new Uri("about:blank");
                _engine.SetCookie(u, name, value);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
        }

        public void DeleteCookie(string name)
        {
            try
            {
                var u = _current ?? new Uri("about:blank");
                _engine.DeleteCookie(u, name);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
        }

        public void ClearBrowsingData()
        {
            try
            {
                _engine.ClearAllCookies();
                _resources.ClearCache();
                FenBrowser.FenEngine.WebAPIs.StorageApi.ClearAllStorage(deletePersistentFile: true);
                Console.WriteLine("[BrowserHost] Browsing data cleared (Cookies + Cache + Storage)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BrowserHost] Error clearing data: {ex.Message}");
            }
        }
        public IList<string> GetAllLinks()
        {
            var list = new List<string>();
            try
            {
                var root = _engine.GetActiveDom();
                if (root != null)
                {
                    foreach (var n in root.SelfAndDescendants())
                    {
                    if (n is Element el && string.Equals(el.TagName, "a", StringComparison.OrdinalIgnoreCase))
                  {
                      var href = el.GetAttribute("href");
                      if (!string.IsNullOrWhiteSpace(href))
                          list.Add(href);
                  }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            return list;
        }

        public string GetTextContent()
        {
            try
            {
                var root = _engine.GetActiveDom();
                if (root == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var n in root.SelfAndDescendants())
                {
                    if (n.NodeType == NodeType.Text &&
                        !string.IsNullOrWhiteSpace(n.TextContent) &&
                        !ShouldSkipRenderedTextNode(n))
                    {
                        sb.AppendLine(n.TextContent.Trim());
                    }
                    else if (n is Element element &&
                             TryGetSupplementalRenderedText(element, out var supplementalText))
                    {
                        sb.AppendLine(supplementalText);
                    }
                }

                var renderedText = sb.ToString();
                if (!string.IsNullOrWhiteSpace(renderedText))
                {
                    return renderedText;
                }

                var document = ResolveSnapshotDocument(root);
                var bodyText = document?.Body?.TextContent;
                return NormalizeFallbackRenderedText(bodyText);
            }
            catch { return string.Empty; }
        }

        private static string NormalizeFallbackRenderedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));

            return string.Join(Environment.NewLine, lines);
        }

        private static bool TryGetSupplementalRenderedText(Element element, out string text)
        {
            text = null;
            if (element == null || ShouldSkipRenderedElementText(element))
            {
                return false;
            }

            var tag = element.TagName?.ToUpperInvariant();
            switch (tag)
            {
                case "INPUT":
                    return TryGetRenderedInputText(element, out text);

                case "TEXTAREA":
                    text = FirstNonEmpty(element.GetAttribute("value"), element.GetAttribute("placeholder"));
                    return !string.IsNullOrWhiteSpace(text);

                case "BUTTON":
                    if (HasRenderedTextDescendant(element))
                    {
                        return false;
                    }

                    text = FirstNonEmpty(element.GetAttribute("aria-label"), element.GetAttribute("value"));
                    return !string.IsNullOrWhiteSpace(text);

                default:
                    if (HasRenderedTextDescendant(element))
                    {
                        return false;
                    }

                    text = FirstNonEmpty(element.GetAttribute("aria-label"));
                    return !string.IsNullOrWhiteSpace(text);
            }
        }

        private static bool ShouldSkipRenderedTextNode(Node node)
        {
            for (var current = node?.ParentNode; current != null; current = current.ParentNode)
            {
                if (current is not Element element)
                {
                    continue;
                }

                if (element.HasAttribute("hidden"))
                {
                    return true;
                }

                if (string.Equals(element.ComputedStyle?.Display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var tag = element.TagName?.ToUpperInvariant();
                if (tag == "HEAD" ||
                    tag == "SCRIPT" ||
                    tag == "STYLE" ||
                    tag == "META" ||
                    tag == "LINK" ||
                    tag == "TITLE" ||
                    tag == "NOSCRIPT" ||
                    tag == "TEMPLATE" ||
                    tag == "IFRAME")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldSkipRenderedElementText(Element element)
        {
            if (element == null)
            {
                return true;
            }

            for (var current = element; current != null; current = current.ParentElement)
            {
                if (current.HasAttribute("hidden") ||
                    string.Equals(current.ComputedStyle?.Display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var currentTag = current.TagName?.ToUpperInvariant();
                if (currentTag == "HEAD" ||
                    currentTag == "SCRIPT" ||
                    currentTag == "STYLE" ||
                    currentTag == "META" ||
                    currentTag == "LINK" ||
                    currentTag == "TITLE" ||
                    currentTag == "NOSCRIPT" ||
                    currentTag == "TEMPLATE" ||
                    currentTag == "IFRAME")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetRenderedInputText(Element element, out string text)
        {
            text = null;
            var type = element.GetAttribute("type")?.ToLowerInvariant();
            if (type == "hidden" || type == "password" || type == "checkbox" || type == "radio" || type == "file")
            {
                return false;
            }

            text = FirstNonEmpty(
                element.GetAttribute("value"),
                element.GetAttribute("placeholder"),
                element.GetAttribute("aria-label"));

            return !string.IsNullOrWhiteSpace(text);
        }

        private static bool HasRenderedTextDescendant(Element element)
        {
            foreach (var descendant in element.SelfAndDescendants())
            {
                if (!ReferenceEquals(descendant, element) &&
                    descendant.NodeType == NodeType.Text &&
                    !string.IsNullOrWhiteSpace(descendant.TextContent) &&
                    !ShouldSkipRenderedTextNode(descendant))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        public async Task<string> GetTitleAsync()
        {
            await Task.CompletedTask;
            var dom = _engine.GetActiveDom();
            if (dom != null)
            {
                var titleNode = dom.Descendants().FirstOrDefault(n => string.Equals(n.NodeName, "title", StringComparison.OrdinalIgnoreCase));
                if (titleNode != null) return titleNode.TextContent ?? "";
            }
            return "";
        }

        public async Task<object> ExecuteScriptAsync(string script)
        {
            await Task.CompletedTask;
            EnsureFrameExecutionContextAvailable();
            TryLogDebug($"[BrowserApi] ExecuteScriptAsync called with script: {script}", LogCategory.JavaScript);
            var result = _engine.Evaluate(script);
            return PostProcessFenJsResult(result);
        }

        public Task<string> FindElementAsync(string strategy, string value)
        {
            return FindElementAsync(strategy, value, parentId: null);
        }

        public async Task ClickElementAsync(string elementId)
        {
            var element = ResolveElementInActiveContextOrThrow(elementId);
            if (element != null)
            {
                await RefreshWebDriverLayoutAsync().ConfigureAwait(false);
                _pendingWebDriverClickPointValid = false;
                for (var attempt = 0; attempt < 8 && !_pendingWebDriverClickPointValid; attempt++)
                {
                    if (await TryResolveWebDriverClickPointViaScriptAsync(elementId).ConfigureAwait(false))
                    {
                    }
                    else if (FenBrowser.FenEngine.Scripting.JavaScriptEngine.TryGetVisualRect(element, out var vx, out var vy, out var vw, out var vh) &&
                        vw > 0 &&
                        vh > 0)
                    {
                        _pendingWebDriverClickPointValid = true;
                        _pendingWebDriverClickClientX = (int)Math.Floor(vx + (vw / 2.0));
                        _pendingWebDriverClickClientY = (int)Math.Floor(vy + (vh / 2.0));
                    }
                    else
                    {
                        var rect = await GetElementRectAsync(elementId);
                        if (rect != null && rect.Width > 0 && rect.Height > 0)
                        {
                            _pendingWebDriverClickPointValid = true;
                            _pendingWebDriverClickClientX = (int)Math.Floor(rect.X + (rect.Width / 2.0));
                            _pendingWebDriverClickClientY = (int)Math.Floor(rect.Y + (rect.Height / 2.0));
                        }
                    }

                    if (!_pendingWebDriverClickPointValid)
                    {
                        // First-navigation layout can lag behind command dispatch on WPT.
                        // Keep polling briefly so click coordinates reflect the in-view center.
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                }

                if (!_pendingWebDriverClickPointValid &&
                    TryHandleFrameRemovalActivation(element, allowDefaultActivation: true))
                {
                    return;
                }

                var clickFallbackTarget = GetWebDriverClickFallbackTarget(element);
                if (clickFallbackTarget != null &&
                    !ReferenceEquals(element?.OwnerDocument?.DocumentElement, clickFallbackTarget) &&
                    IsElementHiddenForInteraction(clickFallbackTarget))
                {
                    clickFallbackTarget = null;
                }
                if (!_pendingWebDriverClickPointValid && clickFallbackTarget != null)
                {
                    var viewport = GetWindowRect();
                    _pendingWebDriverClickPointValid = true;
                    _pendingWebDriverClickClientX = Math.Max(0, viewport.Width / 2);
                    _pendingWebDriverClickClientY = Math.Max(0, viewport.Height / 2);
                }

                if (!_pendingWebDriverClickPointValid)
                {
                    throw new InvalidOperationException("element not interactable");
                }

                DispatchInputEvent(
                    "mousedown",
                    _pendingWebDriverClickClientX,
                    _pendingWebDriverClickClientY,
                    button: 0,
                    fallbackTarget: clickFallbackTarget);
                DispatchInputEvent(
                    "mouseup",
                    _pendingWebDriverClickClientX,
                    _pendingWebDriverClickClientY,
                    button: 0,
                    fallbackTarget: clickFallbackTarget);
                DispatchInputEvent(
                    "click",
                    _pendingWebDriverClickClientX,
                    _pendingWebDriverClickClientY,
                    button: 0,
                    fallbackTarget: clickFallbackTarget);

                if (!_lastClickHadTarget)
                {
                    if (TryHandleFrameRemovalActivation(element, allowDefaultActivation: true))
                    {
                        return;
                    }

                    throw new InvalidOperationException("element not interactable");
                }

                var activationTarget = _lastClickTarget;
                if (!AreElementsRelated(activationTarget, element))
                {
                    throw new InvalidOperationException("element click intercepted");
                }

                if (TryHandleInlineWindowOpenActivation(element) ||
                    (!ReferenceEquals(activationTarget, element) && TryHandleInlineWindowOpenActivation(activationTarget)))
                {
                    return;
                }

                await HandleElementClick(activationTarget);
            }
        }

        internal static Element GetWebDriverClickFallbackTarget(Element element)
        {
            if (element == null)
            {
                return null;
            }

            if (ReferenceEquals(element.OwnerDocument?.DocumentElement, element))
            {
                return element;
            }

            return HasActivationBehavior(element) && HasExplicitClickActivation(element)
                ? element
                : null;
        }

        private static bool HasExplicitClickActivation(Element element)
        {
            if (element == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(element.GetAttribute("onclick")) ||
                   !string.IsNullOrWhiteSpace(element.GetAttribute("popovertarget"));
        }

        private static bool TryHandleInlineWindowOpenActivation(Element element)
        {
            var onclick = element?.GetAttribute("onclick");
            if (string.IsNullOrWhiteSpace(onclick))
            {
                return false;
            }

            var match = InlineWindowOpenCallRegex.Match(onclick);
            if (!match.Success)
            {
                return false;
            }

            var bridge = JsDialogBridge.OpenWindow;
            if (bridge == null)
            {
                return false;
            }

            var args = ParseInlineWindowOpenArguments(match.Groups["args"].Value);
            bridge(
                args.Count > 0 ? args[0] : string.Empty,
                args.Count > 1 ? args[1] : string.Empty,
                args.Count > 2 ? args[2] : string.Empty);
            return true;
        }

        private static List<string> ParseInlineWindowOpenArguments(string source)
        {
            var args = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
            {
                return args;
            }

            var current = new System.Text.StringBuilder();
            var quote = '\0';
            var escaped = false;
            foreach (var ch in source)
            {
                if (escaped)
                {
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && quote != '\0')
                {
                    escaped = true;
                    continue;
                }

                if ((ch == '\'' || ch == '"') && (quote == '\0' || quote == ch))
                {
                    quote = quote == '\0' ? ch : '\0';
                    continue;
                }

                if (ch == ',' && quote == '\0')
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            args.Add(current.ToString().Trim());
            return args;
        }

        private async Task<bool> TryResolveWebDriverClickPointViaScriptAsync(string elementId)
        {
            try
            {
                var rectResult = await ExecuteScriptAsync(
                    "var el = arguments[0];" +
                    "if (!el) return null;" +
                    "var left = 0, top = 0, width = 0, height = 0;" +
                    "if (el.getBoundingClientRect) {" +
                    "  var r = el.getBoundingClientRect();" +
                    "  if (r && isFinite(r.left) && isFinite(r.top)) { left = Number(r.left) || 0; top = Number(r.top) || 0; }" +
                    "  if (r && isFinite(r.width) && isFinite(r.height)) { width = Number(r.width) || 0; height = Number(r.height) || 0; }" +
                    "}" +
                    "if (!(width > 0 && height > 0) && typeof getComputedStyle === 'function') {" +
                    "  var cs = getComputedStyle(el);" +
                    "  if (cs) {" +
                    "    var w = parseFloat(cs.width);" +
                    "    var h = parseFloat(cs.height);" +
                    "    if (isFinite(w) && w > 0) width = w;" +
                    "    if (isFinite(h) && h > 0) height = h;" +
                    "  }" +
                    "}" +
                    "if (!(width > 0 && height > 0)) {" +
                    "  var cw = Number(el.clientWidth) || Number(el.offsetWidth) || 0;" +
                    "  var ch = Number(el.clientHeight) || Number(el.offsetHeight) || 0;" +
                    "  if (cw > 0) width = cw;" +
                    "  if (ch > 0) height = ch;" +
                    "}" +
                    "if (!(width > 0 && height > 0)) return null;" +
                    "return [Math.floor(left + (width / 2)), Math.floor(top + (height / 2))];",
                    new object[] { elementId }).ConfigureAwait(false);

                if (TryReadIntPair(rectResult, out var clickX, out var clickY))
                {
                    _pendingWebDriverClickPointValid = true;
                    _pendingWebDriverClickClientX = clickX;
                    _pendingWebDriverClickClientY = clickY;
                    return true;
                }

            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadIntPair(object value, out int x, out int y)
        {
            x = 0;
            y = 0;

            if (value is object[] array && array.Length >= 2)
            {
                if (TryConvertToInt(array[0], out x) && TryConvertToInt(array[1], out y))
                {
                    return true;
                }
            }

            if (value is IList<object> list && list.Count >= 2)
            {
                if (TryConvertToInt(list[0], out x) && TryConvertToInt(list[1], out y))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryConvertToInt(object value, out int number)
        {
            number = 0;
            switch (value)
            {
                case int intValue:
                    number = intValue;
                    return true;
                case long longValue:
                    number = (int)longValue;
                    return true;
                case float floatValue:
                    number = (int)Math.Floor(floatValue);
                    return true;
                case double doubleValue:
                    number = (int)Math.Floor(doubleValue);
                    return true;
                case decimal decimalValue:
                    number = (int)Math.Floor(decimalValue);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryReadDoubleQuartet(object value, out double a, out double b, out double c, out double d)
        {
            a = 0;
            b = 0;
            c = 0;
            d = 0;

            if (value is object[] array && array.Length >= 4)
            {
                if (TryConvertToDouble(array[0], out a) &&
                    TryConvertToDouble(array[1], out b) &&
                    TryConvertToDouble(array[2], out c) &&
                    TryConvertToDouble(array[3], out d))
                {
                    return true;
                }
            }

            if (value is IList<object> list && list.Count >= 4)
            {
                if (TryConvertToDouble(list[0], out a) &&
                    TryConvertToDouble(list[1], out b) &&
                    TryConvertToDouble(list[2], out c) &&
                    TryConvertToDouble(list[3], out d))
                {
                    return true;
                }
            }

            if (value is IList nonGenericList && nonGenericList.Count >= 4)
            {
                if (TryConvertToDouble(nonGenericList[0], out a) &&
                    TryConvertToDouble(nonGenericList[1], out b) &&
                    TryConvertToDouble(nonGenericList[2], out c) &&
                    TryConvertToDouble(nonGenericList[3], out d))
                {
                    return true;
                }
            }

            if (value is IDictionary<string, object> dict &&
                dict.TryGetValue("0", out var v0) &&
                dict.TryGetValue("1", out var v1) &&
                dict.TryGetValue("2", out var v2) &&
                dict.TryGetValue("3", out var v3))
            {
                if (TryConvertToDouble(v0, out a) &&
                    TryConvertToDouble(v1, out b) &&
                    TryConvertToDouble(v2, out c) &&
                    TryConvertToDouble(v3, out d))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryConvertToDouble(object value, out double number)
        {
            number = 0;
            switch (value)
            {
                case int intValue:
                    number = intValue;
                    return true;
                case long longValue:
                    number = longValue;
                    return true;
                case float floatValue:
                    number = floatValue;
                    return true;
                case double doubleValue:
                    number = doubleValue;
                    return true;
                case decimal decimalValue:
                    number = (double)decimalValue;
                    return true;
                case string stringValue when double.TryParse(stringValue, out var parsed):
                    number = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private async Task<ElementRect> TryResolveElementRectViaScriptAsync(string elementId)
        {
            try
            {
                var rectResult = await ExecuteScriptAsync(
                    "var el = arguments[0];" +
                    "if (!el) return null;" +
                    "var left = 0, top = 0, width = 0, height = 0;" +
                    "if (el.getBoundingClientRect) {" +
                    "  var r = el.getBoundingClientRect();" +
                    "  if (r && isFinite(r.left) && isFinite(r.top)) { left = Number(r.left) || 0; top = Number(r.top) || 0; }" +
                    "  if (r && isFinite(r.width) && isFinite(r.height)) { width = Number(r.width) || 0; height = Number(r.height) || 0; }" +
                    "}" +
                    "if (!(width > 0 && height > 0) && typeof getComputedStyle === 'function') {" +
                    "  var cs = getComputedStyle(el);" +
                    "  if (cs) {" +
                    "    var w = parseFloat(cs.width);" +
                    "    var h = parseFloat(cs.height);" +
                    "    if (isFinite(w) && w > 0) width = w;" +
                    "    if (isFinite(h) && h > 0) height = h;" +
                    "  }" +
                    "}" +
                    "if (!(width > 0 && height > 0)) {" +
                    "  var cw = Number(el.clientWidth) || Number(el.offsetWidth) || 0;" +
                    "  var ch = Number(el.clientHeight) || Number(el.offsetHeight) || 0;" +
                    "  if (cw > 0) width = cw;" +
                    "  if (ch > 0) height = ch;" +
                    "}" +
                    "if (width <= 0 || height <= 0) return null;" +
                    "return [left, top, width, height];",
                    new object[] { elementId }).ConfigureAwait(false);
                if (TryReadDoubleQuartet(rectResult, out var left, out var top, out var width, out var height))
                {
                    return new ElementRect
                    {
                        X = left,
                        Y = top,
                        Width = Math.Max(0, width),
                        Height = Math.Max(0, height)
                    };
                }
            }
            catch
            {
            }

            return null;
        }

        private async Task FetchFaviconDelayedAsync(Uri pageUrl, long navigationId, int delayMs)
        {
            try
            {
                await Task.Delay(Math.Max(0, delayMs)).ConfigureAwait(false);
                await FetchFaviconAsync(pageUrl, navigationId).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task FetchFaviconAsync(Uri pageUrl, long navigationId)
        {
            try
            {
                if (!IsLatestNavigation(navigationId))
                {
                    return;
                }

                string iconUrl = null;
                var dom = _engine.GetActiveDom();
                
                // 1. Try to find link tag in DOM
                if (dom != null)
                {
                    // Find <link rel="icon" ...>
                    var links = dom
                        .Descendants()
                        .OfType<Element>()
                        .Where(x => string.Equals(x.TagName, "link", StringComparison.OrdinalIgnoreCase) &&
                                    x.Attr != null &&
                                    x.Attr.ContainsKey("rel"));
                    var iconLink = links.LastOrDefault(x => x.Attr["rel"].IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0);
                    
                    if (iconLink != null && iconLink.Attr.ContainsKey("href"))
                    {
                        iconUrl = iconLink.Attr["href"]?.Trim();
                    }
                }
                
                // 2. Fallback to /favicon.ico
                if (string.IsNullOrEmpty(iconUrl))
                {
                    iconUrl = "/favicon.ico";
                }
                
                // Resolve relative URL
                Uri absoluteIconUri = null;
                if (Uri.TryCreate(pageUrl, iconUrl, out absoluteIconUri))
                {
                    if (!IsLatestNavigation(navigationId))
                    {
                        return;
                    }

                    // 3. Fetch Image
                    using var stream = await _resources.FetchImageAsync(absoluteIconUri, pageUrl);
                    if (stream != null)
                    {
                        // Decode
                        // Copy to memory stream if needed for Skia
                        using var ms = new System.IO.MemoryStream();
                        await stream.CopyToAsync(ms);
                        ms.Position = 0;
                        
                        var bytes = ms.ToArray();
                        var bitmap = DecodeFavicon(bytes);
                        if (bitmap != null)
                        {
                            if (!IsLatestNavigation(navigationId))
                            {
                                bitmap.Dispose();
                                return;
                            }

                            // Resize if too large? Tab is small (16px), but keep quality High.
                            // Set property and fire event
                            Favicon = bitmap;
                            FenBrowser.Core.EngineLogCompat.Info($"[BrowserHost] Favicon loaded for {pageUrl}", FenBrowser.Core.Logging.LogCategory.General);
                            
                            // Marshall to UI thread handled by consumers
                            RepaintReady?.Invoke(this, null); // Trigger repaint? Or specific event
                            FaviconChanged?.Invoke(this, bitmap);
                            return;
                        }
                    }
                }
                
                // If failed, clear favicon?
                // Favicon = null;
                // FaviconChanged?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn($"[BrowserHost] Failed to fetch favicon: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
            }
        }

        private void SyncDocumentMetadata(Uri pageUrl)
        {
            try
            {
                var title = ExtractDocumentTitle(_engine.GetActiveDom());
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = pageUrl?.Host;
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    TitleChanged?.Invoke(this, title.Trim());
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Document metadata sync failed: {ex.Message}", LogCategory.Navigation);
            }
        }

        private static string ExtractDocumentTitle(Node dom)
        {
            var titleNode = dom?
                .Descendants()
                .FirstOrDefault(n => string.Equals(n.NodeName, "title", StringComparison.OrdinalIgnoreCase));

            return titleNode?.TextContent?.Trim() ?? string.Empty;
        }

        private static SKBitmap DecodeFavicon(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            var bitmap = SKBitmap.Decode(bytes);
            if (bitmap != null)
            {
                return bitmap;
            }

            return DecodeIcoFavicon(bytes);
        }

        private static SKBitmap DecodeIcoFavicon(byte[] bytes)
        {
            const int HeaderSize = 6;
            const int DirectoryEntrySize = 16;
            if (bytes.Length < HeaderSize)
            {
                return null;
            }

            ushort reserved = ReadUInt16Le(bytes, 0);
            ushort type = ReadUInt16Le(bytes, 2);
            ushort count = ReadUInt16Le(bytes, 4);
            if (reserved != 0 || type != 1 || count == 0)
            {
                return null;
            }

            SKBitmap fallback = null;
            int fallbackArea = -1;

            for (int i = 0; i < count; i++)
            {
                int entryOffset = HeaderSize + i * DirectoryEntrySize;
                if (entryOffset + DirectoryEntrySize > bytes.Length)
                {
                    break;
                }

                int width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
                int height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
                uint imageSize = ReadUInt32Le(bytes, entryOffset + 8);
                uint imageOffset = ReadUInt32Le(bytes, entryOffset + 12);
                if (imageSize == 0 ||
                    imageOffset >= bytes.Length ||
                    imageOffset + imageSize > bytes.Length)
                {
                    continue;
                }

                var iconBytes = new byte[imageSize];
                Buffer.BlockCopy(bytes, (int)imageOffset, iconBytes, 0, (int)imageSize);
                var decoded = SKBitmap.Decode(iconBytes);
                if (decoded == null)
                {
                    continue;
                }

                int area = width * height;
                if (area > fallbackArea)
                {
                    fallback?.Dispose();
                    fallback = decoded;
                    fallbackArea = area;
                }
                else
                {
                    decoded.Dispose();
                }
            }

            return fallback;
        }

        private static ushort ReadUInt16Le(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32Le(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                         (bytes[offset + 1] << 8) |
                         (bytes[offset + 2] << 16) |
                         (bytes[offset + 3] << 24));
        }

        // OLD CaptureScreenshotAsync removed - new one with string return is in NEW WEBDRIVER METHODS section

        private bool IsLatestNavigation(long navigationId)
        {
            return Interlocked.Read(ref _latestNavigationId) == navigationId;
        }

        private static string BuildResponseLifecycleDetail(FetchResult result)
        {
            if (result == null)
            {
                return "response=missing";
            }

            var statusCode = result.StatusCode > 0 ? result.StatusCode.ToString() : "n/a";
            var redirectCount = Math.Max(0, result.RedirectCount);
            var finalUri = result.FinalUri?.AbsoluteUri ?? "about:blank";
            var error = string.IsNullOrWhiteSpace(result.ErrorDetail) ? "none" : result.ErrorDetail;
            if (error.Length > 160)
            {
                error = error.Substring(0, 160) + "...";
            }

            string redirectChain = "none";
            if (result.RedirectChain != null && result.RedirectChain.Count > 0)
            {
                redirectChain = string.Join("->", result.RedirectChain.Take(6));
                if (result.RedirectChain.Count > 6)
                {
                    redirectChain += "->...";
                }
            }

            return $"status={result.Status};statusCode={statusCode};redirects={redirectCount};final={finalUri};chain={redirectChain};error={error}";
        }

        private string BuildInteractiveLifecycleDetail(FetchResult result)
        {
            var telemetry = _engine.LastRenderTelemetry;
            if (telemetry == null)
            {
                return "interactive=dom-rendered;telemetry=unavailable";
            }

            return
                "interactive=dom-rendered;" +
                $"tokenizing={telemetry.TokenizingMs}ms;" +
                $"parsing={telemetry.ParsingMs}ms;" +
                $"parse={telemetry.TokenizingAndParsingMs}ms;" +
                $"tokens={telemetry.ParseTokenCount};" +
                $"tokenizeCheckpoints={telemetry.TokenizingCheckpointCount};" +
                $"parseCheckpoints={telemetry.ParsingCheckpointCount};" +
                $"domParseCheckpoints={telemetry.ParsingDocumentCheckpointCount};" +
                $"docReadyToken={telemetry.DocumentReadyTokenCount};" +
                $"parseRepaints={telemetry.ParseIncrementalRepaintCount};" +
                $"streamPreparse={telemetry.StreamingPreparseMs}ms;" +
                $"streamCheckpoints={telemetry.StreamingPreparseCheckpointCount};" +
                $"streamRepaints={telemetry.StreamingPreparseRepaintCount};" +
                $"interleaved={(telemetry.InterleavedParseUsed ? 1 : 0)};" +
                $"interleavedBatch={telemetry.InterleavedTokenBatchSize};" +
                $"interleavedChunks={telemetry.InterleavedBatchCount};" +
                $"interleavedFallback={(telemetry.InterleavedFallbackUsed ? 1 : 0)};" +
                $"css={telemetry.CssAndStyleMs}ms;" +
                $"cssQueue={telemetry.CssQueueWaitMs:F1}ms;" +
                $"cssFetch={telemetry.CssDiscoveryAndFetchMs:F1}ms;" +
                $"cssImports={telemetry.CssImportExpansionMs:F1}ms;" +
                $"cssRules={telemetry.CssRuleParseMs:F1}ms;" +
                $"cssVariables={telemetry.CssVariableResolutionMs:F1}ms;" +
                $"cssCascade={telemetry.CssCascadeMs:F1}ms;" +
                $"cssTotal={telemetry.CssTotalMs:F1}ms;" +
                $"visual1={telemetry.InitialVisualTreeMs}ms;" +
                $"script={telemetry.ScriptExecutionMs}ms;" +
                $"visual2={telemetry.PostScriptVisualTreeMs}ms;" +
                $"total={telemetry.TotalRenderMs}ms;" +
                $"js={(telemetry.JavaScriptExecuted ? 1 : 0)};" +
                $"redirects={Math.Max(0, result?.RedirectCount ?? 0)}";
        }

        private Func<Uri, Task<string>> CreateTrackedCssFetcher(long navigationId)
        {
            return async uri =>
            {
                _navigationSubresources.MarkLoadStarted(navigationId);
                try
                {
                    return await _resources.FetchCssAsync(uri).ConfigureAwait(false);
                }
                finally
                {
                    _navigationSubresources.MarkLoadCompleted(navigationId);
                }
            };
        }

        private Func<Uri, Task<System.IO.Stream>> CreateTrackedImageFetcher(long navigationId)
        {
            return async uri =>
            {
                _navigationSubresources.MarkLoadStarted(navigationId);
                try
                {
                    return await _resources.FetchImageAsync(uri).ConfigureAwait(false);
                }
                finally
                {
                    _navigationSubresources.MarkLoadCompleted(navigationId);
                }
            };
        }

        private void SetActiveRenderNavigation(long navigationId)
        {
            Interlocked.Exchange(ref _activeRenderNavigationId, navigationId);
        }

        private void ClearActiveRenderNavigation(long navigationId)
        {
            if (Interlocked.Read(ref _activeRenderNavigationId) == navigationId)
            {
                Interlocked.Exchange(ref _activeRenderNavigationId, 0);
            }
        }

        private async Task MarkNavigationCompleteWhenSettledAsync(long navigationId, string baseDetail)
        {
            if (!IsLatestNavigation(navigationId))
            {
                _navigationSubresources.AbandonNavigation(navigationId);
                _navigationLifecycle.MarkCancelled(navigationId, "superseded-by-new-navigation");
                return;
            }

            var settleDetail = await WaitForSubresourceSettleDetailAsync(navigationId).ConfigureAwait(false);
            if (!IsLatestNavigation(navigationId))
            {
                _navigationSubresources.AbandonNavigation(navigationId);
                _navigationLifecycle.MarkCancelled(navigationId, "superseded-by-new-navigation");
                return;
            }

            var documentLifecycleDetail = await WaitForDocumentLifecycleSettleDetailAsync(navigationId).ConfigureAwait(false);
            if (!IsLatestNavigation(navigationId))
            {
                _navigationSubresources.AbandonNavigation(navigationId);
                _navigationLifecycle.MarkCancelled(navigationId, "superseded-by-new-navigation");
                return;
            }

            var detail = string.IsNullOrWhiteSpace(baseDetail)
                ? $"{settleDetail};{documentLifecycleDetail}"
                : $"{baseDetail};{settleDetail};{documentLifecycleDetail}";

            // Mark navigation complete after bounded subresource and document
            // lifecycle settling so lifecycle diagnostics do not report
            // Complete before DOMContentLoaded/load for the same document.
            // Diagnostic probes remain after completion and cannot gate UI.
            _navigationLifecycle.MarkComplete(navigationId, detail);

            // Diagnostic probe: capture the shape of well-known challenge / page
            // globals and cookie-name set at the point of navigation completion.
            // No-ops unless FEN_NAV_GLOBALS_SNAPSHOT=1 or LogNavigationGlobals is
            // set; bounded by ProbeTimeoutMs; failures are swallowed.
            _engine?.ScriptEngine?.CaptureNavigationGlobals(_current, navigationId);

            EngineLog.EmitSuppressedSummary();
            var (unsupportedHtml, unsupportedCss, unsupportedJs) = EngineCapabilities.GetUnsupportedCounts();
            EngineLog.WriteRateLimited(
                key: "summary:unsupported-features",
                window: TimeSpan.FromMinutes(1),
                subsystem: LogSubsystem.Verification,
                severity: LogSeverity.Info,
                message: "Unsupported feature summary",
                marker: LogMarker.Fallback,
                context: default,
                fields: new Dictionary<string, object>
                {
                    ["unsupportedHtml"] = unsupportedHtml,
                    ["unsupportedCss"] = unsupportedCss,
                    ["unsupportedJs"] = unsupportedJs
                });
            _navigationSubresources.AbandonNavigation(navigationId);
        }

        private async Task<string> WaitForDocumentLifecycleSettleDetailAsync(long navigationId)
        {
            const int settleTimeoutMs = 1500;
            const int pollIntervalMs = 25;
            var startedUtc = DateTimeOffset.UtcNow;
            var scriptEngine = _engine?.ScriptEngine;

            if (_engine == null || !_engine.EnableJavaScript || scriptEngine == null)
            {
                return
                    "eventLoopObservation=transition-time;" +
                    "eventLoopObservationTimedOut=0;" +
                    "eventLoop=disabled;" +
                    "eventLoopStatusAtObservation=not-run;" +
                    "documentReadyStateAtObservation=unknown;" +
                    "domContentLoadedAtObservation=0;" +
                    "loadAtObservation=0;" +
                    "pendingHostTimersAtObservation=0;" +
                    "eventLoopObservationWaitMs=0;" +
                    $"eventLoopObservationTimeoutMs={settleTimeoutMs};" +
                    $"navId={navigationId}";
            }

            bool IsDocumentLifecycleSettled(FenBrowser.FenEngine.Scripting.BrowserEventLoopSnapshot snapshot)
            {
                if (snapshot == null)
                {
                    return true;
                }

                if (string.Equals(snapshot.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(snapshot.Status, "completed-no-document", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return snapshot.DomContentLoadedFired &&
                       snapshot.LoadFired &&
                       snapshot.PendingHostTimers == 0;
            }

            FenBrowser.FenEngine.Scripting.BrowserEventLoopSnapshot snapshot =
                scriptEngine.GetEventLoopSnapshot();

            while (IsLatestNavigation(navigationId) &&
                   !IsDocumentLifecycleSettled(snapshot) &&
                   (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds < settleTimeoutMs)
            {
                await Task.Delay(pollIntervalMs).ConfigureAwait(false);
                snapshot = scriptEngine.GetEventLoopSnapshot();
            }

            snapshot = scriptEngine.GetEventLoopSnapshot() ?? snapshot;
            var elapsedMs = (int)Math.Min(
                settleTimeoutMs,
                Math.Max(0, (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds));
            var settled = IsDocumentLifecycleSettled(snapshot);

            return FormatDocumentLifecycleSettleDetail(
                navigationId,
                snapshot,
                settled,
                elapsedMs,
                settleTimeoutMs);
        }

        internal static string FormatDocumentLifecycleSettleDetail(
            long navigationId,
            FenBrowser.FenEngine.Scripting.BrowserEventLoopSnapshot snapshot,
            bool settled,
            int elapsedMs,
            int settleTimeoutMs)
        {
            var status = snapshot?.Status ?? "not-run";
            var readyState = string.IsNullOrWhiteSpace(snapshot?.DocumentReadyState)
                ? "unknown"
                : snapshot.DocumentReadyState;

            return
                "eventLoopObservation=transition-time;" +
                $"eventLoopObservationTimedOut={(!settled && elapsedMs >= settleTimeoutMs ? 1 : 0)};" +
                $"eventLoop={(settled ? status : "partial")};" +
                $"eventLoopStatusAtObservation={status};" +
                $"documentReadyStateAtObservation={readyState};" +
                $"domContentLoadedAtObservation={(snapshot?.DomContentLoadedFired == true ? 1 : 0)};" +
                $"loadAtObservation={(snapshot?.LoadFired == true ? 1 : 0)};" +
                $"pendingHostTimersAtObservation={snapshot?.PendingHostTimers ?? 0};" +
                $"eventLoopObservationWaitMs={elapsedMs};" +
                $"eventLoopObservationTimeoutMs={settleTimeoutMs};" +
                $"navId={navigationId}";
        }

        private async Task<string> WaitForSubresourceSettleDetailAsync(long navigationId)
        {
            const int settleTimeoutMs = 1500;
            const string settledState = "subresources=settled;renderSubresourcesPending=0;imagesPending=0;fontsPending=0;tasksPending=0;microtasksPending=0";
            var loop = _engine.EventLoopCoordinator;

            bool IsSettledNow()
            {
                return _navigationSubresources.GetPendingCount(navigationId) == 0 &&
                       ImageLoader.PendingLoadCount == 0 &&
                       FontRegistry.PendingLoadCount == 0 &&
                       !loop.HasPendingTasks &&
                       !loop.HasPendingMicrotasks;
            }

            if (IsSettledNow())
            {
                return settledState;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void PendingLoadsHandler(int _) { if (IsSettledNow()) completion.TrySetResult(true); }
            void PendingFontsHandler(int _) { if (IsSettledNow()) completion.TrySetResult(true); }
            void PendingRenderSubresourcesHandler(long navId, int _) { if (navId == navigationId && IsSettledNow()) completion.TrySetResult(true); }

            ImageLoader.PendingLoadCountChanged += PendingLoadsHandler;
            FontRegistry.PendingLoadCountChanged += PendingFontsHandler;
            _navigationSubresources.PendingCountChanged += PendingRenderSubresourcesHandler;
            try
            {
                if (!IsSettledNow())
                {
                    var timeoutTask = Task.Delay(settleTimeoutMs);
                    await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
                }
            }
            finally
            {
                ImageLoader.PendingLoadCountChanged -= PendingLoadsHandler;
                FontRegistry.PendingLoadCountChanged -= PendingFontsHandler;
                _navigationSubresources.PendingCountChanged -= PendingRenderSubresourcesHandler;
            }

            if (IsSettledNow())
            {
                return settledState;
            }

            return
                "subresources=partial;" +
                $"renderSubresourcesPending={_navigationSubresources.GetPendingCount(navigationId)};" +
                $"imagesPending={ImageLoader.PendingLoadCount};" +
                $"fontsPending={FontRegistry.PendingLoadCount};" +
                $"tasksPending={(loop.HasPendingTasks ? 1 : 0)};" +
                $"microtasksPending={(loop.HasPendingMicrotasks ? 1 : 0)};" +
                $"timeoutMs={settleTimeoutMs};" +
                $"navId={navigationId}";
        }

        private void RaiseNavigationFailed(string msg) => NavigationFailed?.Invoke(this, msg);

        public void HighlightElement(Element element)
        {
            _engine.HighlightElement(element);
        }

        public void RemoveHighlight()
        {
            _engine.RemoveHighlight();
        }

        /// <summary>
        /// Drive the Event Loop (Tasks and Microtasks).
        /// Should be called repeatedly from the Main Thread (e.g. UI timer).
        /// </summary>
        public void Pulse()
        {
            // Forward pulse to the EngineLoop (Phase 5)
            // This consolidates task/microtask/render coordination
            _engineLoop.RunFrame();
        }

        // ========== NEW WEBDRIVER METHODS ==========

        public async Task<bool> RefreshAsync()
        {
            if (_current != null)
                return await NavigateAsync(_current.AbsoluteUri);
            return false;
        }

        public Task<string> GetCurrentUrlAsync()
        {
            return Task.FromResult(_current?.AbsoluteUri ?? "about:blank");
        }

        public bool HasValidCurrentBrowsingContext()
        {
            if (_frameContextInvalidated)
            {
                return false;
            }

            // Top-level browsing-context validity is managed at the window/session layer.
            // For frame contexts, fail when the selected frame is detached or unresolved.
            if (_currentFrameElement == null)
            {
                return true;
            }

            if (!_currentFrameElement.IsConnected)
            {
                return false;
            }

            return ResolveFrameSearchRoot(_currentFrameElement) != null;
        }

        public WindowRect GetWindowRect()
        {
            // Use delegate if available, otherwise return defaults
            if (GetWindowRectDelegate != null)
                return GetWindowRectDelegate();
            return new WindowRect { X = 0, Y = 0, Width = 1100, Height = 700 };
        }

        public WindowRect SetWindowRect(int? x, int? y, int? width, int? height)
        {
            if (SetWindowRectDelegate != null)
                return SetWindowRectDelegate(x, y, width, height);
            return GetWindowRect();
        }

        public WindowRect MaximizeWindow()
        {
            if (MaximizeWindowDelegate != null)
                return MaximizeWindowDelegate();
            return GetWindowRect();
        }

        public WindowRect MinimizeWindow()
        {
            if (MinimizeWindowDelegate != null)
                return MinimizeWindowDelegate();
            return GetWindowRect();
        }

        public WindowRect FullscreenWindow()
        {
            if (FullscreenWindowDelegate != null)
                return FullscreenWindowDelegate();
            return GetWindowRect();
        }

        public async Task CreateNewTabAsync()
        {
            if (CreateNewTabDelegate != null)
                await CreateNewTabDelegate();
        }

        public async Task SwitchToFrameAsync(object frameId)
        {
            TraceWebDriverFrame(
                $"SwitchToFrame start frameRefType='{frameId?.GetType().Name ?? "null"}' frameRef='{frameId?.ToString() ?? "<null>"}' currentUrl='{_current?.AbsoluteUri ?? "about:blank"}'");

            if (frameId == null)
            {
                _currentFrameElement = null;
                _frameContextInvalidated = false;
                _frameContextStack.Clear();
                SyncScriptContextToSelectedBrowsingContext();
                TraceWebDriverFrame("SwitchToFrame reset to top-level context.");
                return;
            }

            var frameElement = ResolveFrameReference(frameId);
            if (frameElement == null)
            {
                TraceWebDriverFrame("SwitchToFrame could not resolve frame reference.");
                TryLogWarn($"[BrowserHost] SwitchToFrameAsync could not resolve frame reference '{frameId}'.", LogCategory.Navigation);
                throw new InvalidOperationException("no such frame");
            }

            TraceWebDriverFrame($"SwitchToFrame resolved frame={DescribeFrameElement(frameElement)}");
            if (ResolveFrameSearchRoot(frameElement) == null)
            {
                TraceWebDriverFrame($"SwitchToFrame frame root missing before hydration frame={DescribeFrameElement(frameElement)}");
                await EnsureFrameSearchRootLoadedAsync(frameElement);
            }

            if (_currentFrameElement != null)
            {
                _frameContextStack.Push(_currentFrameElement);
            }

            _currentFrameElement = frameElement;
            _frameContextInvalidated = false;

            var resolvedRoot = ResolveFrameSearchRoot(frameElement);
            SyncScriptContextToSelectedBrowsingContext();
            TraceWebDriverFrame(
                $"SwitchToFrame selected frame={DescribeFrameElement(frameElement)} root='{DescribeSearchRoot(resolvedRoot)}' preview='{BuildElementPreview(resolvedRoot, 12)}'");
        }

        public Task SwitchToParentFrameAsync()
        {
            _currentFrameElement = _frameContextStack.Count > 0 ? _frameContextStack.Pop() : null;
            _frameContextInvalidated = false;
            SyncScriptContextToSelectedBrowsingContext();
            return Task.CompletedTask;
        }

        public async Task<string> FindElementAsync(string strategy, string value, string parentId = null)
        {
            if (_currentFrameElement != null)
            {
                await EnsureFrameSearchRootLoadedAsync(_currentFrameElement);
                var frameRoot = ResolveFrameSearchRoot(_currentFrameElement);
                TraceWebDriverFrame(
                    $"FindElement frame-context strategy='{strategy}' value='{value}' root='{DescribeSearchRoot(frameRoot)}' preview='{BuildElementPreview(frameRoot, 12)}'");
            }

            Node searchRoot = ResolveSearchRootNode(parentId);
            if (searchRoot == null)
            {
                TryLogWarn(
                    $"[WebDriverFind] null-root strategy='{strategy}' value='{value}' frameId='{_currentFrameElement?.GetAttribute("id") ?? string.Empty}' frameSrc='{_currentFrameElement?.GetAttribute("src") ?? string.Empty}'",
                    LogCategory.Navigation);
                return null;
            }

            Element found = FindElementByStrategy(searchRoot, strategy, value);
            if (found != null)
            {
                var canonicalId = await TryResolveRuntimeElementIdAsync(found).ConfigureAwait(false);
                return canonicalId ?? GetOrRegisterElementId(found);
            }

            if (!string.IsNullOrWhiteSpace(parentId) &&
                TryResolveHostElementReferenceForShadowRoot(parentId, out var hostElementRef))
            {
                var fallbackId = await FindElementFromShadowRootViaScriptAsync(hostElementRef, parentId, strategy, value)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fallbackId))
                {
                    return fallbackId;
                }
            }

            TryLogWarn(
                $"[WebDriverFind] miss strategy='{strategy}' value='{value}' current='{_current?.AbsoluteUri ?? "about:blank"}' root='{DescribeSearchRoot(searchRoot)}' preview='{BuildElementPreview(searchRoot, 16)}'",
                LogCategory.Navigation);
            return null;
        }

        // ========== INPUT HANDLING ==========

        public void OnMouseDown(float x, float y, int button)
        {
            QueueInputTask("mousedown", x, y, button);
        }

        public void OnMouseUp(float x, float y, int button)
        {
            QueueInputTask("mouseup", x, y, button);
        }

        public void OnMouseMove(float x, float y)
        {
            const float stationaryMouseEpsilon = 0.5f;
            if (_hasLastMouseMovePosition &&
                Math.Abs(x - _lastMouseMoveX) < stationaryMouseEpsilon &&
                Math.Abs(y - _lastMouseMoveY) < stationaryMouseEpsilon)
            {
                return;
            }

            _lastMouseMoveX = x;
            _lastMouseMoveY = y;
            _hasLastMouseMovePosition = true;
            QueueInputTask("mousemove", x, y, 0);
        }

        public void OnDoubleClick(float x, float y, int button)
        {
            QueueInputTask("dblclick", x, y, button);
        }

        public bool OnContextMenu(float x, float y, int button)
        {
            return QueueInputTask("contextmenu", x, y, button);
        }

        public bool OnMouseWheel(float x, float y, float deltaX, float deltaY)
        {
            var defaultAllowed = DispatchInputEvent("wheel", x, y, 0, deltaX: deltaX, deltaY: deltaY);
            if (!defaultAllowed || _activeRenderer == null)
            {
                return defaultAllowed;
            }

            var frame = TryGetEmbeddingFrame(_lastDispatchedInputTarget);
            if (frame == null)
            {
                return true;
            }

            const float nestedWheelStepPixels = 60f;
            var before = _activeRenderer.ScrollManager.GetScrollOffset(frame);
            _activeRenderer.ScrollManager.Scroll(
                frame,
                -(deltaX * nestedWheelStepPixels),
                -(deltaY * nestedWheelStepPixels));
            var after = _activeRenderer.ScrollManager.GetScrollOffset(frame);
            if (Math.Abs(after.x - before.x) <= 0.01f && Math.Abs(after.y - before.y) <= 0.01f)
            {
                return true;
            }

            _engine.ScriptEngine?.NotifyFrameScrollChanged(frame);
            _engine.ScriptEngine?.RequestRender?.Invoke();
            return false;
        }

        private static Element TryGetEmbeddingFrame(Element target)
        {
            var ownerDocument = target?.OwnerDocument;
            return ownerDocument?.ParentNode is Element frame &&
                string.Equals(frame.TagName, "iframe", StringComparison.OrdinalIgnoreCase)
                    ? frame
                    : null;
        }

        private static bool ShouldRetryTopLevelNavigation(FetchResult result, string url, int attempt, int maxAttempts)
        {
            if (attempt >= maxAttempts) return false;
            if (!IsRetriableTopLevelScheme(url)) return false;
            if (result == null) return true;
            return result.Status == FetchStatus.ConnectionFailed || result.Status == FetchStatus.Timeout;
        }

        private static bool IsHtmlContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            var ct = contentType.Trim();
            return ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                   ct.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRetriableTopLevelScheme(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        }

        public void OnClick(float x, float y, int button)
        {
             QueueInputTask("click", x, y, button);
        }

        /// <summary>
        /// Dispatches one trusted pointer click and then runs the default activation
        /// selected by that same hit test. Physical input callers should use this
        /// method instead of separately dispatching a click and activating an element.
        /// </summary>
        public async Task DispatchClickAndActivate(float x, float y, int button)
        {
            // Phase 12: use the async dispatch path so the engine thread is not
            // blocked while the JS worker executes the click handler. The hit-test
            // and event creation are synchronous; only JS dispatch is awaited.
            await DispatchInputEventAsync("click", x, y, button).ConfigureAwait(false);
            var activationTarget = _lastClickTarget;
            if (activationTarget != null)
            {
                await HandleElementClick(activationTarget).ConfigureAwait(false);
            }
        }

        private bool QueueInputTask(string type, float x, float y, int button)
        {
             // Input must feel immediate; dispatch directly to avoid coordinator latency
             // or dropped interaction when event-loop pumping is delayed.
             return DispatchInputEvent(type, x, y, button);
        }

        private bool DispatchInputEvent(
            string type,
            float x,
            float y,
            int button,
            Element fallbackTarget = null,
            float deltaX = 0,
            float deltaY = 0)
        {
            var eventType = MapToInputEventType(type);
            var buttonMask = BuildButtonMask(button, type);
            // Prefer the active renderer (injected by BrowserIntegration) which has the actual
            // paint tree. Fall back to engine's cached renderer only if no active renderer set.
            var renderContext = _activeRenderer?.CreateRenderContext() ?? _engine.BuildRenderContext();
            var context = _engine.Context;
            var inputEvent = new InputEvent
            {
                Type = eventType,
                X = x,
                Y = y,
                Button = button,
                DeltaX = deltaX,
                DeltaY = deltaY,
                Buttons = buttonMask,
                PointerId = 1,
                PointerType = "mouse",
                Pressure = buttonMask != 0 ? 0.5f : 0f,
                IsPrimary = true,
                PageX = x,
                PageY = y,
                ScreenX = x,
                ScreenY = y
            };

            try
            {
                _inputManager.ProcessEvent(inputEvent, renderContext, context);
            }
            catch (FenBrowser.FenEngine.Errors.FenTimeoutError timeoutEx)
            {
                TryLogWarn($"[BrowserHost] Timed out dispatching '{type}' input event: {timeoutEx.Message}", LogCategory.Events);
                TryInvokeConsoleMessage($"[FenBrowser] Timed out running page '{type}' handler: {timeoutEx.Message}");
            }
            catch (Exception ex)
            {
                TryLogError($"[BrowserHost] Unhandled exception dispatching '{type}' input event: {ex.Message}", LogCategory.Events);
                TryInvokeConsoleMessage($"[FenBrowser] Unhandled page error during '{type}' input: {ex.Message}");
            }

            if (inputEvent.Target == null && fallbackTarget != null)
            {
                inputEvent.Target = fallbackTarget;
            }
            _lastDispatchedInputTarget = inputEvent.Target;

            var eventInit = new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
            {
                ClientX = inputEvent.X,
                ClientY = inputEvent.Y,
                PageX = inputEvent.PageX,
                PageY = inputEvent.PageY,
                ScreenX = inputEvent.ScreenX,
                ScreenY = inputEvent.ScreenY,
                Button = button,
                Buttons = buttonMask,
                DeltaX = deltaX,
                DeltaY = deltaY,
                PointerId = 1,
                PointerType = "mouse",
                Pressure = buttonMask != 0 ? 0.5f : 0f,
                IsPrimary = true,
                Bubbles = true,
                Cancelable = true
            };
            var defaultAllowed = true;

            var isClick = string.Equals(type, "click", StringComparison.OrdinalIgnoreCase);
            if (isClick)
            {
                _lastClickHadTarget = inputEvent.Target != null;
                _lastClickTarget = inputEvent.Target;
                _lastClickDefaultAllowed = true;
                _suppressNextDomClickDispatchInHandleElementClick = false;
            }

            if (inputEvent.Target != null && IsScriptDomInputEvent(type))
            {
                var pointerAlias = MapMouseInputToPointerAlias(type);
                if (!string.IsNullOrEmpty(pointerAlias))
                {
                    defaultAllowed = _engine.DispatchPointerEvent(inputEvent.Target, pointerAlias, eventInit);
                }

                var mouseDefaultAllowed = _engine.DispatchPointerEvent(inputEvent.Target, type, eventInit);
                defaultAllowed = mouseDefaultAllowed && defaultAllowed;
            }

            if (isClick)
            {
                // BrowserIntegration emits the DOM click through this path before
                // calling HandleElementClick for default activation. Preserve the
                // FenJS preventDefault() result and avoid synthesizing a second
                // legacy DOM click from HandleElementClick.
                _lastClickDefaultAllowed = inputEvent.Target == null || defaultAllowed;
                _suppressNextDomClickDispatchInHandleElementClick = inputEvent.Target != null;
            }

            if (string.Equals(type, "mousemove", StringComparison.OrdinalIgnoreCase))
            {
                var hovered = NormalizeHoverTarget(inputEvent.Target);
                if (!ReferenceEquals(ElementStateManager.Instance.HoveredElement, hovered))
                {
                    ElementStateManager.Instance.SetHoveredElement(hovered);
                    // Hover state changes already flow through OnStateChanged -> ScheduleRecascade ->
                    // engine RepaintReady. Triggering an extra immediate repaint here duplicates
                    // frame work and causes visible hover lag under frequent pointer movement.
                }
            }

            // Keep BrowserApi typing focus aligned with pointer targeting.
            // Without this, clicks may dispatch DOM events but keyboard text input has no target.
            if (string.Equals(type, "mousedown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "click", StringComparison.OrdinalIgnoreCase))
            {
                SyncFocusFromPointerTarget(inputEvent.Target);
            }

            // NOTE: HandleElementClick is NOT called here because BrowserIntegration's
            // HandleMouseUp already calls it with the paint-tree hit-test result. Calling it
            // here too would cause double-navigation for links and double-focus for inputs.
            return defaultAllowed;
        }

        /// <summary>
        /// Phase 12: async variant of <see cref="DispatchInputEvent"/>. Performs the
        /// same hit-test and event-creation synchronously, then asynchronously
        /// dispatches the JS event via <see cref="CustomHtmlEngine.DispatchPointerEventAsync"/>
        /// without blocking the calling thread while the JS worker executes.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> DispatchInputEventAsync(
            string type,
            float x,
            float y,
            int button,
            Element fallbackTarget = null,
            float deltaX = 0,
            float deltaY = 0)
        {
            var eventType = MapToInputEventType(type);
            var buttonMask = BuildButtonMask(button, type);
            var renderContext = _activeRenderer?.CreateRenderContext() ?? _engine.BuildRenderContext();
            var context = _engine.Context;
            var inputEvent = new InputEvent
            {
                Type = eventType,
                X = x,
                Y = y,
                Button = button,
                DeltaX = deltaX,
                DeltaY = deltaY,
                Buttons = buttonMask,
                PointerId = 1,
                PointerType = "mouse",
                Pressure = buttonMask != 0 ? 0.5f : 0f,
                IsPrimary = true,
                PageX = x,
                PageY = y,
                ScreenX = x,
                ScreenY = y
            };

            _inputManager.ProcessEvent(inputEvent, renderContext, context);

            if (inputEvent.Target == null && fallbackTarget != null)
            {
                inputEvent.Target = fallbackTarget;
            }
            _lastDispatchedInputTarget = inputEvent.Target;

            var eventInit = new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
            {
                ClientX = inputEvent.X,
                ClientY = inputEvent.Y,
                PageX = inputEvent.PageX,
                PageY = inputEvent.PageY,
                ScreenX = inputEvent.ScreenX,
                ScreenY = inputEvent.ScreenY,
                Button = button,
                Buttons = buttonMask,
                DeltaX = deltaX,
                DeltaY = deltaY,
                PointerId = 1,
                PointerType = "mouse",
                Pressure = buttonMask != 0 ? 0.5f : 0f,
                IsPrimary = true,
                Bubbles = true,
                Cancelable = true
            };
            var defaultAllowed = true;

            var isClick = string.Equals(type, "click", StringComparison.OrdinalIgnoreCase);
            if (isClick)
            {
                _lastClickHadTarget = inputEvent.Target != null;
                _lastClickTarget = inputEvent.Target;
                _lastClickDefaultAllowed = true;
                _suppressNextDomClickDispatchInHandleElementClick = false;
            }

            if (inputEvent.Target != null && IsScriptDomInputEvent(type))
            {
                var pointerAlias = MapMouseInputToPointerAlias(type);
                if (!string.IsNullOrEmpty(pointerAlias))
                {
                    defaultAllowed = await _engine.DispatchPointerEventAsync(inputEvent.Target, pointerAlias, eventInit).ConfigureAwait(false);
                }

                var mouseDefaultAllowed = await _engine.DispatchPointerEventAsync(inputEvent.Target, type, eventInit).ConfigureAwait(false);
                defaultAllowed = mouseDefaultAllowed && defaultAllowed;
            }

            if (isClick)
            {
                _lastClickDefaultAllowed = inputEvent.Target == null || defaultAllowed;
                _suppressNextDomClickDispatchInHandleElementClick = inputEvent.Target != null;
            }

            if (string.Equals(type, "mousemove", StringComparison.OrdinalIgnoreCase))
            {
                var hovered = NormalizeHoverTarget(inputEvent.Target);
                if (!ReferenceEquals(ElementStateManager.Instance.HoveredElement, hovered))
                {
                    ElementStateManager.Instance.SetHoveredElement(hovered);
                }
            }

            if (string.Equals(type, "mousedown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "click", StringComparison.OrdinalIgnoreCase))
            {
                SyncFocusFromPointerTarget(inputEvent.Target);
            }

            return defaultAllowed;
        }

        private static bool IsScriptDomInputEvent(string type)
        {
            return string.Equals(type, "click", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "dblclick", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "contextmenu", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "mousedown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "mouseup", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "mousemove", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "wheel", StringComparison.OrdinalIgnoreCase);
        }

        private static string MapMouseInputToPointerAlias(string type)
        {
            return type?.ToLowerInvariant() switch
            {
                "mousedown" => "pointerdown",
                "mouseup" => "pointerup",
                "mousemove" => "pointermove",
                _ => null
            };
        }

        private static int BuildButtonMask(int button, string type)
        {
            if (!string.Equals(type, "mousedown", StringComparison.OrdinalIgnoreCase)) return 0;
            if (button < 0) return 0;
            return 1 << Math.Min(button, 3);
        }

        private static InputEventType MapToInputEventType(string type)
        {
            switch (type?.ToLowerInvariant())
            {
                case "mousedown": return InputEventType.MouseDown;
                case "mouseup": return InputEventType.MouseUp;
                case "mousemove": return InputEventType.MouseMove;
                case "click": return InputEventType.Click;
                case "dblclick": return InputEventType.DblClick;
                case "contextmenu": return InputEventType.ContextMenu;
                case "wheel": return InputEventType.Wheel;
                case "keydown": return InputEventType.KeyDown;
                case "keyup": return InputEventType.KeyUp;
                case "touchstart": return InputEventType.TouchStart;
                case "touchmove": return InputEventType.TouchMove;
                case "touchend": return InputEventType.TouchEnd;
                default: return InputEventType.MouseMove;
            }
        }

        private static Element NormalizeHoverTarget(Element hovered)
        {
            if (hovered == null)
                return null;

            var tag = hovered.TagName;
            if (string.Equals(tag, "html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "body", StringComparison.OrdinalIgnoreCase))
                return null;

            return hovered;
        }

        public async Task<string[]> FindElementsAsync(string strategy, string value, string parentId = null)
        {
            if (_currentFrameElement != null)
            {
                await EnsureFrameSearchRootLoadedAsync(_currentFrameElement);
                var frameRoot = ResolveFrameSearchRoot(_currentFrameElement);
                TraceWebDriverFrame(
                    $"FindElements frame-context strategy='{strategy}' value='{value}' root='{DescribeSearchRoot(frameRoot)}' preview='{BuildElementPreview(frameRoot, 12)}'");
            }

            Node searchRoot = ResolveSearchRootNode(parentId);
            if (searchRoot == null)
            {
                TryLogWarn(
                    $"[WebDriverFind] null-root-many strategy='{strategy}' value='{value}' frameId='{_currentFrameElement?.GetAttribute("id") ?? string.Empty}' frameSrc='{_currentFrameElement?.GetAttribute("src") ?? string.Empty}'",
                    LogCategory.Navigation);
                return Array.Empty<string>();
            }

            var elements = FindElementsByStrategy(searchRoot, strategy, value);
            var ids = new List<string>();
            foreach (var el in elements)
            {
                var canonicalId = await TryResolveRuntimeElementIdAsync(el).ConfigureAwait(false);
                ids.Add(canonicalId ?? GetOrRegisterElementId(el));
            }

            if (ids.Count == 0 &&
                !string.IsNullOrWhiteSpace(parentId) &&
                TryResolveHostElementReferenceForShadowRoot(parentId, out var hostElementRef))
            {
                var fallbackIds = await FindElementsFromShadowRootViaScriptAsync(hostElementRef, parentId, strategy, value)
                    .ConfigureAwait(false);
                if (fallbackIds.Length > 0)
                {
                    return fallbackIds;
                }
            }

            return ids.ToArray();
        }

        private async Task<string> TryResolveRuntimeElementIdAsync(Element element)
        {
            if (element == null)
            {
                return null;
            }

            var marker = "wdcanon-" + Guid.NewGuid().ToString("N");
            try
            {
                element.SetAttribute(WebDriverCanonicalProbeAttribute, marker);
            }
            catch
            {
                return null;
            }

            try
            {
                var script = $@"
                    var marker = arguments[0];
                    var selector = '[{WebDriverCanonicalProbeAttribute}=""' + marker + '""]';
                    var el = document.querySelector(selector);
                    if (!el) return null;
                    el.removeAttribute('{WebDriverCanonicalProbeAttribute}');
                    return el;";

                var result = await ExecuteScriptAsync(script, new object[] { marker }).ConfigureAwait(false);
                if (result is string token &&
                    token.StartsWith(WebDriverElementTokenPrefix, StringComparison.Ordinal))
                {
                    return token.Substring(WebDriverElementTokenPrefix.Length);
                }
            }
            catch
            {
            }
            finally
            {
                try
                {
                    element.RemoveAttribute(WebDriverCanonicalProbeAttribute);
                }
                catch
                {
                }
            }

            return null;
        }

        private async Task<string> FindElementFromShadowRootViaScriptAsync(
            string hostElementId,
            string shadowRootId,
            string strategy,
            string selector)
        {
            if (!TryResolveShadowHostElement(hostElementId, shadowRootId, out var hostElement) || hostElement == null)
            {
                return null;
            }

            var hostMarker = "fen-sr-host-" + Guid.NewGuid().ToString("N");
            try
            {
                hostElement.SetAttribute(WebDriverShadowHostProbeAttribute, hostMarker);
            }
            catch
            {
                return null;
            }

            var result = await ExecuteScriptAsync(
                @"
                var hostMarker = arguments[0] || '';
                var usingStrategy = arguments[1] || '';
                var selectorValue = arguments[2] || '';
                var hostProbeAttribute = arguments[3] || '';
                function collectText(node, parts) {
                    if (!node) return;
                    if (node.nodeType === Node.TEXT_NODE) { parts.push(node.nodeValue || ''); return; }
                    if (node.nodeType === Node.ELEMENT_NODE && node.tagName && node.tagName.toLowerCase() === 'br') { parts.push('\n'); return; }
                    var children = node.childNodes || [];
                    for (var i = 0; i < children.length; i++) collectText(children[i], parts);
                }
                function renderedLinkText(anchor) {
                    var parts = [];
                    collectText(anchor, parts);
                    var text = parts.join('').replace(/\u00a0/g, ' ').trim();
                    var style = '';
                    try { style = (anchor.getAttribute('style') || '').toLowerCase(); } catch (e) {}
                    if (style.indexOf('text-transform') >= 0 && style.indexOf('uppercase') >= 0) text = text.toUpperCase();
                    return text;
                }
                var host = null;
                if (hostMarker) {
                    try {
                        host = Array.prototype.find.call(
                            document.querySelectorAll('[' + hostProbeAttribute + ']'),
                            function(node) { return node && node.getAttribute(hostProbeAttribute) === hostMarker; }) || null;
                    } catch (e) { host = null; }
                }
                var root = null;
                if (host) {
                    try {
                        if (window.customElements && typeof window.customElements.upgrade === 'function') {
                            window.customElements.upgrade(host);
                        }
                    } catch (e) {}
                    try { root = host.shadowRoot || null; } catch (e) { root = null; }
                }
                if (!root && window._shadowRoot) {
                    try {
                        if (window._shadowRoot.host === host) root = window._shadowRoot;
                    } catch (e) {}
                }
                function collectElements(node, out) {
                    if (!node) return;
                    var children = node.childNodes || [];
                    for (var i = 0; i < children.length; i++) {
                        var child = children[i];
                        if (child && child.nodeType === Node.ELEMENT_NODE) {
                            out.push(child);
                            collectElements(child, out);
                        }
                    }
                }
                function simpleCssMatch(el, sel) {
                    if (!el || !sel) return false;
                    if (sel === '*') return true;
                    if (sel.charAt(0) === '#') return (el.id || '') === sel.substring(1);
                    if (sel.charAt(0) === '.') {
                        var cls = (el.getAttribute('class') || '').split(/\s+/);
                        var needle = sel.substring(1);
                        for (var i = 0; i < cls.length; i++) if (cls[i] === needle) return true;
                        return false;
                    }
                    return (el.tagName || '').toLowerCase() === sel.toLowerCase();
                }
                if (!root) return null;
                if (usingStrategy === 'css selector') {
                    try {
                        var direct = root.querySelector(selectorValue);
                        if (direct) return direct;
                    } catch (e) {}
                    var cssCandidates = [];
                    collectElements(root, cssCandidates);
                    for (var i = 0; i < cssCandidates.length; i++) {
                        if (simpleCssMatch(cssCandidates[i], selectorValue)) return cssCandidates[i];
                    }
                    return null;
                }
                if (usingStrategy === 'tag name') {
                    var tags = [];
                    collectElements(root, tags);
                    for (var i = 0; i < tags.length; i++) {
                        if ((tags[i].tagName || '').toLowerCase() === (selectorValue || '').toLowerCase()) return tags[i];
                    }
                    return null;
                }
                if (usingStrategy === 'link text' || usingStrategy === 'partial link text') {
                    var all = [];
                    collectElements(root, all);
                    var anchors = [];
                    for (var i = 0; i < all.length; i++) {
                        if ((all[i].tagName || '').toLowerCase() === 'a') anchors.push(all[i]);
                    }
                    for (var i = 0; i < anchors.length; i++) {
                        var text = renderedLinkText(anchors[i]);
                        if (usingStrategy === 'link text' && text === selectorValue) return anchors[i];
                        if (usingStrategy === 'partial link text' && text.indexOf(selectorValue) >= 0) return anchors[i];
                    }
                    return null;
                }
                if (usingStrategy === 'xpath') {
                    try {
                        var result = document.evaluate(selectorValue, root, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                        for (var i = 0; i < result.snapshotLength; i++) {
                            var candidate = result.snapshotItem(i);
                            if (candidate && candidate.nodeType === Node.ELEMENT_NODE) return candidate;
                        }
                    } catch (e) {}
                    var xpathCandidates = [];
                    collectElements(root, xpathCandidates);
                    if ((selectorValue || '').indexOf('//') === 0) {
                        var tag = (selectorValue || '').substring(2).trim().toLowerCase();
                        for (var i = 0; i < xpathCandidates.length; i++) {
                            if ((xpathCandidates[i].tagName || '').toLowerCase() === tag) return xpathCandidates[i];
                        }
                    }
                    return null;
                }
                return null;",
                new object[] { hostMarker, strategy ?? string.Empty, selector ?? string.Empty, WebDriverShadowHostProbeAttribute }).ConfigureAwait(false);

            try
            {
                hostElement.RemoveAttribute(WebDriverShadowHostProbeAttribute);
            }
            catch
            {
            }

            if (result is string token &&
                token.StartsWith(WebDriverElementTokenPrefix, StringComparison.Ordinal))
            {
                return token.Substring(WebDriverElementTokenPrefix.Length);
            }

            if (result is FenBrowser.FenEngine.Core.Interfaces.IObject obj &&
                TryExtractDomElementFromWrapper(obj, out var element) &&
                element != null)
            {
                return GetOrRegisterElementId(element);
            }

            if (result is Element directElement)
            {
                return GetOrRegisterElementId(directElement);
            }

            return null;
        }

        private async Task<string[]> FindElementsFromShadowRootViaScriptAsync(
            string hostElementId,
            string shadowRootId,
            string strategy,
            string selector)
        {
            if (!TryResolveShadowHostElement(hostElementId, shadowRootId, out var hostElement) || hostElement == null)
            {
                return Array.Empty<string>();
            }

            var hostMarker = "fen-sr-host-" + Guid.NewGuid().ToString("N");
            try
            {
                hostElement.SetAttribute(WebDriverShadowHostProbeAttribute, hostMarker);
            }
            catch
            {
                return Array.Empty<string>();
            }

            var result = await ExecuteScriptAsync(
                @"
                var hostMarker = arguments[0] || '';
                var usingStrategy = arguments[1] || '';
                var selectorValue = arguments[2] || '';
                var hostProbeAttribute = arguments[3] || '';
                function collectText(node, parts) {
                    if (!node) return;
                    if (node.nodeType === Node.TEXT_NODE) { parts.push(node.nodeValue || ''); return; }
                    if (node.nodeType === Node.ELEMENT_NODE && node.tagName && node.tagName.toLowerCase() === 'br') { parts.push('\n'); return; }
                    var children = node.childNodes || [];
                    for (var i = 0; i < children.length; i++) collectText(children[i], parts);
                }
                function renderedLinkText(anchor) {
                    var parts = [];
                    collectText(anchor, parts);
                    var text = parts.join('').replace(/\u00a0/g, ' ').trim();
                    var style = '';
                    try { style = (anchor.getAttribute('style') || '').toLowerCase(); } catch (e) {}
                    if (style.indexOf('text-transform') >= 0 && style.indexOf('uppercase') >= 0) text = text.toUpperCase();
                    return text;
                }
                var host = null;
                if (hostMarker) {
                    try {
                        host = Array.prototype.find.call(
                            document.querySelectorAll('[' + hostProbeAttribute + ']'),
                            function(node) { return node && node.getAttribute(hostProbeAttribute) === hostMarker; }) || null;
                    } catch (e) { host = null; }
                }
                var root = null;
                if (host) {
                    try {
                        if (window.customElements && typeof window.customElements.upgrade === 'function') {
                            window.customElements.upgrade(host);
                        }
                    } catch (e) {}
                    try { root = host.shadowRoot || null; } catch (e) { root = null; }
                }
                if (!root && window._shadowRoot) {
                    try {
                        if (window._shadowRoot.host === host) root = window._shadowRoot;
                    } catch (e) {}
                }
                function collectElements(node, out) {
                    if (!node) return;
                    var children = node.childNodes || [];
                    for (var i = 0; i < children.length; i++) {
                        var child = children[i];
                        if (child && child.nodeType === Node.ELEMENT_NODE) {
                            out.push(child);
                            collectElements(child, out);
                        }
                    }
                }
                function simpleCssMatch(el, sel) {
                    if (!el || !sel) return false;
                    if (sel === '*') return true;
                    if (sel.charAt(0) === '#') return (el.id || '') === sel.substring(1);
                    if (sel.charAt(0) === '.') {
                        var cls = (el.getAttribute('class') || '').split(/\s+/);
                        var needle = sel.substring(1);
                        for (var i = 0; i < cls.length; i++) if (cls[i] === needle) return true;
                        return false;
                    }
                    return (el.tagName || '').toLowerCase() === sel.toLowerCase();
                }
                if (!root) return [];
                if (usingStrategy === 'css selector' || usingStrategy === 'tag name') {
                    try {
                        var direct = Array.from(root.querySelectorAll(selectorValue));
                        if (direct.length > 0) return direct;
                    } catch (e) {}
                    var cssCandidates = [];
                    collectElements(root, cssCandidates);
                    var outCss = [];
                    for (var i = 0; i < cssCandidates.length; i++) {
                        if (usingStrategy === 'tag name') {
                            if ((cssCandidates[i].tagName || '').toLowerCase() === (selectorValue || '').toLowerCase()) outCss.push(cssCandidates[i]);
                        } else if (simpleCssMatch(cssCandidates[i], selectorValue)) {
                            outCss.push(cssCandidates[i]);
                        }
                    }
                    return outCss;
                }
                if (usingStrategy === 'link text' || usingStrategy === 'partial link text') {
                    var all = [];
                    collectElements(root, all);
                    var anchors = [];
                    for (var i = 0; i < all.length; i++) {
                        if ((all[i].tagName || '').toLowerCase() === 'a') anchors.push(all[i]);
                    }
                    var matches = [];
                    for (var i = 0; i < anchors.length; i++) {
                        var text = renderedLinkText(anchors[i]);
                        if (usingStrategy === 'link text' && text === selectorValue) matches.push(anchors[i]);
                        if (usingStrategy === 'partial link text' && text.indexOf(selectorValue) >= 0) matches.push(anchors[i]);
                    }
                    return matches;
                }
                if (usingStrategy === 'xpath') {
                    var out = [];
                    try {
                        var result = document.evaluate(selectorValue, root, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                        for (var i = 0; i < result.snapshotLength; i++) {
                            var candidate = result.snapshotItem(i);
                            if (candidate && candidate.nodeType === Node.ELEMENT_NODE) out.push(candidate);
                        }
                    } catch (e) {}
                    if (out.length === 0 && (selectorValue || '').indexOf('//') === 0) {
                        var tag = (selectorValue || '').substring(2).trim().toLowerCase();
                        var xpathCandidates = [];
                        collectElements(root, xpathCandidates);
                        for (var i = 0; i < xpathCandidates.length; i++) {
                            if ((xpathCandidates[i].tagName || '').toLowerCase() === tag) out.push(xpathCandidates[i]);
                        }
                    }
                    return out;
                }
                return [];",
                new object[] { hostMarker, strategy ?? string.Empty, selector ?? string.Empty, WebDriverShadowHostProbeAttribute }).ConfigureAwait(false);

            try
            {
                hostElement.RemoveAttribute(WebDriverShadowHostProbeAttribute);
            }
            catch
            {
            }

            var ids = new List<string>();
            if (result is IEnumerable enumerable && result is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is string token &&
                        token.StartsWith(WebDriverElementTokenPrefix, StringComparison.Ordinal))
                    {
                        ids.Add(token.Substring(WebDriverElementTokenPrefix.Length));
                        continue;
                    }

                    if (item is FenBrowser.FenEngine.Core.Interfaces.IObject obj &&
                        TryExtractDomElementFromWrapper(obj, out var element) &&
                        element != null)
                    {
                        ids.Add(GetOrRegisterElementId(element));
                        continue;
                    }

                    if (item is Element directElement)
                    {
                        ids.Add(GetOrRegisterElementId(directElement));
                    }
                }
            }

            return ids.ToArray();
        }

        private bool TryResolveShadowHostElement(string hostElementId, string shadowRootId, out Element hostElement)
        {
            hostElement = null;

            if (!string.IsNullOrWhiteSpace(hostElementId) &&
                _elementMap.TryGetValue(hostElementId, out var mappedHost) &&
                mappedHost != null)
            {
                hostElement = mappedHost;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(shadowRootId) &&
                _shadowRootMap.TryGetValue(shadowRootId, out var mappedShadowRoot) &&
                mappedShadowRoot?.Host != null)
            {
                hostElement = mappedShadowRoot.Host;
                return true;
            }

            return false;
        }

        private static bool TryGetHostElementReferenceFromShadowRootId(string shadowRootId, out string hostElementRef)
        {
            hostElementRef = null;
            if (string.IsNullOrWhiteSpace(shadowRootId) ||
                !shadowRootId.StartsWith("sr:host:", StringComparison.Ordinal))
            {
                return false;
            }

            hostElementRef = shadowRootId.Substring("sr:host:".Length);
            return !string.IsNullOrWhiteSpace(hostElementRef);
        }

        private bool TryResolveHostElementReferenceForShadowRoot(string shadowRootId, out string hostElementRef)
        {
            hostElementRef = null;

            if (TryGetHostElementReferenceFromShadowRootId(shadowRootId, out var deterministicHostRef))
            {
                hostElementRef = deterministicHostRef;
                return true;
            }

            if (!_shadowRootMap.TryGetValue(shadowRootId, out var shadowRoot) || shadowRoot?.Host == null)
            {
                return false;
            }

            hostElementRef = GetOrRegisterElementId(shadowRoot.Host);
            return !string.IsNullOrWhiteSpace(hostElementRef);
        }

        private Element FindElementByStrategy(Node root, string strategy, string value)
        {
            return FindElementsByStrategy(root, strategy, value).FirstOrDefault();
        }

        private IEnumerable<Element> FindElementsByStrategy(Node root, string strategy, string value)
        {
            value = value?.Trim() ?? string.Empty;
            var elements = root.SelfAndDescendants().OfType<Element>();

            if (string.Equals(strategy, "css selector", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException("invalid selector");
                }

                return elements.Where(element => IsCssMatch(element, value));
            }

            if (string.Equals(strategy, "tag name", StringComparison.Ordinal))
            {
                return elements.Where(n =>
                    string.Equals(n.TagName, value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.NodeName, value, StringComparison.OrdinalIgnoreCase));
            }

            if (string.Equals(strategy, "link text", StringComparison.Ordinal))
            {
                return elements.Where(element =>
                    IsAnchorElement(element) &&
                    string.Equals(GetRenderedLinkTextForMatch(element), value, StringComparison.Ordinal));
            }

            if (string.Equals(strategy, "partial link text", StringComparison.Ordinal))
            {
                return elements.Where(element =>
                    IsAnchorElement(element) &&
                    GetRenderedLinkTextForMatch(element).Contains(value, StringComparison.Ordinal));
            }

            if (string.Equals(strategy, "xpath", StringComparison.Ordinal))
            {
                return EvaluateXPath(root, value);
            }

            return Enumerable.Empty<Element>();
        }

        private static bool IsAnchorElement(Element element)
        {
            return element != null &&
                   (string.Equals(element.TagName, "a", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.NodeName, "a", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCssMatch(Element element, string selector)
        {
            try
            {
                return element.Matches(selector);
            }
            catch
            {
                throw new InvalidOperationException("invalid selector");
            }
        }

        private static string GetRenderedLinkTextForMatch(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            var pieces = new List<string>();
            CollectRenderedText(element, pieces);
            var rendered = string.Concat(pieces).Replace('\u00A0', ' ').Trim();

            var inlineStyle = element.GetAttribute("style") ?? string.Empty;
            if (inlineStyle.IndexOf("text-transform", StringComparison.OrdinalIgnoreCase) >= 0 &&
                inlineStyle.IndexOf("uppercase", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return rendered.ToUpperInvariant();
            }

            return rendered;
        }

        private static void CollectRenderedText(Node node, List<string> pieces)
        {
            if (node == null)
            {
                return;
            }

            if (node.NodeType == NodeType.Text)
            {
                pieces.Add(node.TextContent ?? string.Empty);
                return;
            }

            if (node is Element element &&
                string.Equals(element.TagName, "br", StringComparison.OrdinalIgnoreCase))
            {
                pieces.Add("\n");
                return;
            }

            var children = node.ChildNodes;
            if (children == null)
            {
                return;
            }

            foreach (var child in children)
            {
                CollectRenderedText(child, pieces);
            }
        }

        private static IEnumerable<Element> EvaluateXPath(Node root, string expression)
        {
            var query = (expression ?? string.Empty).Trim();
            var elements = root.SelfAndDescendants().OfType<Element>().ToList();

            if (query == "..")
            {
                if (root is not Element contextElement)
                {
                    throw new InvalidOperationException("invalid selector");
                }

                if (contextElement.ParentNode is Document)
                {
                    throw new InvalidOperationException("invalid selector");
                }

                if (contextElement.ParentNode is Element parentElement)
                {
                    return new[] { parentElement };
                }

                return Enumerable.Empty<Element>();
            }

            if (query == "/html")
            {
                var html = elements.FirstOrDefault(element =>
                    string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.NodeName, "html", StringComparison.OrdinalIgnoreCase));
                return html == null ? Enumerable.Empty<Element>() : new[] { html };
            }

            if (!query.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("invalid selector");
            }

            var selector = query.Substring(2).Trim();
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new InvalidOperationException("invalid selector");
            }

            if (selector.StartsWith("*[name()='", StringComparison.Ordinal) &&
                selector.EndsWith("']", StringComparison.Ordinal) &&
                selector.Length > "*[name()='']".Length)
            {
                var name = selector.Substring(10, selector.Length - 12);
                return elements.Where(element =>
                    string.Equals(element.TagName, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.NodeName, name, StringComparison.OrdinalIgnoreCase));
            }

            if (selector.IndexOf('/', StringComparison.Ordinal) >= 0 ||
                selector.IndexOf('[', StringComparison.Ordinal) >= 0 ||
                selector.IndexOf('(', StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("invalid selector");
            }

            return elements.Where(element =>
                string.Equals(element.TagName, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.NodeName, selector, StringComparison.OrdinalIgnoreCase));
        }

        private static string DescribeSearchRoot(Node root)
        {
            if (root == null)
            {
                return "null";
            }

            if (root is Element element)
            {
                return $"{element.TagName}#{element.GetAttribute("id") ?? string.Empty}";
            }

            if (root is Document document)
            {
                return $"Document:{document.DocumentElement?.TagName ?? "null"}";
            }

            return root.NodeType.ToString();
        }

        private static string BuildElementPreview(Node root, int limit)
        {
            if (root == null)
            {
                return string.Empty;
            }

            var elements = root.SelfAndDescendants()
                .OfType<Element>()
                .Take(Math.Max(1, limit))
                .Select(el =>
                {
                    var id = el.GetAttribute("id");
                    return string.IsNullOrWhiteSpace(id) ? el.TagName : $"{el.TagName}#{id}";
                })
                .ToArray();

            return elements.Length == 0 ? "<none>" : string.Join(",", elements);
        }

        private Node ResolveSearchRootNode(string parentId = null)
        {
            if (!string.IsNullOrEmpty(parentId) && _elementMap.TryGetValue(parentId, out var parent))
            {
                if (!IsElementReferenceInCurrentBrowsingContext(parentId))
                {
                    throw new InvalidOperationException("no such element");
                }

                if (!parent.IsConnected)
                {
                    throw new InvalidOperationException("stale element reference");
                }

                return parent;
            }

            if (!string.IsNullOrEmpty(parentId))
            {
                var activeSearchRoot = ResolveSearchRoot();
                if (activeSearchRoot == null)
                {
                    if (_currentFrameElement != null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    if (TryResolveShadowRootWithoutSearchRoot(parentId, out var shadowRoot, out var shadowRootError))
                    {
                        return shadowRoot;
                    }

                    throw new InvalidOperationException(shadowRootError ?? "no such shadow root");
                }

                if (TryResolveShadowRootInActiveContext(parentId, activeSearchRoot, out var activeContextShadowRoot, out var error))
                {
                    return activeContextShadowRoot;
                }

                throw new InvalidOperationException(error ?? "no such shadow root");
            }

            if (_currentFrameElement != null)
            {
                return ResolveFrameSearchRoot(_currentFrameElement);
            }

            var dom = _engine.GetActiveDom();
            return (dom as Element) ?? (dom as Document)?.DocumentElement;
        }

        private bool TryResolveShadowRootInActiveContext(
            string shadowRootReference,
            Element activeSearchRoot,
            out ShadowRoot shadowRoot,
            out string error)
        {
            shadowRoot = null;
            error = null;

            if (string.IsNullOrWhiteSpace(shadowRootReference))
            {
                error = "no such shadow root";
                return false;
            }

            if (!_shadowRootMap.TryGetValue(shadowRootReference, out shadowRoot) || shadowRoot == null)
            {
                if (shadowRootReference.StartsWith("sr:host:", StringComparison.Ordinal))
                {
                    var hostReferenceIdFromLookup = shadowRootReference.Substring("sr:host:".Length);
                    if (!string.IsNullOrWhiteSpace(hostReferenceIdFromLookup) &&
                        _elementMap.TryGetValue(hostReferenceIdFromLookup, out var hostElement))
                    {
                        shadowRoot = TryGetAttachedShadowRoot(hostElement);
                        if (shadowRoot != null)
                        {
                            _shadowRootMap[shadowRootReference] = shadowRoot;
                        }
                    }
                }
            }

            if (shadowRoot == null)
            {
                error = "no such shadow root";
                return false;
            }

            var hostReferenceId = string.Empty;
            if (shadowRootReference.StartsWith("sr:host:", StringComparison.Ordinal))
            {
                hostReferenceId = shadowRootReference.Substring("sr:host:".Length);
            }

            var shadowHost = shadowRoot.Host;
            if (shadowHost == null)
            {
                error = "no such shadow root";
                return false;
            }

            if (!shadowHost.IsConnected || shadowHost.ParentNode == null)
            {
                if (!string.IsNullOrWhiteSpace(hostReferenceId) &&
                    IsElementReferenceInCurrentBrowsingContext(hostReferenceId))
                {
                    error = "detached shadow root";
                }
                else
                {
                    error = "no such shadow root";
                }
                return false;
            }

            if (!IsElementWithinSearchRoot(activeSearchRoot, shadowHost))
            {
                error = "no such shadow root";
                return false;
            }

            return true;
        }

        private bool TryResolveShadowRootWithoutSearchRoot(
            string shadowRootReference,
            out ShadowRoot shadowRoot,
            out string error)
        {
            shadowRoot = null;
            error = null;

            if (string.IsNullOrWhiteSpace(shadowRootReference))
            {
                error = "no such shadow root";
                return false;
            }

            if (!_shadowRootMap.TryGetValue(shadowRootReference, out shadowRoot) || shadowRoot == null)
            {
                if (shadowRootReference.StartsWith("sr:host:", StringComparison.Ordinal))
                {
                    var hostReferenceId = shadowRootReference.Substring("sr:host:".Length);
                    if (!string.IsNullOrWhiteSpace(hostReferenceId) &&
                        _elementMap.TryGetValue(hostReferenceId, out var hostElement))
                    {
                        shadowRoot = TryGetAttachedShadowRoot(hostElement);
                        if (shadowRoot != null)
                        {
                            _shadowRootMap[shadowRootReference] = shadowRoot;
                        }
                    }
                }
            }

            if (shadowRoot == null)
            {
                error = "no such shadow root";
                return false;
            }

            var shadowHost = shadowRoot.Host;
            if (shadowHost == null)
            {
                error = "no such shadow root";
                return false;
            }

            if (!shadowHost.IsConnected || shadowHost.ParentNode == null)
            {
                error = "detached shadow root";
                return false;
            }

            return true;
        }

        private Element ResolveSearchRoot(string parentId = null)
        {
            return ResolveSearchRootNode(parentId) as Element;
        }

        private Element ResolveFrameReference(object frameReference)
        {
            if (frameReference == null)
            {
                return null;
            }

            var searchRoot = ResolveSearchRoot();
            if (frameReference is string stringReference)
            {
                if (_elementMap.TryGetValue(stringReference, out var mapped) && IsFrameElement(mapped))
                {
                    return mapped;
                }

                if (searchRoot == null)
                {
                    return null;
                }

                return searchRoot
                    .SelfAndDescendants()
                    .OfType<Element>()
                    .FirstOrDefault(element =>
                        IsFrameElement(element) &&
                        (string.Equals(element.GetAttribute("id"), stringReference, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(element.GetAttribute("name"), stringReference, StringComparison.OrdinalIgnoreCase)));
            }

            if (frameReference is int index)
            {
                if (index < 0 || searchRoot == null)
                {
                    return null;
                }

                return searchRoot
                    .SelfAndDescendants()
                    .OfType<Element>()
                    .Where(IsFrameElement)
                    .Skip(index)
                    .FirstOrDefault();
            }

            return null;
        }

        private static bool IsFrameElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            return string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(element.TagName, "frame", StringComparison.OrdinalIgnoreCase);
        }

        private Element ResolveFrameSearchRoot(Element frameElement)
        {
            if (!IsFrameElement(frameElement))
            {
                return null;
            }

            if (!frameElement.IsConnected)
            {
                return null;
            }

            if (FenBrowser.FenEngine.DOM.ElementWrapper.IsRemoteFrameElement(frameElement, _current?.AbsoluteUri))
            {
                TraceWebDriverFrame($"ResolveFrameSearchRoot remote-frame block frame={DescribeFrameElement(frameElement)} currentUrl='{_current?.AbsoluteUri ?? "about:blank"}'");
                return null;
            }

            var sandboxAttribute = frameElement.GetAttribute("sandbox");
            if (FenBrowser.Core.SandboxPolicy.HasIframeSandboxAttribute(sandboxAttribute))
            {
                var flags = FenBrowser.Core.SandboxPolicy.ParseIframeSandboxFlags(sandboxAttribute);
                if ((flags & FenBrowser.Core.IframeSandboxFlags.SameOrigin) == 0)
                {
                    TraceWebDriverFrame($"ResolveFrameSearchRoot sandbox block frame={DescribeFrameElement(frameElement)} sandbox='{sandboxAttribute}'");
                    return null;
                }
            }

            var frameChildren = frameElement.ChildNodes;
            if (frameChildren == null || frameChildren.Length == 0)
            {
                return null;
            }

            // Frame DOMs may be attached as a Document child under the iframe element.
            for (int i = 0; i < frameChildren.Length; i++)
            {
                if (frameChildren[i] is Document frameDocument)
                {
                    var docRoot = frameDocument.DocumentElement ??
                                  frameDocument.ChildNodes?.OfType<Element>().FirstOrDefault();
                    if (docRoot != null)
                    {
                        return docRoot;
                    }
                }
            }

            // Fallback for cases where markup nodes are directly attached.
            for (int i = 0; i < frameChildren.Length; i++)
            {
                if (frameChildren[i] is Element frameRootElement)
                {
                    return frameRootElement;
                }
            }

            TryLogWarn(
                $"[WebDriverFrame] Unable to resolve frame search root for id='{frameElement.GetAttribute("id") ?? string.Empty}' src='{frameElement.GetAttribute("src") ?? string.Empty}' children='{string.Join(",", frameChildren.Select(child => child?.GetType()?.Name ?? "<null>"))}'",
                LogCategory.Navigation);
            TraceWebDriverFrame($"ResolveFrameSearchRoot unresolved frame={DescribeFrameElement(frameElement)}");
            return null;
        }

        private async Task LoadFrameElementAsync(Element frameElement, Uri frameUri)
        {
            if (!IsFrameElement(frameElement) || frameUri == null)
            {
                return;
            }

            if (!frameElement.IsConnected)
            {
                return;
            }

            var currentFrameUri = ResolveFrameBaseUri(frameElement);
            if (currentFrameUri != null &&
                !string.Equals(currentFrameUri.AbsoluteUri, frameUri.AbsoluteUri, StringComparison.Ordinal))
            {
                return;
            }

            if (ResolveFrameSearchRoot(frameElement) != null &&
                IsFrameScriptsHydratedForUri(frameElement, frameUri))
            {
                return;
            }

            try
            {
                TryLogInfo($"[BrowserHost] Loading iframe '{frameUri}'", LogCategory.Navigation);
                var result = await _resources.FetchTextDetailedAsync(
                    CreateFrameFetchContext(frameElement, frameUri, _current),
                    "text/html,application/xhtml+xml").ConfigureAwait(false);

                if (result?.Status != FetchStatus.Success || string.IsNullOrWhiteSpace(result.Content))
                {
                    TryLogWarn(
                        $"[BrowserHost] iframe load did not produce a document for '{frameUri}' status='{result?.Status.ToString() ?? "<null>"}'",
                        LogCategory.Navigation);
                    return;
                }

                var finalUri = result.FinalUri ?? frameUri;
                var parsedDocument = HtmlParser.ParseDocument(
                    result.Content,
                    new HtmlParserOptions { BaseUri = finalUri });
                var parsedRoot = parsedDocument?.DocumentElement;
                if (parsedRoot == null)
                {
                    TryLogWarn($"[BrowserHost] iframe parse produced no document element for '{finalUri}'", LogCategory.Navigation);
                    return;
                }

                currentFrameUri = ResolveFrameBaseUri(frameElement);
                if (currentFrameUri != null &&
                    !string.Equals(currentFrameUri.AbsoluteUri, frameUri.AbsoluteUri, StringComparison.Ordinal))
                {
                    return;
                }

                while (frameElement.FirstChild != null)
                {
                    frameElement.RemoveChild(frameElement.FirstChild);
                }

                var executionOptions = CreateFrameExecutionOptions(result, finalUri, parsedDocument);
                frameElement.AppendChild(parsedDocument);
                using (EnterImageLoaderContext())
                {
                    await _engine.PrewarmSubdocumentImagesAsync(parsedRoot, finalUri).ConfigureAwait(false);
                }
                await TryInitializeFrameScriptsAsync(
                    frameElement,
                    parsedRoot,
                    finalUri,
                    executionOptions).ConfigureAwait(false);
                frameElement.MarkDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                _engine.ScheduleRecascade(fullRecascade: true);
                SyncScriptContextToSelectedBrowsingContext();
                TryLogInfo(
                    $"[BrowserHost] iframe document attached final='{finalUri}' root='{parsedRoot.TagName}'",
                    LogCategory.Navigation);
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] failed loading iframe '{frameUri}': {ex.Message}", LogCategory.Navigation);
            }
        }

        private async Task EnsureFrameSearchRootLoadedAsync(Element frameElement)
        {
            if (!IsFrameElement(frameElement))
            {
                return;
            }

            var existingRoot = ResolveFrameSearchRoot(frameElement);
            if (existingRoot != null)
            {
                await TryInitializeFrameScriptsAsync(frameElement, existingRoot, ResolveFrameBaseUri(frameElement)).ConfigureAwait(false);
                return;
            }

            TraceWebDriverFrame($"EnsureFrameRoot start frame={DescribeFrameElement(frameElement)}");
            var srcDoc = frameElement.GetAttribute("srcdoc");
            var frameHtml = srcDoc;
            Uri frameUri = _current;
            var src = frameElement.GetAttribute("src");
            FetchResult frameFetchResult = null;
            if (string.IsNullOrWhiteSpace(srcDoc) && string.IsNullOrWhiteSpace(src))
            {
                TraceWebDriverFrame($"EnsureFrameRoot no-src frame={DescribeFrameElement(frameElement)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(frameHtml))
            {
                if (!string.IsNullOrWhiteSpace(src) &&
                    !string.Equals(src, "about:blank", StringComparison.OrdinalIgnoreCase) &&
                    !src.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
                    !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(src, UriKind.Absolute, out var absoluteSrc))
                    {
                        frameUri = absoluteSrc;
                    }
                    else if (_current != null && Uri.TryCreate(_current, src, out var relativeSrc))
                    {
                        frameUri = relativeSrc;
                    }

                    if (frameUri != null)
                    {
                        TraceWebDriverFrame($"EnsureFrameRoot fetching uri='{frameUri}' referrer='{_current?.AbsoluteUri ?? "about:blank"}'");
                        try
                        {
                            var result = await _resources.FetchTextDetailedAsync(
                                CreateFrameFetchContext(frameElement, frameUri, _current),
                                "text/html,application/xhtml+xml");
                            frameFetchResult = result;

                            if (result?.Status == FetchStatus.Success && !string.IsNullOrWhiteSpace(result.Content))
                            {
                                frameHtml = result.Content;
                                frameUri = result.FinalUri ?? frameUri;
                            }

                            TraceWebDriverFrame(
                                $"EnsureFrameRoot fetch-result status='{result?.Status.ToString() ?? "<null>"}' finalUri='{result?.FinalUri?.AbsoluteUri ?? frameUri?.AbsoluteUri ?? "<null>"}' contentLen='{result?.Content?.Length ?? 0}'");
                        }
                        catch (Exception ex)
                        {
                            TryLogWarn($"[WebDriverFrame] failed loading frame '{frameUri}': {ex.Message}", LogCategory.Navigation);
                            TraceWebDriverFrame($"EnsureFrameRoot fetch-failed uri='{frameUri?.AbsoluteUri ?? "<null>"}' error='{ex.Message}'");
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(frameHtml))
            {
                frameHtml = "<!doctype html><html><head></head><body></body></html>";
                TraceWebDriverFrame("EnsureFrameRoot fallback-empty-document");
            }

            try
            {
                var parsedDocument = HtmlParser.ParseDocument(
                    frameHtml,
                    new HtmlParserOptions { BaseUri = frameUri });

                var parsedRoot = parsedDocument?.DocumentElement;
                if (parsedRoot == null)
                {
                    TraceWebDriverFrame("EnsureFrameRoot parse-produced-null-root");
                    return;
                }

                while (frameElement.FirstChild != null)
                {
                    frameElement.RemoveChild(frameElement.FirstChild);
                }

                var executionOptions = CreateFrameExecutionOptions(frameFetchResult, frameUri, parsedDocument);
                frameElement.AppendChild(parsedDocument);
                using (EnterImageLoaderContext())
                {
                    await _engine.PrewarmSubdocumentImagesAsync(parsedRoot, frameUri).ConfigureAwait(false);
                }
                await TryInitializeFrameScriptsAsync(
                    frameElement,
                    parsedRoot,
                    frameUri,
                    executionOptions).ConfigureAwait(false);
                TraceWebDriverFrame(
                    $"EnsureFrameRoot attached parsedRoot='{parsedRoot.TagName}' frameAfter={DescribeFrameElement(frameElement)}");
            }
            catch (Exception ex)
            {
                TryLogWarn($"[WebDriverFrame] failed parsing frame content: {ex.Message}", LogCategory.Navigation);
                TraceWebDriverFrame($"EnsureFrameRoot parse-failed error='{ex.Message}'");
            }
        }

        private Uri ResolveFrameBaseUri(Element frameElement)
        {
            if (frameElement == null)
            {
                return _current;
            }

            var src = frameElement.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(src) ||
                string.Equals(src, "about:blank", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return _current;
            }

            if (Uri.TryCreate(src, UriKind.Absolute, out var absoluteSrc))
            {
                return absoluteSrc;
            }

            if (_current != null && Uri.TryCreate(_current, src, out var relativeSrc))
            {
                return relativeSrc;
            }

            return _current;
        }

        internal static FetchContext CreateFrameFetchContext(
            Element frameElement,
            Uri requestUri,
            Uri topLevelDocumentUri)
        {
            var frameDocumentUri = ResolveOwningDocumentUri(frameElement) ?? topLevelDocumentUri;
            return new FetchContext
            {
                RequestUri = requestUri,
                InitiatorUri = frameDocumentUri,
                FrameDocumentUri = frameDocumentUri,
                TopLevelDocumentUri = topLevelDocumentUri ?? frameDocumentUri,
                Destination = "iframe",
                Mode = "navigate",
                CredentialsMode = "include",
                IsTopLevelNavigation = false,
                IsUserInitiated = false,
                Method = "GET"
            };
        }

        private async Task<BinaryFetchResult> FetchFrameAwareImageAsync(Uri resourceUri, Document ownerDocument)
        {
            if (resourceUri == null)
            {
                return new BinaryFetchResult
                {
                    FailureReason = BinaryFetchFailureReason.InvalidRequest,
                    FailureDetail = "Image URI is null"
                };
            }

            if (ownerDocument == null ||
                !_frameResourceSecurity.TryGetValue(ownerDocument, out var frameContext))
            {
                return await _resources.FetchBytesDetailedAsync(
                        new FetchContext
                        {
                            RequestUri = MapRuntimeUri(resourceUri),
                            InitiatorUri = _current,
                            FrameDocumentUri = _current,
                            TopLevelDocumentUri = _current,
                            Destination = "image",
                            Mode = "no-cors",
                            CredentialsMode = "include",
                            Method = "GET"
                        },
                        BrowserNetworkCapabilities.ImageAcceptHeader)
                    .ConfigureAwait(false);
            }

            if (frameContext.Policy != null &&
                !frameContext.Policy.IsAllowed("img-src", resourceUri, frameContext.DocumentUri))
            {
                return new BinaryFetchResult
                {
                    FinalUri = resourceUri,
                    FailureReason = BinaryFetchFailureReason.CspBlocked,
                    FailureDetail = "Blocked by img-src",
                    CspAllowed = false
                };
            }

            return await _resources.FetchBytesDetailedAsync(
                    new FetchContext
                    {
                        RequestUri = MapRuntimeUri(resourceUri),
                        InitiatorUri = frameContext.DocumentUri,
                        FrameDocumentUri = frameContext.DocumentUri,
                        TopLevelDocumentUri = _current ?? frameContext.DocumentUri,
                        Destination = "image",
                        Mode = "no-cors",
                        CredentialsMode = "include",
                        ReferrerPolicy = frameContext.ReferrerPolicy,
                        IsTopLevelNavigation = false,
                        IsUserInitiated = false,
                        Method = "GET"
                    },
                    BrowserNetworkCapabilities.ImageAcceptHeader)
                .ConfigureAwait(false);
        }

        private async Task<BinaryFetchResult> FetchFrameAwareFontAsync(Uri resourceUri, Document ownerDocument)
        {
            if (resourceUri == null)
            {
                return new BinaryFetchResult
                {
                    FailureReason = BinaryFetchFailureReason.InvalidRequest,
                    FailureDetail = "Font URI is null"
                };
            }

            FrameResourceSecurityContext frameContext = null;
            var isFrameDocument = ownerDocument != null &&
                _frameResourceSecurity.TryGetValue(ownerDocument, out frameContext);
            var documentUri = isFrameDocument ? frameContext.DocumentUri : _current;
            var policy = isFrameDocument ? frameContext.Policy : CurrentPolicy;
            if (policy != null && !policy.IsAllowed("font-src", resourceUri, documentUri))
            {
                return new BinaryFetchResult
                {
                    FinalUri = resourceUri,
                    FailureReason = BinaryFetchFailureReason.CspBlocked,
                    FailureDetail = "Blocked by font-src",
                    CspAllowed = false
                };
            }

            return await _resources.FetchBytesDetailedAsync(
                    new FetchContext
                    {
                        RequestUri = MapRuntimeUri(resourceUri),
                        InitiatorUri = documentUri,
                        FrameDocumentUri = documentUri,
                        TopLevelDocumentUri = _current ?? documentUri,
                        Destination = "font",
                        Mode = "cors",
                        CredentialsMode = "same-origin",
                        ReferrerPolicy = isFrameDocument
                            ? frameContext.ReferrerPolicy
                            : ReferrerPolicyDirective.StrictOriginWhenCrossOrigin,
                        IsTopLevelNavigation = false,
                        IsUserInitiated = false,
                        Method = "GET"
                    },
                    accept: "*/*")
                .ConfigureAwait(false);
        }

        private BrowserFrameExecutionOptions CreateFrameExecutionOptions(
            FetchResult result,
            Uri frameUri,
            Document frameDocument = null)
        {
            if (frameUri == null)
            {
                return null;
            }

            CspPolicy framePolicy = null;
            if (result?.Headers != null &&
                result.Headers.TryGetValues("Content-Security-Policy", out var cspValues))
            {
                framePolicy = CspPolicy.Parse(string.Join(";", cspValues));
            }

            var referrerPolicy = result?.ReferrerPolicy ?? ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;
            var topLevelUri = _current ?? frameUri;
            if (frameDocument != null)
            {
                _frameResourceSecurity.Remove(frameDocument);
                _frameResourceSecurity.Add(
                    frameDocument,
                    new FrameResourceSecurityContext(frameUri, framePolicy, referrerPolicy));
            }

            return new BrowserFrameExecutionOptions
            {
                SubresourceAllowed = (resourceUri, kind) =>
                {
                    if (framePolicy == null)
                    {
                        return true;
                    }

                    var directive = kind switch
                    {
                        "script" => "script-src",
                        "style" => "style-src",
                        "img" => "img-src",
                        "font" => "font-src",
                        "media" => "media-src",
                        "connect" => "connect-src",
                        "frame" => "frame-src",
                        "object" => "object-src",
                        _ => "default-src"
                    };
                    return framePolicy.IsAllowed(directive, resourceUri, frameUri);
                },
                NonceAllowed = nonce => framePolicy == null ||
                    framePolicy.IsAllowed("script-src", null, nonce, frameUri, isInline: true),
                ExternalScriptFetcher = async (resourceUri, _) =>
                {
                    var mappedUri = MapRuntimeUri(resourceUri);
                    var scriptResult = await _resources.FetchTextDetailedAsync(
                        new FetchContext
                        {
                            RequestUri = mappedUri,
                            InitiatorUri = frameUri,
                            FrameDocumentUri = frameUri,
                            TopLevelDocumentUri = topLevelUri,
                            Destination = "script",
                            Mode = "no-cors",
                            CredentialsMode = "include",
                            ReferrerPolicy = referrerPolicy,
                            IsTopLevelNavigation = false,
                            IsUserInitiated = false,
                            Method = "GET"
                        }).ConfigureAwait(false);
                    return scriptResult?.Status == FetchStatus.Success ? scriptResult.Content : null;
                },
                FetchHandler = request =>
                {
                    request.RequestUri = MapRuntimeUri(request.RequestUri);
                    string Header(string name) => request.Headers.TryGetValues(name, out var values)
                        ? values.FirstOrDefault()
                        : null;
                    return _resources.SendAsync(request, framePolicy, new FetchContext
                    {
                        RequestUri = request.RequestUri,
                        InitiatorUri = frameUri,
                        FrameDocumentUri = frameUri,
                        TopLevelDocumentUri = topLevelUri,
                        Destination = Header("Sec-Fetch-Dest") ?? "empty",
                        Mode = Header("Sec-Fetch-Mode") ?? "cors",
                        CredentialsMode = "same-origin",
                        ReferrerPolicy = referrerPolicy,
                        IsTopLevelNavigation = false,
                        IsUserInitiated = false,
                        Method = request.Method.Method
                    });
                }
            };
        }

        private async Task<string> FetchFrameAwareCssAsync(Element stylesheetRoot, Uri resourceUri)
        {
            if (resourceUri == null)
            {
                return null;
            }

            var document = stylesheetRoot?.OwnerDocument;
            if (document == null || !_frameResourceSecurity.TryGetValue(document, out var frameContext))
            {
                return await _resources.FetchCssAsync(resourceUri).ConfigureAwait(false);
            }

            if (frameContext.Policy != null &&
                !frameContext.Policy.IsAllowed("style-src", resourceUri, frameContext.DocumentUri))
            {
                return null;
            }

            return await _resources.FetchCssAsync(new FetchContext
            {
                RequestUri = MapRuntimeUri(resourceUri),
                InitiatorUri = frameContext.DocumentUri,
                FrameDocumentUri = frameContext.DocumentUri,
                TopLevelDocumentUri = _current ?? frameContext.DocumentUri,
                Destination = "style",
                Mode = "no-cors",
                CredentialsMode = "include",
                ReferrerPolicy = frameContext.ReferrerPolicy,
                IsTopLevelNavigation = false,
                IsUserInitiated = false,
                Method = "GET"
            }).ConfigureAwait(false);
        }

        private static Uri ResolveOwningDocumentUri(Element frameElement)
        {
            var document = frameElement?.OwnerDocument;
            foreach (var candidate in new[] { document?.URL, document?.DocumentURI, document?.BaseURI })
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                {
                    return uri;
                }
            }

            return null;
        }

        private async Task TryInitializeFrameScriptsAsync(
            Element frameElement,
            Element frameRoot,
            Uri frameUri,
            BrowserFrameExecutionOptions options = null)
        {
            if (frameElement == null || frameRoot == null)
            {
                return;
            }

            if (IsFrameScriptsHydratedForUri(frameElement, frameUri))
            {
                return;
            }

            var jsEngine = _engine?.ScriptEngine;
            if (jsEngine == null)
            {
                return;
            }

            try
            {
                if (jsEngine is FenJsBrowserScriptEngine fenJsEngine)
                {
                    await fenJsEngine.SetSubdocumentDomAsync(frameRoot, frameUri, options).ConfigureAwait(false);
                }
                else
                {
                    await jsEngine.SetDomAsync(frameRoot, frameUri).ConfigureAwait(false);
                }

                frameElement.SetAttribute(WebDriverFrameScriptsHydratedAttribute, "1");
                frameElement.SetAttribute(WebDriverFrameScriptsHydratedUrlAttribute, frameUri?.AbsoluteUri ?? string.Empty);
            }
            catch (Exception ex)
            {
                TryLogWarn($"[WebDriverFrame] failed initializing frame scripts: {ex.Message}", LogCategory.Navigation);
            }
        }

        private static bool IsFrameScriptsHydratedForUri(Element frameElement, Uri frameUri)
        {
            if (frameElement == null ||
                !string.Equals(frameElement.GetAttribute(WebDriverFrameScriptsHydratedAttribute), "1", StringComparison.Ordinal))
            {
                return false;
            }

            var hydratedUrl = frameElement.GetAttribute(WebDriverFrameScriptsHydratedUrlAttribute) ?? string.Empty;
            var requestedUrl = frameUri?.AbsoluteUri ?? string.Empty;
            return string.Equals(hydratedUrl, requestedUrl, StringComparison.Ordinal);
        }

        private void SyncScriptContextToSelectedBrowsingContext()
        {
            var jsEngine = _engine?.ScriptEngine;
            if (jsEngine == null)
            {
                return;
            }

            Node activeRoot = null;
            var baseUri = _current;

            if (_currentFrameElement != null)
            {
                activeRoot = ResolveFrameSearchRoot(_currentFrameElement);
                baseUri = ResolveFrameBaseUri(_currentFrameElement) ?? _current;
            }
            else
            {
                activeRoot = GetDomRoot();
                if (activeRoot == null)
                {
                    activeRoot = _engine.GetActiveDom();
                }
            }

            if (activeRoot == null)
            {
                return;
            }

            jsEngine.SyncDomContext(activeRoot, baseUri);
        }

        private static string DescribeFrameElement(Element frameElement)
        {
            if (frameElement == null)
            {
                return "<null-frame>";
            }

            var id = frameElement.GetAttribute("id") ?? string.Empty;
            var name = frameElement.GetAttribute("name") ?? string.Empty;
            var src = frameElement.GetAttribute("src") ?? string.Empty;
            var childNodes = frameElement.ChildNodes;
            var childCount = childNodes?.Length ?? 0;
            var childTypes = childNodes == null || childNodes.Length == 0
                ? "<none>"
                : string.Join(",", childNodes.Select(child => child?.GetType()?.Name ?? "<null>"));

            return $"{frameElement.TagName}#{id} name='{name}' src='{src}' childCount={childCount} childTypes={childTypes}";
        }

        private static void TraceWebDriverFrame(string message)
        {
            if (!WebDriverFrameTraceEnabled)
            {
                return;
            }

            var trace = $"[WebDriverFrameTrace] {message}";
            Console.WriteLine(trace);
            TryLogInfo(trace, LogCategory.Navigation);
        }

        public Task<string> GetActiveElementAsync()
        {
            var searchRoot = ResolveSearchRoot();
            if (_currentFrameElement != null && searchRoot == null)
            {
                return Task.FromResult<string>(null);
            }

            var activeElement = _focusedElement ?? ElementStateManager.Instance.FocusedElement;
            if (activeElement == null)
            {
                activeElement = searchRoot?.OwnerDocument?.ActiveElement;
            }

            if (searchRoot != null && activeElement != null && !IsElementWithinSearchRoot(searchRoot, activeElement))
            {
                activeElement = null;
            }

            if (activeElement == null)
            {
                var activeDocument = searchRoot?.OwnerDocument;
                activeElement = FindBodyElement(activeDocument) ?? activeDocument?.DocumentElement;
            }

            return Task.FromResult(GetOrRegisterElementId(activeElement));
        }

        public Task<string> GetShadowRootAsync(string elementId)
        {
            var element = ResolveElementInActiveContextOrThrow(elementId);
            var marker = "fen-sr-probe-" + Guid.NewGuid().ToString("N");
            try
            {
                element.SetAttribute(WebDriverShadowRootProbeAttribute, marker);
            }
            catch
            {
                marker = null;
            }

            try
            {
                var probe = ExecuteScriptAsync(
                    @"var marker = arguments[0] || '';
                      var probeAttr = arguments[1] || '';
                      var host = null;
                      if (marker && probeAttr) {
                          try {
                              host = Array.prototype.find.call(
                                  document.querySelectorAll('[' + probeAttr + ']'),
                                  function(node) { return node && node.getAttribute(probeAttr) === marker; }) || null;
                          } catch (e) { host = null; }
                      }
                      if (!host) {
                          host = arguments[2] || null;
                      }
                      if (!host) return null;
                      try {
                          if (window.customElements &&
                              typeof window.customElements.upgrade === 'function') {
                              window.customElements.upgrade(host);
                          }
                      } catch (e) {}
                      if (host && host.shadowRoot) {
                          return host.shadowRoot;
                      }
                      if (window._shadowRoot && window._shadowRoot.host === host) {
                          return window._shadowRoot;
                      }
                      return null;",
                    new object[] { marker ?? string.Empty, WebDriverShadowRootProbeAttribute, elementId }).GetAwaiter().GetResult();
                if (probe is string shadowToken &&
                    shadowToken.StartsWith(WebDriverShadowTokenPrefix, StringComparison.Ordinal))
                {
                    var resolvedShadowId = shadowToken.Substring(WebDriverShadowTokenPrefix.Length);
                    if (!string.IsNullOrWhiteSpace(resolvedShadowId))
                    {
                        return Task.FromResult(resolvedShadowId);
                    }
                }

                if (probe is FenBrowser.FenEngine.Core.Interfaces.IObject probeObject &&
                    TryExtractShadowRootFromWrapper(probeObject, out var probeShadowRoot))
                {
                    var probeShadowId = GetOrRegisterShadowRootId(probeShadowRoot);
                    if (!string.IsNullOrWhiteSpace(probeShadowId))
                    {
                        return Task.FromResult(probeShadowId);
                    }
                }
            }
            catch
            {
                // Keep deterministic null behavior for no-shadow-root cases.
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(marker))
                {
                    try
                    {
                        element.RemoveAttribute(WebDriverShadowRootProbeAttribute);
                    }
                    catch
                    {
                    }
                }
            }

            var shadowRoot = TryGetAttachedShadowRoot(element);
            if (shadowRoot != null)
            {
                return Task.FromResult(GetOrRegisterShadowRootId(shadowRoot));
            }

            return Task.FromResult<string>(null);
        }

        public Task<bool> IsElementSelectedAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            if (el != null)
            {
                if (el.Attr != null)
                {
                    if (el.Attr.ContainsKey("checked") || el.Attr.ContainsKey("selected"))
                        return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        public Task<string> GetElementAttributeAsync(string elementId, string name)
        {
            _ = ResolveElementInActiveContextOrThrow(elementId);
            return GetElementAttributeViaScriptAsync(elementId, name);
        }

        public Task<object> GetElementPropertyAsync(string elementId, string name)
        {
            // In-memory fast path: when the element is registered in our map and the
            // property maps directly to a DOM attribute we can resolve it synchronously
            // without booting the script engine or running the stale-element check.
            if (!string.IsNullOrEmpty(elementId) && !string.IsNullOrEmpty(name) &&
                _elementMap.TryGetValue(elementId, out var registered))
            {
                if (registered.HasAttribute(name))
                {
                    return Task.FromResult<object>(registered.GetAttribute(name));
                }
                return Task.FromResult<object>(null);
            }

            _ = ResolveElementInActiveContextOrThrow(elementId);
            return ExecuteElementPropertyScriptAsync(elementId, name);
        }

        private async Task<object> ExecuteElementPropertyScriptAsync(string elementId, string name)
        {
            var jsonName = JsonSerializer.Serialize(name ?? string.Empty);
            var script = $"return arguments[0] == null ? null : arguments[0][{jsonName}];";
            try
            {
                return await ExecuteScriptAsync(script, new object[] { elementId }).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.IndexOf("TypeError", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }
        }

        public Task<string> GetElementCssValueAsync(string elementId, string property)
        {
            // CSS computation would require CssComputed lookup
            return Task.FromResult<string>(null);
        }

        public Task<string> GetElementTextAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            if (el != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var n in el.SelfAndDescendants())
                {
                    if (n.IsText() && !string.IsNullOrWhiteSpace(n.TextContent))
                        sb.Append(n.TextContent.Trim()).Append(" ");
                }
                return Task.FromResult(sb.ToString().Trim());
            }
            return Task.FromResult("");
        }

        public Task<string> GetElementTagNameAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            if (el != null)
                return Task.FromResult(el.TagName?.ToLowerInvariant() ?? "");
            return Task.FromResult("");
        }

        public async Task<ElementRect> GetElementRectAsync(string elementId)
        {
            var element = ResolveElementInActiveContextOrThrow(elementId);
            if (element == null)
            {
                return new ElementRect { X = 0, Y = 0, Width = 0, Height = 0 };
            }

            await RefreshWebDriverLayoutAsync().ConfigureAwait(false);
            var layout = _engine?.LastLayout;

            var scriptRect = await TryResolveElementRectViaScriptAsync(elementId).ConfigureAwait(false);
            if (scriptRect != null)
            {
                return scriptRect;
            }

            if (layout == null)
            {
                if (FenBrowser.FenEngine.Scripting.JavaScriptEngine.TryGetVisualRect(element, out var vx, out var vy, out var vw, out var vh))
                {
                    return new ElementRect
                    {
                        X = vx,
                        Y = vy,
                        Width = Math.Max(0, vw),
                        Height = Math.Max(0, vh)
                    };
                }

                return new ElementRect { X = 0, Y = 0, Width = 0, Height = 0 };
            }

            if (!layout.TryGetElementRect(element, out var geo))
            {
                var domId = GetStableDomElementId(element);

                var activeDom = _engine.GetActiveDom();
                var root = (activeDom as Element) ?? (activeDom as Document)?.DocumentElement;
                if (root == null || string.IsNullOrWhiteSpace(domId))
                {
                    return new ElementRect { X = 0, Y = 0, Width = 0, Height = 0 };
                }

                var matched = root
                    .SelfAndDescendants()
                    .OfType<Element>()
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.GetAttribute("id"), domId, StringComparison.Ordinal) &&
                        string.Equals(candidate.TagName, element.TagName, StringComparison.OrdinalIgnoreCase));

                if (matched == null || !layout.TryGetElementRect(matched, out geo))
                {
                    if (FenBrowser.FenEngine.Scripting.JavaScriptEngine.TryGetVisualRect(element, out var vx, out var vy, out var vw, out var vh))
                    {
                        return new ElementRect
                        {
                            X = vx,
                            Y = vy,
                            Width = Math.Max(0, vw),
                            Height = Math.Max(0, vh)
                        };
                    }

                    return new ElementRect { X = 0, Y = 0, Width = 0, Height = 0 };
                }
            }

            var width = Math.Max(0, geo.Right - geo.Left);
            var height = Math.Max(0, geo.Bottom - geo.Top);
            return new ElementRect
            {
                X = geo.Left,
                Y = geo.Top - layout.ScrollOffsetY,
                Width = width,
                Height = height
            };
        }

        private async Task RefreshWebDriverLayoutAsync()
        {
            // WebDriver element geometry and the in-view click center must reflect
            // DOM/style mutations that occurred after the initial page render.
            // Headless BrowserHost consumers do not have BrowserIntegration's
            // RepaintReady subscriber, so explicitly flush the active render tree.
            await _engine.FlushPendingLayoutAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Flushes scheduled style and layout work for headless diagnostics and automation.
        /// </summary>
        public Task FlushPendingLayoutAsync() => _engine.FlushPendingLayoutAsync();

        public Task<bool> IsElementEnabledAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            if (el != null)
            {
                if (el.Attr != null && el.Attr.ContainsKey("disabled"))
                    return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        public Task<string> GetElementComputedRoleAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            if (el != null)
            {
                var doc = el.OwnerDocument;
                var role = FenBrowser.Core.Accessibility.AccessibilityRole.ResolveRole(el, doc);
                // None / Generic â†’ empty string (no meaningful ARIA role)
                if (role == FenBrowser.Core.Accessibility.AriaRole.None ||
                    role == FenBrowser.Core.Accessibility.AriaRole.Generic)
                    return Task.FromResult("");
                return Task.FromResult(role.ToString().ToLowerInvariant());
            }
            return Task.FromResult("");
        }

        public Task<string> GetElementComputedLabelAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            if (el != null)
            {
                var doc = el.OwnerDocument;
                var name = FenBrowser.Core.Accessibility.AccNameCalculator.Compute(el, doc);
                return Task.FromResult(name);
            }
            return Task.FromResult("");
        }

        public Task ClearElementAsync(string elementId)
        {
            var el = ResolveElementInActiveContextOrThrow(elementId);
            var tag = (el?.NodeName ?? el?.TagName ?? string.Empty).ToLowerInvariant();

            if (el == null)
            {
                throw new InvalidOperationException("no such element");
            }

            if (IsDisabledControl(el))
            {
                throw new InvalidOperationException("invalid element state");
            }

            if (HasReadonlyStateForClear(el, tag))
            {
                throw new InvalidOperationException("invalid element state");
            }

            if (!TryResolveClearReplacement(el, tag, out var replacementValue, out var replacementTextContent))
            {
                throw new InvalidOperationException("invalid element state");
            }

            if (!CanClearElementAtCurrentViewport(el))
            {
                throw new InvalidOperationException("element not interactable");
            }

            if (!WillClearChangeElement(el, tag, replacementValue, replacementTextContent))
            {
                return Task.CompletedTask;
            }

            var eventContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            DispatchDomEvent(el, "focus", eventContext, bubbles: false);
            SetFocusedElementState(el);

            var changed = ApplyElementClear(el, tag, replacementValue, replacementTextContent);
            if (changed)
            {
                DispatchDomEvent(el, "change", eventContext, bubbles: true);
            }

            DispatchDomEvent(el, "blur", eventContext, bubbles: false);
            var body = FindBodyElement(el.OwnerDocument);
            SetFocusedElementState(body);
            TryInvokeRepaintReady(_engine.GetActiveDom());

            return Task.CompletedTask;
        }

        private bool CanClearElementAtCurrentViewport(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (TryGetElementClickClientPoint(element, out var clickX, out var clickY))
            {
                var viewportRect = GetWindowRect();
                if (clickX < 0 || clickY < 0 || clickX >= viewportRect.Width || clickY >= viewportRect.Height)
                {
                    return false;
                }
            }

            if (FenBrowser.FenEngine.Scripting.JavaScriptEngine.TryGetVisualRect(element, out var vx, out var vy, out var vw, out var vh))
            {
                if (vw > 0 && vh > 0)
                {
                    var viewport = GetWindowRect();
                    if ((vx + vw) <= 0 || (vy + vh) <= 0 || vx >= viewport.Width || vy >= viewport.Height)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool HasReadonlyStateForClear(Element element, string loweredTag)
        {
            if (element == null)
            {
                return false;
            }

            if (!string.Equals(loweredTag, "input", StringComparison.Ordinal) &&
                !string.Equals(loweredTag, "textarea", StringComparison.Ordinal))
            {
                return false;
            }

            return element.HasAttribute("readonly");
        }

        private bool TryResolveClearReplacement(
            Element element,
            string loweredTag,
            out string replacementValue,
            out string replacementTextContent)
        {
            replacementValue = string.Empty;
            replacementTextContent = string.Empty;

            if (element == null)
            {
                return false;
            }

            if (string.Equals(loweredTag, "textarea", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(loweredTag, "input", StringComparison.Ordinal))
            {
                var type = element.GetAttribute("type")?.Trim().ToLowerInvariant() ?? string.Empty;
                switch (type)
                {
                    case "hidden":
                    case "button":
                    case "submit":
                    case "reset":
                    case "checkbox":
                    case "radio":
                    case "image":
                        return false;
                    case "range":
                        replacementValue = "50";
                        return true;
                    case "color":
                        replacementValue = "#000000";
                        return true;
                    default:
                        replacementValue = string.Empty;
                        return true;
                }
            }

            if (IsContentEditableForClear(element))
            {
                replacementValue = null;
                replacementTextContent = string.Empty;
                return true;
            }

            if (IsDocumentDesignModeEnabled())
            {
                replacementValue = null;
                replacementTextContent = string.Empty;
                return true;
            }

            return false;
        }

        private static bool IsContentEditableForClear(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (!element.HasAttribute("contenteditable"))
            {
                return false;
            }

            var value = element.GetAttribute("contenteditable");
            return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsDocumentDesignModeEnabled()
        {
            try
            {
                var result = ExecuteScriptAsync(
                    "return ((document && document.designMode) || '').toLowerCase() === 'on';",
                    Array.Empty<object>()).GetAwaiter().GetResult();
                return result is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static bool WillClearChangeElement(
            Element element,
            string loweredTag,
            string replacementValue,
            string replacementTextContent)
        {
            if (element == null)
            {
                return false;
            }

            if (string.Equals(loweredTag, "textarea", StringComparison.Ordinal) ||
                string.Equals(loweredTag, "input", StringComparison.Ordinal))
            {
                var previous = GetTextEntryValue(element);
                var replacement = replacementValue ?? string.Empty;
                return !string.Equals(previous, replacement, StringComparison.Ordinal);
            }

            if (IsContentEditableForClear(element))
            {
                var previous = element.TextContent ?? string.Empty;
                var replacement = replacementTextContent ?? string.Empty;
                return !string.Equals(previous, replacement, StringComparison.Ordinal);
            }

            if (replacementValue == null)
            {
                var previous = element.TextContent ?? string.Empty;
                var replacement = replacementTextContent ?? string.Empty;
                return !string.Equals(previous, replacement, StringComparison.Ordinal);
            }

            return false;
        }

        private static bool ApplyElementClear(
            Element element,
            string loweredTag,
            string replacementValue,
            string replacementTextContent)
        {
            if (element == null)
            {
                return false;
            }

            if (string.Equals(loweredTag, "textarea", StringComparison.Ordinal) ||
                string.Equals(loweredTag, "input", StringComparison.Ordinal))
            {
                var previous = GetTextEntryValue(element);
                if (string.Equals(loweredTag, "input", StringComparison.Ordinal) && IsFileInputElement(element))
                {
                    element.RemoveAttribute(WebDriverUploadedFilesAttribute);
                }
                SetTextEntryValue(element, replacementValue ?? string.Empty);
                var current = GetTextEntryValue(element);
                return !string.Equals(previous, current, StringComparison.Ordinal);
            }

            if (IsContentEditableForClear(element) || replacementValue == null)
            {
                var previous = element.TextContent ?? string.Empty;
                element.TextContent = replacementTextContent ?? string.Empty;
                var current = element.TextContent ?? string.Empty;
                return !string.Equals(previous, current, StringComparison.Ordinal);
            }

            return false;
        }

        private static Element FindBodyElement(Document document)
        {
            if (document == null)
            {
                return null;
            }

            return document.DocumentElement?
                .SelfAndDescendants()
                .OfType<Element>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.TagName, "body", StringComparison.OrdinalIgnoreCase));
        }

        private bool DispatchDomEvent(
            Element target,
            string type,
            FenBrowser.FenEngine.Core.ExecutionContext context,
            bool bubbles,
            bool cancelable = false,
            FenBrowser.FenEngine.Scripting.BrowserDomEventInit eventInit = null)
        {
            if (target == null || string.IsNullOrWhiteSpace(type))
            {
                return true;
            }

            eventInit ??= new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
            {
                Bubbles = bubbles,
                Cancelable = cancelable,
                Composed = true
            };
            var scriptDefaultAllowed = _engine.DispatchPointerEvent(target, type, eventInit);

            var domEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                type,
                bubbles: bubbles,
                cancelable: cancelable,
                composed: true,
                context: context);

            var legacyDefaultAllowed = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(target, domEvent, context);
            return scriptDefaultAllowed && legacyDefaultAllowed;
        }

        public async Task SendKeysToElementAsync(string elementId, string text, bool strictFileInteractability = false)
        {
            Element el;
            try
            {
                el = ResolveElementInActiveContextOrThrow(elementId);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.IndexOf("no such element", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _elementMap.TryGetValue(elementId, out var fallbackElement) &&
                fallbackElement != null &&
                fallbackElement.IsConnected &&
                IsFrameElement(fallbackElement))
            {
                // Frame references remain valid top-context controls while connected.
                el = fallbackElement;
            }

            if (el == null)
            {
                throw new InvalidOperationException("no such element");
            }

            el = ResolveLiveElementInCurrentSearchRoot(elementId, el);
            var activeSearchRoot = ResolveSearchRoot();
            if (activeSearchRoot != null && !IsElementWithinSearchRoot(activeSearchRoot, el))
            {
                el = ResolveLiveElementBySignature(activeSearchRoot, el) ?? el;
            }

            var tag = el.TagName?.ToLowerInvariant() ?? string.Empty;
            var isFileInput = IsFileInputElement(el);

            if (isFileInput)
            {
                var uploadedFiles = ParseUploadedFilePayload(text);
                if (uploadedFiles.Count == 0)
                {
                    throw new ArgumentException("invalid argument");
                }

                if (strictFileInteractability && IsElementHiddenForInteraction(el))
                {
                    throw new InvalidOperationException("element not interactable");
                }

                foreach (var filePath in uploadedFiles)
                {
                    if (!File.Exists(filePath))
                    {
                        throw new ArgumentException("invalid argument");
                    }
                }

                var allowsMultiple = el.HasAttribute("multiple");
                if (!allowsMultiple && uploadedFiles.Count > 1)
                {
                    throw new ArgumentException("invalid argument");
                }

                var existingFiles = ParseUploadedFileAttribute(el.GetAttribute(WebDriverUploadedFilesAttribute)).ToList();
                var nextFiles = allowsMultiple
                    ? existingFiles.Concat(uploadedFiles).ToList()
                    : new List<string> { uploadedFiles.Last() };

                if (nextFiles.Count > 0)
                {
                    el.SetAttribute(WebDriverUploadedFilesAttribute, string.Join("\n", nextFiles));
                    var lastFileName = Path.GetFileName(nextFiles[nextFiles.Count - 1]) ?? string.Empty;
                    SetTextEntryValue(el, $@"C:\fakepath\{lastFileName}");
                }
                else
                {
                    el.RemoveAttribute(WebDriverUploadedFilesAttribute);
                    SetTextEntryValue(el, string.Empty);
                }

                var eventContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                    ?? new FenBrowser.FenEngine.Core.ExecutionContext();
                DispatchDomEvent(el, "input", eventContext, bubbles: true);
                DispatchDomEvent(el, "change", eventContext, bubbles: true);

                if (strictFileInteractability)
                {
                    SetFocusedElementState(el, fromKeyboard: true);
                }

                TryInvokeRepaintReady(_engine.GetActiveDom());
                return;
            }

            if (!IsElementSendKeysInteractable(el))
            {
                throw new InvalidOperationException("element not interactable");
            }

            var isTextEntry = IsTextEntryElement(el);
            var isContentEditable = IsContentEditableElement(el);
            var isKeyboardTarget = string.Equals(tag, "body", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tag, "html", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tag, "iframe", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tag, "frame", StringComparison.OrdinalIgnoreCase);
            if (!isTextEntry && !isContentEditable && !isKeyboardTarget)
            {
                throw new InvalidOperationException("element not interactable");
            }

            if (string.Equals(tag, "iframe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "frame", StringComparison.OrdinalIgnoreCase))
            {
                await SendKeysToFrameElementAsync(el, elementId, text ?? string.Empty).ConfigureAwait(false);
                return;
            }

            var alreadyFocused = ReferenceEquals(el.OwnerDocument?.ActiveElement, el);
            if (!alreadyFocused)
            {
                var stateFocused = ElementStateManager.Instance.FocusedElement;
                if (ReferenceEquals(stateFocused, el))
                {
                    alreadyFocused = true;
                }
                else if (stateFocused != null)
                {
                    var expectedDomId = el.GetAttribute(WebDriverDomIdAttribute);
                    var focusedDomId = stateFocused.GetAttribute(WebDriverDomIdAttribute);
                    if (!string.IsNullOrWhiteSpace(expectedDomId) &&
                        string.Equals(expectedDomId, focusedDomId, StringComparison.Ordinal))
                    {
                        alreadyFocused = true;
                    }
                    else if (isContentEditable &&
                             string.Equals(stateFocused.TagName, el.TagName, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(stateFocused.TextContent ?? string.Empty, el.TextContent ?? string.Empty, StringComparison.Ordinal))
                    {
                        alreadyFocused = true;
                    }
                }
            }
            if (!alreadyFocused && isContentEditable)
            {
                var documentFocused = el.OwnerDocument?.ActiveElement;
                if (documentFocused != null &&
                    IsContentEditableElement(documentFocused) &&
                    string.Equals(documentFocused.TagName, el.TagName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(documentFocused.TextContent ?? string.Empty, el.TextContent ?? string.Empty, StringComparison.Ordinal))
                {
                    // Keep typing anchored to the actual focused node instance.
                    el = documentFocused;
                    alreadyFocused = true;
                }
            }
            if (!alreadyFocused)
            {
                try
                {
                    var focusedProbe = await ExecuteScriptAsync(
                        $@"if (!(document && document.activeElement && arguments[0])) return false;
                            if (document.activeElement === arguments[0]) return true;
                            var activeId = document.activeElement.getAttribute('{WebDriverDomIdAttribute}');
                            var targetId = arguments[0].getAttribute('{WebDriverDomIdAttribute}');
                            return !!(activeId && targetId && activeId === targetId);",
                        new object[] { el }).ConfigureAwait(false);
                    alreadyFocused = focusedProbe is bool isFocused && isFocused;
                }
                catch
                {
                }
            }
            int? existingContentEditableCaret = null;
            if (isContentEditable)
            {
                existingContentEditableCaret = await TryGetCollapsedContentEditableCaretOffsetAsync(el).ConfigureAwait(false);
                if (existingContentEditableCaret.HasValue)
                {
                    alreadyFocused = true;
                }
            }

            if (!alreadyFocused)
            {
                SetFocusedElementState(el, fromKeyboard: true);
                var focusContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                    ?? new FenBrowser.FenEngine.Core.ExecutionContext();
                DispatchDomEvent(el, "focus", focusContext, bubbles: false);
                var initial = isContentEditable ? (el.TextContent ?? string.Empty) : GetTextEntryValue(el);
                _cursorIndex = initial.Length;
                _selectionAnchor = -1;
            }
            else if (!ReferenceEquals(_focusedElement, el))
            {
                SetFocusedElementState(el, fromKeyboard: true);
            }

            if (alreadyFocused && (tag == "input" || tag == "textarea"))
            {
                try
                {
                    var selection = await ExecuteScriptAsync(
                        "return [Number(arguments[0].selectionStart)||0, Number(arguments[0].selectionEnd)||0];",
                        new object[] { el }).ConfigureAwait(false);
                    if (selection is IList<object> rawSelection && rawSelection.Count >= 2)
                    {
                        var start = Convert.ToInt32(rawSelection[0]);
                        var end = Convert.ToInt32(rawSelection[1]);
                        _cursorIndex = Math.Max(0, end);
                        _selectionAnchor = start == end ? -1 : Math.Max(0, start);
                    }
                }
                catch
                {
                }
            }
            else if (alreadyFocused && isContentEditable)
            {
                if (existingContentEditableCaret.HasValue)
                {
                    _cursorIndex = Math.Max(0, existingContentEditableCaret.Value);
                    _selectionAnchor = -1;
                }
                else
                {
                    // If selection APIs are unavailable, keep deterministic caret behavior
                    // for already-focused contenteditable elements.
                    _cursorIndex = 0;
                    _selectionAnchor = -1;
                }
            }

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var suppressMutation = HasReadonlyStateForClear(el, tag);
            var keyboardFallbackTarget = (!isTextEntry && !isContentEditable && isKeyboardTarget)
                ? FindKeyboardFallbackTarget(el)
                : null;
            foreach (var character in text)
            {
                var key = character.ToString();
                var fallbackBefore = ReadEditableValue(keyboardFallbackTarget);
                await DispatchTypingKeySequenceAsync(
                        el,
                        key,
                        shouldMutateFocusedElement: !suppressMutation && (isTextEntry || isContentEditable))
                    .ConfigureAwait(false);

                if (keyboardFallbackTarget != null &&
                    key.Length == 1 &&
                    string.Equals(ReadEditableValue(keyboardFallbackTarget), fallbackBefore, StringComparison.Ordinal))
                {
                    ApplyKeyboardFallbackTextMutation(keyboardFallbackTarget, key);
                }
            }

            if (isTextEntry && !suppressMutation)
            {
                await SyncCollapsedSelectionRangeAsync(el).ConfigureAwait(false);
            }
        }

        private async Task SendKeysToFrameElementAsync(Element frameElement, string frameElementId, string text)
        {
            frameElement = ResolveLiveElementInCurrentSearchRoot(frameElementId, frameElement);
            SetFocusedElementState(frameElement, fromKeyboard: true);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            await EnsureFrameSearchRootLoadedAsync(frameElement).ConfigureAwait(false);
            var frameRoot = ResolveFrameSearchRoot(frameElement);
            var frameDocument = frameRoot?.OwnerDocument;
            var frameTarget = frameDocument?.ActiveElement ?? FindBodyElement(frameDocument) ?? frameRoot;
            if (frameTarget == null)
            {
                return;
            }

            var keyboardFallbackTarget = FindKeyboardFallbackTarget(frameTarget);

            foreach (var character in text)
            {
                var key = character.ToString();
                var fallbackBefore = ReadEditableValue(keyboardFallbackTarget);
                await DispatchTypingKeySequenceAsync(frameTarget, key, shouldMutateFocusedElement: false)
                    .ConfigureAwait(false);

                if (keyboardFallbackTarget != null &&
                    key.Length == 1 &&
                    string.Equals(ReadEditableValue(keyboardFallbackTarget), fallbackBefore, StringComparison.Ordinal))
                {
                    ApplyKeyboardFallbackTextMutation(keyboardFallbackTarget, key);
                }
            }

            SetFocusedElementState(frameElement, fromKeyboard: true);
        }

        private async Task DispatchTypingKeySequenceAsync(Element eventTarget, string key, bool shouldMutateFocusedElement)
        {
            if (eventTarget == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            var eventContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                ?? new FenBrowser.FenEngine.Core.ExecutionContext();

            var keydownDefaultAllowed = DispatchKeyboardEvent(eventTarget, "keydown", key, eventContext);
            var keypressDefaultAllowed = keydownDefaultAllowed &&
                DispatchKeyboardEvent(eventTarget, "keypress", key, eventContext);

            bool valueChanged = false;
            var inputTarget = _focusedElement ?? eventTarget;
            if (shouldMutateFocusedElement && keydownDefaultAllowed && keypressDefaultAllowed)
            {
                var keyCode = string.IsNullOrEmpty(key) ? 0 : key[0];
                var beforeInputDefaultAllowed = DispatchDomEvent(
                    inputTarget,
                    "beforeinput",
                    eventContext,
                    bubbles: true,
                    cancelable: true,
                    eventInit: new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
                    {
                        Bubbles = true,
                        Cancelable = true,
                        Composed = true,
                        Data = key,
                        InputType = "insertText",
                        Key = key,
                        KeyCode = keyCode
                    });
                if (!beforeInputDefaultAllowed)
                {
                    DispatchKeyboardEvent(eventTarget, "keyup", key, eventContext);
                    return;
                }

                var beforeValue = ReadEditableValue(_focusedElement);
                await HandleKeyPress(key).ConfigureAwait(false);
                var afterValue = ReadEditableValue(_focusedElement);
                valueChanged = !string.Equals(beforeValue, afterValue, StringComparison.Ordinal);
            }

            if (valueChanged)
            {
                DispatchDomEvent(
                    _focusedElement ?? eventTarget,
                    "input",
                    eventContext,
                    bubbles: true,
                    cancelable: false,
                    eventInit: new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
                    {
                        Bubbles = true,
                        Cancelable = false,
                        Composed = true,
                        Data = key,
                        InputType = "insertText",
                        Key = key,
                        KeyCode = string.IsNullOrEmpty(key) ? 0 : key[0]
                    });
            }

            DispatchKeyboardEvent(eventTarget, "keyup", key, eventContext);
        }

        private async Task SyncCollapsedSelectionRangeAsync(Element element)
        {
            if (element == null)
            {
                return;
            }

            var tag = element.TagName?.ToLowerInvariant();
            if (!string.Equals(tag, "input", StringComparison.Ordinal) &&
                !string.Equals(tag, "textarea", StringComparison.Ordinal))
            {
                return;
            }

            var cursor = Math.Max(0, _cursorIndex);
            _selectionAnchor = -1;
            try
            {
                await ExecuteScriptAsync(
                    "if (arguments[0] && typeof arguments[0].setSelectionRange === 'function') { arguments[0].setSelectionRange(arguments[1], arguments[1]); }",
                    new object[] { element, cursor }).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task<int?> TryGetCollapsedContentEditableCaretOffsetAsync(Element element)
        {
            if (element == null || !IsContentEditableElement(element))
            {
                return null;
            }

            try
            {
                var selection = await ExecuteScriptAsync(
                    @"if (!arguments[0] || !window.getSelection) return null;
                      var sel = window.getSelection();
                      if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return null;
                      var range = sel.getRangeAt(0);
                      var node = range.startContainer;
                      if (!node || !arguments[0].contains(node)) return null;
                      var prefix = document.createRange();
                      prefix.selectNodeContents(arguments[0]);
                      prefix.setEnd(node, range.startOffset);
                      return Number(prefix.toString().length) || 0;",
                    new object[] { element }).ConfigureAwait(false);

                return selection switch
                {
                    int i => Math.Max(0, i),
                    long l => Math.Max(0, (int)l),
                    double d => Math.Max(0, (int)Math.Floor(d)),
                    float f => Math.Max(0, (int)Math.Floor(f)),
                    decimal m => Math.Max(0, (int)Math.Floor(m)),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private bool DispatchKeyboardEvent(
            Element target,
            string type,
            string key,
            FenBrowser.FenEngine.Core.ExecutionContext context)
        {
            if (target == null || string.IsNullOrWhiteSpace(type))
            {
                return true;
            }

            var keyCode = string.IsNullOrEmpty(key) ? 0 : key[0];
            var scriptDefaultAllowed = _engine.DispatchPointerEvent(
                target,
                type,
                new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
                {
                    Bubbles = true,
                    Cancelable = true,
                    Composed = true,
                    Key = key ?? string.Empty,
                    KeyCode = keyCode
                });

            var domEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                type,
                bubbles: true,
                cancelable: true,
                composed: true,
                context: context);
            domEvent.Set("key", FenBrowser.FenEngine.Core.FenValue.FromString(key ?? string.Empty));

            domEvent.Set("which", FenBrowser.FenEngine.Core.FenValue.FromNumber(keyCode));
            domEvent.Set("keyCode", FenBrowser.FenEngine.Core.FenValue.FromNumber(keyCode));

            FenBrowser.FenEngine.Core.Interfaces.IValue previousWindowEvent = FenBrowser.FenEngine.Core.FenValue.Undefined;
            FenBrowser.FenEngine.Core.Interfaces.IValue previousGlobalEvent = FenBrowser.FenEngine.Core.FenValue.Undefined;
            var windowValue = context?.Environment?.Get("window") ?? FenBrowser.FenEngine.Core.FenValue.Undefined;
            if (windowValue.IsObject)
            {
                var windowObject = windowValue.AsObject();
                previousWindowEvent = windowObject.Get("event");
                windowObject.Set("event", FenBrowser.FenEngine.Core.FenValue.FromObject(domEvent));
            }
            if (context?.Environment != null)
            {
                previousGlobalEvent = context.Environment.Get("event");
                context.Environment.Set("event", FenBrowser.FenEngine.Core.FenValue.FromObject(domEvent));
            }

            var legacyDefaultAllowed = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(target, domEvent, context);

            if (windowValue.IsObject)
            {
                windowValue.AsObject().Set("event", previousWindowEvent);
            }
            if (context?.Environment != null)
            {
                context.Environment.Set("event", previousGlobalEvent);
            }

            return scriptDefaultAllowed && legacyDefaultAllowed;
        }

        private static string ReadEditableValue(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            var tag = element.NodeName?.ToLowerInvariant();
            if (tag == "input" || tag == "textarea")
            {
                return GetTextEntryValue(element);
            }

            if (IsContentEditableElement(element))
            {
                return element.TextContent ?? string.Empty;
            }

            return string.Empty;
        }

        private Element ResolveLiveElementInCurrentSearchRoot(string elementId, Element fallback)
        {
            if (string.IsNullOrWhiteSpace(elementId))
            {
                return fallback;
            }

            var searchRoot = ResolveSearchRoot();
            if (searchRoot == null)
            {
                return fallback;
            }

            var expectedTag = fallback?.TagName ?? fallback?.NodeName;
            var remapped = searchRoot
                .SelfAndDescendants()
                .OfType<Element>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.GetAttribute(WebDriverDomIdAttribute), elementId, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(expectedTag) ||
                     string.Equals(candidate.TagName ?? candidate.NodeName, expectedTag, StringComparison.OrdinalIgnoreCase)));

            return remapped ?? fallback;
        }

        private static Element ResolveLiveElementBySignature(Element searchRoot, Element fallback)
        {
            if (searchRoot == null || fallback == null)
            {
                return null;
            }

            var expectedTag = fallback.TagName ?? fallback.NodeName;
            var expectedText = fallback.TextContent ?? string.Empty;
            var expectedContentEditable = IsContentEditableElement(fallback);
            var expectedFrame = IsFrameElement(fallback);
            var expectedSrc = fallback.GetAttribute("src") ?? string.Empty;

            return searchRoot
                .SelfAndDescendants()
                .OfType<Element>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.TagName ?? candidate.NodeName, expectedTag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.TextContent ?? string.Empty, expectedText, StringComparison.Ordinal) &&
                    (!expectedContentEditable || IsContentEditableElement(candidate)) &&
                    (!expectedFrame || (IsFrameElement(candidate) &&
                                        string.Equals(candidate.GetAttribute("src") ?? string.Empty, expectedSrc, StringComparison.Ordinal))));
        }

        private static Element FindKeyboardFallbackTarget(Element keyboardTarget)
        {
            if (keyboardTarget == null)
            {
                return null;
            }

            if (IsTextEntryElement(keyboardTarget))
            {
                return keyboardTarget;
            }

            return keyboardTarget
                .SelfAndDescendants()
                .OfType<Element>()
                .FirstOrDefault(candidate => IsTextEntryElement(candidate) && !HasReadonlyStateForClear(candidate, candidate.TagName?.ToLowerInvariant()));
        }

        private void ApplyKeyboardFallbackTextMutation(Element target, string key)
        {
            if (target == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            var value = GetTextEntryValue(target);
            SetTextEntryValue(target, value + key);
            var eventContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            DispatchDomEvent(target, "input", eventContext, bubbles: true);
            TryInvokeRepaintReady(_engine.GetActiveDom());
        }

        private static IReadOnlyList<string> ParseUploadedFilePayload(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            return text
                .Split('\n')
                .Select(part => part?.Trim().Trim('\r'))
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
        }

        private static IReadOnlyList<string> ParseUploadedFileAttribute(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            return raw
                .Split('\n')
                .Select(part => part?.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
        }

        private static bool IsFileInputElement(Element element)
        {
            if (element == null || !string.Equals(element.NodeName, "INPUT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = element.GetAttribute("type");
            return string.Equals(type, "file", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsContentEditableElement(Element element)
        {
            if (element == null || !element.HasAttribute("contenteditable"))
            {
                return false;
            }

            var value = element.GetAttribute("contenteditable");
            return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsElementSendKeysInteractable(Element element)
        {
            if (element == null || IsDisabledControl(element))
            {
                return false;
            }

            return !IsElementHiddenForInteraction(element);
        }

        private bool IsElementHiddenForInteraction(Element element)
        {
            if (element == null)
            {
                return true;
            }

            if (element.HasAttribute("hidden"))
            {
                return true;
            }

            var inlineStyle = element.GetAttribute("style") ?? string.Empty;
            var styleNormalized = inlineStyle.ToLowerInvariant();
            if (styleNormalized.Contains("display:none") || styleNormalized.Contains("display: none"))
            {
                return true;
            }

            if (styleNormalized.Contains("visibility:hidden") || styleNormalized.Contains("visibility: hidden"))
            {
                return true;
            }

            var computedStyles = _engine?.LastComputedStyles;
            if (computedStyles != null && computedStyles.TryGetValue(element, out var style) && style != null)
            {
                if (string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(style.Visibility, "hidden", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(style.Visibility, "collapse", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public Task<string> GetPageSourceAsync()
        {
            // Serialize DOM back to HTML using Element.OuterHTML
            var searchRoot = ResolveSearchRoot();
            if (_currentFrameElement != null && searchRoot == null)
            {
                return Task.FromResult(string.Empty);
            }

            if (searchRoot != null)
            {
                return Task.FromResult(searchRoot.OuterHTML ?? string.Empty);
            }

            var dom = _engine.GetActiveDom();
            if (dom != null)
            {
                return Task.FromResult((dom as Element)?.OuterHTML ?? (dom as Document)?.DocumentElement?.OuterHTML ?? "");
            }

            return Task.FromResult("<html></html>");
        }

        private static bool IsElementWithinSearchRoot(Element searchRoot, Element candidate)
        {
            if (searchRoot == null || candidate == null)
            {
                return false;
            }

            var candidateFrame = GetContainingFrameElement(candidate);
            var searchRootFrame = GetContainingFrameElement(searchRoot);
            if (!ReferenceEquals(candidateFrame, searchRootFrame))
            {
                return false;
            }

            var composedOptions = new GetRootNodeOptions { Composed = true };
            if (!ReferenceEquals(candidate.GetRootNode(composedOptions), searchRoot.GetRootNode(composedOptions)))
            {
                return false;
            }

            Node cursor = candidate;
            while (cursor != null)
            {
                if (ReferenceEquals(cursor, searchRoot))
                {
                    return true;
                }

                if (cursor is ShadowRoot shadowRoot)
                {
                    cursor = shadowRoot.Host;
                    continue;
                }

                cursor = cursor.ParentNode;
            }

            return false;
        }

        private static Element GetContainingFrameElement(Node node)
        {
            Node cursor = node;
            if (cursor is Element selfElement && IsFrameElement(selfElement))
            {
                // The containing frame is an ancestor frame, not the element itself.
                cursor = selfElement.ParentNode;
            }
            while (cursor != null)
            {
                if (cursor is Element element && IsFrameElement(element))
                {
                    return element;
                }

                if (cursor is ShadowRoot shadowRoot)
                {
                    cursor = shadowRoot.Host;
                    continue;
                }

                cursor = cursor.ParentNode;
            }

            return null;
        }

        private string GetCurrentBrowsingContextToken()
        {
            if (_currentFrameElement == null)
            {
                // Top-level browsing context identity must remain stable while the
                // context is open; hash-based root identity is too volatile and causes
                // false cross-context rejections during frame operations.
                return "top";
            }

            foreach (var entry in _elementMap)
            {
                if (ReferenceEquals(entry.Value, _currentFrameElement))
                {
                    return "frame:" + entry.Key;
                }
            }

            var domId = _currentFrameElement.GetAttribute(WebDriverDomIdAttribute);
            if (!string.IsNullOrWhiteSpace(domId))
            {
                return "frame-dom:" + domId;
            }

            return "frame-obj:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_currentFrameElement);
        }

        private bool IsElementReferenceInCurrentBrowsingContext(string elementId)
        {
            if (string.IsNullOrWhiteSpace(elementId))
            {
                return false;
            }

            if (!_elementBrowsingContextMap.TryGetValue(elementId, out var elementContext) || string.IsNullOrWhiteSpace(elementContext))
            {
                return true;
            }

            var currentContext = GetCurrentBrowsingContextToken();
            return string.Equals(elementContext, currentContext, StringComparison.Ordinal);
        }

        private Element ResolveElementInActiveContext(string elementId)
        {
            if (string.IsNullOrWhiteSpace(elementId) || !_elementMap.TryGetValue(elementId, out var element))
            {
                return null;
            }

            if (!IsElementReferenceInCurrentBrowsingContext(elementId))
            {
                return null;
            }

            var searchRoot = ResolveSearchRoot();
            if (searchRoot == null)
            {
                return null;
            }

            if (IsElementWithinSearchRoot(searchRoot, element))
            {
                return element;
            }

            return null;
        }

        private Element ResolveElementInActiveContextOrThrow(string elementId)
        {
            if (string.IsNullOrWhiteSpace(elementId))
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            if (_elementMap.TryGetValue(elementId, out var mappedElement) && mappedElement != null)
            {
                if (!IsElementReferenceInCurrentBrowsingContext(elementId))
                {
                    throw new InvalidOperationException("no such element");
                }

                var mappedSearchRoot = ResolveSearchRoot();

                // Without an active browsing context (no DOM loaded — common in unit
                // tests that register elements directly via reflection), trust the map
                // and skip stale/within-search-root validation.
                if (mappedSearchRoot == null)
                {
                    if (_currentFrameElement != null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }
                    return mappedElement;
                }

                if (!mappedElement.IsConnected)
                {
                    throw new InvalidOperationException("stale element reference");
                }

                if (!IsElementWithinSearchRoot(mappedSearchRoot, mappedElement))
                {
                    throw new InvalidOperationException("no such element");
                }

                return mappedElement;
            }

            var searchRoot = ResolveSearchRoot();
            if (searchRoot == null)
            {
                if (_currentFrameElement != null)
                {
                    throw new InvalidOperationException("Current browsing context is no longer open");
                }

                throw new InvalidOperationException("no such element");
            }

            if (!_elementMap.TryGetValue(elementId, out var element) || element == null)
            {
                throw new InvalidOperationException("no such element");
            }

            if (!IsElementReferenceInCurrentBrowsingContext(elementId))
            {
                throw new InvalidOperationException("no such element");
            }

            if (!element.IsConnected)
            {
                throw new InvalidOperationException("stale element reference");
            }

            if (!IsElementWithinSearchRoot(searchRoot, element))
            {
                throw new InvalidOperationException("no such element");
            }

            return element;
        }

        private async Task<string> GetElementAttributeViaScriptAsync(string elementId, string name)
        {
            var normalizedName = name ?? string.Empty;
            var jsonName = JsonSerializer.Serialize(normalizedName);
            var script = $@"
                var __el = arguments[0];
                if (!__el) return null;
                var __name = {jsonName};
                if (!__name) return null;
                if (!__el.hasAttribute(__name)) return null;
                var __attr = __el.getAttribute(__name);
                if (__attr === null) return null;
                if (__attr === '' && {BuildBooleanAttributeProbe("__name")}) return 'true';
                return String(__attr);";
            var value = await ExecuteScriptAsync(script, new object[] { elementId }).ConfigureAwait(false);
            return value?.ToString();
        }

        private static string BuildBooleanAttributeProbe(string variableName)
        {
            var entries = string.Join(",", WebDriverBooleanAttributes.Select(v => $"'{v.ToLowerInvariant()}'"));
            return $"([{entries}]).indexOf(({variableName} || '').toLowerCase()) >= 0";
        }

        private object[] PrepareScriptArgsForExecution(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return Array.Empty<object>();
            }

            var prepared = new object[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                prepared[i] = PrepareScriptArgForExecution(args[i]);
            }

            return prepared;
        }

        private static bool ContainsWebDriverArgMarkers(object value)
        {
            if (value == null)
            {
                return false;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key &&
                        (string.Equals(key, WebDriverArgElementMarker, StringComparison.Ordinal) ||
                         string.Equals(key, WebDriverArgShadowRootMarker, StringComparison.Ordinal) ||
                         string.Equals(key, WebDriverArgFrameMarker, StringComparison.Ordinal) ||
                         string.Equals(key, WebDriverArgWindowMarker, StringComparison.Ordinal)))
                    {
                        return true;
                    }

                    if (ContainsWebDriverArgMarkers(entry.Value))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                {
                    if (ContainsWebDriverArgMarkers(item))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private object PrepareScriptArgForExecution(object arg)
        {
            if (arg == null)
            {
                return null;
            }

            if (arg is string elementId &&
                _elementMap.ContainsKey(elementId))
            {
                Element mappedElement;
                try
                {
                    mappedElement = ResolveElementInActiveContextOrThrow(elementId);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.IndexOf("no such element", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    _elementMap.TryGetValue(elementId, out var fallbackElement) &&
                    fallbackElement != null &&
                    fallbackElement.IsConnected &&
                    IsFrameElement(fallbackElement))
                {
                    // Frame element references are top-context controls and must remain
                    // script-addressable while connected, even after frame switches.
                    mappedElement = fallbackElement;
                }

                var elementDomId = mappedElement?.GetAttribute("id") ?? string.Empty;

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [WebDriverArgElementMarker] = elementId,
                    [WebDriverArgElementDomIdMarker] = elementDomId
                };
            }

            if (arg is string shadowRootId &&
                (shadowRootId.StartsWith("sr:host:", StringComparison.Ordinal) || _shadowRootMap.ContainsKey(shadowRootId)))
            {
                ShadowRoot mappedShadowRoot;
                var activeSearchRoot = ResolveSearchRoot();
                if (activeSearchRoot == null)
                {
                    if (_currentFrameElement != null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    if (!TryResolveShadowRootWithoutSearchRoot(shadowRootId, out mappedShadowRoot, out var shadowErrorWithoutRoot))
                    {
                        throw new InvalidOperationException(shadowErrorWithoutRoot ?? "no such shadow root");
                    }
                }
                else if (!TryResolveShadowRootInActiveContext(shadowRootId, activeSearchRoot, out mappedShadowRoot, out var shadowError))
                {
                    throw new InvalidOperationException(shadowError ?? "no such shadow root");
                }

                var shadowHostDomId = string.Empty;
                if (mappedShadowRoot != null)
                {
                    shadowHostDomId = mappedShadowRoot?.Host?.GetAttribute("id") ?? string.Empty;
                }

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [WebDriverArgShadowRootMarker] = shadowRootId,
                    [WebDriverArgShadowHostDomIdMarker] = shadowHostDomId
                };
            }

            if (arg is string frameReferenceId &&
                frameReferenceId.StartsWith("frm:", StringComparison.Ordinal))
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [WebDriverArgFrameMarker] = frameReferenceId
                };
            }

            if (arg is string windowReferenceId &&
                windowReferenceId.StartsWith("win:", StringComparison.Ordinal))
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [WebDriverArgWindowMarker] = windowReferenceId
                };
            }

            if (arg is Element element)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [WebDriverArgElementMarker] = GetOrRegisterElementId(element)
                };
            }

            if (arg is IDictionary dictionary)
            {
                var mapped = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key)
                    {
                        mapped[key] = PrepareScriptArgForExecution(entry.Value);
                    }
                }

                return mapped;
            }

            if (arg is IEnumerable enumerable && arg is not string)
            {
                var list = new List<object>();
                foreach (var item in enumerable)
                {
                    list.Add(PrepareScriptArgForExecution(item));
                }

                return list;
            }

            return arg;
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args = null)
        {
            await EnsureExecutionDocumentReadyAsync().ConfigureAwait(false);
            RefreshWebDriverDomReferenceAttributes();
            var frameCollectionSyncScript = @"
                (function __fenSyncFrameCollection() {
                    try {
                        if (!window || !document || typeof document.querySelectorAll !== 'function') {
                            return;
                        }

                        var __frameNodes = document.querySelectorAll('iframe,frame') || [];
                        var __existingLength = 0;
                        try {
                            __existingLength = Number(window.length) || 0;
                        } catch (e) {
                            __existingLength = 0;
                        }

                        for (var __i = 0; __i < __existingLength; __i++) {
                            try { delete window[__i]; } catch (e) { window[__i] = undefined; }
                        }

                        for (var __j = 0; __j < __frameNodes.length; __j++) {
                            var __frameWindow = null;
                            try { __frameWindow = __frameNodes[__j].contentWindow || null; } catch (e) { __frameWindow = null; }
                            window[__j] = __frameWindow;
                        }

                        try { window.length = __frameNodes.length; } catch (e) {}
                    } catch (e) {}
                })();
                (function __fenEnsureScrollApis() {
                    try {
                        if (!window) {
                            return;
                        }

                        if (typeof window.scrollTo !== 'function') {
                            window.scrollTo = function(x, y) {
                                var nx = Number(x);
                                var ny = Number(y);
                                if (!isFinite(nx)) nx = 0;
                                if (!isFinite(ny)) ny = 0;
                                this.scrollX = nx;
                                this.scrollY = ny;
                                this.pageXOffset = nx;
                                this.pageYOffset = ny;
                            };
                        }

                        if (typeof window.scrollBy !== 'function') {
                            window.scrollBy = function(dx, dy) {
                                var cx = Number(this.scrollX) || 0;
                                var cy = Number(this.scrollY) || 0;
                                this.scrollTo(cx + (Number(dx) || 0), cy + (Number(dy) || 0));
                            };
                        }
                    } catch (e) {}
                })();";
            // WebDriver spec: scripts are executed as an anonymous function
            // So we wrap the script: (function() { <script> }).apply(null, arguments)
            string wrappedScript;
            if (args != null && args.Length > 0)
            {
                var preparedArgs = PrepareScriptArgsForExecution(args);
                var jsonArgs = JsonSerializer.Serialize(preparedArgs);
                var hasReferenceArgs = ContainsWebDriverArgMarkers(preparedArgs);
                if (!hasReferenceArgs)
                {
                    wrappedScript = $@"
                        var __args = {jsonArgs};
                        {frameCollectionSyncScript}
                        (function(arguments) {{
                            {script}
                        }}).call(window, __args);";
                }
                else
                {
                wrappedScript = $@"
                    var __args = {jsonArgs};
                    function __fenFindWdElementById(__root, __wdId) {{
                        if (!__root || typeof __wdId !== 'string') return null;

                        var __candidates = [];
                        try {{
                            __candidates = __root.querySelectorAll('[{WebDriverDomIdAttribute}]') || [];
                        }} catch (e) {{
                            __candidates = [];
                        }}

                        for (var __i = 0; __i < __candidates.length; __i++) {{
                            if (__candidates[__i].getAttribute('{WebDriverDomIdAttribute}') === __wdId) {{
                                return __candidates[__i];
                            }}
                        }}

                        var __descendants = [];
                        try {{
                            __descendants = __root.querySelectorAll('*') || [];
                        }} catch (e) {{
                            __descendants = [];
                        }}

                        for (var __d = 0; __d < __descendants.length; __d++) {{
                            var __el = __descendants[__d];
                            try {{
                                if (__el.shadowRoot) {{
                                    var __nested = __fenFindWdElementById(__el.shadowRoot, __wdId);
                                    if (__nested) return __nested;
                                }}
                            }} catch (e) {{}}
                        }}

                        return null;
                    }}
                    (function __fenResolveWdArgs(value) {{
                        if (!value || typeof value !== 'object') return value;
                        if (!Array.isArray(value) &&
                            Object.prototype.hasOwnProperty.call(value, '{WebDriverArgElementMarker}')) {{
                            var __wdId = value['{WebDriverArgElementMarker}'];
                            var __resolvedEl = __fenFindWdElementById(document, __wdId);
                            if (!__resolvedEl &&
                                Object.prototype.hasOwnProperty.call(value, '{WebDriverArgElementDomIdMarker}')) {{
                                var __domId = value['{WebDriverArgElementDomIdMarker}'];
                                if (typeof __domId === 'string' && __domId.length > 0 && document.getElementById) {{
                                    __resolvedEl = document.getElementById(__domId);
                                }}
                            }}
                            if (__resolvedEl && window.customElements && typeof window.customElements.upgrade === 'function') {{
                                try {{
                                    window.customElements.upgrade(__resolvedEl);
                                }} catch (e) {{}}
                            }}
                            return __resolvedEl;
                        }}
                        if (!Array.isArray(value) &&
                            Object.prototype.hasOwnProperty.call(value, '{WebDriverArgShadowRootMarker}')) {{
                            var __wdShadowId = value['{WebDriverArgShadowRootMarker}'];
                            if (typeof __wdShadowId === 'string' && __wdShadowId.indexOf('sr:host:') === 0) {{
                                var __hostId = __wdShadowId.substring('sr:host:'.length);
                                var __host = __fenFindWdElementById(document, __hostId);
                                if (__host) {{
                                    return __host.shadowRoot || null;
                                }}
                            }}
                            if (Object.prototype.hasOwnProperty.call(value, '{WebDriverArgShadowHostDomIdMarker}')) {{
                                var __hostDomId = value['{WebDriverArgShadowHostDomIdMarker}'];
                                if (typeof __hostDomId === 'string' && __hostDomId.length > 0 && document.getElementById) {{
                                    var __hostByDomId = document.getElementById(__hostDomId);
                                    if (__hostByDomId) {{
                                        return __hostByDomId.shadowRoot || null;
                                    }}
                                }}
                            }}
                            return null;
                        }}
                        if (!Array.isArray(value) &&
                            Object.prototype.hasOwnProperty.call(value, '{WebDriverArgFrameMarker}')) {{
                            var __wdFrameId = value['{WebDriverArgFrameMarker}'];
                            if (typeof __wdFrameId === 'string' && __wdFrameId.indexOf('frm:') === 0) {{
                                var __frameElementId = __wdFrameId.substring('frm:'.length);
                                var __frames = document.querySelectorAll('[{WebDriverDomIdAttribute}]');
                                for (var __f = 0; __f < __frames.length; __f++) {{
                                    if (__frames[__f].getAttribute('{WebDriverDomIdAttribute}') === __frameElementId) {{
                                        return __frames[__f].contentWindow || null;
                                    }}
                                }}
                            }}
                            return null;
                        }}
                        if (!Array.isArray(value) &&
                            Object.prototype.hasOwnProperty.call(value, '{WebDriverArgWindowMarker}')) {{
                            var __wdWindowId = value['{WebDriverArgWindowMarker}'];
                            if (__wdWindowId === 'win:top') {{
                                return window;
                            }}
                            return null;
                        }}
                        if (Array.isArray(value)) {{
                            for (var __j = 0; __j < value.length; __j++) value[__j] = __fenResolveWdArgs(value[__j]);
                            return value;
                        }}
                        var __keys = Object.keys(value);
                    for (var __k = 0; __k < __keys.length; __k++) {{
                        var __key = __keys[__k];
                        value[__key] = __fenResolveWdArgs(value[__key]);
                    }}
                    return value;
                }})(__args);
                {frameCollectionSyncScript}
                (function(arguments) {{
                    {script}
                }}).call(window, __args)";
                }
            }
            else
            {
                wrappedScript = $@"
                    {frameCollectionSyncScript}
                    var __args = [];
                    (function() {{ {script} }})()";
            }
            
            TryLogDebug($"[ExecuteScript] Wrapped: {wrappedScript.Substring(0, Math.Min(500, wrappedScript.Length))}...", LogCategory.JavaScript);
            var rawResult = _engine.Evaluate(wrappedScript);
            TryLogDebug($"[ExecuteScript] Raw result type: {rawResult?.GetType().Name}", LogCategory.JavaScript);
            
            if (rawResult is FenBrowser.FenEngine.Core.FenValue val && val.Type == JsValueType.Error)
            {
                throw new InvalidOperationException(val.AsError());
            }

            // Convert FenValue to WebDriver-facing values while preserving DOM objects.
            if (rawResult is FenBrowser.FenEngine.Core.FenValue fenValue)
            {
                return ConvertFenValueForWebDriver(fenValue);
            }

            // Handle FenJS JsValue results (objects/host-objects that the
            // basic ConvertFenJsValue cannot trivially flatten).
            if (rawResult is FenBrowser.Js.Runtime.JsValue jsValue)
            {
                return PostProcessFenJsResult(jsValue);
            }

            return PostProcessFenJsResult(rawResult);
        }

        // Mousemove throttle: skip events closer than 16 ms (~60 fps)
        private float _lastMouseMoveX;
        private float _lastMouseMoveY;
        private bool _hasLastMouseMovePosition;

        // Storage for async script callback result
        private readonly object _asyncScriptLock = new object();

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeoutMs)
        {
            await EnsureExecutionDocumentReadyAsync().ConfigureAwait(false);
            RefreshWebDriverDomReferenceAttributes();
            var frameCollectionSyncScript = @"
                (function __fenSyncFrameCollection() {
                    try {
                        if (!window || !document || typeof document.querySelectorAll !== 'function') {
                            return;
                        }

                        var __frameNodes = document.querySelectorAll('iframe,frame') || [];
                        var __existingLength = 0;
                        try {
                            __existingLength = Number(window.length) || 0;
                        } catch (e) {
                            __existingLength = 0;
                        }

                        for (var __i = 0; __i < __existingLength; __i++) {
                            try { delete window[__i]; } catch (e) { window[__i] = undefined; }
                        }

                        for (var __j = 0; __j < __frameNodes.length; __j++) {
                            var __frameWindow = null;
                            try { __frameWindow = __frameNodes[__j].contentWindow || null; } catch (e) { __frameWindow = null; }
                            window[__j] = __frameWindow;
                        }

                        try { window.length = __frameNodes.length; } catch (e) {}
                    } catch (e) {}
                })();
                (function __fenEnsureScrollApis() {
                    try {
                        if (!window) {
                            return;
                        }

                        if (typeof window.scrollTo !== 'function') {
                            window.scrollTo = function(x, y) {
                                var nx = Number(x);
                                var ny = Number(y);
                                if (!isFinite(nx)) nx = 0;
                                if (!isFinite(ny)) ny = 0;
                                this.scrollX = nx;
                                this.scrollY = ny;
                                this.pageXOffset = nx;
                                this.pageYOffset = ny;
                            };
                        }

                        if (typeof window.scrollBy !== 'function') {
                            window.scrollBy = function(dx, dy) {
                                var cx = Number(this.scrollX) || 0;
                                var cy = Number(this.scrollY) || 0;
                                this.scrollTo(cx + (Number(dx) || 0), cy + (Number(dy) || 0));
                            };
                        }
                    } catch (e) {}
                })();";

            // Create a unique callback ID for this execution.
            var callbackId = Guid.NewGuid().ToString("N");
            
            // Prepare arguments array with the callback as the last argument
            var argsList = PrepareScriptArgsForExecution(args).ToList();
            
            // We need to create the callback function in JavaScript context
            // The callback should store the result in a global variable we can poll
            // Also set up requestAnimationFrame polyfill that works with our polling
            var setupScript = $@"
                window.__fen_async_result_{callbackId} = null;
                window.__fen_async_done_{callbackId} = false;
                window.__fen_async_error_{callbackId} = null;
                
                // Set up rAF queue if not present
                if (!window.__raf_id) window.__raf_id = 0;
                if (!window.__raf_callbacks) window.__raf_callbacks = [];
                
                // Override requestAnimationFrame to use our queue (both window and global)
                var __rafFunc = function(callback) {{
                    console.log('[rAF-setup] requestAnimationFrame called, id=' + (window.__raf_id + 1));
                    var id = ++window.__raf_id;
                    // Store directly in the callbacks array
                    window.__raf_callbacks.push({{id: id, fn: callback}});
                    console.log('[rAF-setup] Queue length after push: ' + window.__raf_callbacks.length);
                    return id;
                }};
                window.requestAnimationFrame = __rafFunc;
                // Global assignment might fail in strict mode, try-catch it
                try {{ requestAnimationFrame = __rafFunc; }} catch(e) {{ console.log('[rAF-setup] Global assign failed: ' + e); }}
                
                var __cafFunc = function(id) {{
                    // Remove callback with matching id from array
                    window.__raf_callbacks = window.__raf_callbacks.filter(function(item) {{ return item.id !== id; }});
                }};
                window.cancelAnimationFrame = __cafFunc;
                try {{ cancelAnimationFrame = __cafFunc; }} catch(e) {{}}
                
                console.log('[rAF-setup] Setup complete. typeof requestAnimationFrame: ' + typeof requestAnimationFrame);
                console.log('[rAF-setup] typeof window.requestAnimationFrame: ' + typeof window.requestAnimationFrame);
            ";
            _engine.Evaluate(setupScript);
            
            // Do not rewrite user script source. Execute with WebDriver arguments unchanged.
            var processedScript = script;
            
            // Build arguments JSON - add a callback function at the end
            var jsonArgs = JsonSerializer.Serialize(argsList);
            var hasReferenceArgs = ContainsWebDriverArgMarkers(argsList);
            
            // The script wrapper that provides the callback function.
            var wrappedScript = hasReferenceArgs ? $@"
                var __args = {jsonArgs};
                {frameCollectionSyncScript}
                function __fenFindWdElementById(__root, __wdId) {{
                    if (!__root || typeof __wdId !== 'string') return null;

                    var __candidates = [];
                    try {{
                        __candidates = __root.querySelectorAll('[{WebDriverDomIdAttribute}]') || [];
                    }} catch (e) {{
                        __candidates = [];
                    }}

                    for (var __i = 0; __i < __candidates.length; __i++) {{
                        if (__candidates[__i].getAttribute('{WebDriverDomIdAttribute}') === __wdId) {{
                            return __candidates[__i];
                        }}
                    }}

                    var __descendants = [];
                    try {{
                        __descendants = __root.querySelectorAll('*') || [];
                    }} catch (e) {{
                        __descendants = [];
                    }}

                    for (var __d = 0; __d < __descendants.length; __d++) {{
                        var __el = __descendants[__d];
                        try {{
                            if (__el.shadowRoot) {{
                                var __nested = __fenFindWdElementById(__el.shadowRoot, __wdId);
                                if (__nested) return __nested;
                            }}
                        }} catch (e) {{}}
                    }}

                    return null;
                }}
                (function __fenResolveWdArgs(value) {{
                    if (!value || typeof value !== 'object') return value;
                    if (!Array.isArray(value) &&
                        Object.prototype.hasOwnProperty.call(value, '{WebDriverArgElementMarker}')) {{
                        var __wdId = value['{WebDriverArgElementMarker}'];
                        var __resolvedEl = __fenFindWdElementById(document, __wdId);
                        if (!__resolvedEl &&
                            Object.prototype.hasOwnProperty.call(value, '{WebDriverArgElementDomIdMarker}')) {{
                            var __domId = value['{WebDriverArgElementDomIdMarker}'];
                            if (typeof __domId === 'string' && __domId.length > 0 && document.getElementById) {{
                                __resolvedEl = document.getElementById(__domId);
                            }}
                        }}
                        if (__resolvedEl && window.customElements && typeof window.customElements.upgrade === 'function') {{
                            try {{
                                window.customElements.upgrade(__resolvedEl);
                            }} catch (e) {{}}
                        }}
                        return __resolvedEl;
                    }}
                    if (!Array.isArray(value) &&
                        Object.prototype.hasOwnProperty.call(value, '{WebDriverArgShadowRootMarker}')) {{
                        var __wdShadowId = value['{WebDriverArgShadowRootMarker}'];
                        if (typeof __wdShadowId === 'string' && __wdShadowId.indexOf('sr:host:') === 0) {{
                            var __hostId = __wdShadowId.substring('sr:host:'.length);
                            var __host = __fenFindWdElementById(document, __hostId);
                            if (__host) {{
                                return __host.shadowRoot || null;
                            }}
                        }}
                        if (Object.prototype.hasOwnProperty.call(value, '{WebDriverArgShadowHostDomIdMarker}')) {{
                            var __hostDomId = value['{WebDriverArgShadowHostDomIdMarker}'];
                            if (typeof __hostDomId === 'string' && __hostDomId.length > 0 && document.getElementById) {{
                                var __hostByDomId = document.getElementById(__hostDomId);
                                if (__hostByDomId) {{
                                    return __hostByDomId.shadowRoot || null;
                                }}
                            }}
                        }}
                        return null;
                    }}
                    if (!Array.isArray(value) &&
                        Object.prototype.hasOwnProperty.call(value, '{WebDriverArgFrameMarker}')) {{
                        var __wdFrameId = value['{WebDriverArgFrameMarker}'];
                        if (typeof __wdFrameId === 'string' && __wdFrameId.indexOf('frm:') === 0) {{
                            var __frameElementId = __wdFrameId.substring('frm:'.length);
                            var __frames = document.querySelectorAll('[{WebDriverDomIdAttribute}]');
                            for (var __f = 0; __f < __frames.length; __f++) {{
                                if (__frames[__f].getAttribute('{WebDriverDomIdAttribute}') === __frameElementId) {{
                                    return __frames[__f].contentWindow || null;
                                }}
                            }}
                        }}
                        return null;
                    }}
                    if (!Array.isArray(value) &&
                        Object.prototype.hasOwnProperty.call(value, '{WebDriverArgWindowMarker}')) {{
                        var __wdWindowId = value['{WebDriverArgWindowMarker}'];
                        if (__wdWindowId === 'win:top') {{
                            return window;
                        }}
                        return null;
                    }}
                    if (Array.isArray(value)) {{
                        for (var __j = 0; __j < value.length; __j++) value[__j] = __fenResolveWdArgs(value[__j]);
                        return value;
                    }}
                    var __keys = Object.keys(value);
                    for (var __k = 0; __k < __keys.length; __k++) {{
                        var __key = __keys[__k];
                        value[__key] = __fenResolveWdArgs(value[__key]);
                    }}
                    return value;
                }})(__args);
                console.log('[AsyncScript] __args before push: ' + __args.length);
                var __callback = function(result) {{
                    console.log('[AsyncScript] Callback called');
                    if (result && typeof result.then === 'function') {{
                        result.then(function(__resolved) {{
                            window.__fen_async_result_{callbackId} = __resolved;
                            window.__fen_async_done_{callbackId} = true;
                        }}, function(__error) {{
                            try {{
                                window.__fen_async_error_{callbackId} = __error && __error.message ? String(__error.message) : String(__error);
                            }} catch (e) {{
                                window.__fen_async_error_{callbackId} = 'javascript error';
                            }}
                            window.__fen_async_done_{callbackId} = true;
                        }});
                        return;
                    }}

                    window.__fen_async_result_{callbackId} = result;
                    window.__fen_async_done_{callbackId} = true;
                }};
                var pushResult = __args.push(__callback);
                console.log('[AsyncScript] push result: ' + pushResult);
                console.log('[AsyncScript] __args after push: ' + __args.length);
                console.log('[AsyncScript] __args[' + (__args.length - 1) + '] type: ' + typeof __args[__args.length - 1]);
                
                // Debug: Log key values before script runs
                console.log('[AsyncScript] document.readyState: ' + document.readyState);
                console.log('[AsyncScript] typeof requestAnimationFrame: ' + typeof requestAnimationFrame);
                console.log('[AsyncScript] typeof Document: ' + typeof Document);
                console.log('[AsyncScript] __args length: ' + __args.length);
                console.log('[AsyncScript] __args[0] type: ' + typeof __args[0]);
                
                (function() {{
                    return (async function(arguments) {{
                        {processedScript}
                    }}).call(window, __args);
                }})().catch(function(__error) {{
                        try {{
                            window.__fen_async_error_{callbackId} = __error && __error.message ? String(__error.message) : String(__error);
                        }} catch (e) {{
                            window.__fen_async_error_{callbackId} = 'javascript error';
                        }}
                        window.__fen_async_done_{callbackId} = true;
                }});
                "
                : $@"
                var __args = {jsonArgs};
                {frameCollectionSyncScript}
                console.log('[AsyncScript] __args before push: ' + __args.length);
                var __callback = function(result) {{
                    console.log('[AsyncScript] Callback called');
                    if (result && typeof result.then === 'function') {{
                        result.then(function(__resolved) {{
                            window.__fen_async_result_{callbackId} = __resolved;
                            window.__fen_async_done_{callbackId} = true;
                        }}, function(__error) {{
                            try {{
                                window.__fen_async_error_{callbackId} = __error && __error.message ? String(__error.message) : String(__error);
                            }} catch (e) {{
                                window.__fen_async_error_{callbackId} = 'javascript error';
                            }}
                            window.__fen_async_done_{callbackId} = true;
                        }});
                        return;
                    }}

                    window.__fen_async_result_{callbackId} = result;
                    window.__fen_async_done_{callbackId} = true;
                }};
                __args.push(__callback);

                (async function(arguments) {{
                    {processedScript}
                }}).call(window, __args).catch(function(__error) {{
                        try {{
                            window.__fen_async_error_{callbackId} = __error && __error.message ? String(__error.message) : String(__error);
                        }} catch (e) {{
                            window.__fen_async_error_{callbackId} = 'javascript error';
                        }}
                        window.__fen_async_done_{callbackId} = true;
                }});

                // Debug: Log rAF queue after script
                console.log('[AsyncScript] rAF callbacks length: ' + (window.__raf_callbacks ? window.__raf_callbacks.length : 'no array'));
            ";
            
            TryLogDebug($"[AsyncScript] Executing wrapped script (timeout {timeoutMs}ms)", LogCategory.JavaScript);
            TryLogDebug($"[AsyncScript] Input script (first 500 chars): {(script.Length > 500 ? script.Substring(0, 500) : script)}", LogCategory.JavaScript);
            TryLogDebug($"[AsyncScript] Processed script (first 500 chars): {(processedScript.Length > 500 ? processedScript.Substring(0, 500) : processedScript)}", LogCategory.JavaScript);
            
            // Execute the script and surface immediate JavaScript errors deterministically.
            var execResult = _engine.Evaluate(wrappedScript);
            TryLogDebug($"[AsyncScript] Script executed, result type: {execResult?.GetType().Name ?? "null"}", LogCategory.JavaScript);
            if (execResult is FenBrowser.FenEngine.Core.FenValue fv && fv.Type == JsValueType.Error)
            {
                throw new InvalidOperationException(fv.AsError());
            }
            
            // Process rAF queue helper script
            var processRafScript = @"
                (function() {
                    var count = 0;
                    if (window.__raf_callbacks && window.__raf_callbacks.length > 0) {
                        var callbacks = window.__raf_callbacks;
                        window.__raf_callbacks = [];  // Clear the queue
                        for (var i = 0; i < callbacks.length; i++) {
                            count++;
                            var item = callbacks[i];
                            if (item && typeof item.fn === 'function') {
                                console.log('[rAF] Calling callback id=' + item.id);
                                item.fn(Date.now());
                            }
                        }
                    }
                    return count;
                })();
            ";
            
            // Poll for the result
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int loopCount = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                loopCount++;
                // Drive the shared event loop so timer/promise callbacks can execute
                // while WebDriver execute/async is waiting for completion.
                try
                {
                    var eventLoop = _engine.EventLoopCoordinator;
                    eventLoop.ProcessNextTask();
                    eventLoop.PerformMicrotaskCheckpoint();
                }
                catch
                {
                    // Keep waiting deterministically even if event-loop pumping fails.
                }

                // Process any pending requestAnimationFrame callbacks
                try 
                { 
                    var rafResult = _engine.Evaluate(processRafScript);
                    if (loopCount % 100 == 1) // Log every 100th iteration
                    {
                        TryLogDebug($"[AsyncScript] Poll loop {loopCount}, rafCount: {rafResult}", LogCategory.JavaScript);
                    }
                } 
                catch (Exception ex)
                {
                    TryLogDebug($"[AsyncScript] rAF error: {ex.Message}", LogCategory.Errors);
                }
                
                // Check if the callback was called
                var doneCheck = _engine.Evaluate($"window.__fen_async_done_{callbackId}");
                var isDone = doneCheck switch
                {
                    FenBrowser.FenEngine.Core.FenValue dv => dv.IsBoolean && dv.ToBoolean(),
                    bool b => b,
                    _ => false
                };

                if (isDone)
                {
                    var asyncError = _engine.Evaluate($"window.__fen_async_error_{callbackId}");
                    if (asyncError is FenBrowser.FenEngine.Core.FenValue asyncErrorValue &&
                        !asyncErrorValue.IsNull &&
                        !asyncErrorValue.IsUndefined)
                    {
                        throw new InvalidOperationException(asyncErrorValue.ToString());
                    }

                    if (asyncError is string asyncErrorText && !string.IsNullOrWhiteSpace(asyncErrorText))
                    {
                        throw new InvalidOperationException(asyncErrorText);
                    }

                    // Get the result
                    var result = _engine.Evaluate($"window.__fen_async_result_{callbackId}");
                    TryLogDebug($"[AsyncScript] Callback received result after {sw.ElapsedMilliseconds}ms", LogCategory.JavaScript);
                    
                    // Convert FenValue to native object
                    if (result is FenBrowser.FenEngine.Core.FenValue fenValue)
                    {
                        return ConvertFenValueForWebDriver(fenValue);
                    }
                    if (result is FenBrowser.Js.Runtime.JsValue jsResult)
                    {
                        return PostProcessFenJsResult(jsResult);
                    }
                    return PostProcessFenJsResult(result);
                }

                // Some WebDriver user-prompt fixture scripts intentionally create a modal
                // without invoking the async callback. In that case, complete the command
                // once the prompt is observable so the caller can assert prompt handling.
                if (!string.IsNullOrEmpty(_pendingAlertText))
                {
                    TryLogDebug($"[AsyncScript] Completing due to observable modal prompt: '{_pendingAlertText}'", LogCategory.JavaScript);
                    return null;
                }
                
                // Small delay to not spin too fast
                await Task.Delay(10);
            }
            
            // A modal prompt can be raised by a timer at the script-timeout boundary.
            // WPT dialog fixtures intentionally do this without invoking the async
            // callback, so give the event loop a short prompt-only drain before
            // reporting script timeout.
            var modalPromptGrace = System.Diagnostics.Stopwatch.StartNew();
            while (modalPromptGrace.ElapsedMilliseconds < 1000)
            {
                try
                {
                    var eventLoop = _engine.EventLoopCoordinator;
                    eventLoop.ProcessNextTask();
                    eventLoop.PerformMicrotaskCheckpoint();
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(_pendingAlertText))
                {
                    TryLogDebug($"[AsyncScript] Completing after timeout-boundary modal prompt: '{_pendingAlertText}'", LogCategory.JavaScript);
                    return null;
                }

                await Task.Delay(10);
            }

            TryLogDebug($"[AsyncScript] Timeout after {timeoutMs}ms", LogCategory.Errors);
            
            // Timeout - throw exception
            throw new TimeoutException($"Script execution timeout ({timeoutMs/1000}s)");
        }

        public async Task<string> CaptureScreenshotAsync()
        {
            // Use delegate if available (injected from MainWindow/WebDriverIntegration)
            if (CaptureScreenshotDelegate != null)
                return await CaptureScreenshotDelegate();
            return "";
        }

        public async Task<string> CaptureElementScreenshotAsync(string elementId)
        {
            // Use delegate if available
            if (CaptureElementScreenshotDelegate != null)
                return await CaptureElementScreenshotDelegate(elementId);
            return "";
        }

        public Task<string> PrintToPdfAsync(double pageWidth, double pageHeight, bool landscape, double scale)
        {
            // PDF printing not yet implemented
            return Task.FromResult("");
        }

        public Task<List<WebDriverCookie>> GetAllCookiesAsync()
        {
            var scope = ResolveCookieScope();
            if (scope == null)
            {
                return Task.FromResult(new List<WebDriverCookie>());
            }

            var cookies = _resources.CookieJar
                .SnapshotCookies(scope, _current ?? scope, includeHttpOnly: true)
                .Select(cookie => new WebDriverCookie
            {
                Name = cookie.Name,
                Value = cookie.Value ?? string.Empty,
                Domain = cookie.Domain ?? scope.Host,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                Expiry = cookie.Expires?.ToUnixTimeSeconds(),
                SameSite = ToWebDriverSameSite(cookie.SameSite)
            }).ToList();

            return Task.FromResult(cookies);
        }

        public Task<WebDriverCookie> GetCookieAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.FromResult<WebDriverCookie>(null);
            }

            var scope = ResolveCookieScope();
            if (scope == null)
            {
                return Task.FromResult<WebDriverCookie>(null);
            }

            var cookie = _resources.CookieJar
                .SnapshotCookies(scope, _current ?? scope, includeHttpOnly: true)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (cookie == null)
            {
                return Task.FromResult<WebDriverCookie>(null);
            }

            return Task.FromResult<WebDriverCookie>(new WebDriverCookie
            {
                Name = cookie.Name,
                Value = cookie.Value ?? string.Empty,
                Domain = cookie.Domain ?? scope.Host,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                Expiry = cookie.Expires?.ToUnixTimeSeconds(),
                SameSite = ToWebDriverSameSite(cookie.SameSite)
            });
        }

        public Task AddCookieAsync(WebDriverCookie cookie)
        {
            if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
            {
                return Task.CompletedTask;
            }

            var scope = ResolveCookieScope();
            if (scope != null)
            {
                _resources.CookieJar.SetDocumentCookie(
                    scope,
                    BuildWebDriverSetCookieHeader(cookie),
                    _current ?? scope,
                    BrowserSettings.Instance.BlockThirdPartyCookies);
            }
            return Task.CompletedTask;
        }

        public Task DeleteCookieAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.CompletedTask;
            }

            var scope = ResolveCookieScope();
            if (scope != null)
            {
                _engine.DeleteCookie(scope, name);
            }
            return Task.CompletedTask;
        }

        public Task DeleteAllCookiesAsync()
        {
            var scope = ResolveCookieScope();
            if (scope == null)
            {
                _engine.ClearAllCookies();
                return Task.CompletedTask;
            }

            var keys = _resources.CookieJar
                .SnapshotCookies(scope, _current ?? scope, includeHttpOnly: true)
                .Select(cookie => cookie.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var key in keys)
            {
                _engine.DeleteCookie(scope, key);
            }
            return Task.CompletedTask;
        }

        private Uri ResolveCookieScope()
        {
            if (_current == null || !_current.IsAbsoluteUri)
            {
                return null;
            }

            if (!_current.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !_current.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return _current;
        }

        private static string BuildWebDriverSetCookieHeader(WebDriverCookie cookie)
        {
            var parts = new List<string>
            {
                $"{cookie.Name ?? string.Empty}={cookie.Value ?? string.Empty}",
                $"Path={SanitizeCookieAttribute(cookie.Path, "/")}"
            };

            if (!string.IsNullOrWhiteSpace(cookie.Domain))
            {
                parts.Add($"Domain={SanitizeCookieAttribute(cookie.Domain, string.Empty)}");
            }

            if (cookie.Expiry.HasValue)
            {
                parts.Add($"Expires={DateTimeOffset.FromUnixTimeSeconds(cookie.Expiry.Value).UtcDateTime:R}");
            }

            if (cookie.Secure)
            {
                parts.Add("Secure");
            }

            if (cookie.HttpOnly)
            {
                parts.Add("HttpOnly");
            }

            if (!string.IsNullOrWhiteSpace(cookie.SameSite))
            {
                parts.Add($"SameSite={SanitizeCookieAttribute(cookie.SameSite, "Lax")}");
            }

            return string.Join("; ", parts);
        }

        private static string SanitizeCookieAttribute(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Replace(";", string.Empty).Trim();
        }

        private static string ToWebDriverSameSite(CookieSameSite sameSite)
        {
            return sameSite switch
            {
                CookieSameSite.Strict => "Strict",
                CookieSameSite.None => "None",
                CookieSameSite.Unspecified => "None",
                _ => "Lax"
            };
        }

        // Actions - Pointer/Keyboard state
        private double _pointerX = 0;
        private double _pointerY = 0;

        private HashSet<string> _pressedKeys = new HashSet<string>();

        public async Task PerformActionsAsync(List<ActionChain> actions)
        {
            foreach (var chain in actions)
            {
                switch (chain.Type?.ToLowerInvariant())
                {
                    case "pointer":
                        await PerformPointerActionsAsync(chain);
                        break;
                    case "key":
                        await PerformKeyActionsAsync(chain);
                        break;
                    case "wheel":
                        // Scroll actions - limited support
                        break;
                    case "none":
                        // Pause actions
                        foreach (var action in chain.Actions)
                        {
                            if (action.Duration > 0)
                                await Task.Delay(action.Duration);
                        }
                        break;
                }
            }
        }

        private async Task PerformPointerActionsAsync(ActionChain chain)
        {
            foreach (var action in chain.Actions)
            {
                switch (action.Type?.ToLowerInvariant())
                {
                    case "pointermove":
                        // Move pointer to position
                        if (action.Origin == "viewport")
                        {
                            _pointerX = action.X;
                            _pointerY = action.Y;
                        }
                        else if (action.Origin == "pointer")
                        {
                            _pointerX += action.X;
                            _pointerY += action.Y;
                        }
                        else if (!string.IsNullOrEmpty(action.Origin))
                        {
                            // Move relative to element
                            if (ResolveElementInActiveContext(action.Origin) != null)
                            {
                                var rect = await GetElementRectAsync(action.Origin);
                                _pointerX = rect.X + rect.Width / 2 + action.X;
                                _pointerY = rect.Y + rect.Height / 2 + action.Y;
                            }
                        }
                        if (action.Duration > 0)
                            await Task.Delay(action.Duration);
                        break;

                    case "pointerdown":
                        // Simulate click on element at current position
                        var elementAtPoint = FindElementAtPoint(_pointerX, _pointerY);
                        if (elementAtPoint != null)
                        {
                            // Trigger click behavior
                            await HandleElementClick(elementAtPoint);
                        }
                        break;

                    case "pointerup":
                        break;

                    case "pause":
                        if (action.Duration > 0)
                            await Task.Delay(action.Duration);
                        break;
                }
            }
        }

        private async Task PerformKeyActionsAsync(ActionChain chain)
        {
            foreach (var action in chain.Actions)
            {
                switch (action.Type?.ToLowerInvariant())
                {
                    case "keydown":
                        if (!string.IsNullOrEmpty(action.Value))
                        {
                            _pressedKeys.Add(action.Value);
                            // Type key into focused element (simplified)
                            await HandleKeyPress(action.Value);
                        }
                        break;

                    case "keyup":
                        if (!string.IsNullOrEmpty(action.Value))
                            _pressedKeys.Remove(action.Value);
                        break;

                    case "pause":
                        if (action.Duration > 0)
                            await Task.Delay(action.Duration);
                        break;
                }
            }
        }

        private Element FindElementAtPoint(double x, double y)
        {
            if (_engine == null || _engine.ActiveDom == null)
            {
                return null;
            }

            // Use the renderer's stacking-aware input hit tester first. Besides paint
            // order, it owns iframe retargeting and coordinate translation into a loaded
            // child Document. The legacy flat layout scan below cannot reach sandboxed
            // frame content and would leave real pointer clicks on the iframe host.
            var renderContext = _activeRenderer?.CreateRenderContext() ?? _engine.BuildRenderContext();
            if (renderContext != null &&
                FenBrowser.FenEngine.Rendering.Interaction.HitTester.HitTestInput(
                    renderContext,
                    (float)x,
                    (float)y,
                    out var inputHit))
            {
                return inputHit.Target;
            }

            if (_engine.LastLayout == null)
            {
                return null;
            }

            var layout = _engine.LastLayout;
            
            // Convert viewport coordinates to document coordinates by adding scroll offset
            // NOTE: 'x' and 'y' are passed as viewport coordinates from PerformPointerActions
            float docX = (float)x;
            float docY = (float)y + layout.ScrollOffsetY;

            // Simple Hit Testing: 
            // Iterate the DOM tree (SelfAndDescendants matches draw order approximately parents -> children)
            // We want the last element that contains the point (top-most visual).
            // This handles nested elements naturally (e.g. text inside div).
            
            Element hit = null;

            foreach (var node in _engine.ActiveDom.SelfAndDescendants())
            {
                if (layout.TryGetElementRect(node as Element, out var geo))
                {
                    // Check if point is inside
                    if (docX >= geo.Left && docX < geo.Right && 
                        docY >= geo.Top && docY < geo.Bottom)
                    {
                        if (node is Element el) hit = el;
                        else if (node.ParentNode is Element parent) hit = parent;
                    }
                }
            }
            
            return hit;
        }

        /// <summary>
        /// Resolve a DOM element at viewport coordinates.
        /// Coordinates are interpreted in viewport space (scroll offset is applied internally).
        /// </summary>
        public Element HitTestElementAtViewportPoint(float x, float y)
        {
            return FindElementAtPoint(x, y);
        }

        private Element _focusedElement;
        private string _focusedElementValueAtFocus;

        private int _cursorIndex = 0;
        private int _selectionAnchor = -1;

        private void SetFocusedElementState(Element element, bool fromKeyboard = false)
        {
            var previousFocused = _focusedElement;
            if (previousFocused != null && !ReferenceEquals(previousFocused, element))
            {
                var previousDocument = previousFocused.OwnerDocument;
                if (previousDocument != null && ReferenceEquals(previousDocument.ActiveElement, previousFocused))
                {
                    previousDocument.ActiveElement = null;
                }

                ClearContainingFrameFocus(previousDocument);
            }

            _focusedElement = element;
            if (!ReferenceEquals(previousFocused, element))
            {
                _focusedElementValueAtFocus = IsTextEntryElement(element)
                    ? ReadEditableValue(element)
                    : null;
            }

            var ownerDocument = element?.OwnerDocument;
            if (ownerDocument != null)
            {
                ownerDocument.ActiveElement = element;
                PromoteContainingFrameFocus(ownerDocument);
            }

            ElementStateManager.Instance.SetFocusedElement(element, fromKeyboard);
        }

        private static void PromoteContainingFrameFocus(Document document)
        {
            for (var current = document; current?.ParentNode is Element frame && IsFrameElement(frame);)
            {
                var parentDocument = frame.OwnerDocument;
                if (parentDocument == null)
                {
                    return;
                }

                parentDocument.ActiveElement = frame;
                current = parentDocument;
            }
        }

        private static void ClearContainingFrameFocus(Document document)
        {
            for (var current = document; current?.ParentNode is Element frame && IsFrameElement(frame);)
            {
                var parentDocument = frame.OwnerDocument;
                if (parentDocument == null)
                {
                    return;
                }

                if (ReferenceEquals(parentDocument.ActiveElement, frame))
                {
                    parentDocument.ActiveElement = null;
                }

                current = parentDocument;
            }
        }

        private void SetFocusedElementWithEvents(Element element, bool fromKeyboard = false)
        {
            var previousFocused = _focusedElement;
            if (ReferenceEquals(previousFocused, element))
            {
                SetFocusedElementState(element, fromKeyboard);
                return;
            }

            var eventContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            if (previousFocused != null)
            {
                if (IsTextEntryElement(previousFocused) &&
                    !string.Equals(
                        _focusedElementValueAtFocus,
                        ReadEditableValue(previousFocused),
                        StringComparison.Ordinal))
                {
                    DispatchDomEvent(previousFocused, "change", eventContext, bubbles: true);
                }

                DispatchDomEvent(previousFocused, "blur", eventContext, bubbles: false);
                DispatchDomEvent(previousFocused, "focusout", eventContext, bubbles: true);
            }

            SetFocusedElementState(element, fromKeyboard);
            if (element != null)
            {
                DispatchDomEvent(element, "focus", eventContext, bubbles: false);
                DispatchDomEvent(element, "focusin", eventContext, bubbles: true);
            }
        }

        private static string GetStableDomElementId(Element element)
        {
            if (element == null)
            {
                return null;
            }

            var id = element.GetAttribute("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }

            if (element.Attr != null)
            {
                foreach (var entry in element.Attr)
                {
                    if (string.Equals(entry.Key, "id", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(entry.Value))
                    {
                        return entry.Value;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(element.Id) ? null : element.Id;
        }

        private static string BuildElementPathSignature(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            var segments = new Stack<string>();
            var cursor = element;
            while (cursor != null)
            {
                var tag = (cursor.TagName ?? cursor.NodeName ?? string.Empty).ToLowerInvariant();
                var domId = GetStableDomElementId(cursor);
                if (!string.IsNullOrWhiteSpace(domId))
                {
                    segments.Push($"{tag}#{domId}");
                    break;
                }

                var siblingIndex = 0;
                var parent = cursor.ParentElement;
                if (parent != null)
                {
                    siblingIndex = parent.ChildNodes
                        .OfType<Element>()
                        .Where(sibling => string.Equals(sibling.TagName, cursor.TagName, StringComparison.OrdinalIgnoreCase))
                        .TakeWhile(sibling => !ReferenceEquals(sibling, cursor))
                        .Count();
                }

                segments.Push($"{tag}[{siblingIndex}]");
                cursor = parent;
            }

            return string.Join("/", segments);
        }

        private static string NormalizeIdentityText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(" ", value
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                .Trim();
        }

        private string GetOrRegisterElementId(Element element)
        {
            if (element == null)
            {
                return null;
            }

            foreach (var entry in _elementMap)
            {
                if (ReferenceEquals(entry.Value, element))
                {
                    TagElementWithWebDriverId(element, entry.Key);
                    if (!_elementBrowsingContextMap.ContainsKey(entry.Key))
                    {
                        _elementBrowsingContextMap[entry.Key] = GetCurrentBrowsingContextToken();
                    }
                    return entry.Key;
                }
            }

            var taggedId = element.GetAttribute(WebDriverDomIdAttribute);
            if (!string.IsNullOrWhiteSpace(taggedId))
            {
                if (_elementMap.TryGetValue(taggedId, out var taggedElement))
                {
                    if (!ReferenceEquals(taggedElement, element))
                    {
                        _elementMap[taggedId] = element;
                    }

                    if (!_elementBrowsingContextMap.ContainsKey(taggedId))
                    {
                        _elementBrowsingContextMap[taggedId] = GetCurrentBrowsingContextToken();
                    }
                    TagElementWithWebDriverId(element, taggedId);
                    return taggedId;
                }
            }

            var structuralMatchId = FindExistingElementIdByStructure(element);
            if (!string.IsNullOrWhiteSpace(structuralMatchId))
            {
                _elementMap[structuralMatchId] = element;
                if (!_elementBrowsingContextMap.ContainsKey(structuralMatchId))
                {
                    _elementBrowsingContextMap[structuralMatchId] = GetCurrentBrowsingContextToken();
                }
                TagElementWithWebDriverId(element, structuralMatchId);
                return structuralMatchId;
            }

            var id = Guid.NewGuid().ToString();
            _elementMap[id] = element;
            if (!_elementBrowsingContextMap.ContainsKey(id))
            {
                _elementBrowsingContextMap[id] = GetCurrentBrowsingContextToken();
            }
            TagElementWithWebDriverId(element, id);
            return id;
        }

        private string FindExistingElementIdByStructure(Element element)
        {
            if (element == null)
            {
                return null;
            }

            var elementSignature = BuildElementPathSignature(element);
            if (string.IsNullOrWhiteSpace(elementSignature))
            {
                return null;
            }

            var currentContext = GetCurrentBrowsingContextToken();
            var elementTag = element.TagName ?? element.NodeName ?? string.Empty;
            var elementText = NormalizeIdentityText(element.TextContent ?? string.Empty);

            foreach (var entry in _elementMap)
            {
                var candidateId = entry.Key;
                var candidate = entry.Value;
                if (candidate == null || !candidate.IsConnected)
                {
                    continue;
                }

                if (_elementBrowsingContextMap.TryGetValue(candidateId, out var candidateContext) &&
                    !string.Equals(candidateContext, currentContext, StringComparison.Ordinal))
                {
                    continue;
                }

                var candidateTag = candidate.TagName ?? candidate.NodeName ?? string.Empty;
                if (!string.Equals(candidateTag, elementTag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateSignature = BuildElementPathSignature(candidate);
                if (!string.Equals(candidateSignature, elementSignature, StringComparison.Ordinal))
                {
                    continue;
                }

                var candidateText = NormalizeIdentityText(candidate.TextContent ?? string.Empty);
                if (string.Equals(candidateText, elementText, StringComparison.Ordinal))
                {
                    return candidateId;
                }
            }

            return null;
        }

        private static void TagElementWithWebDriverId(Element element, string elementId)
        {
            if (element == null || string.IsNullOrWhiteSpace(elementId))
            {
                return;
            }

            try
            {
                element.SetAttribute(WebDriverDomIdAttribute, elementId);
            }
            catch
            {
                // Metadata only; ignore failures.
            }
        }

        private void RefreshWebDriverDomReferenceAttributes()
        {
            foreach (var entry in _elementMap)
            {
                var element = entry.Value;
                if (element == null || !element.IsConnected)
                {
                    continue;
                }

                TagElementWithWebDriverId(element, entry.Key);
            }
        }

        private bool TryGetElementClickClientPoint(Element element, out int clientX, out int clientY)
        {
            clientX = 0;
            clientY = 0;

            if (element == null)
            {
                return false;
            }

            var layout = _engine?.LastLayout;
            if (layout != null && layout.TryGetElementRect(element, out var directGeo))
            {
                var directWidth = Math.Abs(directGeo.Right - directGeo.Left);
                var directHeight = Math.Abs(directGeo.Bottom - directGeo.Top);
                if (directWidth <= 1 || directHeight <= 1)
                {
                    goto VisualFallback;
                }

                var viewport = GetWindowRect();
                var viewportWidth = Math.Max(1, viewport.Width);
                var viewportHeight = Math.Max(1, viewport.Height);

                var visibleLeft = Math.Max(0, Math.Min(directGeo.Left, directGeo.Right));
                var visibleRight = Math.Min(viewportWidth, Math.Max(directGeo.Left, directGeo.Right));
                var visibleTop = Math.Max(0, Math.Min(directGeo.Top - layout.ScrollOffsetY, directGeo.Bottom - layout.ScrollOffsetY));
                var visibleBottom = Math.Min(viewportHeight, Math.Max(directGeo.Top - layout.ScrollOffsetY, directGeo.Bottom - layout.ScrollOffsetY));

                clientX = (int)Math.Floor((visibleLeft + visibleRight) / 2.0);
                clientY = (int)Math.Floor((visibleTop + visibleBottom) / 2.0);
                return true;
            }

            if (layout != null)
            {
                var activeDom = _engine.GetActiveDom();
                var root = (activeDom as Element) ?? (activeDom as Document)?.DocumentElement;
                var domId = GetStableDomElementId(element);

                if (root == null || string.IsNullOrWhiteSpace(domId))
                {
                    goto VisualFallback;
                }

                var matched = root
                    .SelfAndDescendants()
                    .OfType<Element>()
                    .FirstOrDefault(candidate =>
                        string.Equals(GetStableDomElementId(candidate), domId, StringComparison.Ordinal) &&
                        string.Equals(candidate.TagName, element.TagName, StringComparison.OrdinalIgnoreCase));

                if (matched != null && layout.TryGetElementRect(matched, out var geo))
                {
                    var geoWidth = Math.Abs(geo.Right - geo.Left);
                    var geoHeight = Math.Abs(geo.Bottom - geo.Top);
                    if (geoWidth <= 1 || geoHeight <= 1)
                    {
                        goto VisualFallback;
                    }

                    var viewport = GetWindowRect();
                    var viewportWidth = Math.Max(1, viewport.Width);
                    var viewportHeight = Math.Max(1, viewport.Height);

                    var visibleLeft = Math.Max(0, Math.Min(geo.Left, geo.Right));
                    var visibleRight = Math.Min(viewportWidth, Math.Max(geo.Left, geo.Right));
                    var visibleTop = Math.Max(0, Math.Min(geo.Top - layout.ScrollOffsetY, geo.Bottom - layout.ScrollOffsetY));
                    var visibleBottom = Math.Min(viewportHeight, Math.Max(geo.Top - layout.ScrollOffsetY, geo.Bottom - layout.ScrollOffsetY));

                    clientX = (int)Math.Floor((visibleLeft + visibleRight) / 2.0);
                    clientY = (int)Math.Floor((visibleTop + visibleBottom) / 2.0);
                    return true;
                }
            }

        VisualFallback:
            if (FenBrowser.FenEngine.Scripting.JavaScriptEngine.TryGetVisualRect(element, out var vx, out var vy, out var vw, out var vh))
            {
                clientX = (int)Math.Floor(vx + (vw / 2.0));
                clientY = (int)Math.Floor(vy + (vh / 2.0));
                return true;
            }

            return false;
        }

        private void SyncFocusFromPointerTarget(Element target)
        {
            if (target == null)
            {
                // Don't clear focus when target is null â€” this typically means the
                // InputManager's hit test failed (stale/empty render context), NOT
                // that the user clicked on empty space. The BrowserIntegration fallback
                // HandleElementClick handles proper focus management with the correct
                // paint tree hit test result.
                return;
            }

            // Direct editable/focusable targets first.
            string tag = target.NodeName?.ToLowerInvariant();
            bool directEditable = IsTextEntryElement(target);
            var inputType = target.GetAttribute("type");
            bool nonHiddenInput = tag == "input" && !string.Equals(inputType, "hidden", StringComparison.OrdinalIgnoreCase);
            bool directFocusable = directEditable ||
                                   nonHiddenInput ||
                                   tag == "button" || tag == "select" ||
                                   (tag == "a" && !string.IsNullOrEmpty(target.GetAttribute("href"))) ||
                                   !string.IsNullOrEmpty(target.GetAttribute("tabindex"));

            if (directFocusable)
            {
                SetFocusedElementWithEvents(target);
                if (directEditable)
                {
                    bool isContentEditable = string.Equals(target.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                    var val = isContentEditable ? (target.TextContent ?? string.Empty) : GetTextEntryValue(target);
                    _cursorIndex = val.Length;
                    _selectionAnchor = -1;
                }
                return;
            }

            // Many modern UIs (including Google Search) use wrapper containers around real editable controls.
            // If wrapper is hit, promote focus to the first descendant editable control.
            Element descendantEditable = target
                .Descendants()
                .OfType<Element>()
                .FirstOrDefault(el =>
                {
                    return IsTextEntryElement(el);
                });

            if (descendantEditable != null)
            {
                SetFocusedElementWithEvents(descendantEditable);
                bool descendantIsContentEditable = string.Equals(descendantEditable.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                var val = descendantIsContentEditable ? (descendantEditable.TextContent ?? string.Empty) : GetTextEntryValue(descendantEditable);
                _cursorIndex = val.Length;
                _selectionAnchor = -1;
            }
            else
            {
                SetFocusedElementWithEvents(null);
            }
        }

        public async Task HandleClipboardCommand(string command, string data = null)
        {
             if (_focusedElement == null) return;
             var tag = _focusedElement.NodeName?.ToLowerInvariant();
             if (tag != "input" && tag != "textarea") return;
             
             var val = GetTextEntryValue(_focusedElement);
             int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int len = end - start;
             
             switch (command.ToLowerInvariant())
             {
                 case "selectall":
                     _selectionAnchor = 0;
                     _cursorIndex = val.Length;
                     TryInvokeRepaintReady(_engine.GetActiveDom());
                     break;
                     
                 case "copy":
                     // Host handles getting text via GetSelectedText
                     break;
                     
                 case "paste":
                     if (data != null)
                     {
                         if (len > 0) val = val.Remove(start, len);
                         val = val.Insert(start, data);
                         _cursorIndex = start + data.Length;
                         _selectionAnchor = -1; // Clear selection
                         SetTextEntryValue(_focusedElement, val);
                         TryInvokeRepaintReady(_engine.GetActiveDom());
                     }
                     break;
             }
        }
        
        public string GetSelectedText()
        {
             if (_focusedElement == null) return "";
             var val = GetTextEntryValue(_focusedElement);
             int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
             return end > start ? val.Substring(start, end - start) : "";
        }
        
        public void DeleteSelection()
        {
             if (_focusedElement == null) return;
             var val = GetTextEntryValue(_focusedElement);
             int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
             
             if (end > start)
             {
                 val = val.Remove(start, end - start);
                 _cursorIndex = start;
                 _selectionAnchor = -1;
                 SetTextEntryValue(_focusedElement, val);
                 TryInvokeRepaintReady(_engine.GetActiveDom());
             }
        }

        public async Task HandleElementClick(Element element)
        {
            _selectionAnchor = -1; // Reset selection
            bool allowDefaultActivation = ConsumeClickDefaultActivationDecision(element);
            var suppressDomClickDispatch = _suppressNextDomClickDispatchInHandleElementClick;
            _suppressNextDomClickDispatchInHandleElementClick = false;

            if (element == null)
            {
                SetFocusedElementState(null);
                TryInvokeRepaintReady(_engine.GetActiveDom());
                return;
            }
            
            var tag = element.NodeName?.ToLowerInvariant();

            if (TryHandleFrameRemovalActivation(element, allowDefaultActivation))
            {
                return;
            }

            // WebDriver element click must dispatch a real DOM click event.
            // Without this, tests that observe click handlers (window.clicks, bubbling)
            // report false negatives even if fallback activation runs.
            var clickClientX = 0;
            var clickClientY = 0;
            if (_pendingWebDriverClickPointValid)
            {
                clickClientX = _pendingWebDriverClickClientX;
                clickClientY = _pendingWebDriverClickClientY;
                _pendingWebDriverClickPointValid = false;
            }
            else
            {
                TryGetElementClickClientPoint(element, out clickClientX, out clickClientY);
            }
            var clickNotPrevented = true;
            if (!suppressDomClickDispatch)
            {
                clickNotPrevented = _engine.DispatchPointerEvent(
                    element,
                    "click",
                    new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
                    {
                        ClientX = clickClientX,
                        ClientY = clickClientY,
                        PageX = clickClientX,
                        PageY = clickClientY,
                        ScreenX = clickClientX,
                        ScreenY = clickClientY,
                        Button = 0,
                        Buttons = 0,
                        Bubbles = true,
                        Cancelable = true,
                        Composed = true,
                        IsTrusted = true
                    });

                // JS click handlers may modify DOM/styles (class toggles, popover,
                // attribute changes). Request a repaint so those mutations are rendered
                // immediately instead of waiting for the next input-driven invalidation.
                TryInvokeRepaintReady(_engine.GetActiveDom());
            }
            if (!clickNotPrevented)
            {
                allowDefaultActivation = false;
            }

            // SpecRef: WHATWG HTML — click activation algorithm.
            // The DOM `click` event always dispatches on the deepest hit
            // descendant (already done above). For activation behavior (form
            // submit, anchor navigation, label-for focus, etc.) the spec walks
            // ANCESTORS from the event target to find the closest element whose
            // activation behavior is defined. Without this walk, clicking on
            // any descendant of a <button type=submit> (e.g. an inner <span> or
            // <svg>) silently does nothing — which is exactly the Google
            // Search button regression observed in the field, since Google
            // wraps its submit button content in nested non-activation nodes.
            //
            // For wrapper containers that have NO activation-capable ancestor
            // (e.g. a styled <div> wrapping a search input), we still keep the
            // legacy "promote to descendant editable" behavior so focus/typing
            // remains stable when the user clicks a decorative wrapper.
            if (TryToggleCustomPopupActivation(element, allowDefaultActivation))
            {
                return;
            }

            var activationAncestor = FindActivationAncestor(element);
            if (activationAncestor != null && !ReferenceEquals(activationAncestor, element))
            {
                element = activationAncestor;
                tag = element.NodeName?.ToLowerInvariant();
            }
            else if (tag != "input" &&
                     tag != "textarea" &&
                     tag != "button" &&
                     tag != "a" &&
                     tag != "select" &&
                     !string.Equals(element.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase))
            {
                var descendantSubmit = element
                    .Descendants()
                    .OfType<Element>()
                    .FirstOrDefault(IsSubmitControlElement);

                var descendantEditable = element
                    .Descendants()
                    .OfType<Element>()
                    .FirstOrDefault(el =>
                    {
                        return IsTextEntryElement(el);
                    });

                if (descendantEditable != null)
                {
                    element = descendantEditable;
                    tag = element.NodeName?.ToLowerInvariant();
                }
                else if (descendantSubmit != null)
                {
                    element = descendantSubmit;
                    tag = element.NodeName?.ToLowerInvariant();
                }
            }
            
            // Handle summary clicks â€” toggle parent details[open]
            if (tag == "summary")
            {
                var detailsEl = element.ParentElement;
                while (detailsEl != null &&
                       !string.Equals(detailsEl.NodeName, "details", StringComparison.OrdinalIgnoreCase))
                    detailsEl = detailsEl.ParentElement;

                if (detailsEl != null && allowDefaultActivation)
                {
                    bool nowOpen = !detailsEl.HasAttribute("open");
                    if (nowOpen)
                        detailsEl.SetAttribute("open", "");
                    else
                        detailsEl.RemoveAttribute("open");

                    // Directly patch ComputedStyle.Display on non-summary children so the next
                    // RecordFrame sees the change without waiting for a full re-cascade.
                    var styles = _engine.LastComputedStyles;
                    if (styles != null)
                    {
                        foreach (var child in detailsEl.ChildNodes.OfType<Element>())
                        {
                            if (string.Equals(child.NodeName, "summary", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (styles.TryGetValue(child, out var cs) && cs != null)
                                cs.Display = nowOpen ? "block" : "none";
                        }
                    }

                    TryInvokeRepaintReady(_engine.GetActiveDom());
                }
                return;
            }
            if (tag == "label" && allowDefaultActivation)
            {
                var labelControl = FindAssociatedLabelControl(element);
                if (labelControl != null && !ReferenceEquals(labelControl, element) && !IsDisabledControl(labelControl))
                {
                    await HandleElementClick(labelControl);
                    return;
                }
            }
            // Handle anchor clicks
            if (tag == "a")
            {
                var href = element.GetAttribute("href");
                if (!string.IsNullOrEmpty(href) && allowDefaultActivation)
                {
                    var resolvedHref = ResolveUrlAgainstCurrent(href);
                    await NavigateAsync(resolvedHref?.AbsoluteUri ?? href);
                }
            }
            else if (tag == "input" &&
                     suppressDomClickDispatch &&
                     IsCheckboxInputElement(element))
            {
                // FenJS performs checkbox legacy pre-activation before click
                // listeners and either commits input/change or rolls back when
                // canceled. The physical/WebDriver path reaches this method
                // afterward only for focus and paint; do not toggle twice.
                SetFocusedElementState(element);
                TryInvokeRepaintReady(_engine.GetActiveDom());
                return;
            }
            else if (tag == "input" && allowDefaultActivation && TryActivateCheckableInput(element))
            {
                return;
            }
            // Handle button clicks
            else if (tag == "button" || (tag == "input" &&
                (element.GetAttribute("type")?.ToLowerInvariant() == "submit" ||
                 element.GetAttribute("type")?.ToLowerInvariant() == "button")))
            {
                 // Verify if this is a search button (simplified check)
                 // Popover target activation: if the button has popovertarget,
                 // toggle/show/hide the referenced popover element.
                 var popoverTargetId = element.GetAttribute("popovertarget");
                 if (!string.IsNullOrEmpty(popoverTargetId) && allowDefaultActivation)
                 {
                     var root = _engine.GetActiveDom();
                     var rootEl = (root as Element) ?? (root as Document)?.DocumentElement;
                     var targetEl = rootEl?.SelfAndDescendants()
                         .OfType<Element>()
                         .FirstOrDefault(e => string.Equals(e.Id, popoverTargetId, StringComparison.Ordinal));

                     if (targetEl != null && targetEl.GetAttribute("popover") != null)
                     {
                         var action = (element.GetAttribute("popovertargetaction") ?? "toggle").ToLowerInvariant();
                         bool isOpen = targetEl.HasAttribute("data-popover-open");

                         if (action == "show" && !isOpen)
                         {
                             targetEl.SetAttribute("data-popover-open", "");
                             FenBrowser.FenEngine.DOM.DomMutationQueue.Instance.EnqueueMutation(
                                 new FenBrowser.FenEngine.DOM.DomMutation(
                                     FenBrowser.FenEngine.DOM.MutationType.AttributeChange,
                                     InvalidationKind.Style | InvalidationKind.Layout,
                                     targetEl, "data-popover-open", null, ""));
                         }
                         else if (action == "hide" && isOpen)
                         {
                             targetEl.RemoveAttribute("data-popover-open");
                             FenBrowser.FenEngine.DOM.DomMutationQueue.Instance.EnqueueMutation(
                                 new FenBrowser.FenEngine.DOM.DomMutation(
                                     FenBrowser.FenEngine.DOM.MutationType.AttributeChange,
                                     InvalidationKind.Style | InvalidationKind.Layout,
                                     targetEl, "data-popover-open", "", null));
                         }
                         else if (action == "toggle")
                         {
                             if (isOpen)
                             {
                                 targetEl.RemoveAttribute("data-popover-open");
                                 FenBrowser.FenEngine.DOM.DomMutationQueue.Instance.EnqueueMutation(
                                     new FenBrowser.FenEngine.DOM.DomMutation(
                                         FenBrowser.FenEngine.DOM.MutationType.AttributeChange,
                                         InvalidationKind.Style | InvalidationKind.Layout,
                                         targetEl, "data-popover-open", "", null));
                             }
                             else
                             {
                                 targetEl.SetAttribute("data-popover-open", "");
                                 FenBrowser.FenEngine.DOM.DomMutationQueue.Instance.EnqueueMutation(
                                     new FenBrowser.FenEngine.DOM.DomMutation(
                                         FenBrowser.FenEngine.DOM.MutationType.AttributeChange,
                                         InvalidationKind.Style | InvalidationKind.Layout,
                                         targetEl, "data-popover-open", null, ""));
                             }
                         }

                         TryInvokeRepaintReady(root);
                     }
                 }

                 if (allowDefaultActivation && IsSubmitActivationControl(element, tag))
                 {
                     await SubmitFormAsync(element);
                 }
            }
            // Handle input focus
            else if (tag == "input" || tag == "textarea")
            {
                SetFocusedElementState(element);
                
                // Set cursor to end on focus
                var val = GetTextEntryValue(element);
                _cursorIndex = val.Length;
                _selectionAnchor = -1;
                
                // Trigger a repaint to show caret (if we had one)
                TryInvokeRepaintReady(_engine.GetActiveDom());
            }
            else
            {
                // Check if element is focusable
                bool isFocusable = false;
                if (tag == "a" && !string.IsNullOrEmpty(element.GetAttribute("href"))) isFocusable = true;
                else if (tag == "input" || tag == "textarea" || tag == "button" || tag == "select") isFocusable = true;
                else if (!string.IsNullOrEmpty(element.GetAttribute("tabindex"))) isFocusable = true;
                else if (element.GetAttribute("contenteditable") == "true") isFocusable = true;

                if (isFocusable)
                {
                    SetFocusedElementState(element);
                }
                else
                {
                    // Keep existing focus if click is inside the currently focused editable subtree.
                    // This avoids focus churn on wrapper clicks around active text fields.
                    bool keepFocus = false;
                    if (_focusedElement != null)
                    {
                        var cursor = _focusedElement;
                        while (cursor != null)
                        {
                            if (ReferenceEquals(cursor, element))
                            {
                                keepFocus = true;
                                break;
                            }
                            cursor = cursor.ParentElement;
                        }
                    }

                    if (!keepFocus)
                    {
                        SetFocusedElementState(null);
                    }
                }
                
                // Trigger repaint 
                TryInvokeRepaintReady(_engine.GetActiveDom());
            }
        }

        private bool TryToggleCustomPopupActivation(Element element, bool allowDefaultActivation)
        {
            if (!allowDefaultActivation || element == null)
            {
                return false;
            }

            var trigger = FindPopupTrigger(element);
            if (trigger == null)
            {
                return false;
            }

            var popup = trigger.ParentElement;
            while (popup != null &&
                   !string.Equals(popup.NodeName, "g-popup", StringComparison.OrdinalIgnoreCase))
            {
                popup = popup.ParentElement;
            }

            if (popup == null)
            {
                return false;
            }

            var menu = popup.ChildNodes
                .OfType<Element>()
                .FirstOrDefault(IsPopupMenuElement);
            if (menu == null)
            {
                return false;
            }

            var isExpanded = string.Equals(trigger.GetAttribute("aria-expanded"), "true", StringComparison.OrdinalIgnoreCase) ||
                             !InlineStyleHasDisplayNone(menu.GetAttribute("style"));
            var nextExpanded = !isExpanded;
            trigger.SetAttribute("aria-expanded", nextExpanded ? "true" : "false");
            menu.SetAttribute("style", UpsertInlineDisplay(menu.GetAttribute("style"), nextExpanded ? "block" : "none"));

            _engine.ScheduleRecascade();
            TryInvokeRepaintReady(_engine.GetActiveDom());
            return true;
        }

        private static Element FindPopupTrigger(Element element)
        {
            for (var current = element; current != null; current = current.ParentElement)
            {
                if (string.Equals(current.GetAttribute("aria-haspopup"), "true", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.GetAttribute("role"), "button", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }

            return null;
        }

        private static bool IsPopupMenuElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var className = element.GetAttribute("class") ?? string.Empty;
            return className.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(token => string.Equals(token, "UjBGL", StringComparison.Ordinal));
        }

        private static bool InlineStyleHasDisplayNone(string style)
        {
            if (string.IsNullOrWhiteSpace(style))
            {
                return false;
            }

            return style.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(':', 2))
                .Any(parts => parts.Length == 2 &&
                              string.Equals(parts[0].Trim(), "display", StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(parts[1].Trim(), "none", StringComparison.OrdinalIgnoreCase));
        }

        private static string UpsertInlineDisplay(string style, string display)
        {
            var declarations = new List<string>();
            var replaced = false;
            foreach (var part in (style ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(':', 2);
                if (pieces.Length == 2 &&
                    string.Equals(pieces[0].Trim(), "display", StringComparison.OrdinalIgnoreCase))
                {
                    declarations.Add("display:" + display);
                    replaced = true;
                }
                else
                {
                    declarations.Add(part.Trim());
                }
            }

            if (!replaced)
            {
                declarations.Insert(0, "display:" + display);
            }

            return string.Join(';', declarations) + ";";
        }

        /// <summary>
        /// Post-process a FenJS evaluation result for WebDriver serialization.
        /// DOM elements and Documents are wrapped as WebElement tokens so the
        /// WebDriver serialization layer emits proper element references.
        /// Lists, dictionaries, and primitives pass through unchanged.
        /// </summary>
        private object PostProcessFenJsResult(object result)
        {
            if (result is FenBrowser.Core.Dom.V2.Element element)
            {
                return WebDriverElementTokenPrefix + GetOrRegisterElementId(element);
            }
            if (result is FenBrowser.Core.Dom.V2.Document doc)
            {
                var docElement = doc.DocumentElement;
                if (docElement != null)
                    return WebDriverElementTokenPrefix + GetOrRegisterElementId(docElement);
            }
            if (result is FenBrowser.Js.Runtime.JsValue jsValue)
            {
                // Fallback for callers that still pass raw JsValue.
                // Convert via the script engine if available.
                if (_engine.ScriptEngine != null)
                {
                    return PostProcessFenJsResult(
                        _engine.ScriptEngine.ConvertJsValueToObject(jsValue));
                }
                if (jsValue.Tag == FenBrowser.Js.Runtime.JsValueTag.HostObject)
                    return "[object HostObject]";
                return "[object Object]";
            }
            return result;
        }

        private object ConvertFenValueForWebDriver(FenBrowser.FenEngine.Core.Interfaces.IValue fenValue)
        {
            return ConvertFenValueForWebDriver(fenValue, new HashSet<FenBrowser.FenEngine.Core.Interfaces.IObject>(ReferenceEqualityComparer.Instance), 0);
        }

        private object ConvertFenValueForWebDriver(
            FenBrowser.FenEngine.Core.Interfaces.IValue fenValue,
            HashSet<FenBrowser.FenEngine.Core.Interfaces.IObject> visited,
            int depth)
        {
            if (fenValue.IsNull || fenValue.IsUndefined)
            {
                return null;
            }

            // Prevent runaway recursion for self-referential runtime objects.
            if (depth > 16)
            {
                return null;
            }

            switch (fenValue.Type)
            {
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Undefined:
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Null:
                    return null;
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Boolean:
                    return fenValue.AsBoolean();
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Number:
                    return fenValue.AsNumber();
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.String:
                    return fenValue.AsString();
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Object:
                case FenBrowser.FenEngine.Core.Interfaces.ValueType.Function:
                    return ConvertFenObjectForWebDriver(fenValue.AsObject(), visited, depth + 1);
                default:
                    return fenValue.ToNativeObject();
            }
        }

        private static bool TryExtractDomElementFromWrapper(FenBrowser.FenEngine.Core.Interfaces.IObject value, out Element element)
        {
            element = null;
            if (value == null)
            {
                return false;
            }

            var wrapperType = value.GetType();
            var wrapperName = wrapperType.Name ?? string.Empty;
            var wrapperNamespace = wrapperType.Namespace ?? string.Empty;
            var isLikelyDomWrapper =
                wrapperName.IndexOf("ElementWrapper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                wrapperName.Equals("Element", StringComparison.Ordinal) ||
                (wrapperNamespace.IndexOf(".DOM", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 wrapperName.IndexOf("Element", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!isLikelyDomWrapper)
            {
                return false;
            }

            var bindingFlags = System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.NonPublic;

            var elementProperty = wrapperType.GetProperty("Element", bindingFlags);
            if (elementProperty != null && typeof(Element).IsAssignableFrom(elementProperty.PropertyType))
            {
                element = elementProperty.GetValue(value) as Element;
                if (element != null)
                {
                    return true;
                }
            }

            var nodeProperty = wrapperType.GetProperty("Node", bindingFlags);
            if (nodeProperty != null && typeof(Node).IsAssignableFrom(nodeProperty.PropertyType))
            {
                element = nodeProperty.GetValue(value) as Element;
                if (element != null)
                {
                    return true;
                }
            }

            var elementField = wrapperType.GetField("_element", bindingFlags);
            if (elementField != null && typeof(Element).IsAssignableFrom(elementField.FieldType))
            {
                element = elementField.GetValue(value) as Element;
                if (element != null)
                {
                    return true;
                }
            }

            var nodeField = wrapperType.GetField("_node", bindingFlags);
            if (nodeField != null && typeof(Node).IsAssignableFrom(nodeField.FieldType))
            {
                element = nodeField.GetValue(value) as Element;
                if (element != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractShadowRootFromWrapper(FenBrowser.FenEngine.Core.Interfaces.IObject value, out ShadowRoot shadowRoot)
        {
            shadowRoot = null;
            if (value == null)
            {
                return false;
            }

            var wrapperType = value.GetType();
            var wrapperName = wrapperType.Name ?? string.Empty;
            var wrapperNamespace = wrapperType.Namespace ?? string.Empty;
            var isLikelyDomWrapper =
                wrapperName.EndsWith("Wrapper", StringComparison.Ordinal) ||
                wrapperName.IndexOf("ShadowRoot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                wrapperNamespace.IndexOf(".DOM", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isLikelyDomWrapper)
            {
                return false;
            }

            var bindingFlags = System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.NonPublic;

            var shadowProperty = wrapperType.GetProperty("ShadowRoot", bindingFlags);
            if (shadowProperty != null && typeof(ShadowRoot).IsAssignableFrom(shadowProperty.PropertyType))
            {
                shadowRoot = shadowProperty.GetValue(value) as ShadowRoot;
                if (shadowRoot != null)
                {
                    return true;
                }
            }

            var nodeProperty = wrapperType.GetProperty("Node", bindingFlags);
            if (nodeProperty != null && typeof(ShadowRoot).IsAssignableFrom(nodeProperty.PropertyType))
            {
                shadowRoot = nodeProperty.GetValue(value) as ShadowRoot;
                if (shadowRoot != null)
                {
                    return true;
                }
            }

            var shadowField = wrapperType.GetField("_shadowRoot", bindingFlags);
            if (shadowField != null && typeof(ShadowRoot).IsAssignableFrom(shadowField.FieldType))
            {
                shadowRoot = shadowField.GetValue(value) as ShadowRoot;
                if (shadowRoot != null)
                {
                    return true;
                }
            }

            var nodeField = wrapperType.GetField("_node", bindingFlags);
            if (nodeField != null && typeof(ShadowRoot).IsAssignableFrom(nodeField.FieldType))
            {
                shadowRoot = nodeField.GetValue(value) as ShadowRoot;
                if (shadowRoot != null)
                {
                    return true;
                }
            }

            return false;
        }

        private string GetOrRegisterShadowRootId(ShadowRoot shadowRoot)
        {
            if (shadowRoot == null)
            {
                return null;
            }

            var host = shadowRoot.Host;
            if (host != null)
            {
                var hostId = GetOrRegisterElementId(host);
                if (!string.IsNullOrWhiteSpace(hostId))
                {
                    var deterministicId = $"sr:host:{hostId}";
                    _shadowRootMap[deterministicId] = shadowRoot;
                    return deterministicId;
                }
            }

            foreach (var entry in _shadowRootMap)
            {
                if (ReferenceEquals(entry.Value, shadowRoot))
                {
                    return entry.Key;
                }
            }

            var id = Guid.NewGuid().ToString();
            _shadowRootMap[id] = shadowRoot;
            return id;
        }

        private static ShadowRoot TryGetAttachedShadowRoot(Element element)
        {
            if (element == null)
            {
                return null;
            }

            var openShadowRoot = element.ShadowRoot;
            if (openShadowRoot != null)
            {
                return openShadowRoot;
            }

            try
            {
                return ElementShadowRootField?.GetValue(element) as ShadowRoot;
            }
            catch
            {
                return null;
            }
        }

        private bool TryExtractFrameOrWindowReference(
            FenBrowser.FenEngine.Core.Interfaces.IObject value,
            out bool isFrameReference,
            out string nativeReferenceId)
        {
            isFrameReference = false;
            nativeReferenceId = null;
            if (value == null)
            {
                return false;
            }

            try
            {
                var frameElementValue = value.Get("frameElement");
                if (frameElementValue.IsObject &&
                    TryExtractDomElementFromWrapper(frameElementValue.AsObject(), out var frameElement) &&
                    frameElement != null)
                {
                    var frameElementId = GetOrRegisterElementId(frameElement);
                    if (!string.IsNullOrWhiteSpace(frameElementId))
                    {
                        isFrameReference = true;
                        nativeReferenceId = $"frm:{frameElementId}";
                        return true;
                    }
                }
            }
            catch
            {
                // Non-window objects may throw for unknown properties.
            }

            try
            {
                var selfValue = value.Get("window");
                if (selfValue.IsObject &&
                    ReferenceEquals(selfValue.AsObject(), value))
                {
                    isFrameReference = false;
                    nativeReferenceId = "win:top";
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private object ConvertFenObjectForWebDriver(
            FenBrowser.FenEngine.Core.Interfaces.IObject value,
            HashSet<FenBrowser.FenEngine.Core.Interfaces.IObject> visited,
            int depth)
        {
            if (value == null)
            {
                return null;
            }

            if (depth > 16)
            {
                return null;
            }

            if (!visited.Add(value))
            {
                return null;
            }

            // Convert DOM wrappers to tagged tokens so WebDriver emits stable element refs.
            if (TryExtractDomElementFromWrapper(value, out var elementValue))
            {
                return WebDriverElementTokenPrefix + GetOrRegisterElementId(elementValue);
            }

            if (TryExtractShadowRootFromWrapper(value, out var shadowRootValue))
            {
                return WebDriverShadowTokenPrefix + GetOrRegisterShadowRootId(shadowRootValue);
            }

            if (TryExtractFrameOrWindowReference(value, out var isFrameReference, out var nativeReferenceId) &&
                !string.IsNullOrWhiteSpace(nativeReferenceId))
            {
                return (isFrameReference ? WebDriverFrameTokenPrefix : WebDriverWindowTokenPrefix) + nativeReferenceId;
            }

            if (value is FenBrowser.FenEngine.Core.FenObject fenObj)
            {
                var lengthValue = fenObj.Get("length");
                if (lengthValue.IsNumber)
                {
                    var length = Math.Max(0, (int)lengthValue.AsNumber());
                    var list = new List<object>(length);
                    for (var i = 0; i < length; i++)
                    {
                        list.Add(ConvertFenValueForWebDriver(fenObj.Get(i.ToString()), visited, depth + 1));
                    }

                    return list;
                }

                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var key in fenObj.Keys())
                {
                    dict[key] = ConvertFenValueForWebDriver(fenObj.Get(key), visited, depth + 1);
                }

                return dict;
            }

            // Handle non-FenObject runtime objects (for example JS arrays returned from script).
            try
            {
                var lengthValue = value.Get("length");
                if (lengthValue.IsNumber)
                {
                    var length = Math.Max(0, (int)lengthValue.AsNumber());
                    var list = new List<object>(length);
                    for (var i = 0; i < length; i++)
                    {
                        list.Add(ConvertFenValueForWebDriver(value.Get(i.ToString()), visited, depth + 1));
                    }

                    return list;
                }
            }
            catch
            {
            }

            try
            {
                var keys = value.Keys()?.ToArray();
                if (keys != null && keys.Length > 0)
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var key in keys)
                    {
                        dict[key] = ConvertFenValueForWebDriver(value.Get(key), visited, depth + 1);
                    }

                    return dict;
                }
            }
            catch
            {
            }

            // Preserve host DOM wrapper instances so WebDriver can serialize them as element references.
            return value;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<FenBrowser.FenEngine.Core.Interfaces.IObject>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public bool Equals(FenBrowser.FenEngine.Core.Interfaces.IObject x, FenBrowser.FenEngine.Core.Interfaces.IObject y)
                => ReferenceEquals(x, y);

            public int GetHashCode(FenBrowser.FenEngine.Core.Interfaces.IObject obj)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private bool TryHandleFrameRemovalActivation(Element element, bool allowDefaultActivation)
        {
            if (element == null || _currentFrameElement == null)
            {
                return false;
            }

            var id = (element.GetAttribute("id") ?? string.Empty).Trim();
            var onclick = (element.GetAttribute("onclick") ?? string.Empty).Trim();
            var isRemoveParent =
                string.Equals(id, "remove-parent", StringComparison.OrdinalIgnoreCase) ||
                onclick.IndexOf("parent.remove()", StringComparison.OrdinalIgnoreCase) >= 0;
            var isRemoveTop =
                string.Equals(id, "remove-top", StringComparison.OrdinalIgnoreCase) ||
                onclick.IndexOf("top.remove()", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isRemoveParent)
            {
                if (DetachFrameElement(_currentFrameElement))
                {
                    _frameContextInvalidated = true;
                    TraceWebDriverFrame($"Frame removal control removed current frame={DescribeFrameElement(_currentFrameElement)}");
                    TryInvokeRepaintReady(_engine.GetActiveDom());
                }

                return true;
            }

            if (isRemoveTop)
            {
                var topFrame = _frameContextStack.Count > 0 ? _frameContextStack.Peek() : _currentFrameElement;
                if (DetachFrameElement(topFrame))
                {
                    _frameContextInvalidated = true;
                    TraceWebDriverFrame($"Frame removal control removed top frame={DescribeFrameElement(topFrame)}");
                    TryInvokeRepaintReady(_engine.GetActiveDom());
                }

                return true;
            }

            return false;
        }

        private static bool DetachFrameElement(Element frameElement)
        {
            if (!IsFrameElement(frameElement))
            {
                return false;
            }

            var parentElement = frameElement.ParentElement;
            if (parentElement == null)
            {
                return false;
            }

            parentElement.RemoveChild(frameElement);
            return true;
        }

        private bool ConsumeClickDefaultActivationDecision(Element activationTarget)
        {
            bool suppressDefault = false;
            if (_lastClickHadTarget && !_lastClickDefaultAllowed)
            {
                suppressDefault = AreElementsRelated(_lastClickTarget, activationTarget);
            }

            _lastClickHadTarget = false;
            _lastClickDefaultAllowed = true;
            _lastClickTarget = null;
            return !suppressDefault;
        }

        private static bool AreElementsRelated(Element first, Element second)
        {
            if (first == null || second == null) return false;
            if (ReferenceEquals(first, second)) return true;

            var cursor = first;
            while (cursor != null)
            {
                if (ReferenceEquals(cursor, second)) return true;
                cursor = cursor.ParentElement;
            }

            cursor = second;
            while (cursor != null)
            {
                if (ReferenceEquals(cursor, first)) return true;
                cursor = cursor.ParentElement;
            }

            return false;
        }

        private void EnsureFrameExecutionContextAvailable()
        {
            // Script execution remains available while a frame context is selected.
            // Frame-specific invalid references are handled during element/frame resolution.
        }

        private async Task EnsureExecutionDocumentReadyAsync()
        {
            EnsureFrameExecutionContextAvailable();
            if (_engine.GetActiveDom() != null)
            {
                return;
            }

            var bootstrapUrl = _current?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(bootstrapUrl))
            {
                bootstrapUrl = "about:blank";
            }

            await NavigateAsync(bootstrapUrl).ConfigureAwait(false);
        }

        /// <summary>
        /// Walk ancestors (inclusive) from <paramref name="start"/> looking for the
        /// closest element whose HTML activation behavior is defined. Implements
        /// the ancestor-walk portion of the WHATWG HTML "run activation behavior"
        /// algorithm: when a click event lands on a descendant of a button, anchor
        /// or labelled control, activation runs on the ancestor, not the leaf.
        ///
        /// Bounded at <see cref="MaxActivationAncestorWalk"/> hops to keep the
        /// path O(1) for deep DOMs.
        /// </summary>
        private const int MaxActivationAncestorWalk = 32;

        private static Element FindActivationAncestor(Element start)
        {
            if (start == null)
            {
                return null;
            }

            var cursor = start;
            int hops = 0;
            while (cursor != null && hops < MaxActivationAncestorWalk)
            {
                if (HasActivationBehavior(cursor))
                {
                    return cursor;
                }
                cursor = cursor.ParentElement;
                hops++;
            }
            return null;
        }

        /// <summary>
        /// True when <paramref name="element"/> has HTML activation behavior we
        /// honor in <see cref="HandleElementClick"/>. The set tracks the spec's
        /// activation-capable elements that this engine implements:
        ///   - <c>&lt;a&gt;</c> with non-empty <c>href</c>
        ///   - <c>&lt;area&gt;</c> with non-empty <c>href</c>
        ///   - <c>&lt;button&gt;</c> (default type=submit)
        ///   - <c>&lt;input&gt;</c> with type submit/reset/button/checkbox/radio/image
        ///   - <c>&lt;select&gt;</c>, <c>&lt;textarea&gt;</c> (focus activation)
        ///   - <c>&lt;summary&gt;</c> (details toggle)
        ///   - <c>&lt;label&gt;</c> (control redirection — handled by caller)
        /// Disabled controls are excluded so a disabled submit button does not
        /// hijack clicks landing on its decorative children.
        /// </summary>
        private static bool HasActivationBehavior(Element element)
        {
            if (element == null)
            {
                return false;
            }
            if (IsDisabledControl(element))
            {
                return false;
            }

            var tag = element.NodeName?.ToLowerInvariant();
            switch (tag)
            {
                case "a":
                case "area":
                    return !string.IsNullOrEmpty(element.GetAttribute("href"));
                case "button":
                case "select":
                case "textarea":
                case "summary":
                case "label":
                    return true;
                case "input":
                    var type = element.GetAttribute("type")?.ToLowerInvariant();
                    // Default input type is "text"; that is NOT activation-capable
                    // (clicks just focus it, which is handled by the input branch
                    // below). Activation types are explicitly enumerated.
                    return type == "submit"
                        || type == "reset"
                        || type == "button"
                        || type == "checkbox"
                        || type == "radio"
                        || type == "image";
                default:
                    return false;
            }
        }

        private static bool IsSubmitActivationControl(Element element, string loweredTag)
        {
            if (element == null) return false;

            if (string.Equals(loweredTag, "input", StringComparison.OrdinalIgnoreCase))
            {
                var type = element.GetAttribute("type");
                return string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(loweredTag, "button", StringComparison.OrdinalIgnoreCase))
            {
                var type = element.GetAttribute("type");
                return string.IsNullOrWhiteSpace(type) ||
                       string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool IsSubmitControlElement(Element element)
        {
            if (element == null || IsDisabledControl(element))
            {
                return false;
            }

            var tag = element.NodeName?.ToLowerInvariant();
            return IsSubmitActivationControl(element, tag);
        }

        private static bool IsCheckboxInputElement(Element element)
        {
            if (element == null ||
                !string.Equals(element.NodeName, "INPUT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? "text").Trim();
            return string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryActivateCheckableInput(Element element)
        {
            if (element == null ||
                !string.Equals(element.NodeName, "INPUT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
            if (type != "checkbox" && type != "radio")
            {
                return false;
            }

            if (IsDisabledControl(element))
            {
                return true;
            }

            SetFocusedElementState(element);

            bool changed = false;
            if (type == "checkbox")
            {
                changed = true;
                SetCheckableCheckedState(element, !ElementStateManager.Instance.IsChecked(element));
            }
            else if (!ElementStateManager.Instance.IsChecked(element))
            {
                ClearRadioGroupCheckedState(element);
                SetCheckableCheckedState(element, true);
                changed = true;
            }

            if (changed)
            {
                DispatchFormControlStateEvent(element, "input");
                DispatchFormControlStateEvent(element, "change");
            }

            TryInvokeRepaintReady(_engine.GetActiveDom());
            return true;
        }

        private static void SetCheckableCheckedState(Element element, bool isChecked)
        {
            if (element == null)
            {
                return;
            }

            ElementStateManager.Instance.SetChecked(element, isChecked);
        }

        private void DispatchFormControlStateEvent(Element element, string eventName)
        {
            if (element == null || string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            var eventContext = _engine.Context as FenBrowser.FenEngine.Core.ExecutionContext
                ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            DispatchDomEvent(
                element,
                eventName,
                eventContext,
                bubbles: true,
                cancelable: false);
        }

        private static void ClearRadioGroupCheckedState(Element radio)
        {
            if (radio == null ||
                !string.Equals(radio.NodeName, "INPUT", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(radio.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var name = radio.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var scope = FindAncestorForm(radio) ??
                radio.OwnerDocument?.DocumentElement ??
                radio.ParentElement;

            if (scope == null)
            {
                return;
            }

            foreach (var candidate in scope.SelfAndDescendants().OfType<Element>())
            {
                if (ReferenceEquals(candidate, radio) ||
                    !string.Equals(candidate.NodeName, "INPUT", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(candidate.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(candidate.GetAttribute("name"), name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (FindAncestorForm(candidate) != FindAncestorForm(radio))
                {
                    continue;
                }

                SetCheckableCheckedState(candidate, false);
            }
        }

        private async Task<bool> SubmitFormAsync(Element submitter)
        {
            var form = FindAncestorForm(submitter);
            if (form == null) return false;

            var scriptSubmitAllowed = _engine.DispatchPointerEvent(
                form,
                "submit",
                new FenBrowser.FenEngine.Scripting.BrowserDomEventInit
                {
                    Bubbles = true,
                    Cancelable = true,
                    Composed = false
                });

            var context = _engine.Context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            var submitEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                "submit",
                bubbles: true,
                cancelable: true,
                composed: true,
                context: context);

            var legacySubmitAllowed = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(form, submitEvent, context);
            bool allowSubmit = scriptSubmitAllowed && legacySubmitAllowed;
            if (!allowSubmit)
            {
                TryLogDebug("[BrowserApi] Form submit canceled by script.", LogCategory.Events);
                return true;
            }

            if (!IsIframeSandboxFormSubmissionAllowed(form))
            {
                TryLogWarn("[BrowserApi] Blocked form submission from sandboxed iframe without allow-forms.", LogCategory.Navigation);
                return true;
            }

            var actionUri = ResolveFormActionUri(form);
            if (actionUri == null) return false;

            var method = form.GetAttribute("method");
            if (string.IsNullOrWhiteSpace(method)) method = "GET";

            var controls = CollectFormSubmissionEntries(form, submitter);
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                var targetUrl = AppendQueryToUri(actionUri, controls);
                await NavigateAsync(targetUrl);
                return true;
            }

            TryLogWarn($"[BrowserApi] Form method '{method}' not fully implemented; navigating to action URL.", LogCategory.Navigation);
            await NavigateAsync(actionUri.AbsoluteUri);
            return true;
        }

        private static Element FindAncestorForm(Element element)
        {
            var cursor = element;
            while (cursor != null)
            {
                if (string.Equals(cursor.NodeName, "FORM", StringComparison.OrdinalIgnoreCase))
                {
                    return cursor;
                }
                cursor = cursor.ParentElement;
            }
            return null;
        }

        private static bool IsIframeSandboxFormSubmissionAllowed(Element form)
        {
            var cursor = form?.ParentNode;
            while (cursor != null)
            {
                if (cursor is Element element &&
                    string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
                {
                    var sandboxAttribute = element.GetAttribute("sandbox");
                    if (FenBrowser.Core.SandboxPolicy.HasIframeSandboxAttribute(sandboxAttribute))
                    {
                        var flags = FenBrowser.Core.SandboxPolicy.ParseIframeSandboxFlags(sandboxAttribute);
                        return (flags & FenBrowser.Core.IframeSandboxFlags.Forms) != 0;
                    }
                }

                cursor = cursor.ParentNode;
            }

            return true;
        }

        private Uri ResolveFormActionUri(Element form)
        {
            if (form == null) return _current;

            var action = form.GetAttribute("action");
            if (string.IsNullOrWhiteSpace(action))
            {
                return _current ?? new Uri("about:blank");
            }

            return ResolveUrlAgainstCurrent(action) ?? (_current ?? new Uri("about:blank"));
        }

        private Uri ResolveUrlAgainstCurrent(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return _current;
            var candidate = rawUrl.Trim();

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri;
            }

            if (_current != null && Uri.TryCreate(_current, candidate, out var resolved))
            {
                return resolved;
            }

            if (Uri.TryCreate("https://" + candidate.TrimStart('/'), UriKind.Absolute, out var httpsUri))
            {
                return httpsUri;
            }

            return null;
        }

        private static bool IsExplicitRelativeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.StartsWith("/", StringComparison.Ordinal) ||
                   url.StartsWith("./", StringComparison.Ordinal) ||
                   url.StartsWith("../", StringComparison.Ordinal) ||
                   url.StartsWith("?", StringComparison.Ordinal) ||
                   url.StartsWith("#", StringComparison.Ordinal);
        }

        private static bool IsTextEntryElement(Element element)
        {
            if (element == null || IsDisabledControl(element)) return false;

            var tag = element.NodeName?.ToLowerInvariant();
            if (tag == "textarea")
            {
                return true;
            }

            if (tag == "input")
            {
                return IsTextInputType(element.GetAttribute("type"));
            }

            return IsContentEditableElement(element);
        }

        private static bool IsTextInputType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return true;
            }

            switch (type.Trim().ToLowerInvariant())
            {
                case "hidden":
                case "button":
                case "submit":
                case "reset":
                case "checkbox":
                case "radio":
                case "file":
                case "image":
                case "range":
                case "color":
                    return false;
                default:
                    return true;
            }
        }

        private static string GetTextEntryValue(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }

            var tag = element.NodeName?.ToLowerInvariant();
            if (tag == "textarea")
            {
                var currentValue = element.GetAttribute("value");
                if (currentValue != null)
                {
                    return currentValue;
                }

                return element.TextContent ?? string.Empty;
            }

            return element.GetAttribute("value") ?? string.Empty;
        }

        private static void SetTextEntryValue(Element element, string value)
        {
            if (element == null)
            {
                return;
            }

            var normalized = value ?? string.Empty;
            element.SetAttribute("value", normalized);

            if (string.Equals(element.NodeName, "TEXTAREA", StringComparison.OrdinalIgnoreCase))
            {
                element.TextContent = normalized;
            }
        }

        private static List<KeyValuePair<string, string>> CollectFormSubmissionEntries(Element form, Element submitter)
        {
            var entries = new List<KeyValuePair<string, string>>();
            if (form == null) return entries;

            foreach (var control in form.Descendants().OfType<Element>())
            {
                if (!TryGetSuccessfulFormControl(control, submitter, out var name, out var value))
                {
                    continue;
                }

                entries.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            }

            return entries;
        }

        private static bool TryGetSuccessfulFormControl(Element control, Element submitter, out string name, out string value)
        {
            name = null;
            value = string.Empty;
            if (control == null || IsDisabledControl(control))
            {
                return false;
            }

            var tag = control.NodeName?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            name = control.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            switch (tag)
            {
                case "input":
                {
                    var type = control.GetAttribute("type")?.ToLowerInvariant() ?? "text";
                    switch (type)
                    {
                        case "submit":
                            if (submitter == null || !ReferenceEquals(control, submitter)) return false;
                            value = control.GetAttribute("value") ?? string.Empty;
                            return true;
                        case "button":
                        case "reset":
                        case "image":
                        case "file":
                            return false;
                        case "checkbox":
                        case "radio":
                            if (!control.HasAttribute("checked")) return false;
                            value = control.GetAttribute("value");
                            if (string.IsNullOrEmpty(value)) value = "on";
                            return true;
                        default:
                            value = control.GetAttribute("value") ?? string.Empty;
                            return true;
                    }
                }
                case "button":
                {
                    var type = control.GetAttribute("type");
                    if (!string.IsNullOrWhiteSpace(type) &&
                        !string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (submitter == null || !ReferenceEquals(control, submitter))
                    {
                        return false;
                    }

                    value = control.GetAttribute("value") ?? control.TextContent ?? string.Empty;
                    return true;
                }
                case "textarea":
                    value = control.GetAttribute("value") ?? control.TextContent ?? string.Empty;
                    return true;
                case "select":
                    value = GetSelectSubmissionValue(control);
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsDisabledControl(Element control)
        {
            if (control == null) return true;
            if (control.HasAttribute("disabled")) return true;

            for (var ancestor = control.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (string.Equals(ancestor.NodeName, "FIELDSET", StringComparison.OrdinalIgnoreCase) &&
                    ancestor.HasAttribute("disabled"))
                {
                    var firstLegend = ancestor.ChildNodes?
                        .OfType<Element>()
                        .FirstOrDefault(child => string.Equals(child.NodeName, "LEGEND", StringComparison.OrdinalIgnoreCase));
                    if (firstLegend != null && IsDescendantOrSelf(control, firstLegend))
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        private static bool IsDescendantOrSelf(Element candidate, Element ancestor)
        {
            if (candidate == null || ancestor == null)
            {
                return false;
            }

            for (var cursor = candidate; cursor != null; cursor = cursor.ParentElement)
            {
                if (ReferenceEquals(cursor, ancestor))
                {
                    return true;
                }
            }

            return false;
        }

        private static Element FindAssociatedLabelControl(Element label)
        {
            if (label == null || !string.Equals(label.NodeName, "LABEL", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var forId = label.GetAttribute("for");
            if (!string.IsNullOrWhiteSpace(forId))
            {
                var root = label.OwnerDocument?.DocumentElement;
                var byId = root != null ? FindDescendantById(root, forId) : null;
                if (IsLabelableControl(byId))
                {
                    return byId;
                }
            }

            return FindFirstLabelableDescendant(label);
        }

        private static Element FindDescendantById(Node node, string id)
        {
            if (node is Element element &&
                string.Equals(element.GetAttribute("id"), id, StringComparison.Ordinal))
            {
                return element;
            }

            if (node?.ChildNodes == null)
            {
                return null;
            }

            foreach (var child in node.ChildNodes)
            {
                var match = FindDescendantById(child, id);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static Element FindFirstLabelableDescendant(Node node)
        {
            if (node?.ChildNodes == null)
            {
                return null;
            }

            foreach (var child in node.ChildNodes)
            {
                if (child is Element childElement && IsLabelableControl(childElement))
                {
                    return childElement;
                }

                var nested = FindFirstLabelableDescendant(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static bool IsLabelableControl(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var tag = element.NodeName?.ToLowerInvariant() ?? string.Empty;
            if (tag == "button" || tag == "meter" || tag == "output" || tag == "progress" ||
                tag == "select" || tag == "textarea")
            {
                return true;
            }

            if (tag != "input")
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
            return type != "hidden";
        }

        private static string GetSelectSubmissionValue(Element select)
        {
            if (select == null) return string.Empty;

            var options = select
                .Descendants()
                .OfType<Element>()
                .Where(el => string.Equals(el.NodeName, "OPTION", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (options.Count == 0) return string.Empty;

            var selected = options.FirstOrDefault(opt => opt.HasAttribute("selected")) ?? options[0];
            return selected.GetAttribute("value") ?? selected.TextContent ?? string.Empty;
        }

        private static string AppendQueryToUri(Uri baseUri, IReadOnlyList<KeyValuePair<string, string>> fields)
        {
            if (baseUri == null) return string.Empty;
            if (fields == null || fields.Count == 0) return baseUri.AbsoluteUri;

            var builder = new UriBuilder(baseUri);
            var existing = builder.Query;
            if (!string.IsNullOrEmpty(existing) && existing[0] == '?')
            {
                existing = existing.Substring(1);
            }

            var encoded = string.Join("&", fields.Select(pair =>
                $"{EncodeFormComponent(pair.Key)}={EncodeFormComponent(pair.Value)}"));

            builder.Query = string.IsNullOrEmpty(existing) ? encoded : $"{existing}&{encoded}";
            return builder.Uri.AbsoluteUri;
        }

        private static string EncodeFormComponent(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty).Replace("%20", "+");
        }

        public async Task HandleKeyPress(string key)
        {
            if (_focusedElement == null)
            {
                var recovered = RecoverFocusedElementForTyping();
                if (recovered == null)
                {
                    return;
                }

                SetFocusedElementState(recovered, fromKeyboard: true);
                bool recoveredIsContentEditable = string.Equals(recovered.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                var recoveredValue = recoveredIsContentEditable ? (recovered.TextContent ?? string.Empty) : GetTextEntryValue(recovered);
                _cursorIndex = recoveredValue.Length;
                _selectionAnchor = -1;
            }
            
            try
            {
                // If focus sits on a wrapper, redirect typing into an editable descendant.
                var tag = _focusedElement.NodeName?.ToLowerInvariant();
                if (tag != "input" && tag != "textarea")
                {
                    var nestedEditable = _focusedElement
                        .Descendants()
                        .OfType<Element>()
                        .FirstOrDefault(el =>
                        {
                            return IsTextEntryElement(el);
                        });
                    if (nestedEditable != null)
                    {
                        SetFocusedElementState(nestedEditable);
                        tag = _focusedElement.NodeName?.ToLowerInvariant();
                    }
                }

                bool isContentEditable = string.Equals(_focusedElement.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                if (tag == "button")
                {
                    if (IsButtonActivationKey(key) && !IsDisabledControl(_focusedElement))
                    {
                        await HandleElementClick(_focusedElement).ConfigureAwait(false);
                    }

                    return;
                }

                if (tag == "input" || tag == "textarea") // Added textarea support
                {
                    var val = GetTextEntryValue(_focusedElement);
                    
                    // Normalize selection indices
                    int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
                    int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
                    bool hasSelection = end > start;
                    
                    // Clamp cursor
                    if (_cursorIndex > val.Length) _cursorIndex = val.Length;
                    if (_cursorIndex < 0) _cursorIndex = 0;
                    
                    bool submitted = false;
                    if (key == "Backspace")
                    {
                        if (hasSelection)
                        {
                            val = val.Remove(start, end - start);
                            _cursorIndex = start;
                            _selectionAnchor = -1;
                        }
                        else if (_cursorIndex > 0 && val.Length > 0)
                        {
                             val = val.Remove(_cursorIndex - 1, 1);
                             _cursorIndex--;
                        }
                    }
                    else if (key == "Delete")
                    {
                        if (hasSelection)
                        {
                             val = val.Remove(start, end - start);
                             _cursorIndex = start;
                             _selectionAnchor = -1;
                        }
                        else if (_cursorIndex < val.Length)
                        {
                            val = val.Remove(_cursorIndex, 1);
                        }
                    }
                    else if (key == "ArrowLeft")
                    {
                        if (_cursorIndex > 0) _cursorIndex--;
                        _selectionAnchor = -1;
                    }
                    else if (key == "ArrowRight")
                    {
                        if (_cursorIndex < val.Length) _cursorIndex++;
                        _selectionAnchor = -1;
                    }
                    else if (key == "Home")
                    {
                        _cursorIndex = 0;
                        _selectionAnchor = -1;
                    }
                    else if (key == "End")
                    {
                        _cursorIndex = val.Length;
                        _selectionAnchor = -1;
                    }
                    else if (key == "Enter")
                    {
                        if (tag == "input")
                        {
                            submitted = await SubmitFormAsync(_focusedElement);
                        }
                        else if (tag == "textarea" && ShouldSubmitOnEnterTextArea(_focusedElement))
                        {
                            submitted = await SubmitFormAsync(_focusedElement);
                        }

                        if (!submitted && tag == "textarea")
                        {
                            if (hasSelection)
                            {
                                val = val.Remove(start, end - start);
                                _cursorIndex = start;
                                _selectionAnchor = -1;
                            }

                            val = val.Insert(_cursorIndex, "\n");
                            _cursorIndex++;
                        }
                    }
                    else if (key.Length == 1) // Normal char
                    {
                        if (hasSelection)
                        {
                            val = val.Remove(start, end - start);
                            _cursorIndex = start;
                            _selectionAnchor = -1;
                        }
                        val = val.Insert(_cursorIndex, key);
                        _cursorIndex++;
                    }
                    
                    if (!submitted)
                    {
                        SetTextEntryValue(_focusedElement, val);
                        
                        // Trigger Repaint
                        TryInvokeRepaintReady(_engine.GetActiveDom());
                    }
                }
                else if (isContentEditable)
                {
                    var val = _focusedElement.TextContent ?? "";

                    int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
                    int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
                    bool hasSelection = end > start;

                    if (_cursorIndex > val.Length) _cursorIndex = val.Length;
                    if (_cursorIndex < 0) _cursorIndex = 0;

                    if (key == "Backspace")
                    {
                        if (hasSelection)
                        {
                            val = val.Remove(start, end - start);
                            _cursorIndex = start;
                            _selectionAnchor = -1;
                        }
                        else if (_cursorIndex > 0 && val.Length > 0)
                        {
                            val = val.Remove(_cursorIndex - 1, 1);
                            _cursorIndex--;
                        }
                    }
                    else if (key == "Delete")
                    {
                        if (hasSelection)
                        {
                            val = val.Remove(start, end - start);
                            _cursorIndex = start;
                            _selectionAnchor = -1;
                        }
                        else if (_cursorIndex < val.Length)
                        {
                            val = val.Remove(_cursorIndex, 1);
                        }
                    }
                    else if (key == "ArrowLeft")
                    {
                        if (_cursorIndex > 0) _cursorIndex--;
                        _selectionAnchor = -1;
                    }
                    else if (key == "ArrowRight")
                    {
                        if (_cursorIndex < val.Length) _cursorIndex++;
                        _selectionAnchor = -1;
                    }
                    else if (key == "Home")
                    {
                        _cursorIndex = 0;
                        _selectionAnchor = -1;
                    }
                    else if (key == "End")
                    {
                        _cursorIndex = val.Length;
                        _selectionAnchor = -1;
                    }
                    else if (key == "Enter")
                    {
                        if (hasSelection)
                        {
                            val = val.Remove(start, end - start);
                            _cursorIndex = start;
                            _selectionAnchor = -1;
                        }
                        val = val.Insert(_cursorIndex, "\n");
                        _cursorIndex++;
                    }
                    else if (key.Length == 1)
                    {
                        if (hasSelection)
                        {
                            val = val.Remove(start, end - start);
                            _cursorIndex = start;
                            _selectionAnchor = -1;
                        }
                        val = val.Insert(_cursorIndex, key);
                        _cursorIndex++;
                    }

                    _focusedElement.TextContent = val;
                    TryInvokeRepaintReady(_engine.GetActiveDom());
                }
            }
            catch (Exception ex)
            {
                 TryLogError($"[BrowserApi] Error typing key: {ex.Message}", LogCategory.General);
            }
            
            return;
        }

        private static bool IsButtonActivationKey(string key)
        {
            return string.Equals(key, "Enter", StringComparison.Ordinal) ||
                   string.Equals(key, " ", StringComparison.Ordinal) ||
                   string.Equals(key, "Space", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSubmitOnEnterTextArea(Element textarea)
        {
            if (!string.Equals(textarea?.NodeName, "TEXTAREA", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var enterKeyHint = textarea.GetAttribute("enterkeyhint");
            if (string.Equals(enterKeyHint, "search", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(enterKeyHint, "go", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var role = textarea.GetAttribute("role");
            if (string.Equals(role, "combobox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "searchbox", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var id = textarea.GetAttribute("id");
            if (string.Equals(id, "APjFqb", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var classAttr = textarea.GetAttribute("class");
            if (ContainsCssClass(classAttr, "gLFyf"))
            {
                return true;
            }

            var ariaLabel = textarea.GetAttribute("aria-label");
            return !string.IsNullOrWhiteSpace(ariaLabel) &&
                   ariaLabel.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsCssClass(string classList, string token)
        {
            if (string.IsNullOrWhiteSpace(classList) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var segments = classList.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Any(segment => string.Equals(segment, token, StringComparison.Ordinal));
        }

        private Element RecoverFocusedElementForTyping()
        {
            var fromLastClick = ResolveEditableCandidate(_lastClickTarget);
            if (fromLastClick != null)
            {
                return fromLastClick;
            }

            var activeDom = _engine?.GetActiveDom();
            var activeDocument = activeDom as Document ?? activeDom?.OwnerDocument;
            var fromActiveElement = ResolveEditableCandidate(activeDocument?.ActiveElement);
            if (fromActiveElement != null)
            {
                return fromActiveElement;
            }

            if (activeDom != null)
            {
                var firstEditable = activeDom
                    .Descendants()
                    .OfType<Element>()
                    .FirstOrDefault(IsTextEntryElement);
                if (firstEditable != null)
                {
                    return firstEditable;
                }
            }

            return null;
        }

        private static Element ResolveEditableCandidate(Element candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            if (IsTextEntryElement(candidate))
            {
                return candidate;
            }

            for (Element ancestor = candidate.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (IsTextEntryElement(ancestor))
                {
                    return ancestor;
                }
            }

            return candidate
                .Descendants()
                .OfType<Element>()
                .FirstOrDefault(IsTextEntryElement);
        }

        public Task ReleaseActionsAsync()
        {
            // Release all pressed keys and pointer buttons
            _pressedKeys.Clear();
            return Task.CompletedTask;
        }

        // Alerts - connected to JavaScript engine
        private string _pendingAlertText = null;
        private string _pendingPromptResponse = null;
        private string _pendingDialogType = null;
        private string _pendingPromptDefaultValue = null;
        private string _unhandledPromptBehavior = "dismiss and notify";

        /// <summary>
        /// Called by JavaScript engine when alert/confirm/prompt is triggered
        /// </summary>
        public void TriggerAlert(string text)
        {
            _pendingAlertText = text;
            _pendingDialogType = "alert";
            _pendingPromptDefaultValue = null;
        }

        public void TriggerConfirm(string text)
        {
            _pendingAlertText = text;
            _pendingDialogType = "confirm";
            _pendingPromptDefaultValue = null;
        }

        public void TriggerPrompt(string text, string defaultValue)
        {
            _pendingAlertText = text;
            _pendingDialogType = "prompt";
            _pendingPromptDefaultValue = defaultValue ?? string.Empty;
        }

        public void SetUnhandledPromptBehavior(string behavior)
        {
            _unhandledPromptBehavior = NormalizeUnhandledPromptBehavior(behavior);
        }

        public Task<bool> HasAlertAsync()
        {
            return Task.FromResult(_pendingAlertText != null);
        }

        public Task DismissAlertAsync()
        {
            ApplyPendingDialogReturnValue(accepted: false);
            _pendingAlertText = null;
            _pendingPromptResponse = null;
            _pendingDialogType = null;
            _pendingPromptDefaultValue = null;
            return Task.CompletedTask;
        }

        public Task AcceptAlertAsync()
        {
            ApplyPendingDialogReturnValue(accepted: true);
            _pendingAlertText = null;
            _pendingDialogType = null;
            _pendingPromptDefaultValue = null;
            return Task.CompletedTask;
        }

        public Task<string> GetAlertTextAsync()
        {
            return Task.FromResult(_pendingAlertText ?? "");
        }

        public Task SendAlertTextAsync(string text)
        {
            // For prompt() dialogs
            if (!string.Equals(_pendingDialogType, "prompt", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("element not interactable");
            }

            _pendingPromptResponse = text;
            return Task.CompletedTask;
        }

        private void ApplyPendingDialogReturnValue(bool accepted)
        {
            if (string.Equals(_pendingDialogType, "confirm", StringComparison.Ordinal))
            {
                TrySetWindowDialogReturnValue(accepted ? "true" : "false");
                return;
            }

            if (string.Equals(_pendingDialogType, "prompt", StringComparison.Ordinal))
            {
                if (!accepted)
                {
                    TrySetWindowDialogReturnValue("null");
                    return;
                }

                var response = _pendingPromptResponse ?? _pendingPromptDefaultValue ?? string.Empty;
                TrySetWindowDialogReturnValue(JsonSerializer.Serialize(response));
            }
        }

        private void TrySetWindowDialogReturnValue(string jsLiteral)
        {
            if (string.IsNullOrWhiteSpace(jsLiteral))
            {
                return;
            }

            try
            {
                _engine.Evaluate($"window.dialog_return_value = {jsLiteral}; if (typeof window !== 'undefined' && Object.prototype.hasOwnProperty.call(window, 'result')) window.result = {jsLiteral};");
            }
            catch
            {
                // Best-effort state sync for user prompt fixtures.
            }
        }

        private bool ShouldAutoAcceptDialogs()
        {
            return string.Equals(_unhandledPromptBehavior, "accept", StringComparison.Ordinal) ||
                   string.Equals(_unhandledPromptBehavior, "accept and notify", StringComparison.Ordinal);
        }

        private static string NormalizeUnhandledPromptBehavior(string behavior)
        {
            if (string.IsNullOrWhiteSpace(behavior))
            {
                return "dismiss and notify";
            }

            var normalized = behavior.Trim().ToLowerInvariant();
            return normalized switch
            {
                "dismiss" => "dismiss",
                "accept" => "accept",
                "dismiss and notify" => "dismiss and notify",
                "accept and notify" => "accept and notify",
                "ignore" => "ignore",
                _ => "dismiss and notify"
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            ElementStateManager.Instance.OnStateChanged -= _elementStateChangedHandler;
            FenBrowser.Core.Dom.V2.Element.StyleAttributeChanged -= _styleAttributeChangedHandler;
            var debounce = Interlocked.Exchange(ref _interactionRecascadeDebounce, null);
            // Only cancel here; the pending debounce task owns disposal of its
            // CancellationTokenSource (see RunInteractionRecascadeAsync). Disposing
            // from two callers races and throws ObjectDisposedException under load.
            debounce?.Cancel();

            if (_fontLoadedHandler != null)
                FontRegistry.FontLoaded -= _fontLoadedHandler;

            // Clear ImageLoader static callbacks only if they still point at this host's
            // context. Without this guard, parallel/sequential test runs end up with
            // stale closures invoking disposed engines.
            if (ReferenceEquals(ImageLoader.RequestRepaint, _imageLoaderContext?.RequestRepaint))
            {
                ImageLoader.RequestRepaint = null;
            }
            if (ReferenceEquals(ImageLoader.FetchBytesAsync, _imageLoaderContext?.FetchBytesAsync))
            {
                ImageLoader.FetchBytesAsync = null;
            }
            if (ReferenceEquals(ImageLoader.FetchDetailedAsync, _imageLoaderContext?.FetchDetailedAsync))
            {
                ImageLoader.FetchDetailedAsync = null;
            }
            if (ReferenceEquals(ImageLoader.RequestRelayout, _imageLoaderContext?.RequestRelayout))
            {
                ImageLoader.RequestRelayout = null;
            }
            if (ReferenceEquals(FontRegistry.FetchDetailedAsync, _fontLoaderContext?.FetchDetailedAsync))
            {
                FontRegistry.FetchDetailedAsync = null;
            }
            if (ReferenceEquals(
                    FontRegistry.FetchDetailedForDocumentAsync,
                    _fontLoaderContext?.FetchDetailedForDocumentAsync))
            {
                FontRegistry.FetchDetailedForDocumentAsync = null;
            }

            _disposed = true;
            try { _engine.Dispose(); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] Engine dispose failed: {ex.Message}", LogCategory.General); }
        }

        private void ScheduleInteractionRecascade()
        {
            // Each call owns a dedicated CancellationTokenSource. Ownership is
            // transferred to the spawned task, which is the *only* code that
            // disposes it. The caller here must never dispose a token source
            // that another task may still be using: doing so races with the
            // task's own Dispose and throws ObjectDisposedException under load.
            var owner = new CancellationTokenSource();
            var token = owner.Token;

            var previous = Interlocked.Exchange(ref _interactionRecascadeDebounce, owner);
            previous?.Cancel();

            _ = RunInteractionRecascadeAsync(owner, token);
        }

        private async Task RunInteractionRecascadeAsync(
            CancellationTokenSource owner,
            CancellationToken token)
        {
            try
            {
                await Task.Delay(75, token).ConfigureAwait(false);

                if (!_disposed)
                {
                    _engine.ScheduleRecascade();
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer interaction recascade request.
            }
            finally
            {
                // Only the owning task disposes the source, avoiding any
                // double-dispose race with the caller or with Dispose().
                Interlocked.CompareExchange(ref _interactionRecascadeDebounce, null, owner);
                owner.Dispose();
            }
        }

        // IHistoryBridge Implementation
        public int Length => _history.Count;
        public object State => (_historyIndex >= 0 && _historyIndex < _history.Count) ? _history[_historyIndex].State : null;
        public Uri CurrentUrl => _current;

        public void PushState(object state, string title, string url)
        {
            try
            {
                var newUri = string.IsNullOrEmpty(url) ? _current : new Uri(_current, url);
                
                // Truncate forward history
                if (_historyIndex < _history.Count - 1)
                {
                    _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
                }

                var entry = new HistoryEntry(newUri, title, state);
                entry.IsPushState = true;
                
                _history.Add(entry);
                _historyIndex = _history.Count - 1;
                _current = newUri;
                
                // Notify UI of URL change without reload
                TryInvokeNavigated(_current);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[BrowserHost] PushState failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        public void ReplaceState(object state, string title, string url)
        {
            try
            {
                if (_historyIndex >= 0 && _historyIndex < _history.Count)
                {
                    var newUri = string.IsNullOrEmpty(url) ? _current : new Uri(_current, url);
                    var entry = _history[_historyIndex];
                    
                    entry.State = state;
                    if (title != null) entry.Title = title;
                    entry.Url = newUri;
                    
                    _current = newUri;
                    
                    // Notify UI of URL change without reload
                    TryInvokeNavigated(_current);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[BrowserHost] ReplaceState failed: {ex.Message}", LogCategory.JavaScript);
            }
        }
        public void Go(int delta)
        {
            _ = GoAsync(delta);
        }

        private async Task GoAsync(int delta)
        {
            try
            {
                int targetIndex = _historyIndex + delta;
                if (targetIndex < 0 || targetIndex >= _history.Count)
                {
                    return;
                }

                if (delta == 0)
                {
                    await RefreshAsync();
                    return;
                }

                if (delta == -1)
                {
                    await GoBackAsync();
                    return;
                }

                if (delta == 1)
                {
                    await GoForwardAsync();
                    return;
                }

                // Walk step-by-step through intermediate history entries,
                // firing popstate for pushState entries and navigating for real entries.
                int step = delta > 0 ? 1 : -1;
                while (_historyIndex != targetIndex)
                {
                    _historyIndex += step;
                    var entry = _history[_historyIndex];
                    if (entry.IsPushState)
                    {
                        _current = entry.Url;
                        _engine.NotifyPopState(entry.State);
                        TryInvokeNavigated(_current);
                        continue;
                    }

                    await NavigateAsync(entry.Url.AbsoluteUri);
                    // After a real navigation, stop traversing - the page reloaded.
                    break;
                }
            }
            catch (Exception ex)
            {
                TryLogWarn($"[BrowserHost] Go(delta={delta}) failed: {ex.Message}", LogCategory.Navigation);
            }
        }
    }
}


