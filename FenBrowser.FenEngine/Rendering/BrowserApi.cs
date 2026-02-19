using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using FenBrowser.Core;
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
        private readonly ResourceManager _resources = new ResourceManager(new HttpClient());
        private readonly NavigationManager _navManager;
        private readonly FenBrowser.FenEngine.Core.EngineLoop _engineLoop; // Phase 5: Engine Loop
        private readonly InputManager _inputManager = new InputManager();
        private Uri _current;
        private bool _disposed;
        
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private int _historyIndex = -1;
        private bool _isNavigatingHistory;
        
        // Map WebDriver Element IDs to LiteElements
        private readonly Dictionary<string, Element> _elementMap = new Dictionary<string, Element>();
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
        public event EventHandler<bool> LoadingChanged;
        public event EventHandler<string> TitleChanged;
        public event EventHandler<object> RepaintReady;
        public event Action<string> ConsoleMessage;
        public event Action<SKRect?> HighlightRectChanged;
        public event Func<string, JsPermissions, Task<bool>> PermissionRequested;

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
        public Dictionary<Node, CssComputed> ComputedStyles => _engine.LastComputedStyles;
        public CustomHtmlEngine Engine => _engine;

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

        public BrowserHost(bool isPrivate = false)
        {
            IsPrivate = isPrivate;
            _engineLoop = new FenBrowser.FenEngine.Core.EngineLoop(); // Phase 5: Initialize Loop
            _engine.InitHistory(this); // Wire up history bridge
            
            // Initialize FontResolver for @font-face support
            // This allows Core.Css.CssComputed to use the Engine's FontRegistry
            FenBrowser.Core.Css.CssComputed.FontResolver = FenBrowser.FenEngine.Rendering.FontRegistry.TryResolve;
            
            // Get HTTP/2 and Brotli enabled handler from factory
            var config = NetworkConfiguration.Instance;
            var handler = FenBrowser.Core.Network.HttpClientFactory.CreateHandler();
            
            // Add certificate callback for security display + optional soft-fail
            handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            {
                var info = new CertificateInfo
                {
                    Subject   = cert.Subject,
                    Issuer    = cert.Issuer,
                    NotBefore = cert is System.Security.Cryptography.X509Certificates.X509Certificate2 cert2 ? cert2.NotBefore : DateTime.MinValue,
                    NotAfter  = cert is System.Security.Cryptography.X509Certificates.X509Certificate2 cert2b ? cert2b.NotAfter : DateTime.MaxValue,
                    IsValid   = errors == System.Net.Security.SslPolicyErrors.None,
                    Thumbprint = cert.GetCertHashString()
                };

                _lastCertificate = info;
                _lastSslErrors   = errors;

                // Enforce strict validation by default; only allow override via settings.
                if (FenBrowser.Core.NetworkConfiguration.Instance.IgnoreCertificateErrors)
                    return true;
                return errors == System.Net.Security.SslPolicyErrors.None;
            };
            
            // Create HTTP/2 + Brotli enabled client
            var httpClient = FenBrowser.Core.Network.HttpClientFactory.CreateClient(handler);
            
            // Log HTTP/2 and Brotli configuration
            FenLogger.Info($"[BrowserHost] HTTP/2: {config.EnableHttp2}, Brotli: {config.EnableBrotli}, " +
                          $"Version: {httpClient.DefaultRequestVersion}", LogCategory.Network);
            
            _resources = new ResourceManager(httpClient, isPrivate);

            // Wire up Fetch API (Phase 8)
            _engine.FetchHandler = (req) => 
            {
                if (!string.IsNullOrEmpty(WPTRootPath))
                {
                    req.RequestUri = RemapWptUri(req.RequestUri);
                }
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
                    catch { }

                    // Pass the ResourceManager's ID to DevToolsCore so we can correlate completion
                    DevToolsCore.Instance.RecordRequest(req.RequestUri.ToString(), req.Method.ToString(), headers, id);
                }
                catch { }
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
                catch { }
            };

            _resources.NetworkRequestFailed += (id, ex) =>
            {
                try
                {
                    // Use 599 to indicate network failure (treated as error in DevToolsCore)
                    DevToolsCore.Instance.CompleteRequest(id, 599, null, 0, "error");
                }
                catch { }
            };


            _engine.RepaintReady += (elem) =>
            {
                try { RepaintReady?.Invoke(this, elem); }
                catch { }
            };

            _engine.DomReady += (s, dom) =>
            {
                try 
                { 
                    _engineLoop.SetRoot(dom); // Phase 5: Connect DOM to Loop
                    RepaintReady?.Invoke(this, dom); 
                }
                catch { }
            };

            _engine.LoadingChanged += (s, loading) =>
            {
                try { LoadingChanged?.Invoke(this, loading); }
                catch { }
            };

            _engine.TitleChanged += (s, title) =>
            {
                try { TitleChanged?.Invoke(this, title); }
                catch { }
            };

            _engine.AlertTriggered += (msg) =>
            {
                TriggerAlert(msg);
                try { ConsoleMessage?.Invoke($"[Alert] {msg}"); } catch { }
            };

            _engine.ConsoleMessage += (msg) =>
            {
                try { ConsoleMessage?.Invoke(msg); } catch { }
            };

            _engine.HighlightRectChanged += (rect) =>
            {
                try { HighlightRectChanged?.Invoke(rect); }
                catch { }
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
                try { FenLogger.Debug(msg, LogCategory.Network); } catch { }
                try { ConsoleMessage?.Invoke(msg); } catch { }
            };
            _engine.ScriptFetcher = (u) => 
            {
                // DEBUG: Log strict fetcher
                Console.WriteLine($"[ScriptFetcher] Request: {u}");

                if (u.ToString().IndexOf("testharnessreport.js", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("[ScriptFetcher] INJECTING BRIDGE for testharnessreport.js");
                    return Task.FromResult(@"
                        console.log('[Bridge] Injected Bridge Loaded');
                        console.log('[Bridge] typeof add_result_callback: ' + typeof window.add_result_callback);

                        // Fallback: Testharness looks for these global functions to dispatch events
                        window.result_callback = function(t) {
                                console.log('[Bridge] Global Result: ' + t.name + ' ' + t.status);
                                if (window.testRunner && window.testRunner.reportResult) {
                                    window.testRunner.reportResult(t.name, t.status === 0, t.message);
                                }
                        };

                        window.completion_callback = function(t, s) {
                                console.log('[Bridge] Global Complete');
                                if (window.testRunner && window.testRunner.notifyDone) {
                                    window.testRunner.notifyDone();
                                }
                        };

                        if (window.add_result_callback) {
                            window.add_result_callback(window.result_callback);
                        } else { console.log('[Bridge] add_result_callback not found, relying on global hooks'); }
                        
                        if (window.add_completion_callback) {
                            window.add_completion_callback(window.completion_callback);
                        }
                    ");

                }

                u = RemapWptUri(u);
                return _resources.FetchTextAsync(u, referer: _current, accept: null, secFetchDest: "script");
            };
            _navManager = new NavigationManager(_resources);
            
            // Wire up ImageLoader to trigger RepaintReady when images finish loading
            ImageLoader.RequestRepaint = () =>
            {
                try
                {
                    FenLogger.Debug($"[ImageLoader-Repaint] Triggering repaint after image load", LogCategory.Rendering);
                    RepaintReady?.Invoke(this, null);
                }
                catch { }
            };

            ImageLoader.RequestRelayout = () =>
            {
                try
                {
                    FenLogger.Debug($"[ImageLoader-Relayout] Triggering re-layout after image load", LogCategory.Rendering);
                    var dom = _engine.GetActiveDom();
                    if (dom != null) RepaintReady?.Invoke(this, dom);
                }
                catch { }
            };

            // Wire up FontRegistry to trigger RepaintReady when fonts finish loading
            _fontLoadedHandler = (family) =>
            {
                try
                {
                    FenLogger.Debug($"[FontRegistry-Repaint] Triggering repaint after font load: {family}", LogCategory.Rendering);
                    // Trigger layout recalculation by raising RepaintReady
                    RepaintReady?.Invoke(this, null);
                }
                catch { }
            };
            FontRegistry.FontLoaded += _fontLoadedHandler;
        }

        public static string WPTRootPath { get; set; }

        private Uri RemapWptUri(Uri u)
        {
            if (string.IsNullOrEmpty(WPTRootPath)) return u;
            if (u.Scheme != "file") return u;

            // Simple heuristic matches common WPT paths
            // If path looks like /resources/ or /common/ or /images/ but is at drive root C:/resources...
            // AND the file doesn't exist there...
            // REMAP it to WPTRootPath.

            if (System.IO.File.Exists(u.LocalPath)) return u;

            string local = u.LocalPath; // e.g. C:\resources\testharness.js
            string fileName = System.IO.Path.GetFileName(local);
            string dir = System.IO.Path.GetDirectoryName(local);
            
            // We want the part after the drive root. 
            // e.g. \resources
            // Use Path.GetPathRoot
            string root = System.IO.Path.GetPathRoot(local);
            if (local.StartsWith(root))
            {
                string rel = local.Substring(root.Length).TrimStart(System.IO.Path.DirectorySeparatorChar);
                
                // Only remap specific WPT folders to avoid accidents
                if (rel.StartsWith("resources", StringComparison.OrdinalIgnoreCase) || 
                    rel.StartsWith("common", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith("images", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith("fonts", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith("media", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith("css", StringComparison.OrdinalIgnoreCase))
                {
                    string mapped = System.IO.Path.Combine(WPTRootPath, rel);
                     if (System.IO.File.Exists(mapped))
                     {
                         try 
                         { 
                             // FenLogger.Debug($"[WPT-Remap] {u} -> {mapped}", LogCategory.Navigation);
                             return new Uri(mapped);
                         } 
                         catch {}
                     }
                }
            }
            return u;
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
                try { Navigated?.Invoke(this, _current); } catch { }
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
                try { Navigated?.Invoke(this, _current); } catch { }
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
            try { FenLogger.Debug($"[BrowserHost] NavigateAsync called for: '{url}'", LogCategory.Navigation); } catch {}

            if (_disposed) return false;
                if (string.IsNullOrWhiteSpace(url)) return false;

                // Log raw navigation input for diagnostics
                try { FenLogger.Info($"[BrowserHost] Nav raw='{url}'", LogCategory.Navigation); } catch {}

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
                    
                    // Render the generated HTML
                    var elem = await _engine.RenderAsync(sb.ToString(), _current, u => _resources.FetchCssAsync(u), u => _resources.FetchImageAsync(u), u => { _ = NavigateAsync(u.AbsoluteUri); });
                    try { RepaintReady?.Invoke(this, elem); } catch { }

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

                    try { Navigated?.Invoke(this, _current); } catch { }
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
                        try { FenLogger.Info($"[BrowserHost] Resolved relative URL -> '{url}'", LogCategory.Navigation); } catch {}
                    }
                    // Normalize if missing scheme
                    else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                    {
                        var candidate = "https://" + url.TrimStart('/');
                        if (Uri.TryCreate(candidate, UriKind.Absolute, out var normalized))
                        {
                            try { FenLogger.Info($"[BrowserHost] Normalized missing-scheme -> '{normalized}'", LogCategory.Navigation); } catch {}
                            url = normalized.AbsoluteUri;
                        }
                    }
                    else
                    {
                        try { FenLogger.Info($"[BrowserHost] Parsed absolute Uri='{parsed}'", LogCategory.Navigation); } catch {}
                        url = parsed.AbsoluteUri; // canonicalize
                    }

                Console.WriteLine($"[NavigateAsync] Start: {url}");

                _resources.ResetBlockedCount();
                _resources.ActivePolicy = null; // Reset CSP for new page
                _engine.ActivePolicy = null;
                CurrentPolicy = null;

                const int maxTransientNavAttempts = 2;
                FetchResult result = null;
                for (int attempt = 1; attempt <= maxTransientNavAttempts; attempt++)
                {
                    result = await _navManager.NavigateAsync(url, requestKind);
                    if (!ShouldRetryTopLevelNavigation(result, url, attempt, maxTransientNavAttempts))
                        break;

                    int retryDelayMs = 350 * attempt;
                    FenLogger.Warn(
                        $"[BrowserHost] Transient navigation failure ({result.Status}) for '{url}'. Retrying {attempt}/{maxTransientNavAttempts - 1} after {retryDelayMs}ms.",
                        LogCategory.Network);
                    await Task.Delay(retryDelayMs);
                }
                
                // Parse CSP
                if (result.Headers != null && result.Headers.TryGetValues("Content-Security-Policy", out var cspValues))
                {
                    var cspHeader = string.Join(";", cspValues); // Multiple headers are concatenated with comma usually, but CSP allows multiple policies. 
                    // For simplicity, we parse the first or combined? 
                    // Standard says multiple policies are enforced intersection. 
                    // Our parser handles one string. Let's take the first one dependent or join with semicolon? 
                    // CspPolicy.Parse expects semicolon separated directives. 
                    // Use comma if multiple headers? 
                    // Let's just use the first one for now or join.
                    // Actually, if multiple headers, they restrict further. 
                    // CspPolicy doesn't support multiple separate policies yet.
                    // We will parse the combined string.
                    CurrentPolicy = CspPolicy.Parse(string.Join(";", cspValues));
                    _resources.ActivePolicy = CurrentPolicy;
                    _engine.ActivePolicy = CurrentPolicy; // Set on engine for inline script/style CSP checks
                    Console.WriteLine($"[CSP] Policy Applied: {string.Join(";", cspValues)}");
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
                            htmlToRender = ErrorPageRenderer.RenderSslError(url, result.ErrorDetail);
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
                try { FenLogger.Debug($"[BrowserApi] Navigating to: {uri}. Previous _current: {_current?.AbsoluteUri ?? "null"}", LogCategory.General); } catch {}

                // FIX: Set _current BEFORE rendering so UI has access to correct BaseUrl during render events
                _current = uri;
                try { FenLogger.Debug($"[BrowserApi] _current updated early to: {_current?.AbsoluteUri}", LogCategory.General); } catch {}
                
                // Dump raw HTML source for debugging (CURL level)
                try 
                { 
                    string dumpPath = StructuredLogger.DumpRawSource(uri.AbsoluteUri, htmlToRender); 
                    if (!string.IsNullOrEmpty(dumpPath))
                    {
                        FenBrowser.Core.Verification.ContentVerifier.RegisterSourceFile(dumpPath);
                    }
                } catch { }

                var elem = await _engine.RenderAsync(htmlToRender, uri, u => _resources.FetchCssAsync(u), u => _resources.FetchImageAsync(u), u => { _ = NavigateAsync(u.AbsoluteUri); });

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
                catch { }
                
                // _current already set above
                
                // CRITICAL: WPT testharness.js waits for window.load event to start tests.
                // Since our engine might not fire it automatically in all cases, force it here.
                try { 
                    FenLogger.Debug("[BrowserApi] Force-dispatching 'load' event for WPT...", LogCategory.JavaScript);
                    await _engine.ExecuteScriptAsync("window.dispatchEvent(new Event('load'));");
                } catch (Exception ex) { FenLogger.Error($"[BrowserApi] Failed to dispatch load: {ex.Message}", LogCategory.JavaScript); }

                // Debug: Log that _current is now set
                try { FenLogger.Debug($"[BrowserApi] RenderAsync finished. Firing RepaintReady...", LogCategory.General); } catch {}
                
                // Debug: Log that _current is now set
                try { FenLogger.Debug($"[BrowserApi] _current now set to: {_current?.AbsoluteUri}. Firing RepaintReady...", LogCategory.General); } catch {}
                
                try { RepaintReady?.Invoke(this, elem); } catch { }

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
                catch { }

                // FIX: Force re-layout after delay to catch async JS DOM updates (Google blank screen fix)
                // Increased delay to 1500ms to allow CSS stylesheets to fully load and settle,
                // reducing visible layout jitter from premature re-renders.
                Task.Run(async () => 
                {
                    try
                    {
                        await Task.Delay(1500);
                        if (_disposed) return;

                        var dom = _engine.GetActiveDom();
                        if (dom != null) RepaintReady?.Invoke(this, dom);
                    }
                    catch (Exception ex)
                    {
                        // Prevent background crash
                        try { FenLogger.Error($"[BrowserHost] Delayed repaint error: {ex.Message}", LogCategory.Rendering); } catch {}
                    }
                });

                if (!_isNavigatingHistory)
                {
                    if (_historyIndex < _history.Count - 1)
                    {
                        _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
                    }
                    _history.Add(new HistoryEntry(uri));
                    _historyIndex = _history.Count - 1;
                }

                try { Navigated?.Invoke(this, uri); } catch { }
                
                // Fetch Favicon
                _ = FetchFaviconAsync(uri);
                
                return true;
            }
            catch (Exception ex)
            {
                try { System.Diagnostics.Debug.WriteLine("[NavigateAsync] Exception: " + ex.ToString()); } catch { }
                var details = ex.ToString();
                if (details != null && details.Length > 2000) details = details.Substring(0, 2000) + "...";
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
            catch { }
        }

        public void DeleteCookie(string name)
        {
            try
            {
                var u = _current ?? new Uri("about:blank");
                _engine.DeleteCookie(u, name);
            }
            catch { }
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
            catch { }
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
                    if (n.NodeType == NodeType.Text && !string.IsNullOrWhiteSpace(n.TextContent))
                        sb.AppendLine(n.TextContent.Trim());
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
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
            try { FenLogger.Debug($"[BrowserApi] ExecuteScriptAsync called with script: {script}", LogCategory.JavaScript); } catch { }
            return _engine.Evaluate(script);
        }

        public async Task<string> FindElementAsync(string strategy, string value)
        {
            await Task.CompletedTask;
            var dom = _engine.GetActiveDom();
            if (dom == null) throw new Exception("No active DOM");

            Element found = null;
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    found = dom.Descendants().OfType<Element>().FirstOrDefault(n => n.Id == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    found = dom.Descendants().OfType<Element>().FirstOrDefault(n => n.GetAttribute("class") != null && n.GetAttribute("class").Contains(cls));
                }
                else
                {
                    found = dom.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.TagName, value, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (strategy == "xpath")
            {
                if (value.StartsWith("//"))
                {
                    var tag = value.Substring(2);
                    found = dom.Descendants().OfType<Element>().FirstOrDefault(n => string.Equals(n.TagName, tag, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (found != null)
            {
                var id = Guid.NewGuid().ToString();
                _elementMap[id] = found;
                return id;
            }
            throw new Exception("Element not found");
        }

        public async Task ClickElementAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var element))
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
            // Frame support is limited in FenEngine
            return Task.CompletedTask;
        }

        public Task SwitchToParentFrameAsync()
        {
            return Task.CompletedTask;
        }

        public async Task<string> FindElementAsync(string strategy, string value, string parentId = null)
        {
            await Task.CompletedTask;
            var dom = _engine.GetActiveDom();
            if (dom == null) return null;

            Element searchRoot = (dom as Element) ?? (dom as Document)?.DocumentElement;
            if (!string.IsNullOrEmpty(parentId) && _elementMap.TryGetValue(parentId, out var parent))
                searchRoot = parent;

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
             // TODO: Throttle mousemove?
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

            bool handled = _inputManager.ProcessEvent(inputEvent, renderContext, context);

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
                    try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch { }
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
            var dom = _engine.GetActiveDom();
            if (dom == null) return Array.Empty<string>();

            Element searchRoot = (dom as Element) ?? (dom as Document)?.DocumentElement;
            if (!string.IsNullOrEmpty(parentId) && _elementMap.TryGetValue(parentId, out var parent))
                searchRoot = parent;

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

        private Element FindElementByStrategy(Element root, string strategy, string value)
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

        private IEnumerable<Element> FindElementsByStrategy(Element root, string strategy, string value)
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

        public Task<string> GetActiveElementAsync()
        {
            // No focus tracking in FenEngine - return first focusable or null
            return Task.FromResult<string>(null);
        }

        public Task<string> GetShadowRootAsync(string elementId)
        {
            // Shadow DOM not supported
            return Task.FromResult<string>(null);
        }

        public Task<bool> IsElementSelectedAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var el))
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
            if (_elementMap.TryGetValue(elementId, out var el) && el.Attr != null)
            {
                if (el.Attr.TryGetValue(name, out var val))
                    return Task.FromResult(val);
            }
            return Task.FromResult<string>(null);
        }

        public Task<object> GetElementPropertyAsync(string elementId, string name)
        {
            return GetElementAttributeAsync(elementId, name).ContinueWith(t => (object)t.Result);
        }

        public Task<string> GetElementCssValueAsync(string elementId, string property)
        {
            // CSS computation would require CssComputed lookup
            return Task.FromResult<string>(null);
        }

        public Task<string> GetElementTextAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var el))
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
            if (_elementMap.TryGetValue(elementId, out var el))
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
            if (_elementMap.TryGetValue(elementId, out var el))
            {
                if (el.Attr != null && el.Attr.ContainsKey("disabled"))
                    return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        public Task<string> GetElementComputedRoleAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var el))
            {
                if (el.Attr != null && el.Attr.TryGetValue("role", out var role))
                    return Task.FromResult(role);
                // Default roles based on tag
                return Task.FromResult(el.TagName switch
                {
                    "button" => "button",
                    "a" => "link",
                    "input" => "textbox",
                    "img" => "img",
                    _ => ""
                });
            }
            return Task.FromResult("");
        }

        public Task<string> GetElementComputedLabelAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var el))
            {
                if (el.Attr != null)
                {
                    if (el.Attr.TryGetValue("aria-label", out var label)) return Task.FromResult(label);
                    if (el.Attr.TryGetValue("alt", out var alt)) return Task.FromResult(alt);
                    if (el.Attr.TryGetValue("title", out var title)) return Task.FromResult(title);
                }
            }
            return Task.FromResult("");
        }

        public Task ClearElementAsync(string elementId)
        {
            // Clear input/textarea value
            if (_elementMap.TryGetValue(elementId, out var el))
            {
                var tag = el.TagName?.ToLowerInvariant();
                if (tag == "input" || tag == "textarea")
                {
                    el.SetAttribute("value", "");
                }
            }
            return Task.CompletedTask;
        }

        public Task SendKeysToElementAsync(string elementId, string text)
        {
            // Set value on input/textarea elements
            if (_elementMap.TryGetValue(elementId, out var el))
            {
                var tag = el.TagName?.ToLowerInvariant();
                if (tag == "input" || tag == "textarea")
                {
                    var currentValue = el.GetAttribute("value") ?? "";
                    el.SetAttribute("value", currentValue + text);
                }
            }
            return Task.CompletedTask;
        }

        public Task<string> GetPageSourceAsync()
        {
            // Serialize DOM back to HTML using Element.OuterHTML
            var dom = _engine.GetActiveDom();
            if (dom != null)
            {
                return Task.FromResult((dom as Element)?.OuterHTML ?? (dom as Document)?.DocumentElement?.OuterHTML ?? "");
            }
            return Task.FromResult("<html></html>");
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args = null)
        {
            await Task.CompletedTask;
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
            
            try { FenLogger.Debug($"[ExecuteScript] Wrapped: {wrappedScript.Substring(0, Math.Min(500, wrappedScript.Length))}...", LogCategory.JavaScript); } catch { }
            var rawResult = _engine.Evaluate(wrappedScript);
            try { FenLogger.Debug($"[ExecuteScript] Raw result type: {rawResult?.GetType().Name}", LogCategory.JavaScript); } catch { }
            
            if (rawResult is FenBrowser.FenEngine.Core.FenValue val && val.Type == JsValueType.Error)
            {
                throw new Exception(val.AsError());
            }

            // Convert FenValue to native .NET type for proper JSON serialization
            if (rawResult is FenBrowser.FenEngine.Core.FenValue fenValue)
            {
                return fenValue.ToNativeObject();
            }
            
            return rawResult;
        }

        // Storage for async script callback result
        private object _asyncScriptResult = null;
        private bool _asyncScriptDone = false;
        private bool _pointerDown = false;
        private readonly object _asyncScriptLock = new object();

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeoutMs)
        {
            // Reset state
            lock (_asyncScriptLock)
            {
                _asyncScriptResult = null;
                _asyncScriptDone = false;
            }

            // Create a unique callback ID for this execution
            var callbackId = Guid.NewGuid().ToString("N");
            
            // Prepare arguments array with the callback as the last argument
            var argsList = args?.ToList() ?? new List<object>();
            
            // We need to create the callback function in JavaScript context
            // The callback should store the result in a global variable we can poll
            // Also set up requestAnimationFrame polyfill that works with our polling
            var setupScript = $@"
                window.__wptrunner_async_result_{callbackId} = null;
                window.__wptrunner_async_done_{callbackId} = false;
                
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
            
            // The script wrapper that provides the callback function
            // Add debug logging to trace WPT script execution
            var wrappedScript = $@"
                var __args = {jsonArgs};
                console.log('[WPT] __args before push: ' + __args.length);
                var __callback = function(result) {{
                    console.log('[WPT] Callback called with: ' + JSON.stringify(result));
                    window.__wptrunner_async_result_{callbackId} = result;
                    window.__wptrunner_async_done_{callbackId} = true;
                }};
                var pushResult = __args.push(__callback);
                console.log('[WPT] push result: ' + pushResult);
                console.log('[WPT] __args after push: ' + __args.length);
                console.log('[WPT] __args[' + (__args.length - 1) + '] type: ' + typeof __args[__args.length - 1]);
                
                // Debug: Log key values before script runs
                console.log('[WPT] document.readyState: ' + document.readyState);
                console.log('[WPT] typeof requestAnimationFrame: ' + typeof requestAnimationFrame);
                console.log('[WPT] typeof Document: ' + typeof Document);
                console.log('[WPT] __args length: ' + __args.length);
                console.log('[WPT] __args[0] type: ' + typeof __args[0]);
                
                (function() {{
                    {processedScript}
                }})();
                
                // Debug: Log rAF queue after script
                console.log('[WPT] rAF callbacks length: ' + (window.__raf_callbacks ? window.__raf_callbacks.length : 'no array'));
            ";
            
            try { FenLogger.Debug($"[AsyncScript] Executing wrapped script (timeout {timeoutMs}ms)", LogCategory.JavaScript); } catch { }
            try { FenLogger.Debug($"[AsyncScript] Input script (first 500 chars): {(script.Length > 500 ? script.Substring(0, 500) : script)}", LogCategory.JavaScript); } catch { }
            try { FenLogger.Debug($"[AsyncScript] Processed script (first 500 chars): {(processedScript.Length > 500 ? processedScript.Substring(0, 500) : processedScript)}", LogCategory.JavaScript); } catch { }
            
            // Execute the script (it should call the callback eventually)
            try 
            {
                var execResult = _engine.Evaluate(wrappedScript);
                try { FenLogger.Debug($"[AsyncScript] Script executed, result type: {execResult?.GetType().Name ?? "null"}", LogCategory.JavaScript); } catch { }
                if (execResult is FenBrowser.FenEngine.Core.FenValue fv && (int)fv.Type == 10)
                {
                    try { FenLogger.Debug($"[AsyncScript] Script error: {fv.AsError()}", LogCategory.Errors); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { FenLogger.Debug($"[AsyncScript] Script exception: {ex.Message}", LogCategory.Errors); } catch { }
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
                        try { FenLogger.Debug($"[AsyncScript] Poll loop {loopCount}, rafCount: {rafResult}", LogCategory.JavaScript); } catch { }
                    }
                } 
                catch (Exception ex)
                {
                    try { FenLogger.Debug($"[AsyncScript] rAF error: {ex.Message}", LogCategory.Errors); } catch { }
                }
                
                // Check if the callback was called
                var doneCheck = _engine.Evaluate($"window.__wptrunner_async_done_{callbackId}");
                if (doneCheck is FenBrowser.FenEngine.Core.FenValue dv && dv.IsBoolean && dv.ToBoolean())
                {
                    // Get the result
                    var result = _engine.Evaluate($"window.__wptrunner_async_result_{callbackId}");
                    try { FenLogger.Debug($"[AsyncScript] Callback received result after {sw.ElapsedMilliseconds}ms", LogCategory.JavaScript); } catch { }
                    
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
            
            try { FenLogger.Debug($"[AsyncScript] Timeout after {timeoutMs}ms", LogCategory.Errors); } catch { }
            
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

        // Cookie management
        private readonly Dictionary<string, WebDriverCookie> _cookies = new Dictionary<string, WebDriverCookie>();

        public Task<List<WebDriverCookie>> GetAllCookiesAsync()
        {
            return Task.FromResult(_cookies.Values.ToList());
        }

        public Task<WebDriverCookie> GetCookieAsync(string name)
        {
            _cookies.TryGetValue(name, out var cookie);
            return Task.FromResult(cookie);
        }

        public Task AddCookieAsync(WebDriverCookie cookie)
        {
            _cookies[cookie.Name] = cookie;
            return Task.CompletedTask;
        }

        public Task DeleteCookieAsync(string name)
        {
            _cookies.Remove(name);
            return Task.CompletedTask;
        }

        public Task DeleteAllCookiesAsync()
        {
            _cookies.Clear();
            return Task.CompletedTask;
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
                            if (_elementMap.TryGetValue(action.Origin, out var el))
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
            
            try { if (hit != null) FenLogger.Debug($"[BrowserApi] Hit test at ({docX},{docY}) found: {hit.NodeName} (ID: {hit.Id})", LogCategory.General); } catch {}
            
            return hit;
        }

        private Element _focusedElement;

        private int _cursorIndex = 0;
        private int _selectionAnchor = -1;

        private void SyncFocusFromPointerTarget(Element target)
        {
            if (target == null)
            {
                // Don't clear focus when target is null — this typically means the
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
                _focusedElement = target;
                ElementStateManager.Instance.SetFocusedElement(target);
                if (directEditable)
                {
                    bool isContentEditable = string.Equals(target.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                    var val = isContentEditable ? (target.TextContent ?? string.Empty) : (target.GetAttribute("value") ?? string.Empty);
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
                _focusedElement = descendantEditable;
                ElementStateManager.Instance.SetFocusedElement(descendantEditable);
                bool descendantIsContentEditable = string.Equals(descendantEditable.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);
                var val = descendantIsContentEditable ? (descendantEditable.TextContent ?? string.Empty) : (descendantEditable.GetAttribute("value") ?? string.Empty);
                _cursorIndex = val.Length;
                _selectionAnchor = -1;
            }
            else
            {
                _focusedElement = null;
                ElementStateManager.Instance.SetFocusedElement(null);
            }
        }

        public async Task HandleClipboardCommand(string command, string data = null)
        {
             if (_focusedElement == null) return;
             var tag = _focusedElement.NodeName?.ToLowerInvariant();
             if (tag != "input" && tag != "textarea") return;
             
             var val = _focusedElement.GetAttribute("value") ?? "";
             int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int len = end - start;
             
             switch (command.ToLowerInvariant())
             {
                 case "selectall":
                     _selectionAnchor = 0;
                     _cursorIndex = val.Length;
                     try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
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
                         _focusedElement.SetAttribute("value", val);
                         try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
                     }
                     break;
             }
        }
        
        public string GetSelectedText()
        {
             if (_focusedElement == null) return "";
             var val = _focusedElement.GetAttribute("value") ?? "";
             int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
             return end > start ? val.Substring(start, end - start) : "";
        }
        
        public void DeleteSelection()
        {
             if (_focusedElement == null) return;
             var val = _focusedElement.GetAttribute("value") ?? "";
             int start = _selectionAnchor != -1 ? Math.Min(_selectionAnchor, _cursorIndex) : _cursorIndex;
             int end = _selectionAnchor != -1 ? Math.Max(_selectionAnchor, _cursorIndex) : _cursorIndex;
             
             if (end > start)
             {
                 val = val.Remove(start, end - start);
                 _cursorIndex = start;
                 _selectionAnchor = -1;
                 _focusedElement.SetAttribute("value", val);
                 try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
             }
        }

        public async Task HandleElementClick(Element element)
        {
            _selectionAnchor = -1; // Reset selection
            bool allowDefaultActivation = ConsumeClickDefaultActivationDecision(element);
            if (element == null) 
            {
                _focusedElement = null;
                ElementStateManager.Instance.SetFocusedElement(null);
                try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
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
                 try { FenLogger.Debug($"[BrowserApi] Button clicked: {element.NodeName}", LogCategory.General); } catch {}

                 if (allowDefaultActivation && IsSubmitActivationControl(element, tag))
                 {
                     await SubmitFormAsync(element);
                 }
            }
            // Handle input focus
            else if (tag == "input" || tag == "textarea")
            {
                _focusedElement = element;
                ElementStateManager.Instance.SetFocusedElement(element);
                
                // Set cursor to end on focus
                var val = element.GetAttribute("value") ?? "";
                _cursorIndex = val.Length;
                _selectionAnchor = -1;
                
                try { FenLogger.Debug($"[BrowserApi] Input focused: {element.NodeName} (ID: {element.GetAttribute("id")})", LogCategory.General); } catch {}
                
                // Trigger a repaint to show caret (if we had one)
                try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
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
                    _focusedElement = element;
                    ElementStateManager.Instance.SetFocusedElement(element);
                    try { FenLogger.Debug($"[BrowserApi] Element focused: {element.NodeName} (ID: {element.GetAttribute("id")})", LogCategory.General); } catch {}
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
                        _focusedElement = null;
                        ElementStateManager.Instance.SetFocusedElement(null);
                    }
                }
                
                // Trigger repaint 
                try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
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
                try { FenLogger.Debug("[BrowserApi] Form submit canceled by script.", LogCategory.Events); } catch {}
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

            try { FenLogger.Warn($"[BrowserApi] Form method '{method}' not fully implemented; navigating to action URL.", LogCategory.Navigation); } catch {}
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
                        _focusedElement = nestedEditable;
                        tag = _focusedElement.NodeName?.ToLowerInvariant();
                        ElementStateManager.Instance.SetFocusedElement(_focusedElement);
                    }
                }

                bool isContentEditable = string.Equals(_focusedElement.GetAttribute("contenteditable"), "true", StringComparison.OrdinalIgnoreCase);

                if (tag == "input" || tag == "textarea") // Added textarea support
                {
                    var val = _focusedElement.GetAttribute("value") ?? "";
                    
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
                    
                    _focusedElement.SetAttribute("value", val);
                    
                    // Trigger Repaint
                     try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
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
                    try { RepaintReady?.Invoke(this, _engine.GetActiveDom()); } catch {}
                }
            }
            catch (Exception ex)
            {
                 try { FenLogger.Error($"[BrowserApi] Error typing key: {ex.Message}", LogCategory.General); } catch {}
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
            try { _engine.Dispose(); } catch { }
        }

        // IHistoryBridge Implementation
        public int Length => _history.Count;
        public object State => (_historyIndex >= 0 && _historyIndex < _history.Count) ? _history[_historyIndex].State : null;

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
                try { Navigated?.Invoke(this, _current); } catch { }
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
                    try { Navigated?.Invoke(this, _current); } catch { }
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[BrowserHost] ReplaceState failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        public void Go(int delta)
        {
            // Async void is generally bad, but this is an interface method called from JS bridge
            // We'll wrap it in Task.Run to fire and forget
            Task.Run(async () =>
            {
                int targetIndex = _historyIndex + delta;
                if (targetIndex >= 0 && targetIndex < _history.Count)
                {
                    // Update: Determine direction
                    if (delta == 0) 
                    {
                         await RefreshAsync();
                         return;
                    }
                    
                    // For single steps, use GoBack/Forward
                    if (delta == -1) await GoBackAsync();
                    else if (delta == 1) await GoForwardAsync();
                    else
                    {
                        // TODO: Implement multi-step logic correctly regarding popped states
                         _historyIndex = targetIndex;
                         var entry = _history[_historyIndex];
                         if (entry.IsPushState)
                         {
                             _current = entry.Url;
                             _engine.NotifyPopState(entry.State);
                             try { Navigated?.Invoke(this, _current); } catch { }
                         }
                         else
                         {
                            await NavigateAsync(_history[_historyIndex].Url.AbsoluteUri);
                         }
                    }
                }
            });
        }
    }
}




