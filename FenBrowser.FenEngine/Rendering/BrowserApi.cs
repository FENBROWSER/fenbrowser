using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Security;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.DevTools;

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
        Dictionary<LiteElement, CssComputed> ComputedStyles { get; }

        // Events
        event EventHandler<Uri> Navigated;
        event EventHandler<string> NavigationFailed;
        event EventHandler<bool> LoadingChanged;
        event EventHandler<string> TitleChanged;
        event EventHandler<object> RepaintReady;
        event Action<string> ConsoleMessage;
        event Action<Avalonia.Rect?> HighlightRectChanged;

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
        LiteElement GetDomRoot();
        void HighlightElement(LiteElement element);
        void RemoveHighlight();
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

    public sealed class BrowserHost : IBrowser, IDisposable
    {
        private readonly CustomHtmlEngine _engine = new CustomHtmlEngine();
        private readonly ResourceManager _resources = new ResourceManager(new HttpClient());
        private readonly NavigationManager _navManager;
        private Uri _current;
        private bool _disposed;
        
        private readonly List<Uri> _history = new List<Uri>();
        private int _historyIndex = -1;
        private bool _isNavigatingHistory;
        
        // Map WebDriver Element IDs to LiteElements
        private readonly Dictionary<string, LiteElement> _elementMap = new Dictionary<string, LiteElement>();

        public event EventHandler<Uri> Navigated;
        public event EventHandler<string> NavigationFailed;
        public event EventHandler<bool> LoadingChanged;
        public event EventHandler<string> TitleChanged;
        public event EventHandler<object> RepaintReady;
        public event Action<string> ConsoleMessage;
        public event Action<Avalonia.Rect?> HighlightRectChanged;

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
        public Dictionary<LiteElement, CssComputed> ComputedStyles => _engine.LastComputedStyles;

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
            
            // Get HTTP/2 and Brotli enabled handler from factory
            var config = NetworkConfiguration.Instance;
            var handler = FenBrowser.Core.Network.HttpClientFactory.CreateHandler();
            
            // Add certificate callback for security display
            handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            {
                // Capture certificate info
                var info = new CertificateInfo
                {
                    Subject = cert.Subject,
                    Issuer = cert.Issuer,
                    NotBefore = cert.GetEffectiveDateString() != null ? DateTime.Parse(cert.GetEffectiveDateString()) : DateTime.MinValue,
                    IsValid = errors == System.Net.Security.SslPolicyErrors.None,
                    Thumbprint = cert.GetCertHashString()
                };

                if (cert is System.Security.Cryptography.X509Certificates.X509Certificate2 cert2)
                {
                    info.NotBefore = cert2.NotBefore;
                    info.NotAfter = cert2.NotAfter;
                    info.Thumbprint = cert2.Thumbprint;
                }

                _lastCertificate = info;
                _lastSslErrors = errors;

                return errors == System.Net.Security.SslPolicyErrors.None;
            };
            
            // Create HTTP/2 + Brotli enabled client
            var httpClient = FenBrowser.Core.Network.HttpClientFactory.CreateClient(handler);
            
            // Log HTTP/2 and Brotli configuration
            FenLogger.Info($"[BrowserHost] HTTP/2: {config.EnableHttp2}, Brotli: {config.EnableBrotli}, " +
                          $"Version: {httpClient.DefaultRequestVersion}", LogCategory.Network);
            
            _resources = new ResourceManager(httpClient, isPrivate);

            // Wire up DevTools Network Monitoring
            _resources.NetworkRequestStarting += (id, req) =>
            {
                try
                {
                    var headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
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
                try { RepaintReady?.Invoke(this, dom); }
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

            ResourceManager.LogSink = (msg) =>
            {
                Console.WriteLine(msg);
                try { ConsoleMessage?.Invoke(msg); } catch { }
            };
            Console.WriteLine($"[BrowserHost] CWD: {Environment.CurrentDirectory}");
            _navManager = new NavigationManager(_resources);
        }

        private CertificateInfo _lastCertificate;
        private System.Net.Security.SslPolicyErrors _lastSslErrors;
        public CertificateInfo CurrentCertificate => _lastCertificate;


        public LiteElement GetDomRoot()
        {
            return _engine.GetActiveDom();
        }

        public async Task<bool> GoBackAsync()
        {
            if (!CanGoBack) return false;
            _historyIndex--;
            _isNavigatingHistory = true;
            try { return await NavigateAsync(_history[_historyIndex].AbsoluteUri); }
            finally { _isNavigatingHistory = false; }
        }

        public async Task<bool> GoForwardAsync()
        {
            if (!CanGoForward) return false;
            _historyIndex++;
            _isNavigatingHistory = true;
            try { return await NavigateAsync(_history[_historyIndex].AbsoluteUri); }
            finally { _isNavigatingHistory = false; }
        }

        public async Task<bool> NavigateAsync(string url)
        {
            if (_disposed) return false;
            if (string.IsNullOrWhiteSpace(url)) return false;

            try
            {
                bool isViewSource = false;
                if (url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase))
                {
                    isViewSource = true;
                    url = url.Substring("view-source:".Length);
                }

                Console.WriteLine($"[NavigateAsync] Start: {url}");

                _resources.ResetBlockedCount();
                _resources.ActivePolicy = null; // Reset CSP for new page
                _engine.ActivePolicy = null;
                CurrentPolicy = null;

                var result = await _navManager.NavigateAsync(url);
                
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
                    var escaped = System.Net.WebUtility.HtmlEncode(htmlToRender);
                    htmlToRender = $"<html><head><title>Source of {url}</title></head><body style='font-family: Consolas, monospace; white-space: pre; background-color: #f8f8f8; color: #333; padding: 10px; font-size: 13px;'>{escaped}</body></html>";
                }

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
                try { FenLogger.Debug($"[BrowserApi] Navigating to: {uri}. Previous _current: {_current?.AbsoluteUri ?? "null"}", LogCategory.General); } catch {}
                
                var elem = await _engine.RenderAsync(htmlToRender, uri, u => _resources.FetchTextAsync(u), u => _resources.FetchImageAsync(u), u => { _ = NavigateAsync(u.AbsoluteUri); });
                
                // IMPORTANT: Set _current BEFORE firing RepaintReady so UI can access correct BaseUrl
                _current = uri;
                
                // Debug: Log that _current is now set
                try { FenLogger.Debug($"[BrowserApi] _current now set to: {_current?.AbsoluteUri}. Firing RepaintReady...", LogCategory.General); } catch {}
                
                try { RepaintReady?.Invoke(this, elem); } catch { }

                if (!_isNavigatingHistory)
                {
                    if (_historyIndex < _history.Count - 1)
                    {
                        _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
                    }
                    _history.Add(uri);
                    _historyIndex = _history.Count - 1;
                }

                try { Navigated?.Invoke(this, uri); } catch { }
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
                Console.WriteLine("[BrowserHost] Browsing data cleared (Cookies + Cache)");
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
                        if (n.Tag == "a" && n.Attr != null)
                        {
                            string href;
                            if (n.Attr.TryGetValue("href", out href) && !string.IsNullOrWhiteSpace(href))
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
                    if (n.IsText && !string.IsNullOrWhiteSpace(n.Text))
                        sb.AppendLine(n.Text.Trim());
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        public async Task<string> GetTitleAsync()
        {
            var dom = _engine.GetActiveDom();
            if (dom != null)
            {
                var titleNode = dom.Descendants().FirstOrDefault(n => n.Tag == "title");
                if (titleNode != null) return titleNode.Text ?? "";
            }
            return "";
        }

        public async Task<object> ExecuteScriptAsync(string script)
        {
            try { FenLogger.Debug($"[BrowserApi] ExecuteScriptAsync called with script: {script}", LogCategory.JavaScript); } catch { }
            return _engine.Evaluate(script);
        }

        public async Task<string> FindElementAsync(string strategy, string value)
        {
            var dom = _engine.GetActiveDom();
            if (dom == null) throw new Exception("No active DOM");

            LiteElement found = null;
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    found = dom.Descendants().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("id") && n.Attr["id"] == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    found = dom.Descendants().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("class") && n.Attr["class"].Contains(cls));
                }
                else
                {
                    found = dom.Descendants().FirstOrDefault(n => n.Tag == value);
                }
            }
            else if (strategy == "xpath")
            {
                if (value.StartsWith("//"))
                {
                    var tag = value.Substring(2);
                    found = dom.Descendants().FirstOrDefault(n => n.Tag == tag);
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
                if (element.Tag == "a" && element.Attr != null && element.Attr.TryGetValue("href", out var href))
                {
                     await NavigateAsync(href);
                }
            }
        }

        // OLD CaptureScreenshotAsync removed - new one with string return is in NEW WEBDRIVER METHODS section

        private void RaiseNavigationFailed(string msg) => NavigationFailed?.Invoke(this, msg);

        public void HighlightElement(LiteElement element)
        {
            _engine.HighlightElement(element);
        }

        public void RemoveHighlight()
        {
            _engine.RemoveHighlight();
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
            var dom = _engine.GetActiveDom();
            if (dom == null) return null;

            LiteElement searchRoot = dom;
            if (!string.IsNullOrEmpty(parentId) && _elementMap.TryGetValue(parentId, out var parent))
                searchRoot = parent;

            LiteElement found = FindElementByStrategy(searchRoot, strategy, value);
            if (found != null)
            {
                var id = Guid.NewGuid().ToString();
                _elementMap[id] = found;
                return id;
            }
            return null;
        }

        public async Task<string[]> FindElementsAsync(string strategy, string value, string parentId = null)
        {
            var dom = _engine.GetActiveDom();
            if (dom == null) return Array.Empty<string>();

            LiteElement searchRoot = dom;
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

        private LiteElement FindElementByStrategy(LiteElement root, string strategy, string value)
        {
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    return root.Descendants().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("id") && n.Attr["id"] == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    return root.Descendants().FirstOrDefault(n => n.Attr != null && n.Attr.ContainsKey("class") && n.Attr["class"].Contains(cls));
                }
                else
                {
                    return root.Descendants().FirstOrDefault(n => n.Tag == value);
                }
            }
            else if (strategy == "xpath" && value.StartsWith("//"))
            {
                var tag = value.Substring(2);
                return root.Descendants().FirstOrDefault(n => n.Tag == tag);
            }
            else if (strategy == "tag name")
            {
                return root.Descendants().FirstOrDefault(n => n.Tag == value);
            }
            else if (strategy == "link text")
            {
                return root.Descendants().FirstOrDefault(n => n.Tag == "a" && n.Text?.Trim() == value);
            }
            else if (strategy == "partial link text")
            {
                return root.Descendants().FirstOrDefault(n => n.Tag == "a" && n.Text?.Contains(value) == true);
            }
            return null;
        }

        private IEnumerable<LiteElement> FindElementsByStrategy(LiteElement root, string strategy, string value)
        {
            if (strategy == "css selector")
            {
                if (value.StartsWith("#"))
                {
                    var id = value.Substring(1);
                    return root.Descendants().Where(n => n.Attr != null && n.Attr.ContainsKey("id") && n.Attr["id"] == id);
                }
                else if (value.StartsWith("."))
                {
                    var cls = value.Substring(1);
                    return root.Descendants().Where(n => n.Attr != null && n.Attr.ContainsKey("class") && n.Attr["class"].Contains(cls));
                }
                else
                {
                    return root.Descendants().Where(n => n.Tag == value);
                }
            }
            else if (strategy == "tag name")
            {
                return root.Descendants().Where(n => n.Tag == value);
            }
            return Enumerable.Empty<LiteElement>();
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
                    if (n.IsText && !string.IsNullOrWhiteSpace(n.Text))
                        sb.Append(n.Text.Trim()).Append(" ");
                }
                return Task.FromResult(sb.ToString().Trim());
            }
            return Task.FromResult("");
        }

        public Task<string> GetElementTagNameAsync(string elementId)
        {
            if (_elementMap.TryGetValue(elementId, out var el))
                return Task.FromResult(el.Tag?.ToLowerInvariant() ?? "");
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
                return Task.FromResult(el.Tag switch
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
                var tag = el.Tag?.ToLowerInvariant();
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
                var tag = el.Tag?.ToLowerInvariant();
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
            // Serialize DOM back to HTML using LiteElement.ToHtml()
            var dom = _engine.GetActiveDom();
            if (dom != null)
            {
                return Task.FromResult(dom.ToHtml());
            }
            return Task.FromResult("<html></html>");
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args = null)
        {
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
            
            if (rawResult is FenBrowser.FenEngine.Core.ErrorValue ev)
            {
                throw new Exception(ev.Message);
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
                if (execResult is FenBrowser.FenEngine.Core.ErrorValue ev)
                {
                    try { FenLogger.Debug($"[AsyncScript] Script error: {ev.Message}", LogCategory.Errors); } catch { }
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

        public new async Task<string> CaptureScreenshotAsync()
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
        private bool _pointerDown = false;
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
                            await SimulateClickOnElementAsync(elementAtPoint);
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
                            await TypeKeyAsync(action.Value);
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

        private LiteElement FindElementAtPoint(double x, double y)
        {
            // Find element at given coordinates (simplified - would need layout info)
            // For now, return null as hit testing requires layout information
            return null;
        }

        private async Task SimulateClickOnElementAsync(LiteElement element)
        {
            if (element == null) return;
            
            var tag = element.Tag?.ToLowerInvariant();
            
            // Handle anchor clicks
            if (tag == "a")
            {
                var href = element.GetAttribute("href");
                if (!string.IsNullOrEmpty(href))
                {
                    await NavigateAsync(href);
                }
            }
            // Handle button clicks
            else if (tag == "button" || (tag == "input" && 
                (element.GetAttribute("type")?.ToLowerInvariant() == "submit" ||
                 element.GetAttribute("type")?.ToLowerInvariant() == "button")))
            {
                // Would trigger form submission or click handler
            }
            // Handle input focus
            else if (tag == "input" || tag == "textarea")
            {
                // Focus element for typing
            }
        }

        private Task TypeKeyAsync(string key)
        {
            // Type into currently focused element (simplified)
            // In a real implementation, would track focused element and append key
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
            _disposed = true;
            try { _engine.Dispose(); } catch { }
        }
    }
}