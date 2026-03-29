using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Network;
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
        Task SendKeysToElementAsync(string elementId, string text);

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
        private bool _disposed;
        
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private int _historyIndex = -1;
        private bool _isNavigatingHistory;
        
        // Map WebDriver element and shadow-root IDs to live DOM nodes.
        private readonly Dictionary<string, Element> _elementMap = new Dictionary<string, Element>();
        private readonly Dictionary<string, ShadowRoot> _shadowRootMap = new Dictionary<string, ShadowRoot>();
        private readonly Stack<Element> _frameContextStack = new Stack<Element>();
        private Element _currentFrameElement;
        private Action<string> _fontLoadedHandler;

        // External renderer reference: BrowserIntegration injects the actual renderer used for
        // painting so that hit tests in DispatchInputEvent use the correct (populated) paint tree
        // instead of the stale _engine._cachedRenderer that never has Render() called on it.
        private SkiaDomRenderer _activeRenderer;
        public void SetActiveRenderer(SkiaDomRenderer renderer) { _activeRenderer = renderer; }

        // Tracks whether the last dispatched click event allowed default action.
        // BrowserIntegration triggers DOM click first, then calls HandleElementClick for fallback activation.
        private bool _lastClickDefaultAllowed = true;
        private bool _lastClickHadTarget;
        private Element _lastClickTarget;

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
            try { FenLogger.Debug(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Debug log failed: {ex.Message}"); }
        }

        private static void TryLogInfo(string message, LogCategory category = LogCategory.General)
        {
            try { FenLogger.Info(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Info log failed: {ex.Message}"); }
        }

        private static void TryLogWarn(string message, LogCategory category = LogCategory.General)
        {
            try { FenLogger.Warn(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Warn log failed: {ex.Message}"); }
        }

        private static void TryLogError(string message, LogCategory category = LogCategory.General)
        {
            try { FenLogger.Error(message, category); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
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
            FenLogger.Info($"[BrowserHost] HTTP/2: {config.EnableHttp2}, Brotli: {config.EnableBrotli}, " +
                          $"Version: {httpClient.DefaultRequestVersion}", LogCategory.Network);
            
            _resources = new ResourceManager(httpClient, isPrivate);

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
                         FenLogger.Debug($"[Compliance] HTTP Request: {req.Method} {req.RequestUri} Headers: {JsonSerializer.Serialize(dump)}", LogCategory.Network);
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
                                FenLogger.Warn($"[nosniff] Blocked script â€” Content-Type '{scriptResult.ContentType}' is not a JS MIME type: {u}", LogCategory.JavaScript);
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
            
            ImageLoader.FetchBytesAsync = async (uri) =>
            {
                if (uri == null) return null;
                var fetchUri = MapRuntimeUri(uri);
                return await _resources.FetchBytesAsync(
                        fetchUri,
                        referer: _current,
                        accept: "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
                        secFetchDest: "image")
                    .ConfigureAwait(false);
            };

            // Wire up ImageLoader to trigger RepaintReady when images finish loading
            ImageLoader.RequestRepaint = () =>
            {
                try
                {
                    FenLogger.Debug($"[ImageLoader-Repaint] Triggering repaint after image load", LogCategory.Rendering);
                    RepaintReady?.Invoke(this, null);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            ImageLoader.RequestRelayout = () =>
            {
                try
                {
                    FenLogger.Debug($"[ImageLoader-Relayout] Triggering re-layout after image load", LogCategory.Rendering);
                    var dom = _engine.GetActiveDom();
                    if (dom != null) RepaintReady?.Invoke(this, dom);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
            };

            // Wire up FontRegistry to trigger full relayout/repaint when fonts finish loading
            _fontLoadedHandler = (family) =>
            {
                try
                {
                    FenLogger.Debug($"[FontRegistry-Repaint] Triggering relayout after font load: {family}", LogCategory.Rendering);
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

        private Uri MapRuntimeUri(Uri uri)
        {
            return _options.MapRequestUri(uri);
        }

        private CertificateInfo _lastCertificate;
        private System.Net.Security.SslPolicyErrors _lastSslErrors;
        public CertificateInfo CurrentCertificate => _lastCertificate;


        public Element GetDomRoot()
        {
            var dom = _engine.GetActiveDom();
            return (dom as Element) ?? (dom as Document)?.DocumentElement;
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
                _frameContextStack.Clear();
                _elementMap.Clear();

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
                        elem = await _engine.RenderAsync(sb.ToString(), _current, trackedCssFetcher, trackedImageFetcher, u => { _ = NavigateAsync(u.AbsoluteUri); });
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
                Console.WriteLine($"[NavigateAsync] Start: {url}");

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
                    FenLogger.Warn(
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
                FenLogger.Debug($"[BrowserHost] Navigation done. FinalUri: {uri.AbsoluteUri} (Status: {result.Status})", LogCategory.General);

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

                Console.WriteLine($"[NavigateAsync] Rendering content for {uri}");
                
                // Debug: Log navigation with base URL
                // Debug: Log navigation with base URL
                TryLogDebug($"[BrowserApi] Navigating to: {uri}. Previous _current: {_current?.AbsoluteUri ?? "null"}", LogCategory.General);

                // FIX: Set _current BEFORE rendering so UI has access to correct BaseUrl during render events
                _current = uri;
                var commitSource = result.Status == FetchStatus.Success ? "network-document" : "error-document";
                _navigationLifecycle.MarkCommitting(navigationId, _current.AbsoluteUri, commitSource);
                TryLogDebug($"[BrowserApi] _current updated early to: {_current?.AbsoluteUri}", LogCategory.General);
                
                // Dump raw HTML source for debugging (CURL level)
                try 
                { 
                    string dumpPath = StructuredLogger.DumpRawSource(uri.AbsoluteUri, htmlToRender); 
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
                    elem = await _engine.RenderAsync(htmlToRender, uri, trackedCssFetcher, trackedImageFetcher, u => { _ = NavigateAsync(u.AbsoluteUri); });
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

                // Dump Engine Source (Processed DOM level)
                try
                {
                    var activeNode = _engine.ActiveDom;
                    string engineSource = "";
                    if (activeNode is Document d) engineSource = d.DocumentElement?.OuterHTML ?? "";
                    else if (activeNode is Element e) engineSource = e.OuterHTML;
                    else engineSource = activeNode?.NodeValue ?? "";
                    string enginePath = StructuredLogger.DumpEngineSource(uri.AbsoluteUri, engineSource);
                    if (!string.IsNullOrEmpty(enginePath))
                    {
                        FenBrowser.Core.Verification.ContentVerifier.RegisterEngineSourceFile(enginePath);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }
                
                TryLogDebug($"[BrowserApi] RenderAsync finished for {_current?.AbsoluteUri}. Firing RepaintReady...", LogCategory.General);
                
                TryInvokeRepaintReady(elem);
                _navigationLifecycle.MarkInteractive(navigationId, BuildInteractiveLifecycleDetail(result));

                // Dump Rendered Text for side-by-side comparison (Phase 11)
                try
                {
                    string textContent = GetTextContent();
                    var activeDom = _engine.GetActiveDom();
                    int domNodeCount = activeDom?.SelfAndDescendants()?.Count() ?? 0;
                    FenBrowser.Core.Verification.ContentVerifier.RegisterRendered(
                        uri.AbsoluteUri,
                        domNodeCount,
                        textContent?.Length ?? 0);
                    string renderedPath = StructuredLogger.DumpRenderedText(uri.AbsoluteUri, textContent);
                    if (!string.IsNullOrEmpty(renderedPath))
                    {
                        FenBrowser.Core.Verification.ContentVerifier.RegisterRenderedFile(renderedPath);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BrowserHost] Error log failed: {ex.Message}"); }

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
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
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
                    found = searchRoot.Descendants().OfType<Element>().FirstOrDefault(n => n.Id == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    found = searchRoot.Descendants().OfType<Element>().FirstOrDefault(n => n.GetAttribute("class") != null && n.GetAttribute("class").Contains(cls));
                }
                else
                {
                    found = searchRoot.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.TagName, value, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (strategy == "xpath")
            {
                if (value.StartsWith("//"))
                {
                    var tag = value.Substring(2);
                    found = searchRoot.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.TagName, tag, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (found != null)
            {
                var id = Guid.NewGuid().ToString();
                _elementMap[id] = found;
                return id;
            }
            throw new KeyNotFoundException("Element not found");
        }

        public async Task ClickElementAsync(string elementId)
        {
            var element = ResolveElementInActiveContext(elementId);
            if (element != null)
            {
                await HandleElementClick(element);
            }
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
                            FenBrowser.Core.FenLogger.Info($"[BrowserHost] Favicon loaded for {pageUrl}", FenBrowser.Core.Logging.LogCategory.General);
                            
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
                FenBrowser.Core.FenLogger.Warn($"[BrowserHost] Failed to fetch favicon: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
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

            return $"status={result.Status};statusCode={statusCode};redirects={redirectCount};final={finalUri};error={error}";
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

        public Task SwitchToFrameAsync(object frameId)
        {
            if (frameId == null)
            {
                _currentFrameElement = null;
                _frameContextStack.Clear();
                _elementMap.Clear();
                _shadowRootMap.Clear();
                return Task.CompletedTask;
            }

            var frameElement = ResolveFrameReference(frameId);
            if (frameElement == null)
            {
                TryLogWarn($"[BrowserHost] SwitchToFrameAsync could not resolve frame reference '{frameId}'.", LogCategory.Navigation);
                return Task.CompletedTask;
            }

            if (_currentFrameElement != null)
            {
                _frameContextStack.Push(_currentFrameElement);
            }

            _currentFrameElement = frameElement;
            _elementMap.Clear();
            _shadowRootMap.Clear();
            return Task.CompletedTask;
        }

        public Task SwitchToParentFrameAsync()
        {
            _currentFrameElement = _frameContextStack.Count > 0 ? _frameContextStack.Pop() : null;
            _elementMap.Clear();
            _shadowRootMap.Clear();
            return Task.CompletedTask;
        }

        public async Task<string> FindElementAsync(string strategy, string value, string parentId = null)
        {
            await Task.CompletedTask;
            Node searchRoot = ResolveSearchRootNode(parentId);
            if (searchRoot == null) return null;

            Element found = FindElementByStrategy(searchRoot, strategy, value);
            if (found != null)
            {
                var id = Guid.NewGuid().ToString();
                _elementMap[id] = found;
                return id;
            }
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
            // Throttle to ~60 fps (16 ms) to avoid flooding the input queue on high-frequency devices
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long ticksPer16ms = System.Diagnostics.Stopwatch.Frequency / 60;
            if (now - _lastMouseMoveTick < ticksPer16ms)
                return;
            _lastMouseMoveTick = now;
            QueueInputTask("mousemove", x, y, 0);
        }

        private static bool ShouldRetryTopLevelNavigation(FetchResult result, string url, int attempt, int maxAttempts)
        {
            if (attempt >= maxAttempts) return false;
            if (!IsRetriableTopLevelScheme(url)) return false;
            if (result == null) return true;
            return result.Status == FetchStatus.ConnectionFailed || result.Status == FetchStatus.Timeout;
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
                var hovered = inputEvent.Target;
                if (!ReferenceEquals(ElementStateManager.Instance.HoveredElement, hovered))
                {
                    ElementStateManager.Instance.SetHoveredElement(hovered);
                    TryInvokeRepaintReady(_engine.GetActiveDom());
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

        public async Task<string[]> FindElementsAsync(string strategy, string value, string parentId = null)
        {
            await Task.CompletedTask;
            Node searchRoot = ResolveSearchRootNode(parentId);
            if (searchRoot == null) return Array.Empty<string>();

            var elements = FindElementsByStrategy(searchRoot, strategy, value);
            var ids = new List<string>();
            foreach (var el in elements)
            {
                var id = Guid.NewGuid().ToString();
                _elementMap[id] = el;
                ids.Add(id);
            }
            return ids.ToArray();
        }

        private Element FindElementByStrategy(Node root, string strategy, string value)
        {
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    return root.Descendants().OfType<Element>().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("id") && n.Attr["id"] == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    return root.Descendants().OfType<Element>().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("class") && n.Attr["class"].Contains(cls));
                }
                else
                {
                    return root.Descendants().OfType<Element>().FirstOrDefault(n => n.TagName == value);
                }
            }
            else if (strategy == "xpath" && value.StartsWith("//"))
            {
                var tag = value.Substring(2);
                return root.Descendants().OfType<Element>().FirstOrDefault(n => n.TagName == tag);
            }
            else if (strategy == "tag name")
            {
                return root.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.NodeName, value, StringComparison.OrdinalIgnoreCase));
            }
            else if (strategy == "link text")
            {
                return root.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.NodeName, "a", StringComparison.OrdinalIgnoreCase) && n.TextContent?.Trim() == value);
            }
            else if (strategy == "partial link text")
            {
                return root.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.NodeName, "a", StringComparison.OrdinalIgnoreCase) && n.TextContent?.Contains(value) == true);
            }
            return null;
        }

        private IEnumerable<Element> FindElementsByStrategy(Node root, string strategy, string value)
        {
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    return root.Descendants().OfType<Element>().Where(n => n.Attr != null && n.Attr.ContainsKey("id") && n.Attr["id"] == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    return root.Descendants().OfType<Element>().Where(n => n.Attr != null && n.Attr.ContainsKey("class") && n.Attr["class"].Contains(cls));
                }
                else
                {
                    return root.Descendants().OfType<Element>().Where(n => n.TagName == value);
                }
            }
            else if (strategy == "tag name")
            {
                return root.Descendants().OfType<Element>().Where(n => n.TagName == value);
            }
            return Enumerable.Empty<Element>();
        }

        private Node ResolveSearchRootNode(string parentId = null)
        {
            if (!string.IsNullOrEmpty(parentId) && _elementMap.TryGetValue(parentId, out var parent))
            {
                return parent;
            }

            if (!string.IsNullOrEmpty(parentId) && _shadowRootMap.TryGetValue(parentId, out var shadowRoot))
            {
                return shadowRoot;
            }

            if (_currentFrameElement != null)
            {
                return ResolveFrameSearchRoot(_currentFrameElement);
            }

            var dom = _engine.GetActiveDom();
            return (dom as Element) ?? (dom as Document)?.DocumentElement;
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
                    .Descendants()
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
                    .Descendants()
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

            if (FenBrowser.FenEngine.DOM.ElementWrapper.IsRemoteFrameElement(frameElement, _current?.AbsoluteUri))
            {
                return null;
            }

            var sandboxAttribute = frameElement.GetAttribute("sandbox");
            if (FenBrowser.Core.SandboxPolicy.HasIframeSandboxAttribute(sandboxAttribute))
            {
                var flags = FenBrowser.Core.SandboxPolicy.ParseIframeSandboxFlags(sandboxAttribute);
                if ((flags & FenBrowser.Core.IframeSandboxFlags.SameOrigin) == 0)
                {
                    return null;
                }
            }

            return frameElement.ChildNodes?
                .OfType<Element>()
                .FirstOrDefault();
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

            return Task.FromResult(GetOrRegisterElementId(activeElement));
        }

        public Task<string> GetShadowRootAsync(string elementId)
        {
            var element = ResolveElementInActiveContext(elementId);
            if (element?.ShadowRoot == null)
            {
                return Task.FromResult<string>(null);
            }

            foreach (var entry in _shadowRootMap)
            {
                if (ReferenceEquals(entry.Value, element.ShadowRoot))
                {
                    return Task.FromResult(entry.Key);
                }
            }

            var id = Guid.NewGuid().ToString();
            _shadowRootMap[id] = element.ShadowRoot;
            return Task.FromResult(id);
        }

        public Task<bool> IsElementSelectedAsync(string elementId)
        {
            var el = ResolveElementInActiveContext(elementId);
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
            var el = ResolveElementInActiveContext(elementId);
            if (el?.Attr != null)
            {
                if (el.Attr.TryGetValue(name, out var val))
                    return Task.FromResult(val);
            }
            return Task.FromResult<string>(null);
        }

        public Task<object> GetElementPropertyAsync(string elementId, string name)
        {
            var el = ResolveElementInActiveContext(elementId);
            if (el?.Attr != null)
            {
                if (el.Attr.TryGetValue(name, out var val))
                    return Task.FromResult<object>(val);
            }

            return Task.FromResult<object>(null);
        }

        public Task<string> GetElementCssValueAsync(string elementId, string property)
        {
            // CSS computation would require CssComputed lookup
            return Task.FromResult<string>(null);
        }

        public Task<string> GetElementTextAsync(string elementId)
        {
            var el = ResolveElementInActiveContext(elementId);
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
            var el = ResolveElementInActiveContext(elementId);
            if (el != null)
                return Task.FromResult(el.TagName?.ToLowerInvariant() ?? "");
            return Task.FromResult("");
        }

        public Task<ElementRect> GetElementRectAsync(string elementId)
        {
            // Would need layout information from renderer
            return Task.FromResult(new ElementRect { X = 0, Y = 0, Width = 0, Height = 0 });
        }

        public Task<bool> IsElementEnabledAsync(string elementId)
        {
            var el = ResolveElementInActiveContext(elementId);
            if (el != null)
            {
                if (el.Attr != null && el.Attr.ContainsKey("disabled"))
                    return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        public Task<string> GetElementComputedRoleAsync(string elementId)
        {
            var el = ResolveElementInActiveContext(elementId);
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
            var el = ResolveElementInActiveContext(elementId);
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
            // Clear input/textarea value
            var el = ResolveElementInActiveContext(elementId);
            if (el != null)
            {
                var tag = el.TagName?.ToLowerInvariant();
                if (tag == "input" || tag == "textarea")
                {
                    SetTextEntryValue(el, string.Empty);
                }
            }
            return Task.CompletedTask;
        }

        public Task SendKeysToElementAsync(string elementId, string text)
        {
            // Set value on input/textarea elements
            var el = ResolveElementInActiveContext(elementId);
            if (el != null)
            {
                var tag = el.TagName?.ToLowerInvariant();
                if (tag == "input" || tag == "textarea")
                {
                    var currentValue = GetTextEntryValue(el);
                    SetTextEntryValue(el, currentValue + (text ?? string.Empty));
                }
            }
            return Task.CompletedTask;
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

            if (ReferenceEquals(searchRoot, candidate))
            {
                return true;
            }

            var cursor = candidate.ParentNode;
            while (cursor != null)
            {
                if (ReferenceEquals(cursor, searchRoot))
                {
                    return true;
                }

                cursor = cursor.ParentNode;
            }

            return false;
        }

        private Element ResolveElementInActiveContext(string elementId)
        {
            if (string.IsNullOrWhiteSpace(elementId) || !_elementMap.TryGetValue(elementId, out var element))
            {
                return null;
            }

            var searchRoot = ResolveSearchRoot();
            if (_currentFrameElement != null)
            {
                if (searchRoot == null || !IsElementWithinSearchRoot(searchRoot, element))
                {
                    return null;
                }
            }

            return element;
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args = null)
        {
            await Task.CompletedTask;
            EnsureFrameExecutionContextAvailable();
            // WebDriver spec: scripts are executed as an anonymous function
            // So we wrap the script: (function() { <script> }).apply(null, arguments)
            string wrappedScript;
            if (args != null && args.Length > 0)
            {
                var jsonArgs = JsonSerializer.Serialize(args);
                wrappedScript = $"var __args = {jsonArgs}; (function() {{ {script} }}).apply(null, __args)";
            }
            else
            {
                wrappedScript = $"var __args = []; (function() {{ {script} }})()";
            }
            
            TryLogDebug($"[ExecuteScript] Wrapped: {wrappedScript.Substring(0, Math.Min(500, wrappedScript.Length))}...", LogCategory.JavaScript);
            var rawResult = _engine.Evaluate(wrappedScript);
            TryLogDebug($"[ExecuteScript] Raw result type: {rawResult?.GetType().Name}", LogCategory.JavaScript);
            
            if (rawResult is FenBrowser.FenEngine.Core.FenValue val && val.Type == JsValueType.Error)
            {
                throw new InvalidOperationException(val.AsError());
            }

            // Convert FenValue to native .NET type for proper JSON serialization
            if (rawResult is FenBrowser.FenEngine.Core.FenValue fenValue)
            {
                return fenValue.ToNativeObject();
            }
            
            return rawResult;
        }

        // Mousemove throttle: skip events closer than 16 ms (~60 fps)
        private long _lastMouseMoveTick = 0;

        // Storage for async script callback result
        private object _asyncScriptResult = null;
        private bool _asyncScriptDone = false;
        private bool _pointerDown = false;
        private readonly object _asyncScriptLock = new object();

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeoutMs)
        {
            EnsureFrameExecutionContextAvailable();
            // Reset state
            lock (_asyncScriptLock)
            {
                _asyncScriptResult = null;
                _asyncScriptDone = false;
            }

            // Create a unique callback ID for this execution.
            var callbackId = Guid.NewGuid().ToString("N");
            
            // Prepare arguments array with the callback as the last argument
            var argsList = args?.ToList() ?? new List<object>();
            
            // We need to create the callback function in JavaScript context
            // The callback should store the result in a global variable we can poll
            // Also set up requestAnimationFrame polyfill that works with our polling
            var setupScript = $@"
                window.__fen_async_result_{callbackId} = null;
                window.__fen_async_done_{callbackId} = false;
                
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
            
            // Build arguments JSON - add a callback function at the end
            var jsonArgs = JsonSerializer.Serialize(argsList);
            
            // Preprocess script to transform ES6+ syntax to ES5 equivalents
            var processedScript = script;
            
            // 1. Transform destructuring: const [a, b] = expr; -> var __temp = expr; var a = __temp[0]; var b = __temp[1];
            var destructuringPattern = new System.Text.RegularExpressions.Regex(
                @"(const|let|var)\s*\[([^\]]+)\]\s*=\s*([^;]+);",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            processedScript = destructuringPattern.Replace(processedScript, match => {
                var vars = match.Groups[2].Value.Split(',');
                var expr = match.Groups[3].Value;
                var tempId = Guid.NewGuid().ToString("N").Substring(0, 8);
                var result = new System.Text.StringBuilder();
                result.Append($"var __destruct_{tempId} = {expr}; ");
                for (int i = 0; i < vars.Length; i++)
                {
                    var v = vars[i].Trim();
                    if (!string.IsNullOrEmpty(v))
                    {
                        result.Append($"var {v} = __destruct_{tempId}[{i}]; ");
                    }
                }
                return result.ToString();
            });
            
            // 2. Transform const/let to var
            processedScript = System.Text.RegularExpressions.Regex.Replace(processedScript, @"\bconst\s+", "var ");
            processedScript = System.Text.RegularExpressions.Regex.Replace(processedScript, @"\blet\s+", "var ");
            
            // 3. Transform arrow functions: () => { ... } -> function() { ... }
            // Use a more careful approach - only match arrow functions in valid contexts
            // Arrow function with parens and block body: (args) => { ... }
            // Only match when preceded by: comma, =, (, [, {, :, or start of line
            processedScript = System.Text.RegularExpressions.Regex.Replace(
                processedScript,
                @"(,\s*|=\s*|\(\s*|\[\s*|\{\s*|:\s*|^\s*)\(([^)]*)\)\s*=>\s*\{",
                "$1function($2) {",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // Single arg without parens with block body: arg => { ... }
            processedScript = System.Text.RegularExpressions.Regex.Replace(
                processedScript,
                @"(,\s*|=\s*|\(\s*|\[\s*|\{\s*|:\s*|^\s*)(\w+)\s*=>\s*\{",
                "$1function($2) {",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // Arrow function with parens and expression body: (args) => expr
            processedScript = System.Text.RegularExpressions.Regex.Replace(
                processedScript,
                @"(,\s*|=\s*|\(\s*|\[\s*|\{\s*|:\s*|^\s*)\(([^)]*)\)\s*=>\s*([^{;,\r\n\)]+)",
                "$1function($2) { return $3; }",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // Single arg with expression body: arg => expr  
            processedScript = System.Text.RegularExpressions.Regex.Replace(
                processedScript,
                @"(,\s*|=\s*|\(\s*|\[\s*|\{\s*|:\s*|^\s*)(\w+)\s*=>\s*([^{;,\r\n\)]+)",
                "$1function($2) { return $3; }",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // 4. Replace 'arguments' keyword with '__args' so it works with our wrapper
            processedScript = System.Text.RegularExpressions.Regex.Replace(processedScript, @"\barguments\b", "__args");
            
            // The script wrapper that provides the callback function.
            var wrappedScript = $@"
                var __args = {jsonArgs};
                console.log('[AsyncScript] __args before push: ' + __args.length);
                var __callback = function(result) {{
                    console.log('[AsyncScript] Callback called with: ' + JSON.stringify(result));
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
                    {processedScript}
                }})();
                
                // Debug: Log rAF queue after script
                console.log('[AsyncScript] rAF callbacks length: ' + (window.__raf_callbacks ? window.__raf_callbacks.length : 'no array'));
            ";
            
            TryLogDebug($"[AsyncScript] Executing wrapped script (timeout {timeoutMs}ms)", LogCategory.JavaScript);
            TryLogDebug($"[AsyncScript] Input script (first 500 chars): {(script.Length > 500 ? script.Substring(0, 500) : script)}", LogCategory.JavaScript);
            TryLogDebug($"[AsyncScript] Processed script (first 500 chars): {(processedScript.Length > 500 ? processedScript.Substring(0, 500) : processedScript)}", LogCategory.JavaScript);
            
            // Execute the script (it should call the callback eventually)
            try 
            {
                var execResult = _engine.Evaluate(wrappedScript);
                TryLogDebug($"[AsyncScript] Script executed, result type: {execResult?.GetType().Name ?? "null"}", LogCategory.JavaScript);
                if (execResult is FenBrowser.FenEngine.Core.FenValue fv && (int)fv.Type == 10)
                {
                    TryLogDebug($"[AsyncScript] Script error: {fv.AsError()}", LogCategory.Errors);
                }
            }
            catch (Exception ex)
            {
                TryLogDebug($"[AsyncScript] Script exception: {ex.Message}", LogCategory.Errors);
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
                if (doneCheck is FenBrowser.FenEngine.Core.FenValue dv && dv.IsBoolean && dv.ToBoolean())
                {
                    // Get the result
                    var result = _engine.Evaluate($"window.__fen_async_result_{callbackId}");
                    TryLogDebug($"[AsyncScript] Callback received result after {sw.ElapsedMilliseconds}ms", LogCategory.JavaScript);
                    
                    // Convert FenValue to native object
                    if (result is FenBrowser.FenEngine.Core.FenValue fenValue)
                    {
                        return fenValue.ToNativeObject();
                    }
                    return result;
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
                    return entry.Key;
                }
            }

            var id = Guid.NewGuid().ToString();
            _elementMap[id] = element;
            return id;
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
            if (element == null) 
            {
                SetFocusedElementState(null);
                TryInvokeRepaintReady(_engine.GetActiveDom());
                return;
            }
            
            var tag = element.NodeName?.ToLowerInvariant();

            // Wrapper-first DOMs (e.g. Google search box) often receive click on a container.
            // Promote to a descendant editable so focus/typing remains stable.
            if (tag != "input" &&
                tag != "textarea" &&
                tag != "button" &&
                tag != "a" &&
                tag != "select" &&
                !string.Equals(element.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase))
            {
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
            if (_currentFrameElement == null)
            {
                return;
            }

            throw new InvalidOperationException("Frame-scoped script execution is blocked until a dedicated per-frame execution context is available.");
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

            return string.Equals(element.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
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
                case "date":
                case "datetime-local":
                case "month":
                case "time":
                case "week":
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

        public Task HandleKeyPress(string key)
        {
            if (_focusedElement == null) return Task.CompletedTask;
            
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
                    
                    SetTextEntryValue(_focusedElement, val);
                    
                    // Trigger Repaint
                     TryInvokeRepaintReady(_engine.GetActiveDom());
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
            
            return Task.CompletedTask;
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

        /// <summary>
        /// Called by JavaScript engine when alert/confirm/prompt is triggered
        /// </summary>
        public void TriggerAlert(string text)
        {
            _pendingAlertText = text;
        }

        public Task<bool> HasAlertAsync()
        {
            return Task.FromResult(!string.IsNullOrEmpty(_pendingAlertText));
        }

        public Task DismissAlertAsync()
        {
            _pendingAlertText = null;
            _pendingPromptResponse = null;
            return Task.CompletedTask;
        }

        public Task AcceptAlertAsync()
        {
            _pendingAlertText = null;
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
                FenLogger.Error($"[BrowserHost] PushState failed: {ex.Message}", LogCategory.JavaScript);
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
                FenLogger.Error($"[BrowserHost] ReplaceState failed: {ex.Message}", LogCategory.JavaScript);
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
