using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Security;
using FenBrowser.FenEngine.Security; // Added
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.DevTools;
using FenBrowser.FenEngine.Layout;
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
        private readonly CustomHtmlEngine _engine = new CustomHtmlEngine();
        private readonly ResourceManager _resources;
        private readonly NavigationManager _navManager;
        private readonly BrowserHostOptions _options;
        private readonly NavigationLifecycleTracker _navigationLifecycle = new NavigationLifecycleTracker();
        private readonly NavigationSubresourceTracker _navigationSubresources = new NavigationSubresourceTracker();
        private readonly FenBrowser.FenEngine.Core.EngineLoop _engineLoop; // Phase 5: Engine Loop
        private readonly InputManager _inputManager = new InputManager();
        private Uri _current;
        private long _latestNavigationId;
        private long _activeRenderNavigationId;
        private long _googleChallengeRecoveryFollowedNavigationId = -1;
        private long _engineDiagnosticsCapturedNavigationId;
        private long _renderedDiagnosticsCapturedNavigationId;
        private int _engineDiagnosticsCapturedNodeCount;
        private int _renderedDiagnosticsCapturedTextLength;
        private int _renderedDiagnosticsCapturedContentHash;
        private bool _disposed;
        
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
        private readonly string _imageLoaderContextId = Guid.NewGuid().ToString("N");

        // External renderer reference: BrowserIntegration injects the actual renderer used for
        // painting so that hit tests in DispatchInputEvent use the correct (populated) paint tree
        // instead of the stale _engine._cachedRenderer that never has Render() called on it.
        private SkiaDomRenderer _activeRenderer;
        public void SetActiveRenderer(SkiaDomRenderer renderer) { _activeRenderer = renderer; }

        private (double? Width, double? Height) GetRenderViewportHint()
        {
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

        // Tracks whether the last dispatched click event allowed default action.
        // BrowserIntegration triggers DOM click first, then calls HandleElementClick for fallback activation.
        private bool _lastClickDefaultAllowed = true;
        private bool _lastClickHadTarget;
        private Element _lastClickTarget;
        private bool _pendingWebDriverClickPointValid;
        private int _pendingWebDriverClickClientX;
        private int _pendingWebDriverClickClientY;
        private bool _suppressNextDomClickDispatchInHandleElementClick;

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
            
            _resources = new ResourceManager(httpClient, isPrivate);
            _engine.CookieJar = _resources.CookieJar;

            // Wire up Fetch API (Phase 8)
            _engine.FetchHandler = (req) => 
            {
                req.RequestUri = MapRuntimeUri(req.RequestUri);
                return _resources.SendAsync(req, CurrentPolicy);
            };

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
            ElementStateManager.Instance.OnStateChanged += _ => _engine.ScheduleRecascade();

            // Wire DOM attribute mutations (class/id/style changes from JS or DOM manipulation)
            // â†’ CSS re-cascade.  e.g. element.classList.add('active') must reflect in selectors.
            FenBrowser.Core.Dom.V2.Element.StyleAttributeChanged += _ => _engine.ScheduleRecascade();

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
                FetchBytesAsync = async uri =>
                {
                    if (uri == null)
                    {
                        return null;
                    }

                    var fetchUri = MapRuntimeUri(uri);
                    return await _resources.FetchBytesAsync(
                            fetchUri,
                            referer: _current,
                            accept: "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
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
                        if (dom != null)
                        {
                            RepaintReady?.Invoke(this, dom);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}");
                    }
                }
            };
        }

        public IDisposable EnterImageLoaderContext()
        {
            return ImageLoader.EnterRequestContext(_imageLoaderContext);
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

                if (_googleChallengeRecoveryFollowedNavigationId != navigationId &&
                    TryResolveGoogleSearchRecoveryNavigation(result, out var googleRecoveryUri))
                {
                    TryLogInfo(
                        $"[BrowserHost] Google search challenge detected. Following recovery URL: {googleRecoveryUri.AbsoluteUri}",
                        LogCategory.Navigation);
                    result = await _navManager.NavigateAsync(googleRecoveryUri.AbsoluteUri, NavigationRequestKind.Programmatic);
                    url = googleRecoveryUri.AbsoluteUri;
                    _googleChallengeRecoveryFollowedNavigationId = navigationId;
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
                    // Render error page
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
                await MarkNavigationCompleteWhenSettledAsync(navigationId, "document-complete").ConfigureAwait(false);
                
                // Fetch Favicon
                _ = FetchFaviconAsync(uri);
                
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
            return _engine.Evaluate(script);
        }

        public async Task<string> FindElementAsync(string strategy, string value)
        {
            await Task.CompletedTask;
            var searchRoot = ResolveSearchRoot();
            if (searchRoot == null) throw new InvalidOperationException("No active frame DOM");

            Element found = null;
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    found = searchRoot.SelfAndDescendants().OfType<Element>().FirstOrDefault(n => n.Id == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    found = searchRoot.SelfAndDescendants().OfType<Element>().FirstOrDefault(n => n.GetAttribute("class") != null && n.GetAttribute("class").Contains(cls));
                }
                else
                {
                    found = searchRoot.SelfAndDescendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.TagName, value, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (strategy == "xpath")
            {
                if (value.StartsWith("//"))
                {
                    var tag = value.Substring(2);
                    found = searchRoot.SelfAndDescendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.TagName, tag, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (found != null)
            {
                return GetOrRegisterElementId(found);
            }
            throw new KeyNotFoundException("Element not found");
        }

        public async Task ClickElementAsync(string elementId)
        {
            var element = ResolveElementInActiveContextOrThrow(elementId);
            if (element != null)
            {
                var dispatchedByScript = false;
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

                if (!_pendingWebDriverClickPointValid)
                {
                    _pendingWebDriverClickClientX = 0;
                    _pendingWebDriverClickClientY = 0;
                }

                dispatchedByScript = await TryDispatchWebDriverClickViaScriptAsync(
                    elementId,
                    _pendingWebDriverClickClientX,
                    _pendingWebDriverClickClientY).ConfigureAwait(false);

                if (!dispatchedByScript && _pendingWebDriverClickPointValid)
                {
                }

                _suppressNextDomClickDispatchInHandleElementClick = dispatchedByScript;
                await HandleElementClick(element);
            }
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
            catch (Exception ex)
            {
            }

            return false;
        }

        private async Task<bool> TryDispatchWebDriverClickViaScriptAsync(string elementId, int clientX, int clientY)
        {
            try
            {
                var dispatchResult = await ExecuteScriptAsync(
                    "var el = arguments[0];" +
                    "if (!el) return false;" +
                    "var cx = Number(arguments[1]); var cy = Number(arguments[2]);" +
                    "if (!isFinite(cx)) cx = 0; if (!isFinite(cy)) cy = 0;" +
                    "var evt;" +
                    "try {" +
                    "  evt = new MouseEvent('click', { bubbles: true, cancelable: true, composed: true, clientX: cx, clientY: cy, screenX: cx, screenY: cy });" +
                    "} catch (e) {" +
                    "  evt = document.createEvent('MouseEvents');" +
                    "  evt.initMouseEvent('click', true, true, window, 1, cx, cy, cx, cy, false, false, false, false, 0, null);" +
                    "}" +
                    "return el.dispatchEvent(evt);",
                    new object[] { elementId, clientX, clientY }).ConfigureAwait(false);

                if (dispatchResult is bool boolResult)
                {
                    return boolResult;
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

        private async Task FetchFaviconAsync(Uri pageUrl)
        {
            try
            {
                string iconUrl = null;
                var dom = _engine.GetActiveDom();
                
                // 1. Try to find link tag in DOM
                if (dom != null)
                {
                    // Find <link rel="icon" ...>
                    var links = dom.Descendants().OfType<Element>().Where(x => x.TagName == "link" && x.Attr != null && x.Attr.ContainsKey("rel"));
                    var iconLink = links.FirstOrDefault(x => x.Attr["rel"].IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0);
                    
                    if (iconLink != null && iconLink.Attr.ContainsKey("href"))
                    {
                        iconUrl = iconLink.Attr["href"];
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
                    // 3. Fetch Image
                    using var stream = await _resources.FetchImageAsync(absoluteIconUri, pageUrl);
                    if (stream != null)
                    {
                        // Decode
                        // Copy to memory stream if needed for Skia
                        using var ms = new System.IO.MemoryStream();
                        await stream.CopyToAsync(ms);
                        ms.Position = 0;
                        
                        var bitmap = SKBitmap.Decode(ms);
                        if (bitmap != null)
                        {
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

            var detail = string.IsNullOrWhiteSpace(baseDetail)
                ? settleDetail
                : $"{baseDetail};{settleDetail}";
            _navigationLifecycle.MarkComplete(navigationId, detail);
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

        private async Task<string> WaitForSubresourceSettleDetailAsync(long navigationId)
        {
            const int settleTimeoutMs = 1500;
            const string settledState = "subresources=settled;renderSubresourcesPending=0;imagesPending=0;fontsPending=0;tasksPending=0;microtasksPending=0";
            var loop = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;

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

            // Throttle to ~60 fps (16 ms) to avoid flooding the input queue on high-frequency devices
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long ticksPer16ms = System.Diagnostics.Stopwatch.Frequency / 60;
            if (now - _lastMouseMoveTick < ticksPer16ms)
                return;
            _lastMouseMoveTick = now;
            _lastMouseMoveX = x;
            _lastMouseMoveY = y;
            _hasLastMouseMovePosition = true;
            QueueInputTask("mousemove", x, y, 0);
        }

        private static bool ShouldRetryTopLevelNavigation(FetchResult result, string url, int attempt, int maxAttempts)
        {
            if (attempt >= maxAttempts) return false;
            if (!IsRetriableTopLevelScheme(url)) return false;
            if (result == null) return true;
            return result.Status == FetchStatus.ConnectionFailed || result.Status == FetchStatus.Timeout;
        }

        private static bool TryResolveGoogleSearchRecoveryNavigation(FetchResult result, out Uri recoveryUri)
        {
            recoveryUri = null;
            if (result == null || result.Status != FetchStatus.Success || string.IsNullOrWhiteSpace(result.Content))
            {
                return false;
            }

            var finalUri = result.FinalUri;
            if (finalUri == null || !IsGoogleSearchUri(finalUri))
            {
                return false;
            }

            // Already on recovery URL; avoid loops.
            if (finalUri.Query.IndexOf("emsg=SG_REL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var html = result.Content;
            var hasGoogleTroubleBanner =
                html.IndexOf("id=\"yvlrue\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                html.IndexOf("id='yvlrue'", StringComparison.OrdinalIgnoreCase) >= 0 ||
                html.IndexOf("trouble accessing google search", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasGoogleTroubleBanner)
            {
                return false;
            }

            var linkMatch = System.Text.RegularExpressions.Regex.Match(
                html,
                "href\\s*=\\s*['\\\"](?<href>[^'\\\"]*emsg=SG_REL[^'\\\"]*)['\\\"]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!linkMatch.Success)
            {
                return false;
            }

            var hrefValue = System.Net.WebUtility.HtmlDecode(linkMatch.Groups["href"].Value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(hrefValue))
            {
                return false;
            }

            if (!Uri.TryCreate(finalUri, hrefValue, out var candidate))
            {
                return false;
            }

            if (!IsGoogleSearchUri(candidate))
            {
                return false;
            }

            recoveryUri = candidate;
            return true;
        }

        private static bool IsGoogleSearchUri(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var host = uri.Host ?? string.Empty;
            var isGoogleHost =
                host.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);
            if (!isGoogleHost)
            {
                return false;
            }

            return uri.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase);
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

        private void QueueInputTask(string type, float x, float y, int button)
        {
             // Input must feel immediate; dispatch directly to avoid coordinator latency
             // or dropped interaction when event-loop pumping is delayed.
             DispatchInputEvent(type, x, y, button);
        }

        private void DispatchInputEvent(string type, float x, float y, int button)
        {
            var eventType = MapToInputEventType(type);
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
                Buttons = BuildButtonMask(button, type),
                PointerId = 1,
                PointerType = "mouse",
                Pressure = button == 0 ? 0.5f : 0.8f,
                IsPrimary = true,
                PageX = x,
                PageY = y,
                ScreenX = x,
                ScreenY = y
            };

            bool handled = false;
            try
            {
                handled = _inputManager.ProcessEvent(inputEvent, renderContext, context);
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

            if (string.Equals(type, "click", StringComparison.OrdinalIgnoreCase))
            {
                _lastClickHadTarget = inputEvent.Target != null;
                _lastClickTarget = inputEvent.Target;
                // If click target is unknown (stale context), allow fallback activation.
                _lastClickDefaultAllowed = inputEvent.Target == null || handled;
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
        }

        private static int BuildButtonMask(int button, string type)
        {
            if (string.Equals(type, "mouseup", StringComparison.OrdinalIgnoreCase)) return 0;
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

            bool hasHref = !string.IsNullOrWhiteSpace(hovered.GetAttribute("href"));
            bool isFocusable =
                !string.IsNullOrWhiteSpace(hovered.GetAttribute("tabindex")) ||
                string.Equals(hovered.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);

            bool isInteractiveTag =
                string.Equals(tag, "a", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "button", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "input", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "select", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "textarea", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "label", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "summary", StringComparison.OrdinalIgnoreCase);

            if (!isInteractiveTag && !hasHref && !isFocusable)
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
                                frameUri,
                                _current,
                                accept: "text/html,application/xhtml+xml",
                                secFetchDest: "iframe");

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

                frameElement.AppendChild(parsedRoot);
                await TryInitializeFrameScriptsAsync(frameElement, parsedRoot, frameUri).ConfigureAwait(false);
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

        private async Task TryInitializeFrameScriptsAsync(Element frameElement, Element frameRoot, Uri frameUri)
        {
            if (frameElement == null || frameRoot == null)
            {
                return;
            }

            if (string.Equals(frameElement.GetAttribute(WebDriverFrameScriptsHydratedAttribute), "1", StringComparison.Ordinal))
            {
                return;
            }

            var jsEngine = _engine?.JsEngine;
            if (jsEngine == null)
            {
                return;
            }

            try
            {
                await jsEngine.SetDomAsync(frameRoot, frameUri).ConfigureAwait(false);
                frameElement.SetAttribute(WebDriverFrameScriptsHydratedAttribute, "1");
            }
            catch (Exception ex)
            {
                TryLogWarn($"[WebDriverFrame] failed initializing frame scripts: {ex.Message}", LogCategory.Navigation);
            }
        }

        private void SyncScriptContextToSelectedBrowsingContext()
        {
            var jsEngine = _engine?.JsEngine;
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

        public async Task<object> GetElementPropertyAsync(string elementId, string name)
        {
            _ = ResolveElementInActiveContextOrThrow(elementId);

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
            var layout = _engine?.LastLayout;
            var element = ResolveElementInActiveContextOrThrow(elementId);
            if (element == null)
            {
                return new ElementRect { X = 0, Y = 0, Width = 0, Height = 0 };
            }

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

        private static void DispatchDomEvent(Element target, string type, FenBrowser.FenEngine.Core.ExecutionContext context, bool bubbles)
        {
            if (target == null || string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            var domEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                type,
                bubbles: bubbles,
                cancelable: false,
                composed: true,
                context: context);

            FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(target, domEvent, context);
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

            DispatchKeyboardEvent(eventTarget, "keydown", key, eventContext);
            DispatchKeyboardEvent(eventTarget, "keypress", key, eventContext);

            bool valueChanged = false;
            if (shouldMutateFocusedElement)
            {
                var beforeValue = ReadEditableValue(_focusedElement);
                await HandleKeyPress(key).ConfigureAwait(false);
                var afterValue = ReadEditableValue(_focusedElement);
                valueChanged = !string.Equals(beforeValue, afterValue, StringComparison.Ordinal);
            }

            if (valueChanged)
            {
                DispatchDomEvent(_focusedElement ?? eventTarget, "input", eventContext, bubbles: true);
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

        private static void DispatchKeyboardEvent(
            Element target,
            string type,
            string key,
            FenBrowser.FenEngine.Core.ExecutionContext context)
        {
            if (target == null || string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            var domEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                type,
                bubbles: true,
                cancelable: true,
                composed: true,
                context: context);
            domEvent.Set("key", FenBrowser.FenEngine.Core.FenValue.FromString(key ?? string.Empty));

            var keyCode = string.IsNullOrEmpty(key) ? 0 : key[0];
            domEvent.Set("which", FenBrowser.FenEngine.Core.FenValue.FromNumber(keyCode));
            domEvent.Set("keyCode", FenBrowser.FenEngine.Core.FenValue.FromNumber(keyCode));

            FenBrowser.FenEngine.Core.FenValue previousWindowEvent = FenBrowser.FenEngine.Core.FenValue.Undefined;
            FenBrowser.FenEngine.Core.FenValue previousGlobalEvent = FenBrowser.FenEngine.Core.FenValue.Undefined;
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

            FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(target, domEvent, context);

            if (windowValue.IsObject)
            {
                windowValue.AsObject().Set("event", previousWindowEvent);
            }
            if (context?.Environment != null)
            {
                context.Environment.Set("event", previousGlobalEvent);
            }
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

                if (!mappedElement.IsConnected)
                {
                    throw new InvalidOperationException("stale element reference");
                }

                var mappedSearchRoot = ResolveSearchRoot();
                if (mappedSearchRoot == null)
                {
                    if (_currentFrameElement != null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    // Top-level context can transiently have no active search root while
                    // preserving valid mapped references. Keep lookup deterministic.
                    return mappedElement;
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
            
            return rawResult;
        }

        // Mousemove throttle: skip events closer than 16 ms (~60 fps)
        private long _lastMouseMoveTick = 0;
        private float _lastMouseMoveX;
        private float _lastMouseMoveY;
        private bool _hasLastMouseMovePosition;

        // Storage for async script callback result
        private object _asyncScriptResult = null;
        private bool _asyncScriptDone = false;
        private bool _pointerDown = false;
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
            // Reset state
            lock (_asyncScriptLock)
            {
                _asyncScriptResult = null;
                _asyncScriptDone = false;
            }

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
                    var eventLoop = FenBrowser.FenEngine.Core.EventLoop.EventLoopCoordinator.Instance;
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
                    return result;
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

            var snapshot = _engine.GetCookieSnapshot(scope);
            var cookies = snapshot.Select(kv => new WebDriverCookie
            {
                Name = kv.Key,
                Value = kv.Value,
                Domain = scope.Host,
                Path = "/",
                Secure = scope.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
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

            var snapshot = _engine.GetCookieSnapshot(scope);
            if (!snapshot.TryGetValue(name, out var value))
            {
                return Task.FromResult<WebDriverCookie>(null);
            }

            return Task.FromResult<WebDriverCookie>(new WebDriverCookie
            {
                Name = name,
                Value = value,
                Domain = scope.Host,
                Path = "/",
                Secure = scope.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
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
                _engine.SetCookie(scope, cookie.Name, cookie.Value ?? string.Empty, cookie.Path ?? "/");
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
                return Task.CompletedTask;
            }

            var keys = _engine.GetCookieSnapshot(scope).Keys.ToArray();
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
                        _pointerDown = true;
                        // Simulate click on element at current position
                        var elementAtPoint = FindElementAtPoint(_pointerX, _pointerY);
                        if (elementAtPoint != null)
                        {
                            // Trigger click behavior
                            await HandleElementClick(elementAtPoint);
                        }
                        break;

                    case "pointerup":
                        _pointerDown = false;
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
            if (_engine == null || _engine.LastLayout == null || _engine.ActiveDom == null)
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
            
            if (hit != null) TryLogDebug($"[BrowserApi] Hit test at ({docX},{docY}) found: {hit.NodeName} (ID: {hit.Id})", LogCategory.General);
            
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
            }

            _focusedElement = element;

            var ownerDocument = element?.OwnerDocument;
            if (ownerDocument != null)
            {
                ownerDocument.ActiveElement = element;
            }

            ElementStateManager.Instance.SetFocusedElement(element, fromKeyboard);
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
                SetFocusedElementState(target);
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
                SetFocusedElementState(descendantEditable);
                bool descendantIsContentEditable = string.Equals(descendantEditable.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                var val = descendantIsContentEditable ? (descendantEditable.TextContent ?? string.Empty) : GetTextEntryValue(descendantEditable);
                _cursorIndex = val.Length;
                _selectionAnchor = -1;
            }
            else
            {
                SetFocusedElementState(null);
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
            var clickContext = _engine.Context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
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
                var clickEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                    "click",
                    bubbles: true,
                    cancelable: true,
                    composed: true,
                    context: clickContext);
                clickEvent.Set("clientX", FenBrowser.FenEngine.Core.FenValue.FromNumber(clickClientX));
                clickEvent.Set("clientY", FenBrowser.FenEngine.Core.FenValue.FromNumber(clickClientY));
                clickEvent.Set("screenX", FenBrowser.FenEngine.Core.FenValue.FromNumber(clickClientX));
                clickEvent.Set("screenY", FenBrowser.FenEngine.Core.FenValue.FromNumber(clickClientY));
                clickEvent.Set("x", FenBrowser.FenEngine.Core.FenValue.FromNumber(clickClientX));
                clickEvent.Set("y", FenBrowser.FenEngine.Core.FenValue.FromNumber(clickClientY));
                var clickBubbleListeners = FenBrowser.FenEngine.DOM.EventTarget.Registry.Get(element, "click", false).Count;
                var clickCaptureListeners = FenBrowser.FenEngine.DOM.EventTarget.Registry.Get(element, "click", true).Count;
                clickNotPrevented = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(element, clickEvent, clickContext);
            }
            else
            {
            }
            if (!clickNotPrevented)
            {
                allowDefaultActivation = false;
            }

            // Wrapper-first DOMs (e.g. Google search box) often receive click on a container.
            // Promote to a descendant editable so focus/typing remains stable.
            if (tag != "input" &&
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
            
            // Keep native control activation resilient even when page scripts call
            // preventDefault() on wrapper click handlers.
            if (tag == "input" ||
                tag == "textarea" ||
                tag == "button" ||
                tag == "select" ||
                string.Equals(element.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase))
            {
                allowDefaultActivation = true;
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
            // Handle button clicks
            else if (tag == "button" || (tag == "input" && 
                (element.GetAttribute("type")?.ToLowerInvariant() == "submit" ||
                 element.GetAttribute("type")?.ToLowerInvariant() == "button")))
            {
                 // Verify if this is a search button (simplified check)
                 TryLogDebug($"[BrowserApi] Button clicked: {element.NodeName}", LogCategory.General);

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
                
                TryLogDebug($"[BrowserApi] Input focused: {element.NodeName} (ID: {element.GetAttribute("id")})", LogCategory.General);
                
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
                    TryLogDebug($"[BrowserApi] Element focused: {element.NodeName} (ID: {element.GetAttribute("id")})", LogCategory.General);
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

        private object ConvertFenValueForWebDriver(FenBrowser.FenEngine.Core.FenValue fenValue)
        {
            return ConvertFenValueForWebDriver(fenValue, new HashSet<FenBrowser.FenEngine.Core.Interfaces.IObject>(ReferenceEqualityComparer.Instance), 0);
        }

        private object ConvertFenValueForWebDriver(
            FenBrowser.FenEngine.Core.FenValue fenValue,
            HashSet<FenBrowser.FenEngine.Core.Interfaces.IObject> visited,
            int depth)
        {
            if (fenValue == null)
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
                if (frameElementValue != null &&
                    frameElementValue.IsObject &&
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
                if (selfValue != null &&
                    selfValue.IsObject &&
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
                if (lengthValue != null && lengthValue.IsNumber)
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

        private async Task<bool> SubmitFormAsync(Element submitter)
        {
            var form = FindAncestorForm(submitter);
            if (form == null) return false;

            var context = _engine.Context ?? new FenBrowser.FenEngine.Core.ExecutionContext();
            var submitEvent = new FenBrowser.FenEngine.DOM.DomEvent(
                "submit",
                bubbles: true,
                cancelable: true,
                composed: true,
                context: context);

            bool allowSubmit = FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(form, submitEvent, context);
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
            _pointerDown = false;
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
                _engine.Evaluate($"window.dialog_return_value = {jsLiteral};");
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
            
            if (_fontLoadedHandler != null)
                FontRegistry.FontLoaded -= _fontLoadedHandler;

            _disposed = true;
            try { _engine.Dispose(); }
            catch (Exception ex) { TryLogWarn($"[BrowserHost] Engine dispose failed: {ex.Message}", LogCategory.General); }
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



